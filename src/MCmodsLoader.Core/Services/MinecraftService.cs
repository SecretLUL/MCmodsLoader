using System.Text.Json;
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
                        if (!string.IsNullOrWhiteSpace(gameVer))
                            versions.Add(gameVer);
                    }
                }
                else
                {
                    // Check if contains version json
                    string jsonFile = Path.Combine(dir, $"{dirName}.json");
                    if (File.Exists(jsonFile))
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
                            if (!string.IsNullOrWhiteSpace(ver) && !ver.Equals("latest-release", StringComparison.OrdinalIgnoreCase) && !ver.Equals("latest-snapshot", StringComparison.OrdinalIgnoreCase))
                            {
                                if (ver.StartsWith("fabric-loader-", StringComparison.OrdinalIgnoreCase))
                                {
                                    var parts = ver.Split('-');
                                    if (parts.Length >= 4)
                                    {
                                        string gameVer = string.Join("-", parts.Skip(3));
                                        if (!string.IsNullOrWhiteSpace(gameVer))
                                            versions.Add(gameVer);
                                    }
                                }
                                else
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
                    if (!string.IsNullOrEmpty(version))
                    {
                        result.Add(new MinecraftVersionInfo
                        {
                            VersionId = version,
                            Type = stable ? "release" : "snapshot",
                            IsInstalled = installed.Contains(version)
                        });
                    }
                }
            }
        }
        catch
        {
            // If offline or request fails, fall back to installed versions
        }

        // Add any installed version that wasn't in the API list
        foreach (var inst in installed.OrderByDescending(v => v, MinecraftVersionComparer.Instance))
        {
            if (!result.Any(r => r.VersionId.Equals(inst, StringComparison.OrdinalIgnoreCase)))
            {
                result.Insert(0, new MinecraftVersionInfo
                {
                    VersionId = inst,
                    Type = "release",
                    IsInstalled = true
                });
            }
        }

        // If offline and no local versions found, supply common active versions
        if (result.Count == 0)
        {
            string[] fallbackVersions = ["1.21.4", "1.21.1", "1.21", "1.20.4", "1.20.1", "1.19.4"];
            foreach (var fb in fallbackVersions)
            {
                result.Add(new MinecraftVersionInfo
                {
                    VersionId = fb,
                    Type = "release",
                    IsInstalled = false
                });
            }
        }

        // Order: Installed versions first, then maintain official API release ordering using MinecraftVersionComparer
        return result
            .OrderByDescending(v => v.IsInstalled)
            .ThenByDescending(v => v.Type == "release")
            .ThenByDescending(v => v.VersionId, MinecraftVersionComparer.Instance)
            .ToList();
    }
}
