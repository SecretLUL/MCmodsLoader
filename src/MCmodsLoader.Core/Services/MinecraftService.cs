using System.Text.Json;
using System.Text.RegularExpressions;
using MCmodsLoader.Core.Models;
using MCmodsLoader.Core.Utils;

namespace MCmodsLoader.Core.Services;

public interface IMinecraftService
{
    string GetDefaultMinecraftDirectory();
    bool IsValidMinecraftDirectory(string path);
    string GetModsDirectory(string minecraftPath);
    List<string> GetInstalledVersions(string minecraftPath);
    Task<List<MinecraftVersionInfo>> GetAllAvailableVersionsAsync(string minecraftPath);
}

public class MinecraftService : IMinecraftService
{
    private readonly HttpClient _httpClient;

    public MinecraftService(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        if (!_httpClient.DefaultRequestHeaders.Contains("User-Agent"))
        {
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "SecretLUL/MCmodsLoader (github.com/SecretLUL/MCmodsLoader)");
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

        if (Regex.IsMatch(v, @"^\d{2}w\d{2}[a-z]"))
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

        // Official releases in Minecraft strictly follow the format: digits separated by dots (e.g., 1.21.4, 26.2, 26.1)
        return Regex.IsMatch(version.Trim(), @"^\d+(\.\d+)+$");
    }

    public string GetDefaultMinecraftDirectory()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, ".minecraft");
    }

    public bool IsValidMinecraftDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            return false;

        // Valid if directory exists and has typical files/dirs or can have them created
        return true;
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
                using var doc = JsonDocument.Parse(File.ReadAllText(launcherProfilesPath));
                if (doc.RootElement.TryGetProperty("profiles", out var profilesProp) && profilesProp.ValueKind == JsonValueKind.Object)
                {
                    foreach (var profile in profilesProp.EnumerateObject())
                    {
                        if (profile.Value.TryGetProperty("lastVersionId", out var lastVerId))
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

    public async Task<List<MinecraftVersionInfo>> GetAllAvailableVersionsAsync(string minecraftPath)
    {
        var installed = new HashSet<string>(GetInstalledVersions(minecraftPath), StringComparer.OrdinalIgnoreCase);
        var result = new List<MinecraftVersionInfo>();

        try
        {
            var response = await _httpClient.GetAsync("https://meta.fabricmc.net/v2/versions/game");
            if (response.IsSuccessStatusCode)
            {
                using var stream = await response.Content.ReadAsStreamAsync();
                using var doc = await JsonDocument.ParseAsync(stream);
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    string version = el.GetProperty("version").GetString() ?? "";
                    bool stable = el.TryGetProperty("stable", out var s) && s.GetBoolean();
                    
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
