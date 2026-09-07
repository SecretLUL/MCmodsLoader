using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using MCmodsLoader.Core.Models;

namespace MCmodsLoader.Core.Services;

public interface IUpdateService
{
    string CurrentVersion { get; }
    Task<AppUpdateInfo?> CheckForUpdateAsync(string githubRepo = "SecretLUL/MCmodsLoader");
    Task<bool> DownloadAndApplyUpdateAsync(string downloadUrl, IProgress<double>? progress = null, string? targetExePath = null, bool launchAndExit = true, bool startExecutable = true, int? processIdToWait = null);
}

public class UpdateService : IUpdateService
{
    private readonly HttpClient _httpClient;
    private readonly string? _customCurrentVersion;

    public string CurrentVersion
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_customCurrentVersion))
                return _customCurrentVersion;

            var entryAssembly = Assembly.GetEntryAssembly();
            if (entryAssembly != null && (entryAssembly.GetName().Name?.StartsWith("MCmodsLoader", StringComparison.OrdinalIgnoreCase) ?? false))
            {
                var version = entryAssembly.GetName().Version;
                if (version != null)
                {
                    int build = version.Build >= 0 ? version.Build : 0;
                    return $"{version.Major}.{version.Minor}.{build}";
                }
            }

            var coreVersion = typeof(UpdateService).Assembly.GetName().Version;
            if (coreVersion != null && coreVersion.Major > 0)
            {
                int build = coreVersion.Build >= 0 ? coreVersion.Build : 0;
                return $"{coreVersion.Major}.{coreVersion.Minor}.{build}";
            }

            return "1.0.0";
        }
    }

    public UpdateService(HttpClient? httpClient = null, string? currentVersion = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        _customCurrentVersion = currentVersion;
        if (!_httpClient.DefaultRequestHeaders.Contains("User-Agent"))
        {
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "SecretLUL/MCmodsLoader (github.com/SecretLUL/MCmodsLoader)");
        }
    }

    public async Task<AppUpdateInfo?> CheckForUpdateAsync(string githubRepo = "SecretLUL/MCmodsLoader")
    {
        try
        {
            string url = $"https://api.github.com/repos/{githubRepo}/releases/latest";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/vnd.github.v3+json"));

            var response = await _httpClient.SendAsync(req);
            if (!response.IsSuccessStatusCode)
                return null;

            using var stream = await response.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);
            var root = doc.RootElement;

            string tagName = root.TryGetProperty("tag_name", out var tag) ? tag.GetString() ?? "" : "";
            string cleanTag = tagName.Trim().TrimStart('v', 'V');
            string releaseName = root.TryGetProperty("name", out var nm) ? nm.GetString() ?? "" : "";
            string body = root.TryGetProperty("body", out var bd) ? bd.GetString() ?? "" : "";
            string htmlUrl = root.TryGetProperty("html_url", out var hu) ? hu.GetString() ?? "" : "";

            string? exeDownloadUrl = null;
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    string assetName = asset.GetProperty("name").GetString() ?? "";
                    if (assetName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        exeDownloadUrl = asset.GetProperty("browser_download_url").GetString();
                        break;
                    }
                }
            }

            bool hasUpdate = IsNewerVersion(cleanTag, CurrentVersion);

            return new AppUpdateInfo
            {
                CurrentVersion = CurrentVersion,
                LatestVersion = cleanTag,
                HasUpdate = hasUpdate,
                DownloadUrl = exeDownloadUrl,
                ReleaseName = releaseName,
                ReleaseNotes = body,
                HtmlUrl = htmlUrl
            };
        }
        catch
        {
            return null;
        }
    }

    public static bool IsNewerVersion(string latestVersionStr, string currentVersionStr)
    {
        if (Version.TryParse(NormalizeVersionString(latestVersionStr), out var latest) &&
            Version.TryParse(NormalizeVersionString(currentVersionStr), out var current))
        {
            return latest > current;
        }
        return false;
    }

    private static string NormalizeVersionString(string ver)
    {
        string cleaned = ver.Trim().TrimStart('v', 'V');
        var parts = cleaned.Split('-')[0].Split('.');
        while (parts.Length < 3)
        {
            parts = parts.Append("0").ToArray();
        }
        return string.Join(".", parts.Take(4));
    }

    public async Task<bool> DownloadAndApplyUpdateAsync(
        string downloadUrl,
        IProgress<double>? progress = null,
        string? targetExePath = null,
        bool launchAndExit = true,
        bool startExecutable = true,
        int? processIdToWait = null)
    {
        string? newExePath = null;
        try
        {
            string currentExePath = targetExePath
                ?? Environment.ProcessPath
                ?? Process.GetCurrentProcess().MainModule?.FileName
                ?? Path.Combine(AppContext.BaseDirectory, "MCmodsLoader.exe");

            string currentDir = Path.GetDirectoryName(currentExePath) ?? AppContext.BaseDirectory;
            newExePath = Path.Combine(currentDir, "MCmodsLoader.new.exe");

            // Download new exe
            using (var response = await _httpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                long? totalBytes = response.Content.Headers.ContentLength;

                using var stream = await response.Content.ReadAsStreamAsync();
                using var fileStream = new FileStream(newExePath, FileMode.Create, FileAccess.Write, FileShare.None);

                byte[] buffer = new byte[81920];
                long totalRead = 0;
                int read;

                while ((read = await stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, read));
                    totalRead += read;
                    if (totalBytes.HasValue && totalBytes.Value > 0)
                    {
                        progress?.Report((double)totalRead / totalBytes.Value * 100.0);
                    }
                }
            }

            // Create robust updater batch script with wait & retry loop
            int pid = processIdToWait ?? Environment.ProcessId;
            string updaterBat = Path.Combine(currentDir, "update_restart.bat");
            string launchSnippet = startExecutable ? $@"start """" ""{currentExePath}""" : "rem restart disabled in test mode";
            string waitSnippet = pid > 0 ? $@"
set WAIT_COUNT=0
:WAIT_PID
tasklist /FI ""PID eq {pid}"" 2>NUL | find /I ""{pid}"" >NUL
if not errorlevel 1 (
    set /A WAIT_COUNT+=1
    if !WAIT_COUNT! GEQ 15 (
        taskkill /F /PID {pid} >nul 2>&1
    )
    ping 127.0.0.1 -n 2 >nul
    goto WAIT_PID
)
ping 127.0.0.1 -n 2 >nul
" : "";

            string batContent = $@"@echo off
setlocal enabledelayedexpansion
cd /d ""{currentDir}""
echo Updating MCmodsLoader...
{waitSnippet}
set RETRY_COUNT=0
:MOVE_RETRY
move /Y ""{newExePath}"" ""{currentExePath}"" >nul 2>&1
if exist ""{newExePath}"" (
    set /A RETRY_COUNT+=1
    if !RETRY_COUNT! LEQ 10 (
        ping 127.0.0.1 -n 2 >nul
        goto MOVE_RETRY
    )
)

if not exist ""{newExePath}"" (
    {launchSnippet}
)
del ""%~f0""
";

            await File.WriteAllTextAsync(updaterBat, batContent);

            if (launchAndExit)
            {
                var psi = new ProcessStartInfo
                {
                    FileName = updaterBat,
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    WorkingDirectory = currentDir
                };

                Process.Start(psi);
                Environment.Exit(0);
            }

            return true;
        }
        catch
        {
            try
            {
                if (!string.IsNullOrEmpty(newExePath) && File.Exists(newExePath))
                {
                    File.Delete(newExePath);
                }
            }
            catch { }
            return false;
        }
    }
}
