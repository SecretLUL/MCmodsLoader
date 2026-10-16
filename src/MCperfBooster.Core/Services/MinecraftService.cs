using System.Text.Json;
using System.Text.RegularExpressions;
using MCperfBooster.Core.Models;
using MCperfBooster.Core.Utils;

namespace MCperfBooster.Core.Services;

public interface IMinecraftService
{
    string GetDefaultMinecraftDirectory();
    bool IsValidMinecraftDirectory(string path);
    string GetModsDirectory(string minecraftPath);
    List<string> GetInstalledVersions(string minecraftPath);
    Task<List<MinecraftVersionInfo>> GetAllAvailableVersionsAsync(string minecraftPath, CancellationToken ct = default);
}

public partial class MinecraftService : IMinecraftService
{
    private readonly HttpClient _httpClient;

    // The caller's client may carry a long timeout meant for downloads.
    private static readonly TimeSpan MetadataTimeout = TimeSpan.FromSeconds(20);

    private static readonly JsonDocumentOptions LenientJson = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    [GeneratedRegex(@"^\d{2}w\d{2}[a-z]")]
    private static partial Regex SnapshotYearWeekRegex();

    [GeneratedRegex(@"^\d+(\.\d+)+$")]
    private static partial Regex OfficialReleaseRegex();

    public MinecraftService(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        if (!_httpClient.DefaultRequestHeaders.Contains("User-Agent"))
        {
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "SecretLUL/MCperfBooster (github.com/SecretLUL/MCperfBooster)");
        }
    }

    /// <summary>
    /// Checks whether a Minecraft version string represents a snapshot, pre-release, release candidate, or test build.
    /// </summary>
    public static bool IsSnapshot(string version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return false;

        string v = version.Trim().ToLowerInvariant();

        if (v.Contains("snapshot") ||
            v.Contains("-rc") || v.Contains(".rc") || v.Contains(" rc") ||
            v.Contains("-pre") || v.Contains(".pre") || v.Contains(" pre") ||
            v.Contains("beta") || v.Contains("alpha") || v.Contains("experimental") ||
            v.Contains("combat") || v.Contains("unobfuscated") || v.Contains("potato") ||
            v.Contains("shareware") || v.Contains("infdev") || v.Contains("rd-"))
        {
            return true;
        }

        if (SnapshotYearWeekRegex().IsMatch(v))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Checks whether a Minecraft version string represents an official release (e.g. 1.21.4, 26.2, 26.1.2).
    /// </summary>
    public static bool IsOfficialRelease(string version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return false;

        if (IsSnapshot(version))
            return false;

        return OfficialReleaseRegex().IsMatch(version.Trim());
    }

    // Process names (without .exe) of the Java Edition launchers: MinecraftLauncher is
    // the standalone (MSI/legacy) launcher, Minecraft the Microsoft Store / Xbox app
    // package. Bedrock runs as Minecraft.Windows and the game itself as javaw, so an
    // exact match keeps both out.
    private static readonly string[] LauncherProcessNames = { "MinecraftLauncher", "Minecraft" };

    /// <summary>
    /// True if a Minecraft launcher is running. It keeps launcher_profiles.json in memory
    /// and may write it back later, dropping a profile added in the meantime, so callers
    /// should ask the user to close it before installing Fabric.
    /// </summary>
    public static bool IsLauncherRunning()
    {
        foreach (var process in System.Diagnostics.Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (IsLauncherProcessName(process.ProcessName))
                        return true;
                }
                catch
                {
                    // Exited while being enumerated.
                }
            }
        }
        return false;
    }

    internal static bool IsLauncherProcessName(string processName)
    {
        string name = processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? processName[..^4] : processName;
        return LauncherProcessNames.Any(n => n.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Picks the version to preselect: the newest installed version that already has
    /// Fabric, because that is the setup the shared mods folder belongs to; then the
    /// newest installed version; then the newest available release. Preselecting a newer
    /// vanilla version instead would let a quick Inject switch every mod over to it.
    /// <paramref name="installed"/> must be ordered newest first, as GetInstalledVersions
    /// returns it. Returns null when nothing is available.
    /// </summary>
    public static async Task<string?> PickPreferredVersionAsync(
        IReadOnlyList<string> available, IReadOnlyList<string> installed, Func<string, Task<bool>> hasFabricAsync)
    {
        if (available.Count == 0)
            return null;

        var candidates = installed
            .Select(v => available.FirstOrDefault(a => a.Equals(v, StringComparison.OrdinalIgnoreCase)))
            .OfType<string>()
            .ToList();

        foreach (string version in candidates)
        {
            if (await hasFabricAsync(version).ConfigureAwait(false))
                return version;
        }

        return candidates.FirstOrDefault() ?? available[0];
    }

    public string GetDefaultMinecraftDirectory()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, ".minecraft");
    }

    /// <summary>
    /// True for a folder the launcher has already set up: it holds a launcher profile
    /// file or a versions directory. Any other folder (Documents, say) is rejected
    /// rather than having mods, versions and profiles created inside it.
    /// </summary>
    public bool IsValidMinecraftDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            return false;

        return File.Exists(Path.Combine(path, "launcher_profiles.json")) ||
               File.Exists(Path.Combine(path, "launcher_profiles_microsoft_store.json")) ||
               Directory.Exists(Path.Combine(path, "versions"));
    }

    public string GetModsDirectory(string minecraftPath)
    {
        if (string.IsNullOrWhiteSpace(minecraftPath))
        {
            minecraftPath = GetDefaultMinecraftDirectory();
        }

        string modsDir = Path.Combine(minecraftPath, "mods");
        if (!Directory.Exists(modsDir))
        {
            Directory.CreateDirectory(modsDir);
        }
        return modsDir;
    }

    public List<string> GetInstalledVersions(string minecraftPath)
    {
        if (string.IsNullOrWhiteSpace(minecraftPath))
        {
            minecraftPath = GetDefaultMinecraftDirectory();
        }

        var versions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string versionsDir = Path.Combine(minecraftPath, "versions");

        if (Directory.Exists(versionsDir))
        {
            foreach (var dir in Directory.GetDirectories(versionsDir))
            {
                string dirName = Path.GetFileName(dir);
                // If it's a fabric loader version like fabric-loader-0.19.5-26.2, extract base version
                if (dirName.StartsWith("fabric-loader-", StringComparison.OrdinalIgnoreCase))
                {
                    // Format: fabric-loader-{loaderVersion}-{gameVersion}
                    var parts = dirName.Split('-');
                    if (parts.Length >= 4)
                    {
                        string gameVer = string.Join("-", parts.Skip(3));
                        if (!string.IsNullOrWhiteSpace(gameVer) && IsOfficialRelease(gameVer))
                            versions.Add(gameVer);
                    }
                }
                else
                {
                    // Check if contains version json and is an official release
                    string jsonFile = Path.Combine(dir, $"{dirName}.json");
                    if (File.Exists(jsonFile) && IsOfficialRelease(dirName))
                    {
                        versions.Add(dirName);
                    }
                }
            }
        }

        // Also check launcher_profiles.json
        string launcherProfilesPath = Path.Combine(minecraftPath, "launcher_profiles.json");
        if (File.Exists(launcherProfilesPath))
        {
            try
            {
                using var stream = File.OpenRead(launcherProfilesPath);
                using var doc = JsonDocument.Parse(stream, LenientJson);
                if (doc.RootElement.TryGetProperty("profiles", out var profilesProp) && profilesProp.ValueKind == JsonValueKind.Object)
                {
                    foreach (var profile in profilesProp.EnumerateObject())
                    {
                        if (profile.Value.ValueKind == JsonValueKind.Object &&
                            profile.Value.TryGetProperty("lastVersionId", out var lastVerId) &&
                            lastVerId.ValueKind == JsonValueKind.String)
                        {
                            string ver = lastVerId.GetString() ?? "";
                            // The launcher's "latest-release" and "latest-snapshot"
                            // placeholders need no special case: IsOfficialRelease
                            // accepts only dot-separated digits, so both fall out.
                            if (!string.IsNullOrWhiteSpace(ver))
                            {
                                if (ver.StartsWith("fabric-loader-", StringComparison.OrdinalIgnoreCase))
                                {
                                    var parts = ver.Split('-');
                                    if (parts.Length >= 4)
                                    {
                                        string gameVer = string.Join("-", parts.Skip(3));
                                        if (!string.IsNullOrWhiteSpace(gameVer) && IsOfficialRelease(gameVer))
                                            versions.Add(gameVer);
                                    }
                                }
                                else if (IsOfficialRelease(ver))
                                {
                                    versions.Add(ver);
                                }
                            }
                        }
                    }
                }
            }
            catch
            {
                // Ignore corrupt json
            }
        }

        return versions.OrderByDescending(v => v, MinecraftVersionComparer.Instance).ToList();
    }

    public async Task<List<MinecraftVersionInfo>> GetAllAvailableVersionsAsync(string minecraftPath, CancellationToken ct = default)
    {
        var installed = new HashSet<string>(GetInstalledVersions(minecraftPath), StringComparer.OrdinalIgnoreCase);
        var result = new List<MinecraftVersionInfo>();

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(MetadataTimeout);

            using var response = await _httpClient.GetAsync("https://meta.fabricmc.net/v2/versions/game", cts.Token);
            if (response.IsSuccessStatusCode)
            {
                using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cts.Token);
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    string version = el.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
                    bool stable = el.TryGetProperty("stable", out var s) && s.ValueKind == JsonValueKind.True;
                    
                    // Only include official releases (exclude snapshots, pre-releases, and test builds)
                    if (!stable || string.IsNullOrEmpty(version) || !IsOfficialRelease(version))
                        continue;

                    result.Add(new MinecraftVersionInfo
                    {
                        VersionId = version,
                        IsInstalled = installed.Contains(version)
                    });
                }
            }
        }
        catch
        {
            // If offline or request fails, fall back to installed versions
        }

        // Add any installed version that wasn't in the API list (only official releases)
        foreach (var inst in installed.OrderByDescending(v => v, MinecraftVersionComparer.Instance))
        {
            if (!IsOfficialRelease(inst))
                continue;

            if (!result.Any(r => r.VersionId.Equals(inst, StringComparison.OrdinalIgnoreCase)))
            {
                result.Insert(0, new MinecraftVersionInfo
                {
                    VersionId = inst,
                    IsInstalled = true
                });
            }
        }

        // If offline and no local versions found, supply common active versions
        if (result.Count == 0)
        {
            string[] fallbackVersions = ["26.2", "26.1", "1.21.4", "1.21.1", "1.20.1"];
            foreach (var fb in fallbackVersions)
            {
                result.Add(new MinecraftVersionInfo
                {
                    VersionId = fb,
                    IsInstalled = false
                });
            }
        }

        // Order: Maintain official release ordering descending using MinecraftVersionComparer
        return result
            .OrderByDescending(v => v.VersionId, MinecraftVersionComparer.Instance)
            .ToList();
    }
}
