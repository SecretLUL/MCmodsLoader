using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MCperfBooster.Core.Models;
using MCperfBooster.Core.Utils;

namespace MCperfBooster.Core.Services;

public interface IFabricService
{
    Task<FabricStatus> CheckFabricStatusAsync(string minecraftPath, string mcVersion);
    Task<bool> InstallFabricAsync(string minecraftPath, string mcVersion, IProgress<string>? progress = null, CancellationToken ct = default);

    /// <summary>
    /// Registers a launcher profile for the newest installed loader if Fabric is
    /// installed but no profile points at it, then returns the fresh status. Never
    /// throws; on failure the status as found is returned.
    /// </summary>
    Task<FabricStatus> EnsureLauncherProfileAsync(string minecraftPath, string mcVersion, CancellationToken ct = default);
}

/// <summary>
/// What <see cref="FabricService.ApplyJavaSettings"/> or <see cref="FabricService.DetachRuntimes"/> found: how
/// many profiles it is about (<see cref="Matched"/>) and how many of those it had to change
/// (<see cref="Changed"/>). No match means there is nothing to set the Java on; a match that did not change
/// means it was set up already. A profile in a file that could not be read or written counts for neither, and
/// <see cref="Error"/> then says which file and why; it is null when every file worked.
/// </summary>
public sealed record ProfileJavaUpdate(int Matched, int Changed, string? Error = null);

public class FabricService : IFabricService
{
    private readonly HttpClient _httpClient;
    private const string FabricIconBase64 = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAIAAAACABAMAAAAxEHz4AAAAGFBMVEUAAAA4NCrb0LTGvKW8spyAem2uppSakn5SsnMLAAAAAXRSTlMAQObYZgAAAJ5JREFUaIHt1MENgCAMRmFWYAVXcAVXcAVXcH3bhCYNkYjcKO8dSf7v1JASUWdZAlgb0PEmDSMAYYBdGkYApgf8ER3SbwRgesAf0BACMD1gB6S9IbkEEBfwY49oNj4lgLhA64C0o9R9RABTAvp4SX5kB2TA5y8EEAK4pRrxB9QcA4QBWkj3GCAMUCO/xwBhAI/kEsCagCHDY4AwAC3VA6t4zTAMj0OJAAAAAElFTkSuQmCC";
    private const string FabricLoaderPrefix = "fabric-loader-";

    // The caller's client may carry a long timeout meant for downloads.
    private static readonly TimeSpan MetadataTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan InstallerDownloadTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan InstallerRunTimeout = TimeSpan.FromMinutes(3);

    private static readonly JsonDocumentOptions LenientJson = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    public FabricService(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        if (!_httpClient.DefaultRequestHeaders.Contains("User-Agent"))
        {
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "SecretLUL/MCperfBooster (github.com/SecretLUL/MCperfBooster)");
        }
    }

    /// <summary>
    /// Reads the installed Fabric loader for a game version straight off disk. It used
    /// to query meta.fabricmc.net for the newest loader as well, but nothing ever read
    /// that answer, so the status check no longer touches the network.
    /// </summary>
    public Task<FabricStatus> CheckFabricStatusAsync(string minecraftPath, string mcVersion)
    {
        string? installedLoaderVersion = null;
        var installedVersionIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. Check versions directory. Only a folder carrying its version json counts:
        // the launcher cannot start a version without it, and a folder emptied by an
        // uninstall would otherwise keep reporting Fabric as installed.
        string versionsDir = Path.Combine(minecraftPath, "versions");
        if (Directory.Exists(versionsDir))
        {
            foreach (var dir in Directory.GetDirectories(versionsDir))
            {
                string dirName = Path.GetFileName(dir);
                string? extracted = ExtractLoaderVersion(dirName, mcVersion);
                if (extracted == null || !File.Exists(Path.Combine(dir, $"{dirName}.json")))
                    continue;

                installedVersionIds.Add(dirName);
                if (installedLoaderVersion == null || MinecraftVersionComparer.Instance.Compare(extracted, installedLoaderVersion) > 0)
                {
                    installedLoaderVersion = extracted;
                }
            }
        }

        if (installedLoaderVersion == null)
        {
            // A launcher profile alone is not an installation: its version folder is gone.
            return Task.FromResult(new FabricStatus { IsInstalled = false });
        }

        // 2. Find the launcher profile to report. Several Fabric profiles can point at
        // the same game version; prefer the one on the newest installed loader, then
        // one on any installed loader, then the most recently used. A profile on a
        // loader that is not installed cannot be launched and does not count.
        string newestVersionId = $"{FabricLoaderPrefix}{installedLoaderVersion}-{mcVersion}";
        string? profileName = null;
        int bestRank = -1;
        DateTimeOffset bestLastUsed = DateTimeOffset.MinValue;

        foreach (string fileName in new[] { "launcher_profiles.json", "launcher_profiles_microsoft_store.json" })
        {
            string profilesFile = Path.Combine(minecraftPath, fileName);
            if (!File.Exists(profilesFile))
                continue;

            try
            {
                using var stream = File.OpenRead(profilesFile);
                using var doc = JsonDocument.Parse(stream, LenientJson);
                if (!doc.RootElement.TryGetProperty("profiles", out var profiles) || profiles.ValueKind != JsonValueKind.Object)
                    continue;

                foreach (var p in profiles.EnumerateObject())
                {
                    if (p.Value.ValueKind != JsonValueKind.Object ||
                        !p.Value.TryGetProperty("lastVersionId", out var lastVer) ||
                        lastVer.ValueKind != JsonValueKind.String)
                        continue;

                    string ver = lastVer.GetString() ?? "";
                    if (ExtractLoaderVersion(ver, mcVersion) == null)
                        continue;

                    if (!installedVersionIds.Contains(ver))
                        continue;

                    int rank = ver.Equals(newestVersionId, StringComparison.OrdinalIgnoreCase) ? 2 : 1;
                    DateTimeOffset lastUsed = ReadTimestamp(p.Value, "lastUsed");

                    if (rank > bestRank || (rank == bestRank && lastUsed > bestLastUsed))
                    {
                        bestRank = rank;
                        bestLastUsed = lastUsed;
                        profileName = p.Value.TryGetProperty("name", out var nameProp) &&
                                      nameProp.ValueKind == JsonValueKind.String &&
                                      !string.IsNullOrWhiteSpace(nameProp.GetString())
                            ? nameProp.GetString()
                            : p.Name;
                    }
                }
            }
            catch
            {
                // Ignore json read errors
            }
        }

        return Task.FromResult(new FabricStatus
        {
            IsInstalled = true,
            InstalledLoaderVersion = installedLoaderVersion,
            InstalledVersionId = newestVersionId,
            HasLauncherProfile = profileName != null,
            ProfileName = profileName,
        });
    }

    public Task<FabricStatus> EnsureLauncherProfileAsync(string minecraftPath, string mcVersion, CancellationToken ct = default)
    {
        // Off the caller's thread: both steps are file IO. The token is checked rather
        // than handed to Task.Run, which would throw instead of returning a status.
        return Task.Run(async () =>
        {
            FabricStatus status;
            try
            {
                status = await CheckFabricStatusAsync(minecraftPath, mcVersion);
            }
            catch
            {
                return new FabricStatus();
            }

            if (!status.IsInstalled || status.HasLauncherProfile || status.InstalledVersionId == null || ct.IsCancellationRequested)
                return status;

            try
            {
                RegisterProfileInLauncher(minecraftPath, mcVersion, status.InstalledVersionId);
                return await CheckFabricStatusAsync(minecraftPath, mcVersion);
            }
            catch
            {
                return status;
            }
        });
    }

    /// <summary>
    /// Extracts the loader build from a "fabric-loader-&lt;loader&gt;-&lt;mcVersion&gt;" version id,
    /// or null if the id is not a Fabric version for that game version.
    /// </summary>
    private static string? ExtractLoaderVersion(string versionId, string mcVersion)
    {
        string suffix = $"-{mcVersion}";
        if (!versionId.StartsWith(FabricLoaderPrefix, StringComparison.OrdinalIgnoreCase) ||
            !versionId.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) ||
            versionId.Length < FabricLoaderPrefix.Length + suffix.Length + 1)
            return null;

        string extracted = versionId.Substring(FabricLoaderPrefix.Length, versionId.Length - FabricLoaderPrefix.Length - suffix.Length);
        return string.IsNullOrWhiteSpace(extracted) ? null : extracted;
    }

    private static DateTimeOffset ReadTimestamp(JsonElement obj, string property) =>
        obj.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? ParseTimestamp(value.GetString())
            : DateTimeOffset.MinValue;

    private static DateTimeOffset ReadTimestamp(JsonObject obj, string property) =>
        obj[property] is JsonValue value && value.TryGetValue<string>(out string? text)
            ? ParseTimestamp(text)
            : DateTimeOffset.MinValue;

    private static DateTimeOffset ParseTimestamp(string? text) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : DateTimeOffset.MinValue;

    /// <summary>
    /// The newest stable loader build for a game version, or the newest build at all
    /// when none is marked stable. Meta lists the builds newest first.
    /// </summary>
    internal async Task<string?> GetLatestLoaderVersionAsync(string mcVersion, CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(MetadataTimeout);

            string url = $"https://meta.fabricmc.net/v2/versions/loader/{Uri.EscapeDataString(mcVersion)}";
            using var response = await _httpClient.GetAsync(url, cts.Token);
            if (!response.IsSuccessStatusCode)
                return null;

            using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cts.Token);

            string? firstVersion = null;
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (!item.TryGetProperty("loader", out var loader) ||
                    !loader.TryGetProperty("version", out var ver) ||
                    ver.ValueKind != JsonValueKind.String)
                    continue;

                string? version = ver.GetString();
                if (string.IsNullOrEmpty(version))
                    continue;

                if (loader.TryGetProperty("stable", out var st) && st.ValueKind == JsonValueKind.True)
                    return version;

                firstVersion ??= version;
            }

            return firstVersion;
        }
        catch
        {
            // Network or parsing error
        }

        return null;
    }

    public async Task<bool> InstallFabricAsync(string minecraftPath, string mcVersion, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        progress?.Report($"Checking latest Fabric version for Minecraft {mcVersion}...");
        string? loaderVersion = await GetLatestLoaderVersionAsync(mcVersion, ct);
        if (string.IsNullOrEmpty(loaderVersion))
        {
            progress?.Report($"Could not find a compatible Fabric version for Minecraft {mcVersion}.");
            return false;
        }

        progress?.Report($"Fabric Loader {loaderVersion} found.");

        // Strategy 1: Check for Java and run official Fabric Installer CLI
        string? javaPath = FindJavaExecutable(minecraftPath);
        if (!string.IsNullOrEmpty(javaPath))
        {
            progress?.Report($"Java found ({javaPath}). Using official Fabric Installer...");
            bool cliSuccess = await InstallViaCliAsync(javaPath, minecraftPath, mcVersion, loaderVersion, progress, ct);
            if (cliSuccess)
            {
                progress?.Report("Fabric installed successfully via official installer!");
                return true;
            }
            if (ct.IsCancellationRequested)
                return false;
            progress?.Report("CLI installer was unsuccessful. Switching to direct profile installation...");
        }
        else
        {
            progress?.Report("No Java installation found. Using direct profile installation...");
        }

        // Strategy 2: Direct installation via Fabric Meta profile JSON
        bool directSuccess = await InstallDirectAsync(minecraftPath, mcVersion, loaderVersion, progress, ct);
        if (directSuccess)
        {
            progress?.Report("Fabric installed successfully!");
            return true;
        }

        progress?.Report("Error installing Fabric.");
        return false;
    }

    // Newest first. Epsilon is the launcher's Java 25.
    private static readonly string[] KnownRuntimeComponents =
    {
        "java-runtime-epsilon", "java-runtime-delta", "java-runtime-gamma", "java-runtime-beta", "java-runtime-alpha", "jre-legacy"
    };

    private static readonly string[] RuntimePlatforms = { "windows-x64", "windows" };

    internal static string? FindJavaExecutable(string minecraftPath) =>
        FindJavaExecutable(
            Environment.GetEnvironmentVariable("JAVA_HOME"),
            Environment.GetEnvironmentVariable("PATH"),
            GetMinecraftRuntimeRoots(minecraftPath));

    /// <summary>
    /// Resolves java.exe to a full path. Starting a bare "java" would let CreateProcess
    /// search the application directory and the working directory first, typically
    /// Downloads, where any planted java.exe would run. Relative PATH entries are skipped
    /// for the same reason.
    /// </summary>
    internal static string? FindJavaExecutable(string? javaHome, string? pathVariable, IEnumerable<string> runtimeRoots)
    {
        // 1. JAVA_HOME
        if (TryFullPath(javaHome, out string? home))
        {
            string javaExe = Path.Combine(home, "bin", "java.exe");
            if (File.Exists(javaExe))
                return javaExe;
        }

        // 2. Absolute PATH entries
        if (!string.IsNullOrEmpty(pathVariable))
        {
            foreach (string entry in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!TryFullPath(Environment.ExpandEnvironmentVariables(entry.Trim('"')), out string? dir))
                    continue;

                string javaExe = Path.Combine(dir, "java.exe");
                if (File.Exists(javaExe))
                    return javaExe;
            }
        }

        // 3. Runtimes bundled with the Minecraft launcher
        foreach (string root in runtimeRoots)
        {
            if (!TryFullPath(root, out string? runtimeBase) || !Directory.Exists(runtimeBase))
                continue;

            foreach (string component in KnownRuntimeComponents)
            {
                foreach (string platform in RuntimePlatforms)
                {
                    string javaExe = Path.Combine(runtimeBase, component, platform, component, "bin", "java.exe");
                    if (File.Exists(javaExe))
                        return javaExe;
                }
            }

            try
            {
                var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
                string? found = Directory.EnumerateFiles(runtimeBase, "java.exe", options)
                    .FirstOrDefault(f => string.Equals(Path.GetFileName(Path.GetDirectoryName(f)), "bin", StringComparison.OrdinalIgnoreCase));
                if (found != null)
                    return found;
            }
            catch
            {
                // Ignore search errors
            }
        }

        return null;
    }

    /// <summary>
    /// Where the launcher keeps its Java runtimes: inside .minecraft, in the Microsoft
    /// Store launcher's package cache, and next to the legacy standalone launcher.
    /// </summary>
    internal static IEnumerable<string> GetMinecraftRuntimeRoots(string minecraftPath)
    {
        if (!string.IsNullOrWhiteSpace(minecraftPath))
            yield return Path.Combine(minecraftPath, "runtime");

        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(localAppData))
            yield return Path.Combine(localAppData, "Packages", "Microsoft.4297127D64EC6_8wekyb3d8bbwe", "LocalCache", "Local", "runtime");

        string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrEmpty(programFilesX86))
            yield return Path.Combine(programFilesX86, "Minecraft Launcher", "runtime");
    }

    private static bool TryFullPath(string? path, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? fullPath)
    {
        fullPath = null;
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            return false;

        try
        {
            fullPath = Path.GetFullPath(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> InstallViaCliAsync(string javaPath, string minecraftPath, string mcVersion, string loaderVersion, IProgress<string>? progress, CancellationToken ct)
    {
        string tempJar = Path.Combine(Path.GetTempPath(), $"fabric-installer-{Guid.NewGuid():N}.jar");

        try
        {
            progress?.Report("Downloading Fabric Installer...");
            string installerUrl = await GetInstallerJarUrlAsync(ct);
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                cts.CancelAfter(InstallerDownloadTimeout);
                using var response = await _httpClient.GetAsync(installerUrl, cts.Token);
                response.EnsureSuccessStatusCode();
                var bytes = await response.Content.ReadAsByteArrayAsync(cts.Token);
                await File.WriteAllBytesAsync(tempJar, bytes, cts.Token);
            }

            progress?.Report("Running Fabric Installer...");
            var result = await ProcessRunner.RunAsync(
                javaPath,
                new[]
                {
                    // Piped output is otherwise written in the ANSI code page, which
                    // garbles non-ASCII paths in the error tail. Java 8-18 read the sun.*
                    // properties, 19+ the plain ones; unknown properties are ignored.
                    "-Dsun.stdout.encoding=UTF-8", "-Dsun.stderr.encoding=UTF-8",
                    "-Dstdout.encoding=UTF-8", "-Dstderr.encoding=UTF-8",
                    "-jar", tempJar, "client", "-dir", minecraftPath, "-mcversion", mcVersion, "-loader", loaderVersion
                },
                InstallerRunTimeout,
                Path.GetTempPath(),
                ct,
                Encoding.UTF8);

            string errorTail = ProcessRunner.Tail(string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError);
            if (result.TimedOut)
            {
                progress?.Report($"Fabric Installer did not finish within {InstallerRunTimeout.TotalMinutes:0} minutes and was stopped.{FormatTail(errorTail)}");
                return false;
            }

            if (result.ExitCode != 0)
            {
                progress?.Report($"Fabric Installer exited with code {result.ExitCode}.{FormatTail(errorTail)}");
                return false;
            }

            // The installer can exit cleanly without having written the version (an
            // unknown game version, for one), which the launcher could not start.
            string versionId = $"{FabricLoaderPrefix}{loaderVersion}-{mcVersion}";
            if (!File.Exists(Path.Combine(minecraftPath, "versions", versionId, $"{versionId}.json")))
            {
                progress?.Report($"Fabric Installer finished but did not create {versionId}.{FormatTail(errorTail)}");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            progress?.Report($"Installer error: {ex.Message}");
        }
        finally
        {
            try
            {
                if (File.Exists(tempJar))
                    File.Delete(tempJar);
            }
            catch
            {
                // Ignore temp delete failure
            }
        }

        return false;
    }

    private static string FormatTail(string tail) => string.IsNullOrWhiteSpace(tail) ? "" : $" Output: {tail}";

    private async Task<string> GetInstallerJarUrlAsync(CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(MetadataTimeout);

            using var response = await _httpClient.GetAsync("https://meta.fabricmc.net/v2/versions/installer", cts.Token);
            if (response.IsSuccessStatusCode)
            {
                using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cts.Token);
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    if (el.TryGetProperty("stable", out var st) && st.ValueKind == JsonValueKind.True &&
                        el.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String)
                    {
                        return url.GetString()!;
                    }
                }
            }
        }
        catch
        {
            // Fallback
        }

        return "https://maven.fabricmc.net/net/fabricmc/fabric-installer/1.1.2/fabric-installer-1.1.2.jar";
    }

    public async Task<bool> InstallDirectAsync(string minecraftPath, string mcVersion, string loaderVersion, IProgress<string>? progress, CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(MetadataTimeout);

            progress?.Report("Downloading Fabric profile JSON...");
            string profileUrl = $"https://meta.fabricmc.net/v2/versions/loader/{Uri.EscapeDataString(mcVersion)}/{Uri.EscapeDataString(loaderVersion)}/profile/json";
            using var response = await _httpClient.GetAsync(profileUrl, cts.Token);
            if (!response.IsSuccessStatusCode)
            {
                progress?.Report($"Failed to download profile JSON (HTTP {response.StatusCode})");
                return false;
            }

            string profileJsonContent = await response.Content.ReadAsStringAsync(cts.Token);

            string versionId = $"fabric-loader-{loaderVersion}-{mcVersion}";
            string versionDir = Path.Combine(minecraftPath, "versions", versionId);
            Directory.CreateDirectory(versionDir);

            string targetJsonPath = Path.Combine(versionDir, $"{versionId}.json");
            AtomicFile.WriteAllText(targetJsonPath, profileJsonContent);

            progress?.Report("Updating Minecraft Launcher profiles...");
            RegisterProfileInLauncher(minecraftPath, mcVersion, versionId);

            return true;
        }
        catch (Exception ex)
        {
            progress?.Report($"Direct installation error: {ex.Message}");
            return false;
        }
    }

    // What the launcher itself puts in a new profile.
    private const string LauncherDefaultJavaArgs = "-Xmx2G -XX:+UnlockExperimentalVMOptions -XX:+UseG1GC -XX:G1NewSizePercent=20 -XX:G1ReservePercent=20 -XX:MaxGCPauseMillis=50 -XX:G1HeapRegionSize=32M";

    private static readonly string[] ProfileFileNames = { "launcher_profiles.json", "launcher_profiles_microsoft_store.json" };

    private static readonly JsonSerializerOptions ProfileWriteOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static void RegisterProfileInLauncher(string minecraftPath, string mcVersion, string versionId) =>
        RegisterProfileInLauncher(minecraftPath, mcVersion, versionId, JvmArguments.DetectSystem());

    /// <param name="system">What a new profile's memory settings are sized for.</param>
    internal static void RegisterProfileInLauncher(string minecraftPath, string mcVersion, string versionId, SystemProfile system)
    {
        string defaultProfilesFile = Path.Combine(minecraftPath, "launcher_profiles.json");
        string msStoreProfilesFile = Path.Combine(minecraftPath, "launcher_profiles_microsoft_store.json");

        // Only a profile created here gets these; an existing one keeps what its owner chose. A new profile
        // starts on the launcher's own Java, so the arguments must suit that one (Java 8 for 1.16.5), not
        // the GraalVM the injection may put it on later. Games older than the table covers (before 1.16.5)
        // keep the launcher's own defaults.
        string newProfileJavaArgs = JavaRuntimeCatalog.GetJavaMajor(mcVersion) is not null
            ? JvmArguments.Build(JavaRuntimeCatalog.GetLauncherJavaMajor(mcVersion, minecraftPath), graalVm: false, system, distantHorizons: false)
            : LauncherDefaultJavaArgs;

        // If neither file exists, create a default launcher_profiles.json
        if (!File.Exists(defaultProfilesFile) && !File.Exists(msStoreProfilesFile))
        {
            try
            {
                var initialObj = new JsonObject
                {
                    ["profiles"] = new JsonObject(),
                    ["version"] = 3
                };
                AtomicFile.WriteAllText(defaultProfilesFile, initialObj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            }
            catch
            {
                // Ignore initialization error
            }
        }

        string[] profileFiles = { defaultProfilesFile, msStoreProfilesFile };

        foreach (var file in profileFiles)
        {
            if (!File.Exists(file))
                continue;

            try
            {
                JsonObject? rootObj;
                using (var stream = File.OpenRead(file))
                {
                    rootObj = JsonNode.Parse(stream, documentOptions: LenientJson) as JsonObject;
                }

                if (rootObj is not null)
                {
                    if (rootObj["profiles"] is not JsonObject profilesObj)
                    {
                        profilesObj = new JsonObject();
                        rootObj["profiles"] = profilesObj;
                    }

                    // Which profile to point at the new loader, in order: our own
                    // "fabric-loader-<mc>" profile while it still runs Fabric (a user who
                    // switched it to vanilla or Quilt keeps that choice), else the most
                    // recently used Fabric profile for this game version, else a new one.
                    string newKey = $"fabric-loader-{mcVersion}";
                    JsonObject? existingProfile = null;
                    DateTimeOffset existingLastUsed = DateTimeOffset.MinValue;
                    bool newKeyTaken = false;

                    foreach (var kvp in profilesObj)
                    {
                        if (kvp.Value is not JsonObject pObj)
                            continue;

                        string lastVer = pObj["lastVersionId"] is JsonValue val && val.TryGetValue<string>(out string? s) && s != null ? s : string.Empty;
                        bool isOwnKey = kvp.Key.Equals(newKey, StringComparison.OrdinalIgnoreCase);
                        newKeyTaken |= isOwnKey;

                        if (isOwnKey && lastVer.StartsWith(FabricLoaderPrefix, StringComparison.OrdinalIgnoreCase))
                        {
                            existingProfile = pObj;
                            break;
                        }

                        if (ExtractLoaderVersion(lastVer, mcVersion) != null)
                        {
                            DateTimeOffset lastUsed = ReadTimestamp(pObj, "lastUsed");
                            if (existingProfile == null || lastUsed > existingLastUsed)
                            {
                                existingProfile = pObj;
                                existingLastUsed = lastUsed;
                            }
                        }
                    }

                    string nowIso = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

                    if (existingProfile != null)
                    {
                        // Preserve existing profile's javaArgs, resolution, name, etc. Only
                        // the version changes; lastUsed stays the launcher's to set.
                        existingProfile["lastVersionId"] = versionId;
                        if (!existingProfile.ContainsKey("icon") || existingProfile["icon"] == null)
                        {
                            existingProfile["icon"] = FabricIconBase64;
                        }
                    }
                    else
                    {
                        // Create brand-new profile with memory settings sized for this PC
                        var newProfile = new JsonObject
                        {
                            ["created"] = nowIso,
                            ["icon"] = FabricIconBase64,
                            ["lastUsed"] = nowIso,
                            ["lastVersionId"] = versionId,
                            ["name"] = $"Fabric {mcVersion}",
                            ["type"] = "custom",
                            ["javaArgs"] = newProfileJavaArgs
                        };

                        // Our key may now hold the user's non-Fabric profile; leave it be.
                        string key = newKey;
                        for (int n = 2; newKeyTaken && profilesObj.Any(kvp => kvp.Key.Equals(key, StringComparison.OrdinalIgnoreCase)); n++)
                        {
                            key = $"{newKey}-{n}";
                        }

                        profilesObj[key] = newProfile;
                    }

                    var options = new JsonSerializerOptions
                    {
                        WriteIndented = true,
                        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                    };

                    // Atomic, so a crash mid-write cannot corrupt the user's profiles.
                    AtomicFile.WriteAllText(file, rootObj.ToJsonString(options));
                }
            }
            catch
            {
                // Continue with other files if any error
            }
        }
    }

    /// <summary>
    /// Sets the Java arguments and the Java executable (<paramref name="javaExecutable"/> is the path to
    /// javaw.exe; null removes the setting, so the launcher uses its own Java again) on every Fabric profile of
    /// this game version. Profiles with a game folder of their own belong to another modpack and are left alone.
    /// Returns how many profiles it is about and how many of those actually changed, over both launcher
    /// profile files; a file that cannot be read or written is skipped and named in the result's error.
    /// </summary>
    public static ProfileJavaUpdate ApplyJavaSettings(string minecraftPath, string mcVersion, string? javaExecutable, string javaArgs)
    {
        return EditProfiles(minecraftPath, profile =>
        {
            if (ReadString(profile, "lastVersionId") is not { } versionId || ExtractLoaderVersion(versionId, mcVersion) == null)
                return ProfileEdit.NotApplicable;

            if (ReadString(profile, "gameDir") is { Length: > 0 } gameDir && !IsSamePath(gameDir, minecraftPath))
                return ProfileEdit.NotApplicable;

            bool changed = SetString(profile, "javaArgs", javaArgs);
            if (javaExecutable != null)
                changed |= SetString(profile, "javaDir", javaExecutable);
            else
                changed |= profile.Remove("javaDir");

            return changed ? ProfileEdit.Changed : ProfileEdit.AlreadyDone;
        });
    }

    /// <summary>
    /// Takes the profiles off a runtime this app installed, before that runtime is deleted: removes the Java
    /// executable of every profile that points below <paramref name="runtimesRoot"/> and, where
    /// <paramref name="argsForGameVersion"/> has arguments for the profile's game version, puts them in place of
    /// the Java arguments (they were tuned for that runtime). A set <see cref="ProfileJavaUpdate.Error"/> means
    /// some profiles may still point at the runtimes, which must then not be deleted.
    /// </summary>
    public static ProfileJavaUpdate DetachRuntimes(string minecraftPath, string runtimesRoot, Func<string, string?> argsForGameVersion)
    {
        return EditProfiles(minecraftPath, profile =>
        {
            if (ReadString(profile, "javaDir") is not { Length: > 0 } javaDir || !IsPathBelow(javaDir, runtimesRoot))
                return ProfileEdit.NotApplicable;

            profile.Remove("javaDir");
            if (ReadString(profile, "lastVersionId") is { } versionId && FindFabricJavaArgs(versionId, argsForGameVersion) is { } javaArgs)
                SetString(profile, "javaArgs", javaArgs);

            return ProfileEdit.Changed;
        });
    }

    /// <summary>What an edit made of one launcher profile.</summary>
    private enum ProfileEdit
    {
        /// <summary>The edit is not about this profile; it was left alone.</summary>
        NotApplicable,

        /// <summary>The edit is about this profile, which was as wanted already.</summary>
        AlreadyDone,

        /// <summary>The edit is about this profile and changed it.</summary>
        Changed,
    }

    /// <summary>
    /// Runs <paramref name="edit"/> on every profile in the launcher profile files and rewrites a file only
    /// when it reported a change. Counts the profiles the edit was about and the ones it changed, in files
    /// that could be read and, where there was something to change, written.
    /// </summary>
    private static ProfileJavaUpdate EditProfiles(string minecraftPath, Func<JsonObject, ProfileEdit> edit)
    {
        int matchedProfiles = 0;
        int changedProfiles = 0;
        var errors = new List<string>();
        foreach (string fileName in ProfileFileNames)
        {
            string file = Path.Combine(minecraftPath, fileName);
            if (!File.Exists(file))
                continue;

            try
            {
                JsonObject? rootObj;
                using (var stream = File.OpenRead(file))
                {
                    rootObj = JsonNode.Parse(stream, documentOptions: LenientJson) as JsonObject;
                }

                if (rootObj?["profiles"] is not JsonObject profiles)
                    continue;

                int matchedHere = 0;
                int changedHere = 0;
                foreach (var entry in profiles)
                {
                    if (entry.Value is not JsonObject profile)
                        continue;

                    ProfileEdit result = edit(profile);
                    if (result != ProfileEdit.NotApplicable)
                        matchedHere++;
                    if (result == ProfileEdit.Changed)
                        changedHere++;
                }

                if (changedHere > 0)
                {
                    // Atomic, so a crash mid-write cannot corrupt the user's profiles.
                    AtomicFile.WriteAllText(file, rootObj.ToJsonString(ProfileWriteOptions));
                }

                // Counted only now: a file that failed above must not report profiles it did not manage to set up.
                matchedProfiles += matchedHere;
                changedProfiles += changedHere;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException)
            {
                // This file cannot be read or written (a read-only launcher_profiles.json, for one); the other
                // one may still work, and the caller learns about this one.
                errors.Add($"{fileName}: {ex.Message}");
            }
        }

        return new ProfileJavaUpdate(matchedProfiles, changedProfiles, errors.Count > 0 ? string.Join(" ", errors) : null);
    }

    /// <summary>
    /// Arguments for the game version of a "fabric-loader-&lt;loader&gt;-&lt;mc&gt;" id. Loader and game version can
    /// both contain dashes ("0.20.0-beta.2", "26.3-pre-2"), so every split is tried, the longest game version first.
    /// </summary>
    private static string? FindFabricJavaArgs(string versionId, Func<string, string?> argsForGameVersion)
    {
        if (!versionId.StartsWith(FabricLoaderPrefix, StringComparison.OrdinalIgnoreCase))
            return null;

        string rest = versionId[FabricLoaderPrefix.Length..];
        for (int dash = rest.IndexOf('-'); dash >= 0; dash = rest.IndexOf('-', dash + 1))
        {
            if (argsForGameVersion(rest[(dash + 1)..]) is { } args)
                return args;
        }

        return null;
    }

    private static string? ReadString(JsonObject obj, string property) =>
        obj[property] is JsonValue value && value.TryGetValue<string>(out string? text) ? text : null;

    private static bool SetString(JsonObject obj, string property, string value)
    {
        if (ReadString(obj, property) == value)
            return false;

        obj[property] = value;
        return true;
    }

    private static bool IsSamePath(string a, string b)
    {
        try
        {
            return string.Equals(TrimmedFullPath(a), TrimmedFullPath(b), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool IsPathBelow(string path, string folder)
    {
        try
        {
            return Path.GetFullPath(path).StartsWith(TrimmedFullPath(folder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static string TrimmedFullPath(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}

internal sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError, bool TimedOut);

/// <summary>
/// Runs a child process to completion with both output pipes drained concurrently, so a
/// child filling one pipe while the other is being read cannot deadlock, and with a
/// timeout after which the whole process tree is killed.
/// </summary>
internal static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(string fileName, IEnumerable<string> arguments, TimeSpan timeout, string? workingDirectory = null, CancellationToken ct = default, Encoding? outputEncoding = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = outputEncoding,
            StandardErrorEncoding = outputEncoding,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        if (!string.IsNullOrEmpty(workingDirectory))
            psi.WorkingDirectory = workingDirectory;

        // ArgumentList quotes each argument correctly, including paths ending in '\'.
        foreach (string arg in arguments)
            psi.ArgumentList.Add(arg);

        using var proc = new Process { StartInfo = psi };
        proc.Start();

        Task<string> stdoutTask = proc.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = proc.StandardError.ReadToEndAsync();

        bool timedOut = false;
        using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            cts.CancelAfter(timeout);
            try
            {
                await proc.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                timedOut = !ct.IsCancellationRequested;
                try
                {
                    proc.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Already gone
                }
            }
        }

        // Once the tree is dead the pipes close; the grace period only guards against a
        // stray grandchild that escaped the kill and still holds a pipe handle.
        var drained = Task.WhenAll(stdoutTask, stderrTask);
        await Task.WhenAny(drained, Task.Delay(TimeSpan.FromSeconds(5), CancellationToken.None));

        string stdout = stdoutTask.IsCompletedSuccessfully ? stdoutTask.Result : "";
        string stderr = stderrTask.IsCompletedSuccessfully ? stderrTask.Result : "";
        int exitCode = proc.HasExited ? proc.ExitCode : -1;

        ct.ThrowIfCancellationRequested();
        return new ProcessResult(timedOut ? -1 : exitCode, stdout, stderr, timedOut);
    }

    /// <summary>The last few non-empty lines of an output stream, on a single line.</summary>
    public static string Tail(string output, int maxLines = 3, int maxChars = 300)
    {
        if (string.IsNullOrWhiteSpace(output))
            return "";

        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string tail = string.Join(" | ", lines.TakeLast(maxLines));
        return tail.Length <= maxChars ? tail : "..." + tail[^maxChars..];
    }
}
