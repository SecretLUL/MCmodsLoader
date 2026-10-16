using System.IO.Compression;
using System.Text.Json;

namespace MCperfBooster.Core.Utils;

/// <param name="NestedModIds">
/// Ids of the mods embedded via fabric.mod.json "jars" (one level deep); only filled when requested.
/// </param>
public record FabricModMetadata(string Id, string Name, string Version, string? MinecraftDependency = null, IReadOnlyList<string>? NestedModIds = null);

public static class JarInspector
{
    // Nested jars are read into memory to open them (scans run in parallel); bigger ones are skipped
    internal const int MaxNestedJarSize = 16 * 1024 * 1024;

    public static FabricModMetadata? ReadFabricModJson(string jarFilePath, bool includeNestedJars = false)
    {
        if (!File.Exists(jarFilePath))
            return null;

        try
        {
            // Share delete too, so a concurrent scan never makes renaming or deleting the jar fail
            using var fileStream = new FileStream(jarFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var archive = new ZipArchive(fileStream, ZipArchiveMode.Read);
            return ReadFromArchive(archive, includeNestedJars);
        }
        catch
        {
            return null;
        }
    }

    private static FabricModMetadata? ReadFromArchive(ZipArchive archive, bool includeNestedJars)
    {
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

        IReadOnlyList<string>? nestedIds = null;
        if (includeNestedJars)
        {
            nestedIds = root.TryGetProperty("jars", out var jarsProp) && jarsProp.ValueKind == JsonValueKind.Array
                ? ReadNestedModIds(archive, jarsProp)
                : Array.Empty<string>();
        }

        return new FabricModMetadata(id, name, version, mcDep, nestedIds);
    }

    private static List<string> ReadNestedModIds(ZipArchive archive, JsonElement jars)
    {
        var ids = new List<string>();
        var seenFiles = new HashSet<string>(StringComparer.Ordinal);
        foreach (var jar in jars.EnumerateArray())
        {
            if (jar.ValueKind != JsonValueKind.Object ||
                !jar.TryGetProperty("file", out var fileProp) || fileProp.ValueKind != JsonValueKind.String ||
                !seenFiles.Add(fileProp.GetString()!) ||
                archive.GetEntry(fileProp.GetString()!) is not { } nestedEntry ||
                nestedEntry.Length > MaxNestedJarSize)
            {
                continue;
            }

            try
            {
                // ZipArchive needs a seekable stream. The declared size isn't trusted: the buffer grows as
                // data arrives and reading stops at the cap.
                using var buffer = new MemoryStream();
                using (var nestedStream = nestedEntry.Open())
                {
                    if (!CopyAtMost(nestedStream, buffer, MaxNestedJarSize))
                        continue;
                }
                buffer.Position = 0;

                using var nestedArchive = new ZipArchive(buffer, ZipArchiveMode.Read);
                if (ReadFromArchive(nestedArchive, includeNestedJars: false) is { } nested)
                    ids.Add(nested.Id);
            }
            catch
            {
                // A broken nested jar doesn't make the outer jar unreadable
            }
        }

        return ids;
    }

    /// <summary>Copies the stream; false (with a partial copy) if it is longer than <paramref name="maxBytes"/>.</summary>
    private static bool CopyAtMost(Stream source, Stream destination, int maxBytes)
    {
        byte[] chunk = new byte[81920];
        long total = 0;
        int read;
        while ((read = source.Read(chunk, 0, chunk.Length)) > 0)
        {
            total += read;
            if (total > maxBytes)
                return false;

            destination.Write(chunk, 0, read);
        }

        return true;
    }
}
