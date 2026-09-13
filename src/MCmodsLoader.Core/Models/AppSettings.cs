using System.Text.Json;

namespace MCmodsLoader.Core.Models;

public sealed class AppSettings
{
    public bool IncludeXaeroMinimap { get; set; } = true;
    public bool IncludeXaeroWorldMap { get; set; } = true;

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

            if (root.TryGetProperty("includeXaeroMinimap", out var pMinimap) && (pMinimap.ValueKind == JsonValueKind.True || pMinimap.ValueKind == JsonValueKind.False))
            {
                settings.IncludeXaeroMinimap = pMinimap.GetBoolean();
            }

            if (root.TryGetProperty("includeXaeroWorldMap", out var pWorldMap) && (pWorldMap.ValueKind == JsonValueKind.True || pWorldMap.ValueKind == JsonValueKind.False))
            {
                settings.IncludeXaeroWorldMap = pWorldMap.GetBoolean();
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
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            string json = $$"""
            {
              "includeXaeroMinimap": {{IncludeXaeroMinimap.ToString().ToLowerInvariant()}},
              "includeXaeroWorldMap": {{IncludeXaeroWorldMap.ToString().ToLowerInvariant()}}
            }
            """;
            File.WriteAllText(path, json);
        }
        catch
        {
        }
    }
}
