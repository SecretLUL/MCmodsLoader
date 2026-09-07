using System.Text.Json;
using MCmodsLoader.Core.Models;

namespace MCmodsLoader.Core.Services;

public interface IModrinthService
{
    Task<ModrinthVersionFile?> GetLatestVersionFileAsync(string projectSlugOrId, string mcVersion);
    Task DownloadFileAsync(string downloadUrl, string targetFilePath, IProgress<double>? progress = null, CancellationToken ct = default);
}

public class ModrinthService : IModrinthService
{
    private readonly HttpClient _httpClient;

    public ModrinthService(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        if (!_httpClient.DefaultRequestHeaders.Contains("User-Agent"))
        {
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "SecretLUL/MCmodsLoader (github.com/SecretLUL/MCmodsLoader)");
        }
    }

    public async Task<ModrinthVersionFile?> GetLatestVersionFileAsync(string projectSlugOrId, string mcVersion)
    {
        try
        {
            string url = $"https://api.modrinth.com/v2/project/{Uri.EscapeDataString(projectSlugOrId)}/version" +
                         $"?loaders=%5B%22fabric%22%5D&game_versions=%5B%22{Uri.EscapeDataString(mcVersion)}%22%5D";

            var response = await _httpClient.GetAsync(url);
            if (!response.IsSuccessStatusCode)
                return null;

            using var stream = await response.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);

            if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
                return null;

            // First entry is newest matching release
            var versionItem = doc.RootElement[0];
            if (!versionItem.TryGetProperty("files", out var filesProp) || filesProp.GetArrayLength() == 0)
                return null;

            JsonElement chosenFile = default;
            bool found = false;

            // Prefer primary jar, ignoring sources, dev, and javadoc jars
            foreach (var file in filesProp.EnumerateArray())
            {
                string fn = file.GetProperty("filename").GetString() ?? "";
                if (fn.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
                {
                    if (fn.EndsWith("-sources.jar", StringComparison.OrdinalIgnoreCase) ||
                        fn.EndsWith("-dev.jar", StringComparison.OrdinalIgnoreCase) ||
                        fn.EndsWith("-javadoc.jar", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    bool isPrimary = file.TryGetProperty("primary", out var prim) && prim.GetBoolean();
                    if (isPrimary)
                    {
                        chosenFile = file;
                        found = true;
                        break;
                    }
                    if (!found)
                    {
                        chosenFile = file;
                        found = true;
                    }
                }
            }

            if (!found)
                return null;

            string fileUrl = chosenFile.GetProperty("url").GetString()!;
            string filename = chosenFile.GetProperty("filename").GetString()!;

            return new ModrinthVersionFile
            {
                Url = fileUrl,
                Filename = filename,
            };
        }
        catch
        {
            return null;
        }
    }

    public async Task DownloadFileAsync(string downloadUrl, string targetFilePath, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        string dir = Path.GetDirectoryName(targetFilePath) ?? "";
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        string tempPath = $"{targetFilePath}.download";

        try
        {
            using var response = await _httpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            long? totalBytes = response.Content.Headers.ContentLength;

            using (var contentStream = await response.Content.ReadAsStreamAsync(ct))
            using (var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
            {
                byte[] buffer = new byte[81920];
                long totalRead = 0;
                int read;

                while ((read = await contentStream.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, read), ct);
                    totalRead += read;

                    if (totalBytes.HasValue && totalBytes.Value > 0)
                    {
                        double pct = (double)totalRead / totalBytes.Value * 100.0;
                        progress?.Report(pct);
                    }
                }
            }

            try
            {
                if (File.Exists(targetFilePath))
                {
                    File.Delete(targetFilePath);
                }

                File.Move(tempPath, targetFilePath);
            }
            catch (IOException ex)
            {
                throw new IOException($"Could not overwrite '{Path.GetFileName(targetFilePath)}'. If Minecraft is running, please close it and try again.", ex);
            }

            progress?.Report(100.0);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { }
            }
        }
    }
}
