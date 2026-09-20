using System.Reflection;
using System.Text.Json;
using MCmodsLoader.Core.Models;

namespace MCmodsLoader.Core.Constants;

public static class ModPresets
{
    private static readonly object SyncLock = new();
    private static IReadOnlyList<ModDefinition>? _cachedMods;

    public static string DefaultRemoteUrl { get; set; } =
        "https://raw.githubusercontent.com/SecretLUL/MCmodsLoader/main/src/MCmodsLoader.Core/Resources/default_mods.json";

    public static string GetLocalCacheFilePath()
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MCmodsLoader");
        return Path.Combine(dir, "mods.json");
    }

    public static IReadOnlyList<ModDefinition> GetDefaultFpsModPack()
    {
        if (_cachedMods != null)
        {
            return _cachedMods;
        }

        lock (SyncLock)
        {
            if (_cachedMods != null)
            {
                return _cachedMods;
            }

            string localCache = GetLocalCacheFilePath();
            if (File.Exists(localCache))
            {
                try
                {
                    string json = File.ReadAllText(localCache);
                    var cached = ParseJson(json);
                    if (cached.Count > 0)
                    {
                        _cachedMods = cached;
                        return _cachedMods;
                    }
                }
                catch
                {
                }
            }

            _cachedMods = LoadFromResource();
            return _cachedMods;
        }
    }

    public static IReadOnlyList<ModDefinition> GetFilteredModPack(bool includeXaeroMinimap, bool includeXaeroWorldMap)
    {
        return GetDefaultFpsModPack()
            .Where(m => (includeXaeroMinimap || m.Slug != "xaeros-minimap") &&
                        (includeXaeroWorldMap || m.Slug != "xaeros-world-map"))
            .ToList();
    }

    public static List<ModDefinition> LoadFromResource()
    {
        var assembly = typeof(ModPresets).Assembly;
        string? resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("default_mods.json", StringComparison.OrdinalIgnoreCase));

        if (resourceName == null)
        {
            throw new InvalidOperationException("Embedded resource 'default_mods.json' was not found.");
        }

        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream == null)
        {
            throw new InvalidOperationException($"Unable to open resource stream for '{resourceName}'.");
        }

        using var reader = new StreamReader(stream);
        return ParseJson(reader.ReadToEnd());
    }

    public static List<ModDefinition> ParseJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new List<ModDefinition>();
        }

        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            return new List<ModDefinition>();
        }

        var list = new List<ModDefinition>();
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string? slug = item.TryGetProperty("slug", out var pSlug) ? pSlug.GetString() : null;
            string? name = item.TryGetProperty("name", out var pName) ? pName.GetString() : null;
            string? fabricModId = item.TryGetProperty("fabricModId", out var pId) ? pId.GetString() : null;

            if (!string.IsNullOrWhiteSpace(slug) &&
                !string.IsNullOrWhiteSpace(name) &&
                !string.IsNullOrWhiteSpace(fabricModId))
            {
                list.Add(new ModDefinition
                {
                    Slug = slug.Trim(),
                    Name = name.Trim(),
                    FabricModId = fabricModId.Trim()
                });
            }
        }

        return list;
    }

    public static async Task<bool> TryUpdateFromRemoteAsync(HttpClient? httpClient = null, string? remoteUrl = null, CancellationToken ct = default)
    {
        string url = remoteUrl ?? DefaultRemoteUrl;
        try
        {
            var client = httpClient ?? new HttpClient();
            var response = await client.GetAsync(url, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            string json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var parsed = ParseJson(json);
            if (parsed.Count == 0)
            {
                return false;
            }

            lock (SyncLock)
            {
                _cachedMods = parsed;
            }

            try
            {
                string localPath = GetLocalCacheFilePath();
                string? dir = Path.GetDirectoryName(localPath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                await File.WriteAllTextAsync(localPath, json, ct).ConfigureAwait(false);
            }
            catch
            {
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    public static void ResetToDefault()
    {
        lock (SyncLock)
        {
            _cachedMods = null;
            try
            {
                string localCache = GetLocalCacheFilePath();
                if (File.Exists(localCache))
                {
                    File.Delete(localCache);
                }
            }
            catch
            {
            }
        }
    }
}
