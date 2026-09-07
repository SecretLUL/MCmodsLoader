using System.Diagnostics;
using System.Runtime.InteropServices;
using MCmodsLoader.Core.Models;
using MCmodsLoader.Core.Services;
using MCmodsLoader.UI.Interop;

namespace MCmodsLoader.UI;

/// <summary>
/// The behaviour ported from the WPF code-behind. The flow is unchanged: detect the
/// Minecraft directory, list versions, scan mods, install Fabric when missing, then
/// download or update the curated mod pack.
/// </summary>
internal sealed partial class MainWindow
{
    private readonly IMinecraftService _minecraftService;
    private readonly IFabricService _fabricService;
    private readonly IModrinthService _modrinthService;
    private readonly IModManagerService _modManagerService;
    private readonly IUpdateService _updateService;

    private string _minecraftPath = "Detecting .minecraft...";
    private List<ModDefinition> _currentMods = new();
    private readonly List<string> _versions = new();
    private AppUpdateInfo? _availableUpdate;
    private bool _isBusy;

    // Painted state.
    private string _appVersionText = "v?";
    private string _statusText = "Ready to inject mods.";
    private string _subStatusText = "Click 'Inject Performance Mods' to install Fabric (if missing) and all essential FPS mods.";
    private string _modCountText = "0 / 0 Installed";
    private uint _modCountColor = Theme.Accent;
    private string _fabricStatusText = "Checking...";
    private uint _fabricStatusColor = Theme.TextMuted;
    private string _bannerTitle = string.Empty;
    private string _bannerDetails = string.Empty;
    private bool _bannerVisible;
    private bool _progressVisible;

    private MainWindow()
    {
        _updateService = new UpdateService();

        var httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        httpClient.DefaultRequestHeaders.Add(
            "User-Agent", $"MCmodsLoader/{_updateService.CurrentVersion} (github.com/SecretLUL/MCmodsLoader)");

        _minecraftService = new MinecraftService(httpClient);
        _fabricService = new FabricService(httpClient);
        _modrinthService = new ModrinthService(httpClient);
        _modManagerService = new ModManagerService(_modrinthService);
    }

    // ---- Command routing -------------------------------------------------

    private void OnCommand(int id, int notification)
    {
        if (notification != Native.BN_CLICKED) return;

        switch (id)
        {
            case IdVersions: _picker.Toggle(_hwnd); break;
            case IdInject: _ = OnInjectAsync(); break;
            case IdFabric: _ = OnInstallFabricAsync(); break;
            case IdRefresh: _ = OnRefreshAsync(); break;
            case IdBrowse: _ = OnBrowsePathAsync(); break;
            case IdCheckUpdates: _ = OnCheckUpdatesAsync(); break;
            case IdUpdateNow: _ = OnApplyUpdateAsync(); break;
            case IdOpenMods: OnOpenModsFolder(); break;
        }
    }

    private string? SelectedVersion => _picker.SelectedItem;

    private void OnVersionSelected(string version) => _ = RunGuardedAsync(() => RefreshAllAsync(version));

    // ---- Startup ---------------------------------------------------------

    private async Task OnLoadedAsync()
    {
        _appVersionText = $"v{_updateService.CurrentVersion}";
        _minecraftPath = _minecraftService.GetDefaultMinecraftDirectory();
        Repaint();

        await InitializeVersionsAsync();
        _ = CheckForUpdatesAsync();
    }

    private async Task InitializeVersionsAsync()
    {
        SetBusy(true, "Detecting installed Minecraft versions...");

        string? versionToSelect = null;
        try
        {
            var versions = await _minecraftService.GetAllAvailableVersionsAsync(_minecraftPath);

            _versions.Clear();
            _versions.AddRange(versions.Select(v => v.VersionId));

            if (_versions.Count > 0)
            {
                // Prefer a version the user already has installed, else the newest release.
                var installed = _minecraftService.GetInstalledVersions(_minecraftPath);
                string? preferred = installed.FirstOrDefault(v => _versions.Contains(v)) ?? _versions[0];

                int index = Math.Max(0, _versions.IndexOf(preferred));
                versionToSelect = _versions[index];
                _picker.SetItems(_versions, index);
            }
            else
            {
                _picker.SetItems(_versions, -1);
            }
        }
        catch (Exception ex)
        {
            SetStatus($"Error loading versions: {ex.Message}");
        }
        finally
        {
            SetBusy(false);
        }

        if (!string.IsNullOrEmpty(versionToSelect))
        {
            await RefreshAllAsync(versionToSelect);
        }
    }

    private async Task RefreshAllAsync(string mcVersion, bool updateStatusText = true)
    {
        string modsDir = _minecraftService.GetModsDirectory(_minecraftPath);

        _currentMods = _modManagerService.ScanModsDirectory(modsDir);
        int installedCount = _currentMods.Count(m => m.IsInstalled);

        _modCountText = $"{installedCount} / {_currentMods.Count} Installed";
        _modCountColor = installedCount == _currentMods.Count ? Theme.Success : Theme.Warning;

        if (updateStatusText)
        {
            if (installedCount == _currentMods.Count)
            {
                _statusText = "All performance mods ready!";
                _subStatusText = $"{installedCount} of {_currentMods.Count} curated FPS mods are installed and up to date.";
            }
            else
            {
                _statusText = "Ready to inject mods.";
                _subStatusText = $"Click 'Inject Performance Mods' to install Fabric (if missing) and {_currentMods.Count - installedCount} missing FPS mod(s).";
            }
        }

        _fabricStatusText = "Checking...";
        _fabricStatusColor = Theme.TextMuted;
        Repaint();

        try
        {
            var status = await _fabricService.CheckFabricStatusAsync(_minecraftPath, mcVersion);
            if (status.IsInstalled)
            {
                _fabricStatusText = $"Installed v{status.InstalledLoaderVersion}";
                _fabricStatusColor = Theme.Success;
                _btnFabric.SetText("Reinstall");
            }
            else
            {
                _fabricStatusText = "Not Installed";
                _fabricStatusColor = Theme.Danger;
                _btnFabric.SetText("Install Fabric");
            }
        }
        catch
        {
            _fabricStatusText = "Status unknown";
            _fabricStatusColor = Theme.TextMuted;
        }

        Repaint();
    }

    // ---- Actions ---------------------------------------------------------

    private async Task OnInjectAsync()
    {
        if (_isBusy) return;

        if (SelectedVersion is not { } selectedVersion)
        {
            Warn("Please select a Minecraft version first.", "No Version Selected");
            return;
        }

        SetBusy(true, "Preparing performance injection...");
        ShowProgress(true, 0);

        try
        {
            var fabricStatus = await _fabricService.CheckFabricStatusAsync(_minecraftPath, selectedVersion);
            if (!fabricStatus.IsInstalled)
            {
                SetStatus("Installing Fabric Loader...");
                SetSubStatus($"Injecting Fabric Loader for Minecraft {selectedVersion}...");

                bool fabricOk = await _fabricService.InstallFabricAsync(
                    _minecraftPath, selectedVersion, new Progress<string>(SetSubStatus));

                if (!fabricOk)
                {
                    SetStatus("Fabric installation warning");
                    SetSubStatus("Could not install Fabric automatically. Continuing with mods injection...");
                }
                else
                {
                    _fabricStatusText = "Installed";
                    _fabricStatusColor = Theme.Success;
                    Repaint();
                }
            }

            string modsDir = _minecraftService.GetModsDirectory(_minecraftPath);
            SetStatus("Resolving & downloading mods...");

            var result = await _modManagerService.InstallOrUpdateModsAsync(
                modsDir, selectedVersion, _currentMods,
                new Progress<string>(SetSubStatus),
                new Progress<double>(SetProgress));

            await RefreshAllAsync(selectedVersion, updateStatusText: false);
            SetProgress(100);

            string profile = fabricStatus.ProfileName ?? $"Fabric {selectedVersion}";

            if (result.FailedCount == 0)
            {
                SetStatus("Injection Complete!");
                if (result.InstalledOrUpdatedCount > 0)
                {
                    SetSubStatus($"Successfully injected {result.InstalledOrUpdatedCount} performance mod(s) ({result.AlreadyUpToDateCount} up to date). Launch Minecraft and select '{profile}'!");
                    Inform(
                        $"All {result.TotalCount} performance mods and Fabric have been successfully configured!\n\n" +
                        $"• Injected / updated: {result.InstalledOrUpdatedCount}\n" +
                        $"• Already up to date: {result.AlreadyUpToDateCount}\n\n" +
                        $"Open your Minecraft Launcher and launch the '{profile}' profile to enjoy maximum FPS!",
                        "Zero-Setup Injection Successful");
                }
                else
                {
                    SetSubStatus($"All {result.TotalCount} performance mods are already up to date. Ready to play!");
                    Inform(
                        $"All {result.TotalCount} performance mods are already installed, up to date, and ready!\n\n" +
                        $"Open your Minecraft Launcher and select the '{profile}' profile to play with high FPS!",
                        "Mods Up to Date");
                }
            }
            else
            {
                SetStatus($"Injection completed with {result.FailedCount} warning(s)");
                SetSubStatus($"Installed {result.InstalledOrUpdatedCount} mods. {result.FailedCount} mod(s) could not be downloaded for MC {selectedVersion}.");

                string failedList = string.Join("\n• ", result.FailedModNames);
                Warn(
                    $"Performance injection completed with {result.FailedCount} warning(s):\n\n" +
                    $"• Installed / updated: {result.InstalledOrUpdatedCount}\n" +
                    $"• Already up to date: {result.AlreadyUpToDateCount}\n\n" +
                    $"The following mod(s) are not available for Minecraft {selectedVersion} on Modrinth:\n• {failedList}\n\n" +
                    $"The remaining performance mods are installed and ready to play!",
                    "Injection Finished with Warnings");
            }
        }
        catch (Exception ex)
        {
            SetStatus("Error during injection");
            SetSubStatus(ex.Message);
            Error($"Injection failed: {ex.Message}", "Error");
        }
        finally
        {
            SetBusy(false);
            ShowProgress(false);
        }
    }

    private async Task OnInstallFabricAsync()
    {
        if (_isBusy) return;

        if (SelectedVersion is not { } selectedVersion)
        {
            Warn("Please select a Minecraft version first.", "No Version Selected");
            return;
        }

        SetBusy(true, "Installing Fabric Loader...");
        ShowProgress(true, 50);

        try
        {
            bool success = await _fabricService.InstallFabricAsync(
                _minecraftPath, selectedVersion, new Progress<string>(SetSubStatus));

            await RefreshAllAsync(selectedVersion);

            if (success)
            {
                SetStatus("Fabric installed successfully!");
                SetSubStatus($"Fabric profile created for Minecraft {selectedVersion}.");
                Inform($"Fabric Loader successfully installed for Minecraft {selectedVersion}!", "Success");
            }
            else
            {
                SetStatus("Fabric installation failed");
                SetSubStatus("Could not install Fabric automatically. Please check your internet connection or install manually.");
                Error("Fabric installation failed. Please check your internet connection.", "Fabric Installer");
            }
        }
        catch (Exception ex)
        {
            SetStatus("Error installing Fabric");
            SetSubStatus(ex.Message);
        }
        finally
        {
            SetBusy(false);
            ShowProgress(false);
        }
    }

    private async Task OnRefreshAsync()
    {
        if (_isBusy || SelectedVersion is not { } selectedVersion) return;

        SetBusy(true, "Refreshing mods and status...");
        try
        {
            await RefreshAllAsync(selectedVersion);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void OnOpenModsFolder()
    {
        try
        {
            string modsDir = _minecraftService.GetModsDirectory(_minecraftPath);
            Directory.CreateDirectory(modsDir);
            Process.Start(new ProcessStartInfo { FileName = modsDir, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Error($"Could not open mods directory: {ex.Message}", "Error");
        }
    }

    private async Task OnBrowsePathAsync()
    {
        if (_isBusy) return;

        string? selected = FolderPicker.Pick(_hwnd, "Select .minecraft directory");
        if (selected == null) return;

        if (_minecraftService.IsValidMinecraftDirectory(selected))
        {
            _minecraftPath = selected;
            Repaint();
            await InitializeVersionsAsync();
        }
        else
        {
            Warn("The selected folder does not appear to be a valid Minecraft directory.", "Invalid Directory");
        }
    }

    private async Task OnCheckUpdatesAsync()
    {
        SetBusy(true, "Checking for updates...");
        await CheckForUpdatesAsync(manualCheck: true);
        SetBusy(false, "Update check finished.");
    }

    private async Task CheckForUpdatesAsync(bool manualCheck = false)
    {
        try
        {
            var update = await _updateService.CheckForUpdateAsync("SecretLUL/MCmodsLoader");

            if (update is { HasUpdate: true })
            {
                _availableUpdate = update;
                _bannerTitle = $"New update available: v{update.LatestVersion} (Current: v{update.CurrentVersion})";
                _bannerDetails = string.IsNullOrWhiteSpace(update.ReleaseName)
                    ? "A newer version of MCmodsLoader is available on GitHub."
                    : update.ReleaseName;
                ShowBanner(true);
                return;
            }

            ShowBanner(false);

            if (manualCheck)
            {
                if (update != null)
                {
                    Inform($"You are running the latest version (v{_updateService.CurrentVersion}).", "Up to Date");
                }
                else
                {
                    Warn("Could not check for updates. Please verify your internet connection.", "Update Check");
                }
            }
        }
        catch
        {
            if (manualCheck)
            {
                Warn("Could not check for updates. Please verify your internet connection.", "Update Check");
            }
        }
    }

    private async Task OnApplyUpdateAsync()
    {
        if (_availableUpdate?.DownloadUrl == null)
        {
            OpenReleasePage();
            return;
        }

        SetBusy(true, "Downloading update...");
        ShowProgress(true, 0);

        var progress = new Progress<double>(pct =>
        {
            SetProgress(pct);
            SetSubStatus($"Downloading update: {pct:F0}%");
        });

        bool ok = await _updateService.DownloadAndApplyUpdateAsync(_availableUpdate.DownloadUrl, progress);
        if (!ok)
        {
            SetBusy(false);
            ShowProgress(false);
            Error("Failed to download or apply the update automatically. Opening release page...", "Update Failed");
            OpenReleasePage();
        }
    }

    private void OpenReleasePage()
    {
        if (string.IsNullOrEmpty(_availableUpdate?.HtmlUrl)) return;
        try
        {
            Process.Start(new ProcessStartInfo { FileName = _availableUpdate.HtmlUrl, UseShellExecute = true });
        }
        catch
        {
            // Opening a browser is a convenience; failing to do so is not worth an error dialog.
        }
    }

    // ---- Small helpers ---------------------------------------------------

    /// <summary>Runs work that must not overlap with an action already in flight.</summary>
    private async Task RunGuardedAsync(Func<Task> work)
    {
        if (_isBusy) return;
        SetBusy(true);
        try { await work(); }
        finally { SetBusy(false); }
    }

    private void SetBusy(bool isBusy, string? status = null)
    {
        _isBusy = isBusy;

        _btnInject.SetEnabled(!isBusy);
        _btnFabric.SetEnabled(!isBusy);
        _btnBrowse.SetEnabled(!isBusy);
        _btnRefresh.SetEnabled(!isBusy);
        _btnCheckUpdates.SetEnabled(!isBusy);
        _picker.Field.SetEnabled(!isBusy);
        if (isBusy) _picker.Close();

        if (!string.IsNullOrEmpty(status)) _statusText = status;
        Repaint();
    }

    private void SetStatus(string text) { _statusText = text; Repaint(); }
    private void SetSubStatus(string text) { _subStatusText = text; Repaint(); }

    private void SetProgress(double percent)
    {
        int value = (int)Math.Clamp(Math.Round(percent), 0, 100);
        Native.SendMessageW(_prgProgress, Native.PBM_SETPOS, value, 0);
    }

    private void ShowProgress(bool visible, double percent = 0)
    {
        _progressVisible = visible;
        SetProgress(percent);
        Native.ShowWindow(_prgProgress, visible ? Native.SW_SHOW : 0);
    }

    private void ShowBanner(bool visible)
    {
        if (_bannerVisible == visible) return;

        _bannerVisible = visible;
        Relayout();       // the window grows or shrinks by the banner's height
        Repaint();
    }

    private void Repaint() => Native.InvalidateRect(_hwnd, 0, false);

    private void Inform(string text, string caption) => Native.MessageBoxW(_hwnd, text, caption, Native.MB_OK | Native.MB_ICONINFORMATION);
    private void Warn(string text, string caption) => Native.MessageBoxW(_hwnd, text, caption, Native.MB_OK | Native.MB_ICONWARNING);
    private void Error(string text, string caption) => Native.MessageBoxW(_hwnd, text, caption, Native.MB_OK | Native.MB_ICONERROR);
}

/// <summary>
/// Folder picker built on SHBrowseForFolder. The modern IFileDialog would need COM
/// interfaces, which cost more than they are worth for one dialog in an AOT binary.
/// </summary>
internal static class FolderPicker
{
    private const int MaxPath = 260;

    public static string? Pick(nint owner, string title)
    {
        nint buffer = Marshal.AllocHGlobal(MaxPath * sizeof(char));
        try
        {
            var info = new Native.BROWSEINFO
            {
                HwndOwner = owner,
                LpszTitle = title,
                UlFlags = Native.BIF_RETURNONLYFSDIRS | Native.BIF_NEWDIALOGSTYLE | Native.BIF_EDITBOX,
            };

            nint pidl = Native.SHBrowseForFolderW(ref info);
            if (pidl == 0) return null;

            try
            {
                return Native.SHGetPathFromIDListW(pidl, buffer) ? Marshal.PtrToStringUni(buffer) : null;
            }
            finally
            {
                Native.ILFree(pidl);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
