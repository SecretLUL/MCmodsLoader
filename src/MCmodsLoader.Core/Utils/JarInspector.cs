using System.IO.Compression;
using System.Text.Json;

namespace MCmodsLoader.Core.Utils;

public record FabricModMetadata(string Id, string Name, string Version, string? MinecraftDependency = null);

public static class JarInspector
{
    public static FabricModMetadata? ReadFabricModJson(string jarFilePath)
    {
        if (!File.Exists(jarFilePath))
            return null;

        try
        {
            using var fileStream = new FileStream(jarFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var archive = new ZipArchive(fileStream, ZipArchiveMode.Read);
            var entry = archive.GetEntry("fabric.mod.json")
                        ?? archive.Entries.FirstOrDefault(e => e.FullName.TrimStart('.', '/', '\\').Equals("fabric.mod.json", StringComparison.OrdinalIgnoreCase));
            if (entry == null)
                return null;

            using var stream = entry.Open();
            var jsonOptions = new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            };
            using var doc = JsonDocument.Parse(stream, jsonOptions);
            var root = doc.RootElement;

            string id = root.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? "" : "";
            string name = root.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? "" : "";
            string version = root.TryGetProperty("version", out var verProp) ? verProp.GetString() ?? "" : "";

            if (string.IsNullOrWhiteSpace(id))
                return null;

            string? mcDep = null;
            if (root.TryGetProperty("depends", out var dependsProp) && dependsProp.ValueKind == JsonValueKind.Object)
            {
                if (dependsProp.TryGetProperty("minecraft", out var mcProp))
                {
                    if (mcProp.ValueKind == JsonValueKind.String)
                    {
                        mcDep = mcProp.GetString();
                    }
                    else if (mcProp.ValueKind == JsonValueKind.Array)
                    {
                        var list = new List<string>();
                        foreach (var item in mcProp.EnumerateArray())
                        {
                            if (item.GetString() is { } s) list.Add(s);
                        }
                        mcDep = string.Join(" || ", list);
                    }
                }
            }

            return new FabricModMetadata(id, name, version, mcDep);
        }
        catch
        {
            return null;
        }
    }
}
