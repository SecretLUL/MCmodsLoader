using System.Text;
using System.Text.Json;
using MCperfBooster.Core.Utils;

namespace MCperfBooster.Core.Models;

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

    /// <summary>Whether Minecraft runs on a GraalVM the app downloads, instead of the launcher's own Java.</summary>
    public bool UseGraalVm { get; set; } = true;

    /// <summary>Whether the user has agreed to that download (it is large and under Oracle's license).</summary>
    public bool GraalVmConsentGiven { get; set; }

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
        var copy = new AppSettings
        {
            UseGraalVm = UseGraalVm,
            GraalVmConsentGiven = GraalVmConsentGiven,
        };
        foreach (var (slug, enabled) in _optionalMods)
        {
            copy._optionalMods[slug] = enabled;
        }
        return copy;
    }

    public static string GetDefaultSettingsFilePath() => Path.Combine(AppPaths.DataDirectory, "settings.json");

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

            // Files from before GraalVM have neither key and keep the defaults.
            if (root.TryGetProperty("useGraalVm", out var useGraalVm) && useGraalVm.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                settings.UseGraalVm = useGraalVm.GetBoolean();
            }

            if (root.TryGetProperty("graalVmConsent", out var consent) && consent.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                settings.GraalVmConsentGiven = consent.GetBoolean();
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
                writer.WriteBoolean("useGraalVm", UseGraalVm);
                writer.WriteBoolean("graalVmConsent", GraalVmConsentGiven);
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
