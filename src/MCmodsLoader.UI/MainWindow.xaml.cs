using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MCmodsLoader.Core.Constants;
using MCmodsLoader.Core.Models;
using MCmodsLoader.Core.Services;
using Microsoft.Win32;

namespace MCmodsLoader.UI;

public partial class MainWindow : Window
{
    private readonly IMinecraftService _minecraftService;
    private readonly IFabricService _fabricService;
    private readonly IModrinthService _modrinthService;
    private readonly IModManagerService _modManagerService;
    private readonly IUpdateService _updateService;

    private string _minecraftPath = string.Empty;
    private List<ModDefinition> _currentMods = new();
    private AppUpdateInfo? _availableUpdate;
    private bool _isBusy = false;

    public MainWindow()
    {
        InitializeComponent();

        _updateService = new UpdateService();
        var httpClient = new System.Net.Http.HttpClient
        {
            Timeout = TimeSpan.FromMinutes(10)
        };
        httpClient.DefaultRequestHeaders.Add("User-Agent", $"MCmodsLoader/{_updateService.CurrentVersion} (github.com/SecretLUL/MCmodsLoader)");

        _minecraftService = new MinecraftService(httpClient);
        _fabricService = new FabricService(httpClient);
        _modrinthService = new ModrinthService(httpClient);
        _modManagerService = new ModManagerService(_modrinthService);

        Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        TxtAppVersion.Text = $"v{_updateService.CurrentVersion}";

        _minecraftPath = _minecraftService.GetDefaultMinecraftDirectory();
        TxtMinecraftPath.Text = _minecraftPath;

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

            CmbVersions.SelectionChanged -= CmbVersions_SelectionChanged;
            CmbVersions.Items.Clear();
            foreach (var v in versions)
            {
                CmbVersions.Items.Add(v.VersionId);
            }

            if (CmbVersions.Items.Count > 0)
            {
                // Prefer an installed version, or default to first release
                var installedVersions = _minecraftService.GetInstalledVersions(_minecraftPath);
                string? defaultVersion = installedVersions.FirstOrDefault() ?? CmbVersions.Items[0] as string;

                if (!string.IsNullOrEmpty(defaultVersion) && CmbVersions.Items.Contains(defaultVersion))
                {
                    CmbVersions.SelectedItem = defaultVersion;
                    versionToSelect = defaultVersion;
                }
                else
                {
                    CmbVersions.SelectedIndex = 0;
                    versionToSelect = CmbVersions.Items[0] as string;
                }
            }
        }
        catch (Exception ex)
        {
            TxtStatus.Text = $"Error loading versions: {ex.Message}";
        }
        finally
        {
            CmbVersions.SelectionChanged += CmbVersions_SelectionChanged;
            SetBusy(false);
        }

        if (!string.IsNullOrEmpty(versionToSelect))
        {
            await RefreshAllAsync(versionToSelect);
        }
    }

    private async void CmbVersions_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CmbVersions.SelectedItem is string selectedVersion)
        {
            await RefreshAllAsync(selectedVersion);
        }
    }

    private async Task RefreshAllAsync(string mcVersion, bool updateStatusText = true)
    {
        string modsDir = _minecraftService.GetModsDirectory(_minecraftPath);

        // 1. Scan mods folder
        _currentMods = _modManagerService.ScanModsDirectory(modsDir);

        int installedCount = _currentMods.Count(m => m.IsInstalled);
        TxtModCount.Text = $"{installedCount} / {_currentMods.Count} Installed";
        TxtModCount.Foreground = installedCount == _currentMods.Count
            ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#34D399"))
            : new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FBBF24"));

        if (updateStatusText)
        {
            if (installedCount == _currentMods.Count)
            {
                TxtStatus.Text = "All performance mods ready!";
                TxtSubStatus.Text = $"{installedCount} of {_currentMods.Count} curated FPS mods are installed and up to date.";
            }
            else
            {
                TxtStatus.Text = "Ready to inject mods.";
                TxtSubStatus.Text = $"Click 'Inject Performance Mods' to install Fabric (if missing) and {(_currentMods.Count - installedCount)} missing FPS mod(s).";
            }
        }

        // 2. Check Fabric status
        try
        {
            TxtFabricStatus.Text = "Checking...";
            TxtFabricStatus.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#94A3B8"));

            var fabricStatus = await _fabricService.CheckFabricStatusAsync(_minecraftPath, mcVersion);
            if (fabricStatus.IsInstalled)
            {
                TxtFabricStatus.Text = $"Installed (v{fabricStatus.InstalledLoaderVersion})";
                TxtFabricStatus.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#34D399"));
                BtnInstallFabric.Content = "Reinstall";
            }
            else
            {
                TxtFabricStatus.Text = "Not Installed";
                TxtFabricStatus.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F87171"));
                BtnInstallFabric.Content = "Install Fabric";
            }
        }
        catch
        {
            TxtFabricStatus.Text = "Status unknown";
            TxtFabricStatus.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#94A3B8"));
        }
    }

    private async void BtnInject_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;

        string? selectedVersion = CmbVersions.SelectedItem as string;
        if (string.IsNullOrEmpty(selectedVersion))
        {
            MessageBox.Show("Please select a Minecraft version first.", "No Version Selected", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        SetBusy(true, "Preparing performance injection...");
        PrgProgress.Visibility = Visibility.Visible;
        PrgProgress.Value = 0;

        try
        {
            // Step 1: Check and Install Fabric if missing
            var fabricStatus = await _fabricService.CheckFabricStatusAsync(_minecraftPath, selectedVersion);
            if (!fabricStatus.IsInstalled)
            {
                TxtStatus.Text = "Installing Fabric Loader...";
                TxtSubStatus.Text = $"Injecting Fabric Loader for Minecraft {selectedVersion}...";

                var fabricProgress = new Progress<string>(msg =>
                {
                    TxtSubStatus.Text = msg;
                });

                bool fabricOk = await _fabricService.InstallFabricAsync(_minecraftPath, selectedVersion, fabricProgress);
                if (!fabricOk)
                {
                    TxtStatus.Text = "Fabric installation warning";
                    TxtSubStatus.Text = "Could not install Fabric automatically. Continuing with mods injection...";
                }
                else
                {
                    TxtFabricStatus.Text = "Installed";
                    TxtFabricStatus.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#34D399"));
                }
            }

            // Step 2: Fetch and Install Mods
            string modsDir = _minecraftService.GetModsDirectory(_minecraftPath);
            TxtStatus.Text = "Resolving & downloading mods...";

            var statusProgress = new Progress<string>(msg =>
            {
                TxtSubStatus.Text = msg;
            });

            var pctProgress = new Progress<double>(pct =>
            {
                PrgProgress.Value = pct;
            });

            var result = await _modManagerService.InstallOrUpdateModsAsync(
                modsDir,
                selectedVersion,
                _currentMods,
                statusProgress,
                pctProgress);

            // Step 3: Refresh UI list
            await RefreshAllAsync(selectedVersion, updateStatusText: false);

            PrgProgress.Value = 100;
            string profileNameToLaunch = fabricStatus.ProfileName ?? $"Fabric {selectedVersion}";

            if (result.FailedCount == 0)
            {
                TxtStatus.Text = "⚡ Injection Complete!";
                if (result.InstalledOrUpdatedCount > 0)
                {
                    TxtSubStatus.Text = $"Successfully injected {result.InstalledOrUpdatedCount} performance mod(s) ({result.AlreadyUpToDateCount} up to date). Launch Minecraft and select '{profileNameToLaunch}'!";
                    MessageBox.Show(
                        $"All {result.TotalCount} performance mods and Fabric have been successfully configured!\n\n" +
                        $"• Injected / updated: {result.InstalledOrUpdatedCount}\n" +
                        $"• Already up to date: {result.AlreadyUpToDateCount}\n\n" +
                        $"Open your Minecraft Launcher and launch the '{profileNameToLaunch}' profile to enjoy maximum FPS!",
                        "Zero-Setup Injection Successful",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
                else
                {
                    TxtSubStatus.Text = $"All {result.TotalCount} performance mods are already up to date. Ready to play!";
                    MessageBox.Show(
                        $"All {result.TotalCount} performance mods are already installed, up to date, and ready!\n\n" +
                        $"Open your Minecraft Launcher and select the '{profileNameToLaunch}' profile to play with high FPS!",
                        "Mods Up to Date",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
            }
            else
            {
                TxtStatus.Text = $"Injection completed with {result.FailedCount} warning(s)";
                TxtSubStatus.Text = $"Installed {result.InstalledOrUpdatedCount} mods. {result.FailedCount} mod(s) could not be downloaded for MC {selectedVersion}.";

                string failedList = string.Join("\n• ", result.FailedModNames);
                MessageBox.Show(
                    $"Performance injection completed with {result.FailedCount} warning(s):\n\n" +
                    $"• Installed / updated: {result.InstalledOrUpdatedCount}\n" +
                    $"• Already up to date: {result.AlreadyUpToDateCount}\n\n" +
                    $"The following mod(s) are not available for Minecraft {selectedVersion} on Modrinth:\n• {failedList}\n\n" +
                    $"The remaining performance mods are installed and ready to play!",
                    "Injection Finished with Warnings",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            TxtStatus.Text = "Error during injection";
            TxtSubStatus.Text = ex.Message;
            MessageBox.Show($"Injection failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
            PrgProgress.Visibility = Visibility.Collapsed;
        }
    }

    private async void BtnInstallFabric_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;

        string? selectedVersion = CmbVersions.SelectedItem as string;
        if (string.IsNullOrEmpty(selectedVersion))
        {
            MessageBox.Show("Please select a Minecraft version first.", "No Version Selected", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        SetBusy(true, "Installing Fabric Loader...");
        PrgProgress.Visibility = Visibility.Visible;
        PrgProgress.Value = 50;

        try
        {
            var progress = new Progress<string>(msg =>
            {
                TxtSubStatus.Text = msg;
            });

            bool success = await _fabricService.InstallFabricAsync(_minecraftPath, selectedVersion, progress);
            await RefreshAllAsync(selectedVersion);

            if (success)
            {
                TxtStatus.Text = "Fabric installed successfully!";
                TxtSubStatus.Text = $"Fabric profile created for Minecraft {selectedVersion}.";
                MessageBox.Show($"Fabric Loader successfully installed for Minecraft {selectedVersion}!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                TxtStatus.Text = "Fabric installation failed";
                TxtSubStatus.Text = "Could not install Fabric automatically. Please check your internet connection or install manually.";
                MessageBox.Show("Fabric installation failed. Please check your internet connection.", "Fabric Installer", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        catch (Exception ex)
        {
            TxtStatus.Text = "Error installing Fabric";
            TxtSubStatus.Text = ex.Message;
        }
        finally
        {
            SetBusy(false);
            PrgProgress.Visibility = Visibility.Collapsed;
        }
    }

    private async void BtnRefresh_Click(object sender, RoutedEventArgs e)
    {
        if (CmbVersions.SelectedItem is string selectedVersion)
        {
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
    }

    private void BtnOpenModsFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string modsDir = _minecraftService.GetModsDirectory(_minecraftPath);
            if (!Directory.Exists(modsDir))
            {
                Directory.CreateDirectory(modsDir);
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = modsDir,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not open mods directory: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void BtnBrowsePath_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select .minecraft directory",
            InitialDirectory = _minecraftPath
        };

        if (dialog.ShowDialog() == true)
        {
            string selected = dialog.FolderName;
            if (_minecraftService.IsValidMinecraftDirectory(selected))
            {
                _minecraftPath = selected;
                TxtMinecraftPath.Text = _minecraftPath;
                await InitializeVersionsAsync();
            }
            else
            {
                MessageBox.Show("The selected folder does not appear to be a valid Minecraft directory.", "Invalid Directory", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }

    private async void BtnCheckUpdates_Click(object sender, RoutedEventArgs e)
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
            if (update != null)
            {
                if (update.HasUpdate)
                {
                    _availableUpdate = update;
                    BannerUpdate.Visibility = Visibility.Visible;
                    TxtUpdateTitle.Text = $"🎉 New update available: v{update.LatestVersion} (Current: v{update.CurrentVersion})";
                    TxtUpdateDetails.Text = string.IsNullOrWhiteSpace(update.ReleaseName)
                        ? "A newer version of MCmodsLoader is available on GitHub."
                        : update.ReleaseName;
                }
                else
                {
                    BannerUpdate.Visibility = Visibility.Collapsed;
                    if (manualCheck)
                    {
                        MessageBox.Show($"You are running the latest version (v{_updateService.CurrentVersion}).", "Up to Date", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                }
            }
            else
            {
                BannerUpdate.Visibility = Visibility.Collapsed;
                if (manualCheck)
                {
                    MessageBox.Show("Could not check for updates. Please verify your internet connection.", "Update Check", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
        }
        catch
        {
            if (manualCheck)
            {
                MessageBox.Show("Could not check for updates. Please verify your internet connection.", "Update Check", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }

    private async void BtnApplyUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_availableUpdate?.DownloadUrl == null)
        {
            if (!string.IsNullOrEmpty(_availableUpdate?.HtmlUrl))
            {
                Process.Start(new ProcessStartInfo { FileName = _availableUpdate.HtmlUrl, UseShellExecute = true });
            }
            return;
        }

        SetBusy(true, "Downloading update...");
        PrgProgress.Visibility = Visibility.Visible;
        PrgProgress.Value = 0;

        var progress = new Progress<double>(pct =>
        {
            PrgProgress.Value = pct;
            TxtSubStatus.Text = $"Downloading update: {pct:F0}%";
        });

        bool ok = await _updateService.DownloadAndApplyUpdateAsync(_availableUpdate.DownloadUrl, progress);
        if (!ok)
        {
            SetBusy(false);
            PrgProgress.Visibility = Visibility.Collapsed;
            MessageBox.Show("Failed to download or apply the update automatically. Opening release page...", "Update Failed", MessageBoxButton.OK, MessageBoxImage.Error);
            if (!string.IsNullOrEmpty(_availableUpdate?.HtmlUrl))
            {
                Process.Start(new ProcessStartInfo { FileName = _availableUpdate.HtmlUrl, UseShellExecute = true });
            }
        }
    }

    private void SetBusy(bool isBusy, string? status = null)
    {
        _isBusy = isBusy;
        BtnInject.IsEnabled = !isBusy;
        BtnInstallFabric.IsEnabled = !isBusy;
        BtnBrowsePath.IsEnabled = !isBusy;
        BtnRefresh.IsEnabled = !isBusy;
        BtnCheckUpdates.IsEnabled = !isBusy;
        CmbVersions.IsEnabled = !isBusy;

        if (!string.IsNullOrEmpty(status))
        {
            TxtStatus.Text = status;
        }
    }
}