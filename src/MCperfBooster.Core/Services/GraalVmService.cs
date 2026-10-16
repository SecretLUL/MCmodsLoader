using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MCperfBooster.Core.Models;
using MCperfBooster.Core.Utils;

namespace MCperfBooster.Core.Services;

/// <summary>A GraalVM that is unpacked and ready to be set in a launcher profile.</summary>
public sealed record InstalledRuntime(int JavaMajor, string HomeDirectory, string JavaExe, string JavawExe, string Version, string Sha256);

public enum RuntimeInstallPhase
{
    Downloading,
    Extracting,
}

/// <summary>Progress of the download or the unpacking, in bytes. <see cref="TotalBytes"/> is 0 while the size is unknown.</summary>
public sealed record RuntimeDownloadProgress(RuntimeInstallPhase Phase, long BytesDone, long TotalBytes);

/// <summary>
/// What <see cref="IGraalVmService.EnsureInstalledAsync"/> ended up with.
/// <see cref="Runtime"/> is the runtime that can be used afterwards: the new one, or the one that was already
/// installed when an update did not work out. <see cref="Downloaded"/> is true when this call installed a new
/// copy (first install or update). <see cref="Error"/> says why the call failed; <see cref="Notice"/> is a
/// remark on a call that worked (offline, so the installed version was kept; runtime in use, so it was not replaced).
/// </summary>
public sealed record RuntimeInstallResult(InstalledRuntime? Runtime, bool Downloaded, string? Error, string? Notice);

public sealed record ArgumentTestResult(bool Ok, int ExitCode, string Output);

public interface IGraalVmService
{
    /// <summary>The folder that holds the runtimes, one subfolder per Java major version.</summary>
    string RuntimesRoot { get; }

    /// <summary>The installed runtime for a Java major version, or null when there is none or it is incomplete.</summary>
    InstalledRuntime? GetInstalled(int javaMajor);

    /// <summary>
    /// Makes sure the newest GraalVM for a Java major version is installed: downloads, verifies and unpacks
    /// it when it is missing or outdated. Failures come back in the result; only cancelling throws
    /// (<see cref="OperationCanceledException"/>), after the half-finished files are cleaned up.
    /// </summary>
    Task<RuntimeInstallResult> EnsureInstalledAsync(int javaMajor, IProgress<RuntimeDownloadProgress>? progress = null, CancellationToken ct = default);

    /// <summary>Starts the runtime once with the arguments (and eager Graal initialization) to see whether the JVM accepts them.</summary>
    Task<ArgumentTestResult> TestArgumentsAsync(InstalledRuntime runtime, string javaArgs, CancellationToken ct = default);

    /// <summary>Deletes every installed GraalVM. Best effort: a runtime in use stays.</summary>
    void RemoveAll();
}

public sealed class GraalVmService : IGraalVmService
{
    private const string MarkerFileName = "mcperfbooster-runtime.json";
    private const string LockFileName = ".install.lock";
    private const string UserAgent = "SecretLUL/MCperfBooster (github.com/SecretLUL/MCperfBooster)";
    private const int BufferSize = 128 * 1024;
    private const int MoveAttempts = 4;

    // Room for the unpacked runtime next to the zip it comes from.
    private const long UnpackedBytes = 700L * 1024 * 1024;

    private static readonly TimeSpan ChecksumTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ArgumentTestTimeout = TimeSpan.FromSeconds(60);

    // Not needed to run the game and 300-480 MB per version: jlink modules, native-image
    // libraries and the JDK sources. Relative to the zip's top folder.
    private static readonly string[] ExcludedFolders = { "jmods/", "lib/static/", "lib/svm/" };
    private const string ExcludedFile = "lib/src.zip";

    private readonly HttpClient _http;
    private readonly Func<int, GraalVmBuild?> _catalog;

    public GraalVmService(HttpClient http, string? runtimesRoot = null, Func<int, GraalVmBuild?>? catalog = null)
    {
        _http = http;
        _catalog = catalog ?? JavaRuntimeCatalog.GetBuild;
        RuntimesRoot = Path.GetFullPath(runtimesRoot ?? Path.Combine(AppPaths.DataDirectory, "runtimes"));

        if (!_http.DefaultRequestHeaders.Contains("User-Agent"))
        {
            _http.DefaultRequestHeaders.Add("User-Agent", UserAgent);
        }
    }

    public string RuntimesRoot { get; }

    // The download may only stall this long, then it is given up on.
    internal TimeSpan IdleTimeout { get; set; } = TimeSpan.FromSeconds(60);

    // Free bytes on the drive of a path, or null when that cannot be told. Replaceable so tests need no full disk.
    internal Func<string, long?> GetFreeSpace { get; set; } = FreeSpaceOf;

    // How long to wait between attempts to rename a folder a virus scanner may be holding.
    internal TimeSpan MoveRetryDelay { get; set; } = TimeSpan.FromMilliseconds(250);

    // Starts java.exe. Replaceable so tests need no real Java.
    internal Func<string, IReadOnlyList<string>, TimeSpan, CancellationToken, Task<ProcessResult>> RunProcess { get; set; } =
        (fileName, arguments, timeout, ct) => ProcessRunner.RunAsync(fileName, arguments, timeout, Path.GetTempPath(), ct);

    private string HomeFor(int javaMajor) => Path.Combine(RuntimesRoot, $"graalvm-{javaMajor}");

    public InstalledRuntime? GetInstalled(int javaMajor)
    {
        string home = HomeFor(javaMajor);
        string javaw = Path.Combine(home, "bin", "javaw.exe");
        string marker = Path.Combine(home, MarkerFileName);
        if (!File.Exists(marker) || !File.Exists(javaw))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(marker));
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("javaMajor", out var major) || major.ValueKind != JsonValueKind.Number ||
                !major.TryGetInt32(out int markedMajor) || markedMajor != javaMajor)
                return null;

            string? version = ReadString(root, "version");
            string? sha256 = ReadString(root, "sha256");
            if (string.IsNullOrWhiteSpace(version) || string.IsNullOrWhiteSpace(sha256))
                return null;

            return new InstalledRuntime(javaMajor, home, Path.Combine(home, "bin", "java.exe"), javaw, version, sha256);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // A marker that cannot be read counts as "not installed": the next run installs afresh.
            return null;
        }
    }

    private static string? ReadString(JsonElement obj, string property) =>
        obj.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    public async Task<RuntimeInstallResult> EnsureInstalledAsync(int javaMajor, IProgress<RuntimeDownloadProgress>? progress = null, CancellationToken ct = default)
    {
        GraalVmBuild? build = _catalog(javaMajor);
        if (build == null)
            return Failed(javaMajor, $"No GraalVM build is available for Java {javaMajor}.");

        FileStream installLock;
        try
        {
            Directory.CreateDirectory(RuntimesRoot);

            // One install at a time: a second one would sweep the first one's half-finished files away.
            installLock = new FileStream(Path.Combine(RuntimesRoot, LockFileName), FileMode.OpenOrCreate, FileAccess.ReadWrite,
                FileShare.None, bufferSize: 1, FileOptions.DeleteOnClose);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Failed(javaMajor, $"Could not set up the Java folder {RuntimesRoot}: {ex.Message} Is another MCperfBooster window installing Java right now?");
        }

        string zipPath = Path.Combine(RuntimesRoot, $".download-graalvm-{javaMajor}.zip");
        string stagingDir = Path.Combine(RuntimesRoot, $".staging-graalvm-{javaMajor}-{Guid.NewGuid():N}");
        try
        {
            return await InstallAsync(build, javaMajor, zipPath, stagingDir, progress, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Failed(javaMajor, Describe(ex));
        }
        finally
        {
            // A failed or cancelled run leaves its partial files here; a finished one has moved them on already.
            TryDeleteFile(zipPath);
            TryDeleteDirectory(stagingDir);
            installLock.Dispose();
        }
    }

    private async Task<RuntimeInstallResult> InstallAsync(GraalVmBuild build, int javaMajor, string zipPath, string stagingDir,
        IProgress<RuntimeDownloadProgress>? progress, CancellationToken ct)
    {
        await Task.Run(SweepLeftovers, ct).ConfigureAwait(false);

        InstalledRuntime? installed = GetInstalled(javaMajor);

        string expectedSha256;
        try
        {
            expectedSha256 = await GetExpectedSha256Async(build, ct).ConfigureAwait(false);
        }
        catch (RuntimeInstallException ex) when (installed != null)
        {
            // Offline is no reason to refuse the runtime that is already here.
            return new RuntimeInstallResult(installed, false, null, $"{ex.Message} Keeping the installed GraalVM {installed.Version}.");
        }

        if (installed != null && string.Equals(installed.Sha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
            return new RuntimeInstallResult(installed, false, null, null);

        EnsureFreeSpace(build);

        string actualSha256 = await DownloadAsync(build, zipPath, progress, ct).ConfigureAwait(false);
        if (!string.Equals(actualSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new RuntimeInstallException(
                "The downloaded GraalVM does not match its checksum: the file is damaged or was changed on the way. Nothing was installed, please try again.");
        }

        string version = await Task.Run(() => Unpack(build, javaMajor, zipPath, stagingDir, actualSha256, progress, ct), ct).ConfigureAwait(false);
        TryDeleteFile(zipPath);

        return await SwapAsync(javaMajor, stagingDir, installed, version).ConfigureAwait(false);
    }

    /// <summary>
    /// Puts the unpacked runtime in place of the installed one. The old folder is renamed first rather than
    /// deleted: Windows refuses that while Minecraft runs on it, and then the old runtime simply stays.
    /// </summary>
    private async Task<RuntimeInstallResult> SwapAsync(int javaMajor, string stagingDir, InstalledRuntime? previous, string version)
    {
        string target = HomeFor(javaMajor);
        string? oldDir = null;

        if (Directory.Exists(target))
        {
            oldDir = Path.Combine(RuntimesRoot, $".old-graalvm-{javaMajor}-{Guid.NewGuid():N}");
            try
            {
                await MoveDirectoryAsync(target, oldDir).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                TryDeleteDirectory(stagingDir);
                return previous != null
                    ? new RuntimeInstallResult(previous, false, null,
                        $"A newer GraalVM was downloaded, but the installed {previous.Version} could not be replaced because it is in use (is Minecraft running?). Close Minecraft and try again.")
                    : Failed(javaMajor, $"Could not replace the unusable Java folder {target}: {ex.Message}");
            }
        }

        try
        {
            await MoveDirectoryAsync(stagingDir, target).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (oldDir != null)
            {
                try
                {
                    Directory.Move(oldDir, target);
                }
                catch (Exception restoreEx) when (restoreEx is IOException or UnauthorizedAccessException)
                {
                    return Failed(javaMajor, $"Could not move the new GraalVM into {target} ({ex.Message}), and the previous one could not be put back: {restoreEx.Message}");
                }
            }

            return Failed(javaMajor, $"Could not move the new GraalVM into {target}: {ex.Message}");
        }

        if (oldDir != null)
        {
            string toDelete = oldDir;
            await Task.Run(() => TryDeleteDirectory(toDelete)).ConfigureAwait(false);
        }

        InstalledRuntime? runtime = GetInstalled(javaMajor);
        return runtime != null
            ? new RuntimeInstallResult(runtime, true, null, null)
            : Failed(javaMajor, $"GraalVM {version} was installed into {target} but could not be verified there.");
    }

    private async Task MoveDirectoryAsync(string source, string destination)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                Directory.Move(source, destination);
                return;
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < MoveAttempts)
            {
                // A virus scanner may be holding a file for a moment; a runtime in use stays locked and fails in the end.
                await Task.Delay(MoveRetryDelay).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// The SHA-256 the download has to match: pinned in the catalog, or published next to the download.
    /// Throws with a message that reads on its own.
    /// </summary>
    private async Task<string> GetExpectedSha256Async(GraalVmBuild build, CancellationToken ct)
    {
        if (build.PinnedSha256 != null)
        {
            return ParseSha256(build.PinnedSha256)
                ?? throw new RuntimeInstallException("The checksum built into this version of MCperfBooster for GraalVM is not a valid SHA-256.");
        }

        if (build.ChecksumUrl == null)
            throw new RuntimeInstallException("There is no checksum to verify the GraalVM download with.");

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ChecksumTimeout);

            using HttpResponseMessage response = await _http.GetAsync(build.ChecksumUrl, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new RuntimeInstallException($"Could not get the checksum of the GraalVM download: the server answered HTTP {(int)response.StatusCode}.");

            string text = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            return ParseSha256(text)
                ?? throw new RuntimeInstallException("Could not get the checksum of the GraalVM download: the server did not send a SHA-256 hash.");
        }
        catch (HttpRequestException ex)
        {
            throw new RuntimeInstallException($"Could not get the checksum of the GraalVM download: {ex.Message}");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new RuntimeInstallException("Could not get the checksum of the GraalVM download: the server did not answer in time.");
        }
    }

    /// <summary>The lower-case SHA-256 in a text that holds just the hash (or "hash  file name"), or null.</summary>
    internal static string? ParseSha256(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        string[] tokens = text.Trim().TrimStart('﻿').Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
        return tokens.Length > 0 && tokens[0].Length == 64 && tokens[0].All(char.IsAsciiHexDigit)
            ? tokens[0].ToLowerInvariant()
            : null;
    }

    private void EnsureFreeSpace(GraalVmBuild build)
    {
        long needed = build.ApproximateDownloadMegabytes * 1024L * 1024L + UnpackedBytes;
        if (GetFreeSpace(RuntimesRoot) is long free && free < needed)
        {
            throw new RuntimeInstallException(
                $"Not enough free disk space for GraalVM on {Path.GetPathRoot(RuntimesRoot)}: {FormatMegabytes(needed)} are needed, {FormatMegabytes(free)} are free. Free up some space and try again.");
        }
    }

    // Megabytes, because this only ever shows up when the free space is below about a gigabyte.
    private static string FormatMegabytes(long bytes) =>
        (bytes / (1024L * 1024L)).ToString(CultureInfo.InvariantCulture) + " MB";

    private static long? FreeSpaceOf(string path)
    {
        try
        {
            string? driveRoot = Path.GetPathRoot(Path.GetFullPath(path));
            return string.IsNullOrEmpty(driveRoot) ? null : new DriveInfo(driveRoot).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            // A network share has no drive to ask; better to try the install than to refuse it.
            return null;
        }
    }

    /// <summary>
    /// Streams the zip to disk, hashing it on the way so the file is not read a second time.
    /// Returns the lower-case SHA-256 of what was received.
    /// </summary>
    private async Task<string> DownloadAsync(GraalVmBuild build, string zipPath, IProgress<RuntimeDownloadProgress>? progress, CancellationToken ct)
    {
        // Fires when the user cancels or when no data arrived for IdleTimeout. It is re-armed with every
        // chunk, so a slow but steady connection is never cut off.
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
        idle.CancelAfter(IdleTimeout);

        try
        {
            using HttpResponseMessage response = await _http.GetAsync(build.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, idle.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new RuntimeInstallException($"Could not download GraalVM: the server answered HTTP {(int)response.StatusCode} ({response.ReasonPhrase}).");

            long total = response.Content.Headers.ContentLength ?? 0;
            await using Stream source = await response.Content.ReadAsStreamAsync(idle.Token).ConfigureAwait(false);
            await using var target = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, useAsync: true);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            var throttle = new ProgressThrottle(progress, RuntimeInstallPhase.Downloading, total);
            throttle.Report(0, force: true);

            byte[] buffer = new byte[BufferSize];
            long done = 0;
            while (true)
            {
                int read;
                try
                {
                    read = await source.ReadAsync(buffer.AsMemory(), idle.Token).ConfigureAwait(false);
                }
                catch (IOException ex)
                {
                    throw new RuntimeInstallException($"The GraalVM download was interrupted: {ex.Message}");
                }

                if (read == 0)
                    break;

                idle.CancelAfter(IdleTimeout);
                hash.AppendData(buffer, 0, read);
                await target.WriteAsync(buffer.AsMemory(0, read), idle.Token).ConfigureAwait(false);
                done += read;
                throttle.Report(done);
            }

            throttle.Report(done, force: true);
            return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new RuntimeInstallException(FormattableString.Invariant(
                $"The GraalVM download stalled: no data arrived for {IdleTimeout.TotalSeconds:0.#} seconds. Check the internet connection and try again."));
        }
    }

    /// <summary>
    /// Unpacks the zip into the staging folder (leaving out what the game does not need), checks that
    /// it is the Java that was asked for and writes the marker. Returns the version for the marker.
    /// </summary>
    private static string Unpack(GraalVmBuild build, int javaMajor, string zipPath, string stagingDir, string sha256,
        IProgress<RuntimeDownloadProgress>? progress, CancellationToken ct)
    {
        ExtractArchive(zipPath, stagingDir, progress, ct);

        if (!File.Exists(Path.Combine(stagingDir, "bin", "java.exe")) || !File.Exists(Path.Combine(stagingDir, "bin", "javaw.exe")))
            throw new RuntimeInstallException("The downloaded archive is not a Java runtime: bin\\java.exe or bin\\javaw.exe is missing.");

        string? releaseVersion = ReadReleaseVersion(Path.Combine(stagingDir, "release"));
        if (releaseVersion == null)
            throw new RuntimeInstallException("The downloaded archive is not a Java runtime: its release file does not name a version.");

        if (ParseJavaMajor(releaseVersion) != javaMajor)
            throw new RuntimeInstallException($"The downloaded archive holds Java {releaseVersion}, but Java {javaMajor} was expected.");

        string version = build.FixedVersion ?? releaseVersion;
        WriteMarker(Path.Combine(stagingDir, MarkerFileName), javaMajor, version, sha256, build.DownloadUrl);
        return version;
    }

    private static void ExtractArchive(string zipPath, string stagingDir, IProgress<RuntimeDownloadProgress>? progress, CancellationToken ct)
    {
        using ZipArchive archive = ZipFile.OpenRead(zipPath);
        string stagingFull = Path.GetFullPath(stagingDir);

        // Everything sits in one folder named after the build ("graalvm-jdk-21.0.12+7.1"). The name changes
        // with each release, so it is looked up. Paths are checked here, before anything is written (Zip-Slip).
        var topFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            string name = entry.FullName.Replace('\\', '/');
            if (name.Length == 0)
                continue;

            ResolveInside(stagingFull, name);
            int slash = name.IndexOf('/');
            if (slash < 0)
                throw new RuntimeInstallException($"The downloaded archive does not look like a Java runtime: '{name}' is not inside a folder.");

            topFolders.Add(name[..slash]);
        }

        if (topFolders.Count != 1)
            throw new RuntimeInstallException($"The downloaded archive does not look like a Java runtime: it holds {topFolders.Count} top-level folders instead of one.");

        string prefix = topFolders.Single() + "/";
        var work = new List<(ZipArchiveEntry Entry, string Destination, bool IsDirectory)>();
        long totalBytes = 0;
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            string name = entry.FullName.Replace('\\', '/');
            if (name.Length == 0)
                continue;

            string relative = name[prefix.Length..];
            if (relative.Length == 0 || IsExcluded(relative))
                continue;

            bool isDirectory = relative.EndsWith('/');
            work.Add((entry, ResolveInside(stagingFull, relative), isDirectory));
            if (!isDirectory)
                totalBytes += entry.Length;
        }

        Directory.CreateDirectory(stagingFull);
        var throttle = new ProgressThrottle(progress, RuntimeInstallPhase.Extracting, totalBytes);
        throttle.Report(0, force: true);

        byte[] buffer = new byte[BufferSize];
        long done = 0;
        foreach (var (entry, destination, isDirectory) in work)
        {
            ct.ThrowIfCancellationRequested();
            if (isDirectory)
            {
                Directory.CreateDirectory(destination);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using Stream input = entry.Open();
            using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, FileOptions.SequentialScan);

            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                output.Write(buffer, 0, read);
                done += read;
                throttle.Report(done);
            }
        }

        throttle.Report(done, force: true);
    }

    /// <summary>
    /// The full path of an archive entry below a folder; throws when the entry would land outside of it
    /// ("../x", an absolute path, a drive).
    /// </summary>
    private static string ResolveInside(string folderFull, string relative)
    {
        string full = Path.GetFullPath(Path.Combine(folderFull, relative));
        string folderPrefix = folderFull.EndsWith(Path.DirectorySeparatorChar) ? folderFull : folderFull + Path.DirectorySeparatorChar;

        // The separator on the prefix keeps ".staging-x-evil" from passing as ".staging-x".
        if (!full.StartsWith(folderPrefix, StringComparison.OrdinalIgnoreCase))
            throw new RuntimeInstallException($"The downloaded archive was rejected: the entry '{relative}' would be unpacked outside of its folder.");

        return full;
    }

    internal static bool IsExcluded(string pathInsideTopFolder)
    {
        string path = pathInsideTopFolder.Replace('\\', '/');
        return path.Equals(ExcludedFile, StringComparison.OrdinalIgnoreCase) ||
               ExcludedFolders.Any(folder => path.StartsWith(folder, StringComparison.OrdinalIgnoreCase));
    }

    private static string? ReadReleaseVersion(string releasePath)
    {
        if (!File.Exists(releasePath))
            return null;

        foreach (string line in File.ReadLines(releasePath))
        {
            int equals = line.IndexOf('=');
            if (equals <= 0 || line[..equals].Trim() != "JAVA_VERSION")
                continue;

            string value = line[(equals + 1)..].Trim().Trim('"');
            return value.Length > 0 ? value : null;
        }

        return null;
    }

    /// <summary>The major version in a JAVA_VERSION: "21.0.12" and "25" give 21 and 25, the old "1.8.0_402" gives 8.</summary>
    internal static int? ParseJavaMajor(string version)
    {
        int end = 0;
        while (end < version.Length && char.IsAsciiDigit(version[end]))
            end++;

        if (end == 0 || !int.TryParse(version.AsSpan(0, end), NumberStyles.None, CultureInfo.InvariantCulture, out int first))
            return null;

        if (first != 1 || end >= version.Length || version[end] != '.')
            return first;

        int start = end + 1;
        end = start;
        while (end < version.Length && char.IsAsciiDigit(version[end]))
            end++;

        return end > start && int.TryParse(version.AsSpan(start, end - start), NumberStyles.None, CultureInfo.InvariantCulture, out int second)
            ? second
            : null;
    }

    private static void WriteMarker(string path, int javaMajor, string version, string sha256, string source)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("javaMajor", javaMajor);
            writer.WriteString("version", version);
            writer.WriteString("sha256", sha256);
            writer.WriteString("source", source);
            writer.WriteEndObject();
        }

        AtomicFile.WriteAllText(path, Encoding.UTF8.GetString(buffer.ToArray()));
    }

    /// <summary>Removes what an earlier run left behind when it was killed or failed half way.</summary>
    private void SweepLeftovers()
    {
        foreach (string pattern in new[] { ".download-*", ".staging-*", ".old-*" })
        {
            string[] entries;
            try
            {
                entries = Directory.GetFileSystemEntries(RuntimesRoot, pattern);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (string entry in entries)
            {
                if (Directory.Exists(entry))
                    TryDeleteDirectory(entry);
                else
                    TryDeleteFile(entry);
            }
        }
    }

    public async Task<ArgumentTestResult> TestArgumentsAsync(InstalledRuntime runtime, string javaArgs, CancellationToken ct = default)
    {
        var arguments = new List<string>(javaArgs.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            // Java 17 and 21 only look at Graal options when the first method is compiled, which a bare
            // "-version" never gets to; eager initialization checks them at startup.
            "-XX:+EagerJVMCI",
            "-version",
        };

        try
        {
            ProcessResult result = await RunProcess(runtime.JavaExe, arguments, ArgumentTestTimeout, ct).ConfigureAwait(false);
            if (result.TimedOut)
                return new ArgumentTestResult(false, result.ExitCode, $"java.exe did not finish within {ArgumentTestTimeout.TotalSeconds:0} seconds.");

            string output = ProcessRunner.Tail(string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError, maxLines: 4, maxChars: 400);
            return new ArgumentTestResult(result.ExitCode == 0, result.ExitCode, output);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            return new ArgumentTestResult(false, -1, $"Could not start {runtime.JavaExe}: {ex.Message}");
        }
    }

    public void RemoveAll()
    {
        string[] folders;
        try
        {
            folders = Directory.GetDirectories(RuntimesRoot, "graalvm-*");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // No runtimes folder, nothing to remove.
            return;
        }

        foreach (string folder in folders)
        {
            // The marker goes first: a folder that cannot be deleted completely (Minecraft is running on
            // it) must not keep passing for an installed runtime.
            TryDeleteFile(Path.Combine(folder, MarkerFileName));
            TryDeleteDirectory(folder);
        }
    }

    private RuntimeInstallResult Failed(int javaMajor, string error) =>
        new(GetInstalled(javaMajor), false, error, null);

    private static string Describe(Exception ex) => ex switch
    {
        RuntimeInstallException => ex.Message,
        HttpRequestException => $"Could not download GraalVM: {ex.Message}",
        InvalidDataException => $"The downloaded file is not a usable archive: {ex.Message}",
        IOException or UnauthorizedAccessException => $"Could not write the Java files: {ex.Message}",
        _ => $"Installing GraalVM failed: {ex.Message}",
    };

    private static bool TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Leftovers are swept at the start of the next install.
            return false;
        }
    }

    private static bool TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>An expected failure with a message meant for the user.</summary>
    private sealed class RuntimeInstallException : Exception
    {
        public RuntimeInstallException(string message) : base(message)
        {
        }
    }

    /// <summary>
    /// Reports at most ten times a second, and always the first and last value. A report per chunk would
    /// drown a UI thread in thousands of updates.
    /// </summary>
    private sealed class ProgressThrottle
    {
        private readonly IProgress<RuntimeDownloadProgress>? _progress;
        private readonly RuntimeInstallPhase _phase;
        private readonly long _total;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private long _lastReportMs = -1000;

        public ProgressThrottle(IProgress<RuntimeDownloadProgress>? progress, RuntimeInstallPhase phase, long total)
        {
            _progress = progress;
            _phase = phase;
            _total = total;
        }

        public void Report(long done, bool force = false)
        {
            if (_progress == null)
                return;

            long now = _clock.ElapsedMilliseconds;
            if (!force && now - _lastReportMs < 100)
                return;

            _lastReportMs = now;
            _progress.Report(new RuntimeDownloadProgress(_phase, done, _total));
        }
    }
}
