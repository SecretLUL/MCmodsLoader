using System.Reflection;
using System.Text.Json;
using MCperfBooster.Core.Models;
using MCperfBooster.Core.Utils;

namespace MCperfBooster.Core.Constants;

public static class ModPresets
{
    private static readonly object SyncLock = new();
    private static IReadOnlyList<ModDefinition>? _cachedMods;
    private static string _localCacheFilePath = GetDefaultLocalCacheFilePath();

    /// <summary>
    /// A remote list shorter than this is treated as broken (a truncated or emptied
    /// file on main) rather than as the new curated pack.
    /// </summary>
    internal const int MinimumModCount = 5;

    private static readonly TimeSpan RemoteTimeout = TimeSpan.FromSeconds(15);

    // One client for the app's lifetime; a new HttpClient per call leaks sockets.
    private static readonly Lazy<HttpClient> SharedClient = new(() =>
    {
        var client = new HttpClient { Timeout = RemoteTimeout };
        client.DefaultRequestHeaders.Add("User-Agent", "SecretLUL/MCperfBooster (github.com/SecretLUL/MCperfBooster)");
        return client;
    });

    // v1.2.x clients read a default_mods.json and treat every entry as mandatory, so the list with
    // optional mods got a file name of its own.
    private const string ResourceFileName = "default_mods.v2.json";

    public static string DefaultRemoteUrl { get; set; } =
        "https://raw.githubusercontent.com/SecretLUL/MCperfBooster/main/src/MCperfBooster.Core/Resources/" + ResourceFileName;

    /// <summary>
    /// Where the last good remote list is cached. Settable so tests can keep away from
    /// the real %LOCALAPPDATA%; changing it drops the in-memory list as well.
    /// </summary>
    public static string LocalCacheFilePath
    {
        get
        {
            lock (SyncLock)
            {
                return _localCacheFilePath;
            }
        }
        set
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value);
            lock (SyncLock)
            {
                _localCacheFilePath = value;
                _cachedMods = null;
            }
        }
    }

    public static string GetLocalCacheFilePath() => LocalCacheFilePath;

    private static string GetDefaultLocalCacheFilePath()
    {
        // Not mods.json: an older build sharing the folder would read optional mods from it as mandatory ones.
        return Path.Combine(AppPaths.DataDirectory, "mods.v2.json");
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

            string localCache = _localCacheFilePath;
            if (File.Exists(localCache))
            {
                try
                {
                    string json = File.ReadAllText(localCache);
                    var cached = ParseJson(json);
                    if (IsPlausibleModList(cached))
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

    /// <summary>The mandatory mods plus the optional ones <paramref name="isEnabled"/> switches on.</summary>
    public static IReadOnlyList<ModDefinition> GetFilteredModPack(Func<ModDefinition, bool> isEnabled)
    {
        return GetDefaultFpsModPack()
            .Where(m => !m.IsOptional || isEnabled(m))
            .ToList();
    }

    /// <summary>The mods offered under "Advanced", in list order.</summary>
    public static IReadOnlyList<ModDefinition> GetOptionalMods()
    {
        return GetDefaultFpsModPack().Where(m => m.IsOptional).ToList();
    }

    public static List<ModDefinition> LoadFromResource()
    {
        var assembly = typeof(ModPresets).Assembly;
        string? resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(ResourceFileName, StringComparison.OrdinalIgnoreCase));

        if (resourceName == null)
        {
            throw new InvalidOperationException($"Embedded resource '{ResourceFileName}' was not found.");
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

            string? slug = item.TryGetProperty("slug", out var pSlug) && pSlug.ValueKind == JsonValueKind.String ? pSlug.GetString() : null;
            string? name = item.TryGetProperty("name", out var pName) && pName.ValueKind == JsonValueKind.String ? pName.GetString() : null;
            string? fabricModId = item.TryGetProperty("fabricModId", out var pId) && pId.ValueKind == JsonValueKind.String ? pId.GetString() : null;

            if (!string.IsNullOrWhiteSpace(slug) &&
                !string.IsNullOrWhiteSpace(name) &&
                !string.IsNullOrWhiteSpace(fabricModId))
            {
                list.Add(new ModDefinition
                {
                    Slug = slug.Trim(),
                    Name = name.Trim(),
                    FabricModId = fabricModId.Trim(),
                    IsOptional = ReadBool(item, "optional") ?? false,
                    EnabledByDefault = ReadBool(item, "enabledByDefault") ?? true,
                });
            }
        }

        return list;
    }

    private static bool? ReadBool(JsonElement item, string propertyName) =>
        item.TryGetProperty(propertyName, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    /// <summary>
    /// Rejects lists that are evidently broken: too short to be the curated pack, or
    /// naming the same Modrinth project twice.
    /// </summary>
    internal static bool IsPlausibleModList(IReadOnlyList<ModDefinition> mods)
    {
        if (mods.Count < MinimumModCount)
            return false;

        var slugs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return mods.All(m => slugs.Add(m.Slug));
    }

    /// <summary>
    /// Fetches the curated list from <paramref name="remoteUrl"/> (default: main on
    /// GitHub) and adopts it if it is plausible. Returns true only if the adopted list
    /// differs from the one in use (same entries in the same order count as unchanged),
    /// so callers rescan the mods folder only when there is something new. False also
    /// covers network errors and rejected lists, which leave everything as it was.
    /// </summary>
    public static async Task<bool> TryUpdateFromRemoteAsync(HttpClient? httpClient = null, string? remoteUrl = null, CancellationToken ct = default)
    {
        string url = remoteUrl ?? DefaultRemoteUrl;
        try
        {
            // A caller's client may carry a long timeout meant for downloads.
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(RemoteTimeout);

            var client = httpClient ?? SharedClient.Value;
            using var response = await client.GetAsync(url, cts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            string json = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            var parsed = ParseJson(json);
            if (!IsPlausibleModList(parsed))
            {
                return false;
            }

            bool changed = !SameEntries(GetDefaultFpsModPack(), parsed);

            string localPath;
            lock (SyncLock)
            {
                if (changed)
                    _cachedMods = parsed;
                localPath = _localCacheFilePath;
            }

            // An unchanged list still seeds a missing cache, so the next start-up does
            // not fall back to the embedded copy while offline.
            if (changed || !File.Exists(localPath))
            {
                try
                {
                    AtomicFile.WriteAllText(localPath, json);
                }
                catch
                {
                }
            }

            return changed;
        }
        catch
        {
            return false;
        }
    }

    // Only the curated fields; ModDefinition also carries scan state that is irrelevant here.
    private static bool SameEntries(IReadOnlyList<ModDefinition> a, IReadOnlyList<ModDefinition> b) =>
        a.Count == b.Count && a.Zip(b).All(p =>
            p.First.Slug == p.Second.Slug && p.First.Name == p.Second.Name && p.First.FabricModId == p.Second.FabricModId &&
            p.First.IsOptional == p.Second.IsOptional && p.First.EnabledByDefault == p.Second.EnabledByDefault);

    public static void ResetToDefault()
    {
        lock (SyncLock)
        {
            _cachedMods = null;
            try
            {
                string localCache = _localCacheFilePath;
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
