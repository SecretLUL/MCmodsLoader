using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using MCmodsLoader.Core.Constants;
using MCmodsLoader.Core.Models;
using MCmodsLoader.Core.Services;
using MCmodsLoader.Core.Utils;
using MCmodsLoader.UI.Interop;

namespace MCmodsLoader.UI;

/// <summary>
/// The behaviour ported from the WPF code-behind. The flow is unchanged: detect the
/// Minecraft directory, list versions, scan mods, install Fabric when missing, then
/// download or update the curated mod pack.
/// </summary>
internal sealed partial class MainWindow
{
    private const string GitHubRepo = "SecretLUL/MCmodsLoader";

    private const string NotAvailableStatus = "Not available for this version";

    // Asking Modrinth whether the missing mods exist for a version must not hold anything up for long.
    private static readonly TimeSpan AvailabilityCheckTimeout = TimeSpan.FromSeconds(20);

    // Longest list a result dialog shows per section before summarising the rest.
    private const int MaxListedPerSection = 10;

    // The shared client's 10-minute timeout is sized for mod downloads; the preset list
    // is a few KB, so a stalled request must not hold a refresh up for that long.
    private static readonly TimeSpan PresetFetchTimeout = TimeSpan.FromSeconds(15);

    private readonly HttpClient _httpClient;
    private readonly IMinecraftService _minecraftService;
    private readonly IFabricService _fabricService;
    private readonly IModrinthService _modrinthService;
    private readonly IModManagerService _modManagerService;
    private readonly IUpdateService _updateService;

    private AppSettings _settings = new();

    // What the last scan made of each optional mod (slug -> on); the Advanced menu shows it.
    private readonly Dictionary<string, bool> _optionalModStates = new(StringComparer.OrdinalIgnoreCase);

    // Per Minecraft version, which mods Modrinth has a build of (slug -> available), learned from
    // injections and from asking about the missing ones after a scan. A mod without a build is
    // left out of the count, so a pack that lacks only such mods reads as complete. Session only:
    // a build can be published any day.
    private readonly Dictionary<string, Dictionary<string, bool>> _availability = new(StringComparer.OrdinalIgnoreCase);

    private string _minecraftPath = "Detecting .minecraft...";
    private bool _minecraftDirValid;
    private readonly List<string> _versions = new();
    private AppUpdateInfo? _availableUpdate;
    private bool _isBusy;

    // Scans and Fabric checks run off the UI thread and can overlap (fast version
    // switching, a toggle during a refresh); only the most recently started one may
    // apply its result, or an older, slower one would overwrite fresher numbers. Any
    // scan bumps the scan sequence; only a full refresh (counts + Fabric card) bumps
    // the refresh sequence, so a toggle's recount does not cancel the Fabric check.
    private int _scanSequence;
    private int _refreshSequence;

    // A scan asked to rewrite the status line may be superseded by one that was not
    // (the preset recount); the request survives until a scan gets to apply it, or an
    // action writes a status of its own.
    private bool _statusUpdatePending;

    // Set when an action writes the status line, so a late recount (the availability check)
    // leaves the action's text alone.
    private bool _statusChangedSinceScan;

    // Scans, the cleanup of switched-off mods and the installer all work on the mods
    // folder; a scan holding jars open would make the installer's deletes fail.
    private readonly SemaphoreSlim _modsFolderLock = new(1, 1);

    // Holding an arrow key on the version field changes the selection ~30 times a
    // second; the refresh only runs once the selection has settled.
    private static readonly TimeSpan SelectionSettleDelay = TimeSpan.FromMilliseconds(250);
    private int _selectionTicket;

    private nint _focusBeforeBusy;

    // Progress<T> posts every report through the message queue, so reports can still
    // be pending when an operation has finished, and would then run inside the result
    // dialog's modal loop and replace the final status. Reports carry the epoch they
    // were created in and are dropped once it has moved on.
    private int _progressEpoch;

    private bool _reportingError;
    private bool _paintErrorReported;

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

        _httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        _httpClient.DefaultRequestHeaders.Add(
            "User-Agent", $"MCmodsLoader/{_updateService.CurrentVersion} (github.com/SecretLUL/MCmodsLoader)");

        _minecraftService = new MinecraftService(_httpClient);
        _fabricService = new FabricService(_httpClient);
        _modrinthService = new ModrinthService(_httpClient);
        _modManagerService = new ModManagerService(_modrinthService);
    }

    // ---- Command routing -------------------------------------------------

    private void OnCommand(int id, int notification)
    {
        if (notification != Native.BN_CLICKED) return;

        switch (id)
        {
            case IdAdvanced: OnToggleAdvanced(); break;
            case IdVersions when !_isBusy: _picker.Toggle(_hwnd); break;
            case IdInject: Fire(OnInjectAsync); break;
            case IdFabric: Fire(OnInstallFabricAsync); break;
            case IdRefresh: Fire(OnRefreshAsync); break;
            case IdBrowse: Fire(OnBrowsePathAsync); break;
            case IdCheckUpdates: Fire(OnCheckUpdatesAsync); break;
            case IdUpdateNow: Fire(OnApplyUpdateAsync); break;
            case IdOpenMods: OnOpenModsFolder(); break;
        }
    }

    private void OnToggleAdvanced()
    {
        if (_isBusy) return;

        // Before the first scan nothing is known about the mods folder yet; the settings
        // and the pack's defaults decide then.
        _advancedMenu.SetOptions(ModPresets.GetOptionalMods()
            .Select(m => new AdvancedMenu.Option(m.Slug, m.Name,
                _optionalModStates.TryGetValue(m.Slug, out bool on) ? on : _settings.IsOptionalModEnabled(m, presentInModsFolder: false)))
            .ToList());
        Native.GetWindowRect(_btnAdvanced.Hwnd, out var rect);
        _advancedMenu.Toggle(_hwnd, rect);
    }

    private void OnModToggled(string slug, bool isEnabled)
    {
        _settings.SetOptionalModEnabled(slug, isEnabled);
        _optionalModStates[slug] = isEnabled;

        // Persisted right away: Inject reads these, and a restart must not quietly
        // bring back a mod the user switched off. Save() swallows I/O errors itself.
        _settings.Save();

        if (SelectedVersion is { } ver)
        {
            Fire(() => UpdateModCountsAsync(ver));
        }
    }

    private string? SelectedVersion => _picker.SelectedItem;

    // Not busy-guarded: the refresh only reads, and the sequence numbers keep a burst
    // of version changes from applying anything but the last one.
    private void OnVersionSelected(string version)
    {
        int ticket = ++_selectionTicket;

        // The card would otherwise show the previous version's Fabric until the refresh
        // lands. Without a valid folder no refresh runs, so nothing would replace it.
        if (_minecraftDirValid)
        {
            _fabricStatusText = "Checking...";
            _fabricStatusColor = Theme.TextMuted;
            Repaint();
        }

        Fire(async () =>
        {
            await Task.Delay(SelectionSettleDelay);
            if (ticket == _selectionTicket) await RefreshAllAsync(version);
        });
    }

    // ---- Startup ---------------------------------------------------------

    private async Task OnLoadedAsync()
    {
        // Removes the previous build a self-update left renamed next to the exe. It
        // retries in the background while that process exits, and never throws.
        UpdateService.CleanupAfterUpdate();

        _settings = AppSettings.Load();

        _appVersionText = $"v{_updateService.CurrentVersion}";
        _minecraftPath = _minecraftService.GetDefaultMinecraftDirectory();
        _minecraftDirValid = _minecraftService.IsValidMinecraftDirectory(_minecraftPath);
        Repaint();

        try
        {
            if (_minecraftDirValid)
            {
                await InitializeVersionsAsync();
            }
            else
            {
                // Scanning would create .minecraft\mods in a folder the launcher never made.
                ShowMissingDirectoryHint();
            }
        }
        finally
        {
            Fire(() => CheckForUpdatesAsync());
        }
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
                // Prefer the installed version that already has Fabric, then any installed
                // version, then the newest release. Reading the versions folder and the
                // launcher profiles is disk work, so it stays off the UI thread.
                string path = _minecraftPath;
                var available = _versions.ToList();
                string preferred = await Task.Run(() => MinecraftService.PickPreferredVersionAsync(
                        available,
                        _minecraftService.GetInstalledVersions(path),
                        async v => (await _fabricService.CheckFabricStatusAsync(path, v)).IsInstalled))
                    ?? _versions[0];

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

    /// <summary>
    /// The check only accepts a folder the launcher has set up (a launcher profile file
    /// or a versions folder), so a .minecraft that exists but is empty is told apart
    /// from one that is missing.
    /// </summary>
    private bool MinecraftFolderExists => Directory.Exists(_minecraftPath);

    private void ShowMissingDirectoryHint()
    {
        _statusText = MinecraftFolderExists ? "Minecraft folder not set up yet" : "Minecraft folder not found";
        _subStatusText = "Start the Minecraft Launcher once so it sets up .minecraft, or choose the folder with 'Browse'.";
        _modCountText = "0 / 0 Installed";
        _modCountColor = Theme.Warning;
        _fabricStatusText = "Unknown";
        _fabricStatusColor = Theme.TextMuted;
        Repaint();
    }

    /// <summary>
    /// Inject and Install Fabric register the Fabric profile in launcher_profiles.json.
    /// A running launcher keeps that file in memory and can write it back later, which
    /// drops the new profile, so the user is asked first. Runs before the action turns
    /// busy, so declining leaves nothing to undo. Returns false to abort.
    /// </summary>
    /// <remarks>
    /// Called on the UI thread on purpose: the process list is read in a few
    /// milliseconds, and awaiting it would leave a window for a second click.
    /// </remarks>
    private bool ConfirmLauncherClosed()
    {
        bool running;
        try
        {
            running = MinecraftService.IsLauncherRunning();
        }
        catch
        {
            // Not being able to tell is no reason to block the action.
            running = false;
        }

        if (!running) return true;

        int answer = Native.MessageBoxW(_hwnd,
            "The Minecraft Launcher is running.\n\n" +
            "MCmodsLoader is about to register the Fabric profile in the launcher's settings. " +
            "A running launcher can overwrite them later and remove the new profile again.\n\n" +
            "Close the Minecraft Launcher first, then click 'Yes'.\n\nContinue anyway?",
            "Minecraft Launcher Is Running",
            Native.MB_YESNO | Native.MB_ICONWARNING | Native.MB_DEFBUTTON2);

        return answer == Native.IDYES;
    }

    /// <summary>
    /// Inject and Install Fabric write into the Minecraft directory, so they refuse to
    /// run against a folder that is not one rather than creating it from scratch.
    /// </summary>
    private bool EnsureMinecraftDirectory()
    {
        if (_minecraftDirValid && _minecraftService.IsValidMinecraftDirectory(_minecraftPath)) return true;

        _minecraftDirValid = false;
        ShowMissingDirectoryHint();
        string problem = MinecraftFolderExists
            ? $"The Minecraft folder has not been set up by the launcher yet (no launcher profiles or versions folder):\n{_minecraftPath}"
            : $"No Minecraft installation was found at:\n{_minecraftPath}";
        Warn($"{problem}\n\n" +
             "Start the Minecraft Launcher once so it sets up the folder and then click 'Refresh', " +
             "or choose your .minecraft folder with 'Browse'.",
            "Minecraft Not Found");
        return false;
    }

    // ---- Scanning --------------------------------------------------------

    private async Task UpdateModCountsAsync(string mcVersion, bool updateStatusText = true)
    {
        if (!_minecraftDirValid) return;

        if (updateStatusText) _statusUpdatePending = true;
        int sequence = ++_scanSequence;
        string minecraftPath = _minecraftPath;
        var (_, mods, optionalStates) = await WithModsFolderAsync(() =>
            ScanModsAsync(minecraftPath, mcVersion, removeDisabledMods: false));

        // A newer scan (another version, a toggle, an injection) was started meanwhile,
        // or the folder it read is no longer the one shown (Browse, or found missing).
        if (sequence != _scanSequence || !IsCurrentFolder(minecraftPath)) return;

        foreach (var (slug, on) in optionalStates)
        {
            _optionalModStates[slug] = on;
        }

        bool writeStatus = _statusUpdatePending;
        _statusUpdatePending = false;
        ShowModCounts(mcVersion, mods, writeStatus);

        Fire(() => CheckAvailabilityAsync(minecraftPath, mcVersion, mods, sequence, writeStatus));
    }

    private void RememberAvailability(string mcVersion, IEnumerable<ModDefinition> mods)
    {
        var known = AvailabilityFor(mcVersion);
        foreach (var mod in mods)
        {
            if (mod.Status == NotAvailableStatus) known[mod.Slug] = false;
            else if (mod.DownloadUrl != null) known[mod.Slug] = true;
        }
    }

    private Dictionary<string, bool> AvailabilityFor(string mcVersion)
    {
        if (!_availability.TryGetValue(mcVersion, out var known))
        {
            known = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            _availability[mcVersion] = known;
        }
        return known;
    }

    /// <summary>
    /// Shows the mod count, leaving out mods Modrinth has no build of for this version: they
    /// cannot be installed, so they do not keep a pack from being complete.
    /// </summary>
    private void ShowModCounts(string mcVersion, List<ModDefinition> mods, bool writeStatus)
    {
        var known = AvailabilityFor(mcVersion);
        bool Unavailable(ModDefinition m) => !m.IsInstalled && known.TryGetValue(m.Slug, out bool available) && !available;

        var expected = mods.Where(m => !Unavailable(m)).ToList();
        var notYet = mods.Where(Unavailable).Select(m => m.Name).ToList();
        int installedCount = expected.Count(m => m.IsInstalled);

        _modCountText = $"{installedCount} / {expected.Count} Installed";
        _modCountColor = installedCount == expected.Count ? Theme.Success : Theme.Warning;

        if (writeStatus)
        {
            _statusChangedSinceScan = false;
            string note = notYet.Count > 0 ? $" Not available for {mcVersion} yet: {string.Join(", ", notYet)}." : string.Empty;
            if (installedCount == expected.Count)
            {
                _statusText = "All performance mods ready!";
                _subStatusText = $"{installedCount} of {expected.Count} curated FPS mods are installed and up to date.{note}";
            }
            else
            {
                _statusText = "Ready to inject mods.";
                _subStatusText = $"Click 'Inject Performance Mods' to install Fabric (if missing) and {expected.Count - installedCount} missing FPS mod(s).{note}";
            }
        }

        Repaint();
    }

    /// <summary>
    /// Asks Modrinth, once per version and session, whether the mods a scan found missing have a
    /// build for <paramref name="mcVersion"/>, and recounts if some do not. Unanswered questions
    /// (offline, slow) are simply asked again after the next scan.
    /// </summary>
    private async Task CheckAvailabilityAsync(
        string minecraftPath, string mcVersion, List<ModDefinition> mods, int sequence, bool statusWasWritten)
    {
        var known = AvailabilityFor(mcVersion);
        var unknown = mods.Where(m => !m.IsInstalled && !known.ContainsKey(m.Slug)).ToList();
        if (unknown.Count == 0) return;

        using var timeout = new CancellationTokenSource(AvailabilityCheckTimeout);
        var answers = await Task.WhenAll(unknown.Select(async m =>
        {
            try
            {
                return (m.Slug, Available: (bool?)(await _modrinthService.GetLatestVersionFileAsync(m.Slug, mcVersion, timeout.Token) != null));
            }
            catch
            {
                return (m.Slug, Available: (bool?)null);
            }
        }));

        foreach (var (slug, available) in answers)
        {
            if (available is { } value) known[slug] = value;
        }

        // Only the latest scan's numbers may be replaced, and its status line only while no
        // action or newer scan has written one of its own since.
        if (answers.Any(a => a.Available == false) && sequence == _scanSequence &&
            IsCurrentFolder(minecraftPath) && SelectedVersion == mcVersion)
        {
            ShowModCounts(mcVersion, mods, statusWasWritten && !_statusChangedSinceScan);
        }
    }

    /// <summary>
    /// Runs mods-folder work one piece at a time. Waiters resume on the UI thread, and
    /// nothing inside <paramref name="work"/> may take the lock again.
    /// </summary>
    private async Task<T> WithModsFolderAsync<T>(Func<Task<T>> work)
    {
        await _modsFolderLock.WaitAsync();
        try
        {
            return await work();
        }
        finally
        {
            _modsFolderLock.Release();
        }
    }

    /// <summary>True while <paramref name="minecraftPath"/> is still the folder in use and valid.</summary>
    private bool IsCurrentFolder(string minecraftPath) => _minecraftDirValid && _minecraftPath == minecraftPath;

    /// <summary>
    /// Reads every jar in the mods folder, which takes a while on a full folder, so it
    /// runs on the thread pool. The inputs are captured here, on the UI thread. Callers
    /// hold the mods-folder lock (<see cref="WithModsFolderAsync"/>).
    /// </summary>
    private Task<(string ModsDir, List<ModDefinition> Mods, Dictionary<string, bool> OptionalStates)> ScanModsAsync(
        string minecraftPath, string mcVersion, bool removeDisabledMods)
    {
        // A copy: the scan runs on the pool while a toggle may change the settings.
        var settings = _settings.Clone();
        var pack = ModPresets.GetDefaultFpsModPack();

        return Task.Run(() =>
        {
            string modsDir = _minecraftService.GetModsDirectory(minecraftPath);
            if (removeDisabledMods)
            {
                CleanUpDisabledMods(modsDir, pack.Where(m => m.IsOptional && settings.IsOptionalModSwitchedOff(m.Slug)).ToList());
            }

            // The whole pack is scanned, so an optional mod someone installed by hand is found;
            // it then counts as switched on and is kept up to date, unless the user switched it off.
            var scanned = _modManagerService.ScanModsDirectory(modsDir, pack, mcVersion);
            var optionalStates = scanned
                .Where(m => m.IsOptional)
                .ToDictionary(m => m.Slug, m => settings.IsOptionalModEnabled(m, m.InstalledFileName != null), StringComparer.OrdinalIgnoreCase);
            var mods = scanned.Where(m => !m.IsOptional || optionalStates[m.Slug]).ToList();
            return (modsDir, mods, optionalStates);
        });
    }

    /// <summary>Refreshes the mod count and the Fabric card; returns the Fabric status read.</summary>
    private async Task<FabricStatus?> RefreshAllAsync(string mcVersion, bool updateStatusText = true)
    {
        if (!_minecraftDirValid) return null;

        int refresh = ++_refreshSequence;
        await UpdateModCountsAsync(mcVersion, updateStatusText);

        // A refresh started later (another version, Browse) owns the Fabric card now.
        if (refresh != _refreshSequence) return null;

        return await RefreshFabricStatusAsync(mcVersion, refresh);
    }

    private async Task<FabricStatus?> RefreshFabricStatusAsync(string mcVersion, int refresh)
    {
        _fabricStatusText = "Checking...";
        _fabricStatusColor = Theme.TextMuted;
        Repaint();

        string minecraftPath = _minecraftPath;
        FabricStatus? status;
        try
        {
            status = await CheckFabricAsync(mcVersion);
        }
        catch
        {
            status = null;
        }

        // The version check also covers a refresh for a version that is no longer selected.
        if (refresh != _refreshSequence || mcVersion != SelectedVersion || !IsCurrentFolder(minecraftPath)) return status;

        if (status is null)
        {
            _fabricStatusText = "Status unknown";
            _fabricStatusColor = Theme.TextMuted;
        }
        else if (status.IsInstalled && !status.HasLauncherProfile)
        {
            // Installed, but the launcher has nothing to start it with; Inject and
            // Reinstall create the profile.
            _fabricStatusText = $"v{status.InstalledLoaderVersion} - no profile";
            _fabricStatusColor = Theme.Warning;
            _btnFabric.SetText("Reinstall");
        }
        else if (status.IsInstalled)
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

        Repaint();
        return status;
    }

    /// <summary>The check reads the versions folder and launcher_profiles.json from disk.</summary>
    private Task<FabricStatus> CheckFabricAsync(string mcVersion)
    {
        string minecraftPath = _minecraftPath;
        return Task.Run(() => _fabricService.CheckFabricStatusAsync(minecraftPath, mcVersion));
    }

    /// <summary>
    /// The Fabric state an action reports on; null when it could not be read. An
    /// installed loader that no launcher profile points at gets one here, since the
    /// mods are of no use until the launcher can start Fabric.
    /// </summary>
    private async Task<FabricStatus?> RecheckFabricAsync(string mcVersion)
    {
        try
        {
            var status = await CheckFabricAsync(mcVersion);
            if (status is { IsInstalled: true, HasLauncherProfile: false })
            {
                status = await _fabricService.EnsureLauncherProfileAsync(_minecraftPath, mcVersion);
            }
            return status;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// How to start the game: by the launcher profile's name when there is one,
    /// otherwise by the version id the user has to pick in a new launcher installation.
    /// </summary>
    private static string LaunchHint(string mcVersion, FabricStatus fabric) => fabric.ProfileName is { } profile
        ? $"Open your Minecraft Launcher and launch the '{profile}' profile"
        : "No launcher profile uses Fabric yet: in the Minecraft Launcher, create an installation (Installations > New) " +
          $"with the version '{fabric.InstalledVersionId ?? $"Fabric Loader for {mcVersion}"}' and launch it";

    /// <summary>
    /// Brings the counts and the Fabric card up to date after an action. A failure here
    /// must not turn a finished action into a reported failure or swallow its dialog.
    /// </summary>
    private async Task RefreshAfterActionAsync(string mcVersion)
    {
        try
        {
            await RefreshAllAsync(mcVersion, updateStatusText: false);
        }
        catch
        {
            // The counts stay as they were; the next refresh corrects them.
        }
    }

    // ---- Actions ---------------------------------------------------------

    private async Task OnInjectAsync()
    {
        if (_isBusy) return;
        if (!EnsureMinecraftDirectory()) return;

        if (SelectedVersion is not { } selectedVersion)
        {
            Warn("Please select a Minecraft version first.", "No Version Selected");
            return;
        }

        // The dialog's modal loop keeps running queued work; re-check before turning busy.
        if (!ConfirmLauncherClosed() || _isBusy) return;

        SetBusy(true, "Preparing performance injection...");
        ShowProgress(true, 0);

        try
        {
            string minecraftPath = _minecraftPath;
            var subStatus = LiveProgress<string>(SetSubStatus);
            bool fabricInstallFailed = false;

            var fabricBefore = await CheckFabricAsync(selectedVersion);
            if (!fabricBefore.IsInstalled)
            {
                SetStatus("Installing Fabric Loader...");
                SetSubStatus($"Injecting Fabric Loader for Minecraft {selectedVersion}...");

                // Core resumes on whatever context it was started on; starting it on the
                // pool keeps its file and process work off the message loop.
                fabricInstallFailed = !await Task.Run(() =>
                    _fabricService.InstallFabricAsync(minecraftPath, selectedVersion, subStatus));

                if (fabricInstallFailed)
                {
                    SetStatus("Fabric installation warning");
                    SetSubStatus("Could not install Fabric automatically. Continuing with mods injection...");
                }
            }

            SetStatus("Checking installed mods...");
            var percent = LiveProgress<double>(SetProgress);

            // Cleanup, scan and install hold the mods folder as one piece: no refresh
            // may read jars while the installer replaces or deletes them.
            var (result, injectedMods) = await WithModsFolderAsync(async () =>
            {
                // Scan again now: the counts on screen may be from before a toggle, a
                // version change or files the user moved by hand.
                var (modsDir, mods, _) = await ScanModsAsync(minecraftPath, selectedVersion, removeDisabledMods: true);

                SetStatus("Resolving & downloading mods...");
                var outcome = await Task.Run(() => _modManagerService.InstallOrUpdateModsAsync(
                    modsDir, selectedVersion, mods, subStatus, percent));
                return (outcome, mods);
            });

            StopProgressReports();

            // The run looked every mod up, so the count can leave out what has no build yet.
            RememberAvailability(selectedVersion, injectedMods);

            // What is on disk now decides what the summary claims, not what was attempted.
            var fabricAfter = await RecheckFabricAsync(selectedVersion);
            await RefreshAfterActionAsync(selectedVersion);
            SetProgress(100);

            ShowReport(BuildInjectionReport(selectedVersion, result, fabricAfter, fabricInstallFailed));
        }
        catch (Exception ex)
        {
            StopProgressReports();
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

    /// <summary>The outcome of an action: the status lines it leaves behind and the dialog it shows.</summary>
    private readonly record struct ActionReport(string Status, string SubStatus, string Caption, string Message, bool IsWarning);

    private void ShowReport(ActionReport report)
    {
        SetStatus(report.Status);
        SetSubStatus(report.SubStatus);
        if (report.IsWarning) Warn(report.Message, report.Caption);
        else Inform(report.Message, report.Caption);
    }

    /// <summary>
    /// Turns an injection's outcome into the final status and dialog. It only claims
    /// that everything is configured when nothing failed, was skipped or warned about,
    /// Fabric was found on disk afterwards and a launcher profile can start it; it only
    /// names a profile the Fabric check reported. A mod with no build for this Minecraft
    /// version yet is not a problem but a note: it is installed once one is published,
    /// and its old jar being disabled belongs to that.
    /// </summary>
    private static ActionReport BuildInjectionReport(
        string mcVersion, ModInstallResult result, FabricStatus? fabric, bool fabricInstallFailed)
    {
        bool fabricReady = fabric is { IsInstalled: true };
        bool hasProfile = fabricReady && fabric!.ProfileName != null;
        bool hasProblems = result.FailedCount > 0 || result.Skipped.Count > 0 || result.Warnings.Count > 0;
        bool complete = hasProfile && !hasProblems;

        int availableCount = result.TotalCount - result.UnavailableModNames.Count;
        string notYet = result.UnavailableModNames.Count > 0
            ? $" Not available for {mcVersion} yet: {string.Join(", ", result.UnavailableModNames)}."
            : string.Empty;

        string launchHint = fabricReady ? LaunchHint(mcVersion, fabric!) : string.Empty;

        var text = new StringBuilder();
        if (complete)
        {
            string all = result.UnavailableModNames.Count > 0
                ? $"All {availableCount} performance mods available for Minecraft {mcVersion}"
                : $"All {result.TotalCount} performance mods";
            text.Append(result.InstalledOrUpdatedCount > 0
                ? $"{all} and Fabric have been successfully configured!\n\n"
                : $"{all} are already installed, up to date, and ready!\n\n");
        }
        else
        {
            text.Append($"The injection for Minecraft {mcVersion} finished, but not everything is ready:\n\n");
        }

        // Every mod lands in exactly one of these buckets, so the counts add up to the total.
        text.Append($"• Injected / updated: {result.InstalledOrUpdatedCount}\n");
        text.Append($"• Already up to date: {result.AlreadyUpToDateCount}\n");
        if (result.FailedCount > 0) text.Append($"• Failed: {result.FailedCount}\n");
        if (result.UnavailableModNames.Count > 0) text.Append($"• Not available for {mcVersion} yet: {result.UnavailableModNames.Count}\n");
        if (result.Skipped.Count > 0) text.Append($"• Skipped: {result.Skipped.Count}\n");
        text.Append(!fabricReady ? "• Fabric Loader: not installed\n"
            : hasProfile ? $"• Fabric Loader: v{fabric!.InstalledLoaderVersion}\n"
            : $"• Fabric Loader: v{fabric!.InstalledLoaderVersion} (no launcher profile)\n");

        AppendSection(text, "Could not be installed:",
            result.Failures.Select(f => $"{f.ModName} ({f.Reason})"));
        AppendSection(text, $"Not available for Minecraft {mcVersion} yet (installed as soon as Modrinth has a build):",
            result.UnavailableModNames);
        AppendSection(text, "Skipped:",
            result.Skipped.Select(s => $"{s.ModName} ({s.Reason})"));
        AppendSection(text, "Incompatible jars disabled (renamed to .jar.disabled so the game can start):",
            result.DisabledFileNames);
        AppendSection(text, "Warnings:", result.Warnings);

        text.Append('\n');
        if (fabricReady)
        {
            text.Append(complete
                ? $"{launchHint} to enjoy maximum FPS!"
                : $"The installed mods are ready to use. {launchHint} to play.");
        }
        else if (fabric is null)
        {
            text.Append("The Fabric Loader status could not be checked. The mods only load with Fabric, so check the Fabric card or click 'Install Fabric'.");
        }
        else
        {
            text.Append(fabricInstallFailed
                ? "Fabric Loader could not be installed automatically, so the mods will not load yet. Check your internet connection and click 'Install Fabric' to try again."
                : $"Fabric Loader is not installed for Minecraft {mcVersion}, so the mods will not load yet. Click 'Install Fabric' to install it.");
        }

        if (complete)
        {
            return result.InstalledOrUpdatedCount > 0
                ? new ActionReport("Injection Complete!",
                    $"Successfully injected {result.InstalledOrUpdatedCount} performance mod(s) ({result.AlreadyUpToDateCount} up to date). {launchHint}!{notYet}",
                    "Zero-Setup Injection Successful", text.ToString(), IsWarning: false)
                : new ActionReport("Injection Complete!",
                    $"All {availableCount} performance mods are already up to date. Ready to play!{notYet}",
                    "Mods Up to Date", text.ToString(), IsWarning: false);
        }

        // Disabled jars always belong to a mod that was not updated in this run, so they
        // are listed but not counted a second time; missing builds are notes, not issues.
        int issueCount = result.FailedCount + result.Skipped.Count + result.Warnings.Count;

        string status = !fabricReady ? "Injection finished - Fabric missing"
            : issueCount > 0 ? $"Injection completed with {issueCount} issue(s)"
            : "Injection finished - no launcher profile";
        string subStatus = $"{result.SuccessCount} of {availableCount} mods ready for MC {mcVersion}."
                           + (!fabricReady ? " Fabric Loader is not installed."
                               : !hasProfile ? " Create a Fabric installation in the launcher."
                               : string.Empty)
                           + notYet;

        return new ActionReport(status, subStatus, "Injection Finished with Warnings", text.ToString(), IsWarning: true);
    }

    /// <summary>
    /// Appends a titled bullet list, or nothing when it is empty. Long lists are cut so
    /// the message box stays on screen.
    /// </summary>
    private static void AppendSection(StringBuilder text, string heading, IEnumerable<string> items)
    {
        var list = items.ToList();
        if (list.Count == 0) return;

        text.Append($"\n{heading}\n");
        foreach (var item in list.Take(MaxListedPerSection))
        {
            text.Append($"• {item}\n");
        }
        if (list.Count > MaxListedPerSection)
        {
            text.Append($"…and {list.Count - MaxListedPerSection} more\n");
        }
    }

    private async Task OnInstallFabricAsync()
    {
        if (_isBusy) return;
        if (!EnsureMinecraftDirectory()) return;

        if (SelectedVersion is not { } selectedVersion)
        {
            Warn("Please select a Minecraft version first.", "No Version Selected");
            return;
        }

        // The dialog's modal loop keeps running queued work; re-check before turning busy.
        if (!ConfirmLauncherClosed() || _isBusy) return;

        SetBusy(true, "Installing Fabric Loader...");
        ShowProgress(true, 50);

        try
        {
            string minecraftPath = _minecraftPath;
            var subStatus = LiveProgress<string>(SetSubStatus);
            bool success = await Task.Run(() =>
                _fabricService.InstallFabricAsync(minecraftPath, selectedVersion, subStatus));

            StopProgressReports();

            // The Fabric card and the launcher go by what is on disk, so that decides
            // what the user is told, not only the installer's return value.
            var status = await RecheckFabricAsync(selectedVersion);
            await RefreshAfterActionAsync(selectedVersion);

            if (status is { IsInstalled: true })
            {
                string where = $"{LaunchHint(selectedVersion, status)}.";
                SetStatus("Fabric installed successfully!");
                SetSubStatus(where);
                Inform($"Fabric Loader successfully installed for Minecraft {selectedVersion}!\n\n{where}", "Success");
            }
            else
            {
                SetStatus("Fabric installation failed");
                SetSubStatus(success
                    ? $"The installer finished, but no Fabric Loader for Minecraft {selectedVersion} was found afterwards."
                    : "Could not install Fabric automatically. Please check your internet connection or install manually.");
                Error("Fabric installation failed. Please check your internet connection.", "Fabric Installer");
            }
        }
        catch (Exception ex)
        {
            StopProgressReports();
            SetStatus("Error installing Fabric");
            SetSubStatus(ex.Message);
            Error($"Fabric installation failed: {ex.Message}", "Fabric Installer");
        }
        finally
        {
            SetBusy(false);
            ShowProgress(false);
        }
    }

    private async Task OnRefreshAsync()
    {
        if (_isBusy) return;

        // The launcher may have created .minecraft since startup.
        if (!_minecraftDirValid)
        {
            _minecraftDirValid = _minecraftService.IsValidMinecraftDirectory(_minecraftPath);
            if (_minecraftDirValid)
            {
                await InitializeVersionsAsync();
            }
            else
            {
                EnsureMinecraftDirectory();
            }
            return;
        }

        if (SelectedVersion is not { } selectedVersion) return;

        SetBusy(true, "Refreshing mods and status...");
        try
        {
            await TryUpdatePresetsAsync();
            await RefreshAllAsync(selectedVersion);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void OnOpenModsFolder()
    {
        // GetModsDirectory creates the folder, which must not happen outside a real .minecraft.
        if (!EnsureMinecraftDirectory()) return;

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
            _minecraftDirValid = true;
            Repaint();
            await InitializeVersionsAsync();
        }
        else
        {
            Warn($"The selected folder does not appear to be a Minecraft directory:\n{selected}\n\n" +
                 "Choose the .minecraft folder the launcher uses (it contains launcher_profiles.json or a 'versions' folder). " +
                 "If Minecraft has never been started, start the launcher once first.",
                "Invalid Directory");
        }
    }

    private async Task OnCheckUpdatesAsync()
    {
        if (_isBusy) return;

        const string checking = "Checking for updates...";
        string previousStatus = _statusText;
        SetBusy(true, checking);
        try
        {
            await CheckForUpdatesAsync(manualCheck: true);
        }
        finally
        {
            // The check has its own dialog, so the main status goes back to what it was,
            // unless something else has replaced it in the meantime.
            if (_statusText == checking) _statusText = previousStatus;
            SetBusy(false);
        }
    }

    private async Task CheckForUpdatesAsync(bool manualCheck = false)
    {
        bool presetUpdated = await TryUpdatePresetsAsync();

        AppUpdateInfo? update;
        try
        {
            update = await _updateService.CheckForUpdateAsync(GitHubRepo);
        }
        catch
        {
            update = null;
        }

        if (update is { HasUpdate: true })
        {
            _availableUpdate = update;
            _bannerTitle = $"New update available: v{update.LatestVersion} (Current: v{update.CurrentVersion})";
            _bannerDetails = string.IsNullOrWhiteSpace(update.ReleaseName)
                ? "A newer version of MCmodsLoader is available on GitHub."
                : update.ReleaseName;
            ShowBanner(true);
        }
        else if (update != null)
        {
            // Only a check that actually reached GitHub may take a known update away.
            _availableUpdate = null;
            ShowBanner(false);
            if (manualCheck)
            {
                Inform($"You are running the latest version (v{_updateService.CurrentVersion}).", "Up to Date");
            }
        }
        else if (manualCheck)
        {
            Warn("Could not check for updates. Please verify your internet connection.", "Update Check");
        }

        if (presetUpdated && SelectedVersion is { } ver)
        {
            await UpdateModCountsAsync(ver, updateStatusText: false);
        }
    }

    private async Task<bool> TryUpdatePresetsAsync()
    {
        using var timeout = new CancellationTokenSource(PresetFetchTimeout);
        return await ModPresets.TryUpdateFromRemoteAsync(_httpClient, ct: timeout.Token);
    }

    private async Task OnApplyUpdateAsync()
    {
        if (_isBusy) return;

        // Captured now: a background update check may replace the field while this runs.
        if (_availableUpdate is not { DownloadUrl: not null } update)
        {
            OpenReleasePage();
            return;
        }

        SetBusy(true, "Downloading update...");
        ShowProgress(true, 0);

        var progress = LiveProgress<double>(pct =>
        {
            SetProgress(pct);
            SetSubStatus($"Downloading update: {pct:F0}%");
        });

        bool ok;
        try
        {
            // Normally this swaps the exe, starts the new build and exits the process.
            // It returns false (current exe untouched, also under dotnet.exe) on failure,
            // and true when the new build is in place but could not be started.
            ok = await _updateService.DownloadAndApplyUpdateAsync(update, progress);
        }
        catch
        {
            ok = false;
        }

        StopProgressReports();
        SetBusy(false);
        ShowProgress(false);

        if (ok)
        {
            // Installed on disk already: offering it again would download it twice.
            _availableUpdate = null;
            ShowBanner(false);
            SetStatus("Update installed - restart required");
            SetSubStatus($"Close MCmodsLoader and start it again to use v{update.LatestVersion}.");
            Inform($"MCmodsLoader v{update.LatestVersion} has been installed, but it could not be started automatically.\n\n" +
                   "Please close MCmodsLoader and start it again.",
                "Update Installed");
            return;
        }

        SetStatus("Automatic update failed");
        SetSubStatus("Opening the release page so the update can be downloaded manually.");
        Error("Failed to download or apply the update automatically. Opening release page...", "Update Failed");
        OpenReleasePage();
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

    // ---- Error reporting -------------------------------------------------

    /// <summary>
    /// Starts a handler without awaiting it. Whatever it throws is reported instead of
    /// disappearing with the discarded Task.
    /// </summary>
    private void Fire(Func<Task> work) => _ = RunSafeAsync(work);

    private async Task RunSafeAsync(Func<Task> work)
    {
        try
        {
            await work();
        }
        catch (Exception ex)
        {
            ReportError(ex);
        }
    }

    /// <summary>
    /// The one place unexpected exceptions end up: fire-and-forget handlers, queued
    /// callbacks and the window procedure. In a NativeAOT build an exception that
    /// escapes a window procedure terminates the process without a word.
    /// </summary>
    private void ReportError(Exception ex)
    {
        // A popup holding mouse capture would take the first click meant for the dialog.
        // Either may not exist yet when the fault happens during window creation.
        _picker?.Close();
        _advancedMenu?.Close();

        _statusText = "Unexpected error";
        _subStatusText = ex.Message;
        Repaint();

        // The dialog's modal loop keeps dispatching, so a repeating fault must not
        // stack up one dialog per message.
        if (_reportingError) return;

        _reportingError = true;
        try
        {
            Error($"An unexpected error occurred:\n\n{ex.Message}", "MCmodsLoader");
        }
        finally
        {
            _reportingError = false;
        }
    }

    /// <summary>Called by the window procedure for an exception thrown while handling <paramref name="msg"/>.</summary>
    private void ReportWndProcError(uint msg, Exception ex)
    {
        // A fault while painting recurs on every repaint, and reporting it repaints;
        // report it once and leave the window as it is after that.
        if (msg is Native.WM_PAINT or Native.WM_DRAWITEM)
        {
            if (_paintErrorReported) return;
            _paintErrorReported = true;
        }

        ReportError(ex);
    }

    /// <summary>
    /// For the other native callbacks (popup procedures, the button subclass): the same
    /// last line of defence as the main window procedure.
    /// </summary>
    internal static void ReportCallbackError(uint msg, Exception ex) => _instance?.ReportWndProcError(msg, ex);

    // ---- Small helpers ---------------------------------------------------

    private IProgress<T> LiveProgress<T>(Action<T> handler)
    {
        int epoch = _progressEpoch;
        return new Progress<T>(value =>
        {
            if (epoch == _progressEpoch) handler(value);
        });
    }

    /// <summary>Drops every report still queued from the operation that just ended.</summary>
    private void StopProgressReports() => _progressEpoch++;

    private void SetBusy(bool isBusy, string? status = null)
    {
        bool wasBusy = _isBusy;
        _isBusy = isBusy;

        // Disabling the focused button leaves the window without keyboard focus, so it
        // is remembered here and handed back once the buttons are enabled again.
        if (isBusy && !wasBusy)
        {
            nint focus = Native.GetFocus();
            _focusBeforeBusy = focus != 0 && Native.IsChild(_hwnd, focus) ? focus : 0;
        }

        _btnInject.SetEnabled(!isBusy);
        _btnFabric.SetEnabled(!isBusy);
        _btnBrowse.SetEnabled(!isBusy);
        _btnRefresh.SetEnabled(!isBusy);
        _btnCheckUpdates.SetEnabled(!isBusy);
        _btnUpdateNow.SetEnabled(!isBusy);
        _btnAdvanced.SetEnabled(!isBusy);
        _picker.Field.SetEnabled(!isBusy);
        if (isBusy)
        {
            _picker.Close();
            _advancedMenu.Close();
        }
        else if (wasBusy)
        {
            RestoreFocus();
        }

        if (!string.IsNullOrEmpty(status)) _statusText = status;
        Repaint();
    }

    private void RestoreFocus()
    {
        nint saved = _focusBeforeBusy;
        _focusBeforeBusy = 0;

        // Only while this window is the active one: SetFocus on an inactive window would
        // activate it, and only when nobody has placed the focus anywhere since.
        if (Native.GetActiveWindow() != _hwnd) return;
        nint current = Native.GetFocus();
        if (current != 0 && current != _hwnd) return;

        // No fallback when nothing was focused before (startup, a mouse click): parking
        // the focus on Inject would let a single stray Space start an injection.
        if (saved != 0 && Native.IsWindow(saved) && Native.IsWindowEnabled(saved) && Native.IsWindowVisible(saved))
        {
            Native.SetFocus(saved);
        }
    }

    /// <summary>
    /// Removes the jars of optional mods the user switched off; a mod that is merely off by
    /// default is never touched, it may have been installed by hand. Runs on the thread pool.
    /// Ownership is decided by Core's predicate, the same one the scanner uses, so a jar
    /// whose name merely starts like one of these mods (an addon, a fork) is left alone.
    /// </summary>
    private static void CleanUpDisabledMods(string modsDir, IReadOnlyList<ModDefinition> disabledMods)
    {
        if (!Directory.Exists(modsDir) || disabledMods.Count == 0) return;

        foreach (var jar in Directory.GetFiles(modsDir, "*.jar"))
        {
            string fileName = Path.GetFileName(jar);
            var metadata = JarInspector.ReadFabricModJson(jar);

            if (disabledMods.Any(m => ModManagerService.IsJarOfMod(fileName, metadata, m.FabricModId, m.Slug)))
            {
                try { File.Delete(jar); } catch { }
            }
        }
    }

    /// <summary>An action's own status replaces any status line a scan still meant to write.</summary>
    private void SetStatus(string text) { _statusUpdatePending = false; _statusChangedSinceScan = true; _statusText = text; Repaint(); }
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
