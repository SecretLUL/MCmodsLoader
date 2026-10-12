using System.Text;
using System.Text.Json;
using MCmodsLoader.Core.Utils;

namespace MCmodsLoader.Core.Models;

public sealed class AppSettings
{
    // Earlier builds stored the two Xaero switches under fixed names.
    private static readonly (string Key, string Slug)[] LegacySwitches =
    {
        ("includeXaeroMinimap", "xaeros-minimap"),
        ("includeXaeroWorldMap", "xaeros-world-map"),
    };

    private readonly Dictionary<string, bool> _optionalMods = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The optional mods the user has switched on or off, by slug. A mod missing here was never switched.</summary>
    public IReadOnlyDictionary<string, bool> OptionalModChoices => _optionalMods;

    public void SetOptionalModEnabled(string slug, bool enabled) => _optionalMods[slug] = enabled;

    /// <summary>
    /// Whether an optional mod is on: the user's own choice if there is one; otherwise on if its jar is already in
    /// the mods folder (someone installed it, so it is kept up to date from now on) or if the pack turns it on by
    /// default.
    /// </summary>
    public bool IsOptionalModEnabled(ModDefinition mod, bool presentInModsFolder) =>
        _optionalMods.TryGetValue(mod.Slug, out bool choice) ? choice : presentInModsFolder || mod.EnabledByDefault;

    /// <summary>
    /// True only if the user switched the mod off. Only then are its jars removed: a mod that is merely off by
    /// default may have been installed by hand.
    /// </summary>
    public bool IsOptionalModSwitchedOff(string slug) => _optionalMods.TryGetValue(slug, out bool choice) && !choice;

    /// <summary>A copy to read on another thread while this one may still change.</summary>
    public AppSettings Clone()
    {
        var copy = new AppSettings();
        foreach (var (slug, enabled) in _optionalMods)
        {
            copy._optionalMods[slug] = enabled;
        }
        return copy;
    }

    public static string GetDefaultSettingsFilePath()
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MCmodsLoader");
        return Path.Combine(dir, "settings.json");
    }

    public static AppSettings Load(string? customPath = null)
    {
        string path = customPath ?? GetDefaultSettingsFilePath();
        var settings = new AppSettings();

        if (!File.Exists(path))
        {
            return settings;
        }

        try
        {
            string json = File.ReadAllText(path);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("optionalMods", out var choices) && choices.ValueKind == JsonValueKind.Object)
            {
                foreach (var choice in choices.EnumerateObject())
                {
                    if (choice.Value.ValueKind is JsonValueKind.True or JsonValueKind.False && !string.IsNullOrWhiteSpace(choice.Name))
                    {
                        settings._optionalMods[choice.Name] = choice.Value.GetBoolean();
                    }
                }
            }

            foreach (var (key, slug) in LegacySwitches)
            {
                if (!settings._optionalMods.ContainsKey(slug) &&
                    root.TryGetProperty(key, out var legacy) && legacy.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    settings._optionalMods[slug] = legacy.GetBoolean();
                }
            }
        }
        catch
        {
        }

        return settings;
    }

    public void Save(string? customPath = null)
    {
        string path = customPath ?? GetDefaultSettingsFilePath();
        try
        {
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                writer.WriteStartObject("optionalMods");
                foreach (var (slug, enabled) in _optionalMods.OrderBy(c => c.Key, StringComparer.OrdinalIgnoreCase))
                {
                    writer.WriteBoolean(slug, enabled);
                }
                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            // Atomic, so a crash mid-save cannot leave a truncated settings file behind.
            AtomicFile.WriteAllText(path, Encoding.UTF8.GetString(buffer.ToArray()));
        }
        catch
        {
        }
    }
}
