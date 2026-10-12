using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using MCmodsLoader.Core.Models;

namespace MCmodsLoader.Core.Services;

public interface IUpdateService
{
    string CurrentVersion { get; }
    Task<AppUpdateInfo?> CheckForUpdateAsync(string githubRepo = "SecretLUL/MCmodsLoader", CancellationToken ct = default);

    /// <summary>
    /// Downloads and verifies the release asset, swaps it in for the running executable,
    /// starts it and exits this process. Returns false, with the current executable left
    /// untouched, if any step fails.
    /// </summary>
    Task<bool> DownloadAndApplyUpdateAsync(AppUpdateInfo update, IProgress<double>? progress = null, CancellationToken ct = default);
}

public class UpdateService : IUpdateService
{
    internal const string StagedFileName = "MCmodsLoader.new.exe";

    // Left behind by the batch-script updater of v1.2.0 and earlier.
    private const string LegacyUpdaterScriptName = "update_restart.bat";

    private static readonly TimeSpan MetadataTimeout = TimeSpan.FromSeconds(20);

    // Antivirus scanners, Defender and OneDrive briefly lock a freshly written exe, so
    // every rename and the restart get a few attempts before the update is given up.
    private const int StepAttempts = 5;
    private static readonly TimeSpan StepRetryDelay = TimeSpan.FromMilliseconds(300);

    private readonly HttpClient _httpClient;
    private readonly string? _customCurrentVersion;

    // ERROR_CANCELLED: the user dismissed a security prompt; asking again is pointless.
    private const int ErrorCancelled = 1223;

    /// <summary>
    /// Passed to the new build as "--updated-from=&lt;pid&gt;". Its CleanupAfterUpdate
    /// signals the named event so the old process knows the new one came up.
    /// </summary>
    internal const string UpdatedFromArgument = "--updated-from=";

    /// <summary>
    /// Starts the swapped-in build with the given arguments and returns its process, if
    /// one could be obtained; replaceable in tests.
    /// </summary>
    internal Func<string, string, Process?> StartExecutable { get; set; } = StartDetached;

    /// <summary>Ends this process after a successful restart; replaceable in tests.</summary>
    internal Action ExitProcess { get; set; } = () => Environment.Exit(0);

    /// <summary>How long the old process waits for the new one to report in.</summary>
    internal TimeSpan StartupConfirmationTimeout { get; set; } = TimeSpan.FromSeconds(10);

    internal enum StartupOutcome { Signalled, Exited, Vanished, StillRunning }

    public string CurrentVersion
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_customCurrentVersion))
                return _customCurrentVersion;

            var entryAssembly = Assembly.GetEntryAssembly();
            if (entryAssembly != null && (entryAssembly.GetName().Name?.StartsWith("MCmodsLoader", StringComparison.OrdinalIgnoreCase) ?? false))
            {
                var infoVer = entryAssembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
                if (!string.IsNullOrWhiteSpace(infoVer))
                {
                    string clean = infoVer.Split('+')[0].Trim().TrimStart('v', 'V');
                    if (Version.TryParse(clean.Split('-')[0], out _))
                        return clean;
                }

                var version = entryAssembly.GetName().Version;
                if (version != null && (version.Major > 0 || version.Minor > 0 || version.Build > 0))
                {
                    int build = version.Build >= 0 ? version.Build : 0;
                    return $"{version.Major}.{version.Minor}.{build}";
                }
            }

            var coreAssembly = typeof(UpdateService).Assembly;
            var coreInfoVer = coreAssembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(coreInfoVer))
            {
                string clean = coreInfoVer.Split('+')[0].Trim().TrimStart('v', 'V');
                if (Version.TryParse(clean.Split('-')[0], out _))
                    return clean;
            }

            var coreVersion = coreAssembly.GetName().Version;
            if (coreVersion != null && (coreVersion.Major > 0 || coreVersion.Minor > 0 || coreVersion.Build > 0))
            {
                int build = coreVersion.Build >= 0 ? coreVersion.Build : 0;
                return $"{coreVersion.Major}.{coreVersion.Minor}.{build}";
            }

            return "0.0.0";
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

    public async Task<AppUpdateInfo?> CheckForUpdateAsync(string githubRepo = "SecretLUL/MCmodsLoader", CancellationToken ct = default)
    {
        try
        {
            // The caller's client may carry a long timeout meant for downloads.
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(MetadataTimeout);

            string url = $"https://api.github.com/repos/{githubRepo}/releases/latest";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/vnd.github.v3+json"));

            using var response = await _httpClient.SendAsync(req, cts.Token);
            if (!response.IsSuccessStatusCode)
                return null;

            using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cts.Token);
            var root = doc.RootElement;

            string tagName = root.TryGetProperty("tag_name", out var tag) ? tag.GetString() ?? "" : "";
            string cleanTag = tagName.Trim().TrimStart('v', 'V');
            string releaseName = root.TryGetProperty("name", out var nm) ? nm.GetString() ?? "" : "";
            string htmlUrl = root.TryGetProperty("html_url", out var hu) ? hu.GetString() ?? "" : "";

            // Prefer the asset named like the shipped binary, else the first .exe.
            JsonElement? exeAsset = null;
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    string assetName = asset.TryGetProperty("name", out var an) ? an.GetString() ?? "" : "";
                    if (assetName.Equals("MCmodsLoader.exe", StringComparison.OrdinalIgnoreCase))
                    {
                        exeAsset = asset;
                        break;
                    }
                    if (exeAsset == null && assetName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        exeAsset = asset;
                    }
                }
            }

            string? exeDownloadUrl = null;
            string? sha256 = null;
            if (exeAsset is { } exe)
            {
                exeDownloadUrl = exe.TryGetProperty("browser_download_url", out var du) ? du.GetString() : null;
                if (exe.TryGetProperty("digest", out var dg) && dg.ValueKind == JsonValueKind.String)
                {
                    sha256 = ParseSha256Digest(dg.GetString());
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
                HtmlUrl = htmlUrl,
                DownloadSha256 = sha256
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Extracts the hex hash from a GitHub asset digest such as "sha256:1f8a...".</summary>
    internal static string? ParseSha256Digest(string? digest)
    {
        const string prefix = "sha256:";
        if (string.IsNullOrWhiteSpace(digest) || !digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return null;

        string hex = digest.Substring(prefix.Length).Trim();
        return hex.Length == 64 && hex.All(Uri.IsHexDigit) ? hex.ToLowerInvariant() : null;
    }

    public static bool IsNewerVersion(string latestVersionStr, string currentVersionStr)
    {
        if (Version.TryParse(NormalizeVersionString(latestVersionStr), out var latest) &&
            Version.TryParse(NormalizeVersionString(currentVersionStr), out var current))
        {
            // 1.3.0 supersedes 1.3.0-beta.1, which compares equal once the suffix is cut.
            if (latest == current)
                return !latestVersionStr.Contains('-') && currentVersionStr.Contains('-');

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

    public Task<bool> DownloadAndApplyUpdateAsync(AppUpdateInfo update, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        string? exePath = GetCurrentExecutablePath();
        if (exePath == null)
            return Task.FromResult(false);

        return ApplyUpdateAsync(update, exePath, restartAndExit: true, progress, ct);
    }

    /// <summary>
    /// The update proper, with the target path and the final restart exposed for tests.
    /// Returns true once the new build is in place: normally this process then exits,
    /// but if the new build could not be started and could not be rolled back either,
    /// it returns true without exiting and the user has to restart by hand.
    /// </summary>
    internal async Task<bool> ApplyUpdateAsync(AppUpdateInfo update, string targetExePath, bool restartAndExit, IProgress<double>? progress, CancellationToken ct)
    {
        targetExePath = Path.GetFullPath(targetExePath);

        string? stagedPath = await DownloadUpdateAsync(update, targetExePath, progress, ct);
        if (stagedPath == null)
            return false;

        string? backupPath = await SwapExecutableAsync(targetExePath, stagedPath);
        if (backupPath == null)
        {
            // The download is only expendable once the target holds an executable again.
            if (File.Exists(targetExePath))
                TryDelete(stagedPath);
            return false;
        }

        if (!restartAndExit)
            return true;

        // A started process can still be blocked or quarantined by antivirus right
        // after launch, so this process only exits once the new one has reported in
        // (or at least is still alive when the wait runs out).
        using EventWaitHandle? startedSignal = CreateStartupSignal(Environment.ProcessId);
        string arguments = UpdatedFromArgument + Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);

        Process? started = null;
        if (await RetryAsync(() => started = StartExecutable(targetExePath, arguments)))
        {
            using (started)
            {
                var outcome = await WaitForStartupAsync(startedSignal, started, targetExePath, StartupConfirmationTimeout);
                if (outcome is StartupOutcome.Signalled or StartupOutcome.StillRunning)
                {
                    ExitProcess();
                    return true;
                }
            }
        }

        // The new build cannot be started or did not come up; put the running one back
        // so the next launch is not left with a binary that never worked.
        return await RollbackSwapAsync(targetExePath, stagedPath, backupPath);
    }

    private static Process? StartDetached(string exePath, string arguments)
    {
        return Process.Start(new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = arguments,
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(exePath) ?? ""
        });
    }

    internal static string GetStartupSignalName(int oldProcessId) =>
        $"Local\\MCmodsLoader.UpdateStarted.{oldProcessId}";

    private static EventWaitHandle? CreateStartupSignal(int processId)
    {
        try
        {
            return new EventWaitHandle(false, EventResetMode.ManualReset, GetStartupSignalName(processId));
        }
        catch
        {
            // Without the handshake the wait falls back to watching the process.
            return null;
        }
    }

    /// <summary>
    /// Waits until the new build signals that it started, exits, or loses its exe. A
    /// process still running at the timeout is assumed to be alive (a slow first start
    /// under a virus scan), as is one whose handle the shell did not hand out.
    /// </summary>
    internal static async Task<StartupOutcome> WaitForStartupAsync(WaitHandle? startedSignal, Process? process, string targetExePath, TimeSpan timeout)
    {
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            // Checked first: a build that reported in and then closed is fine.
            if (startedSignal != null && startedSignal.WaitOne(0))
                return StartupOutcome.Signalled;
            if (HasExited(process))
                return StartupOutcome.Exited;
            if (!File.Exists(targetExePath))
                return StartupOutcome.Vanished;
            if (elapsed.Elapsed >= timeout)
                return StartupOutcome.StillRunning;

            await Task.Delay(100);
        }
    }

    private static bool HasExited(Process? process)
    {
        try
        {
            return process != null && process.HasExited;
        }
        catch
        {
            // No access to the process: nothing can be concluded from it.
            return false;
        }
    }

    /// <summary>
    /// The new build's side of the handshake: signals the old process named by
    /// "--updated-from=&lt;pid&gt;", if any. Unknown or malformed arguments are ignored.
    /// </summary>
    internal static void SignalUpdateStarted(IEnumerable<string> commandLineArgs)
    {
        // Named events exist only on Windows, the one platform the app ships for.
        if (!OperatingSystem.IsWindows())
            return;

        try
        {
            foreach (string arg in commandLineArgs)
            {
                if (!arg.StartsWith(UpdatedFromArgument, StringComparison.OrdinalIgnoreCase) ||
                    !int.TryParse(arg.AsSpan(UpdatedFromArgument.Length), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int pid))
                    continue;

                if (EventWaitHandle.TryOpenExisting(GetStartupSignalName(pid), out var signal))
                {
                    using (signal)
                    {
                        signal.Set();
                    }
                }
            }
        }
        catch
        {
            // The old process falls back to its timeout.
        }
    }

    /// <summary>
    /// Downloads the release asset next to <paramref name="targetExePath"/> as
    /// MCmodsLoader.new.exe and verifies it. Returns the staged path, or null (with
    /// nothing left on disk) if the download failed or did not verify.
    /// </summary>
    internal async Task<string?> DownloadUpdateAsync(AppUpdateInfo update, string targetExePath, IProgress<double>? progress, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(update.DownloadUrl))
            return null;

        string dir = Path.GetDirectoryName(Path.GetFullPath(targetExePath)) ?? AppContext.BaseDirectory;
        string stagedPath = Path.Combine(dir, StagedFileName);
        if (stagedPath.Equals(Path.GetFullPath(targetExePath), StringComparison.OrdinalIgnoreCase))
            return null;

        try
        {
            using (var response = await _httpClient.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                long? totalBytes = response.Content.Headers.ContentLength;

                using var stream = await response.Content.ReadAsStreamAsync(ct);
                using var fileStream = new FileStream(stagedPath, FileMode.Create, FileAccess.Write, FileShare.None);

                byte[] buffer = new byte[81920];
                long totalRead = 0;
                int read;

                while ((read = await stream.ReadAsync(buffer, ct)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, read), ct);
                    totalRead += read;
                    if (totalBytes.HasValue && totalBytes.Value > 0)
                    {
                        progress?.Report((double)totalRead / totalBytes.Value * 100.0);
                    }
                }

                if (totalBytes.HasValue && totalRead != totalBytes.Value)
                    throw new IOException($"Download truncated: {totalRead} of {totalBytes.Value} bytes.");
            }

            if (VerifyExecutable(stagedPath, update.DownloadSha256))
                return stagedPath;
        }
        catch
        {
            // Network, disk or cancellation; fall through to the cleanup below.
        }

        TryDelete(stagedPath);
        return null;
    }

    /// <summary>
    /// True if the file is a non-empty PE image ("MZ" header) and, when a hash is
    /// given, its SHA-256 matches. Guards against swapping in an HTML error page or a
    /// corrupted download.
    /// </summary>
    internal static bool VerifyExecutable(string path, string? expectedSha256)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (fs.Length < 2 || fs.ReadByte() != 'M' || fs.ReadByte() != 'Z')
                return false;

            if (string.IsNullOrWhiteSpace(expectedSha256))
                return true;

            fs.Position = 0;
            string actual = Convert.ToHexString(SHA256.HashData(fs));
            return actual.Equals(expectedSha256.Trim(), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Moves the staged build into place. Windows refuses to overwrite or delete a
    /// running image but does allow renaming it, so the current executable is first
    /// renamed to "&lt;name&gt;.old.exe" and the new one moved to the freed path. If that
    /// second move fails the rename is undone. Returns the backup path, or null if the
    /// swap did not happen; the target then holds the previous build unless even
    /// restoring it failed, in which case it survives as the backup.
    /// </summary>
    internal static async Task<string?> SwapExecutableAsync(string targetExePath, string stagedExePath)
    {
        string backupPath = GetBackupPath(targetExePath, unique: false);

        // A backup still locked by a lingering old process cannot be reused.
        if (File.Exists(backupPath) && !TryDelete(backupPath))
            backupPath = GetBackupPath(targetExePath, unique: true);

        if (!await RetryAsync(() => File.Move(targetExePath, backupPath)))
            return null;

        if (await RetryAsync(() => File.Move(stagedExePath, targetExePath)))
            return backupPath;

        // Whatever a failed move may have left at the target is not to be trusted.
        await RetryAsync(() => File.Move(backupPath, targetExePath, overwrite: true));
        return null;
    }

    /// <summary>
    /// Undoes a swap after the new build failed to start. Each step is attempted on its
    /// own: the new build is parked as the staged file again, the backup is restored over
    /// whatever is left at the target, and the new build is only deleted once the
    /// previous one is back. Returns true if the new build ended up installed after all.
    /// </summary>
    private static async Task<bool> RollbackSwapAsync(string targetExePath, string stagedExePath, string backupPath)
    {
        bool parked = await RetryAsync(() => File.Move(targetExePath, stagedExePath, overwrite: true));
        bool restored = await RetryAsync(() => File.Move(backupPath, targetExePath, overwrite: true));
        if (restored)
        {
            if (parked)
                TryDelete(stagedExePath);
            return false;
        }

        // The previous build cannot come back; keep the new one rather than nothing.
        if (parked && !File.Exists(targetExePath))
            await RetryAsync(() => File.Move(stagedExePath, targetExePath));

        return File.Exists(targetExePath);
    }

    private static async Task<bool> RetryAsync(Action step)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                step();
                return true;
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException ||
                                       ex is Win32Exception { NativeErrorCode: ErrorCancelled })
            {
                // Waiting does not bring a missing file back, and a user who cancelled a
                // security prompt does not want to see it four more times.
                return false;
            }
            catch when (attempt < StepAttempts)
            {
                await Task.Delay(StepRetryDelay);
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Removes what an update leaves next to the executable: the renamed previous build
    /// (which stays locked until the old process has exited, so deletion is retried in
    /// the background for a few seconds), a stale MCmodsLoader.new.exe from an aborted
    /// download, and the batch script of the old updater. Call once at startup, once
    /// the app is up: it also tells the process that installed this build (named by
    /// "--updated-from=&lt;pid&gt;" on the command line) that the update came up.
    /// </summary>
    public static void CleanupAfterUpdate(string? exePath = null)
    {
        SignalUpdateStarted(Environment.GetCommandLineArgs().Skip(1));
        _ = CleanupAfterUpdateAsync(exePath, TimeSpan.FromSeconds(10));
    }

    internal static Task CleanupAfterUpdateAsync(string? exePath, TimeSpan retryFor)
    {
        string? target = exePath ?? GetCurrentExecutablePath();
        if (string.IsNullOrEmpty(target))
            return Task.CompletedTask;

        return Task.Run(async () =>
        {
            try
            {
                target = Path.GetFullPath(target);
                string? dir = Path.GetDirectoryName(target);
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                    return;

                var deadline = DateTime.UtcNow + retryFor;
                while (true)
                {
                    bool allGone = true;
                    foreach (string leftover in FindUpdateLeftovers(dir, target))
                    {
                        allGone &= TryDelete(leftover);
                    }

                    if (allGone || DateTime.UtcNow >= deadline)
                        return;

                    await Task.Delay(500);
                }
            }
            catch
            {
                // Cleanup must never take the app down.
            }
        });
    }

    private static IEnumerable<string> FindUpdateLeftovers(string dir, string exePath)
    {
        string baseName = Path.GetFileNameWithoutExtension(exePath);
        var candidates = new List<string>
        {
            Path.Combine(dir, LegacyUpdaterScriptName),
            Path.Combine(dir, StagedFileName),
            GetBackupPath(exePath, unique: false)
        };

        // Unique backups made when "<name>.old.exe" itself was still locked.
        foreach (string file in Directory.EnumerateFiles(dir, $"{baseName}.old-*.exe"))
        {
            candidates.Add(file);
        }

        // Never delete the executable that is running, whatever it is named.
        return candidates.Where(p => File.Exists(p) && !p.Equals(exePath, StringComparison.OrdinalIgnoreCase));
    }

    private static string GetBackupPath(string exePath, bool unique)
    {
        string dir = Path.GetDirectoryName(exePath) ?? "";
        string baseName = Path.GetFileNameWithoutExtension(exePath);
        string suffix = unique ? $".old-{Guid.NewGuid().ToString("N")[..8]}.exe" : ".old.exe";
        return Path.Combine(dir, baseName + suffix);
    }

    /// <summary>
    /// The path of the running executable, or null when running under the dotnet host,
    /// whose path must never be renamed or replaced.
    /// </summary>
    private static string? GetCurrentExecutablePath()
    {
        string? path = Environment.ProcessPath;
        if (string.IsNullOrEmpty(path))
            return null;

        return Path.GetFileName(path).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase) ? null : path;
    }

    private static bool TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                // A read-only exe stays read-only when renamed to the backup, and
                // File.Delete refuses read-only files.
                var attributes = File.GetAttributes(path);
                if (attributes.HasFlag(FileAttributes.ReadOnly))
                    File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
                File.Delete(path);
            }
            return true;
        }
        catch
        {
            return false;
        }
    }
}
