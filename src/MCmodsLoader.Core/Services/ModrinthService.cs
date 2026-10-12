using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MCmodsLoader.Core.Models;

namespace MCmodsLoader.Core.Services;

public interface IModrinthService
{
    /// <summary>
    /// Returns the newest Fabric jar of the project for <paramref name="mcVersion"/>, preferring releases over alpha and
    /// beta builds, or null when Modrinth answered but has no such version (an unknown project counts as "not available"
    /// too). Network/HTTP errors and timeouts are thrown, so they can't be mistaken for "not available".
    /// </summary>
    Task<ModrinthVersionFile?> GetLatestVersionFileAsync(string projectSlugOrId, string mcVersion, CancellationToken ct = default);

    /// <summary>
    /// Returns the jar of one specific Modrinth version, or null if it doesn't exist or isn't a Fabric build for
    /// <paramref name="mcVersion"/>. Errors are thrown as for <see cref="GetLatestVersionFileAsync"/>.
    /// </summary>
    Task<ModrinthVersionFile?> GetVersionFileAsync(string versionId, string mcVersion, CancellationToken ct = default);

    /// <summary>Looks up projects by id or slug. Unknown ones are simply missing from the result.</summary>
    Task<IReadOnlyList<ModrinthProject>> GetProjectsAsync(IEnumerable<string> idsOrSlugs, CancellationToken ct = default);

    /// <summary>
    /// Downloads into a temp file, verifies it against <paramref name="expectedSha512"/> (or <paramref name="expectedSha1"/>
    /// if that's all there is) and only then moves it to <paramref name="targetFilePath"/>.
    /// </summary>
    Task DownloadFileAsync(string downloadUrl, string targetFilePath, IProgress<double>? progress = null, CancellationToken ct = default, string? expectedSha512 = null, string? expectedSha1 = null);
}

public class ModrinthService : IModrinthService
{
    private readonly HttpClient _httpClient;

    // The shared HttpClient's Timeout is sized for large downloads, metadata calls get a much shorter one.
    internal TimeSpan MetadataTimeout { get; init; } = TimeSpan.FromSeconds(20);

    // With ResponseHeadersRead the HttpClient's Timeout no longer covers the body, so guard against stalls.
    internal TimeSpan DownloadStallTimeout { get; init; } = TimeSpan.FromSeconds(60);

    public ModrinthService(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        if (!_httpClient.DefaultRequestHeaders.Contains("User-Agent"))
        {
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "SecretLUL/MCmodsLoader (github.com/SecretLUL/MCmodsLoader)");
        }
    }

    public async Task<ModrinthVersionFile?> GetLatestVersionFileAsync(string projectSlugOrId, string mcVersion, CancellationToken ct = default)
    {
        string url = $"https://api.modrinth.com/v2/project/{Uri.EscapeDataString(projectSlugOrId)}/version" +
                     $"?loaders=%5B%22fabric%22%5D&game_versions=%5B%22{Uri.EscapeDataString(mcVersion)}%22%5D";

        using var doc = await GetJsonAsync(url, ct).ConfigureAwait(false);
        if (doc == null || doc.RootElement.ValueKind != JsonValueKind.Array)
            return null;

        // Versions are sorted newest first. The newest release that ships a usable jar wins; an alpha or
        // beta is only taken when this Minecraft version has no release at all. A newer Sodium alpha would
        // otherwise be installed next to an Iris that was built for the Sodium release before it.
        (JsonElement Version, JsonElement File)? preRelease = null;
        foreach (var version in doc.RootElement.EnumerateArray())
        {
            if (version.ValueKind != JsonValueKind.Object ||
                !version.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array ||
                ChooseJar(files) is not { } file)
            {
                continue;
            }

            if (IsRelease(version))
                return await ResolveDependencyProjectsAsync(BuildVersionFile(version, file), ct).ConfigureAwait(false);

            preRelease ??= (version, file);
        }

        return preRelease is { } newest
            ? await ResolveDependencyProjectsAsync(BuildVersionFile(newest.Version, newest.File), ct).ConfigureAwait(false)
            : null;
    }

    public async Task<ModrinthVersionFile?> GetVersionFileAsync(string versionId, string mcVersion, CancellationToken ct = default)
    {
        using var doc = await GetJsonAsync($"https://api.modrinth.com/v2/version/{Uri.EscapeDataString(versionId)}", ct).ConfigureAwait(false);
        if (doc == null || doc.RootElement.ValueKind != JsonValueKind.Object)
            return null;

        var version = doc.RootElement;
        if (!ListContains(version, "game_versions", mcVersion) || !ListContains(version, "loaders", "fabric") ||
            !version.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array ||
            ChooseJar(files) is not { } file)
        {
            return null;
        }

        return await ResolveDependencyProjectsAsync(BuildVersionFile(version, file), ct).ConfigureAwait(false);
    }

    // A version without a type is treated as a release, as Modrinth did before it had types.
    private static bool IsRelease(JsonElement version) =>
        GetString(version, "version_type") is not { } type || type.Equals("release", StringComparison.OrdinalIgnoreCase);

    private static bool ListContains(JsonElement element, string propertyName, string value) =>
        element.TryGetProperty(propertyName, out var list) && list.ValueKind == JsonValueKind.Array &&
        list.EnumerateArray().Any(item => item.ValueKind == JsonValueKind.String &&
                                          string.Equals(item.GetString(), value, StringComparison.OrdinalIgnoreCase));

    /// <summary>Required dependencies that only name a version get their project id from GET /v2/version/{id}.</summary>
    private async Task<ModrinthVersionFile> ResolveDependencyProjectsAsync(ModrinthVersionFile file, CancellationToken ct)
    {
        var dependencies = file.Dependencies.ToList();
        bool changed = false;

        for (int i = 0; i < dependencies.Count; i++)
        {
            var dep = dependencies[i];
            if (dep.ProjectId != null || dep.VersionId == null ||
                !dep.DependencyType.Equals("required", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                using var doc = await GetJsonAsync($"https://api.modrinth.com/v2/version/{Uri.EscapeDataString(dep.VersionId)}", ct).ConfigureAwait(false);
                if (doc != null && GetString(doc.RootElement, "project_id") is { } projectId)
                {
                    dependencies[i] = dep with { ProjectId = projectId };
                    changed = true;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // Unresolvable: the dependency keeps a null project id and isn't checked
            }
        }

        return changed ? file with { Dependencies = dependencies } : file;
    }

    public async Task<IReadOnlyList<ModrinthProject>> GetProjectsAsync(IEnumerable<string> idsOrSlugs, CancellationToken ct = default)
    {
        var ids = idsOrSlugs.Where(i => !string.IsNullOrWhiteSpace(i)).Distinct(StringComparer.Ordinal).ToList();
        if (ids.Count == 0)
            return Array.Empty<ModrinthProject>();

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            foreach (var id in ids) writer.WriteStringValue(id);
            writer.WriteEndArray();
        }

        string url = "https://api.modrinth.com/v2/projects?ids=" + Uri.EscapeDataString(Encoding.UTF8.GetString(buffer.ToArray()));

        using var doc = await GetJsonAsync(url, ct).ConfigureAwait(false);
        var projects = new List<ModrinthProject>();
        if (doc == null || doc.RootElement.ValueKind != JsonValueKind.Array)
            return projects;

        foreach (var item in doc.RootElement.EnumerateArray())
        {
            string? id = GetString(item, "id");
            string? slug = GetString(item, "slug");
            if (id == null || slug == null)
                continue;

            var loaders = new List<string>();
            if (item.TryGetProperty("loaders", out var loadersProp) && loadersProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var loader in loadersProp.EnumerateArray())
                {
                    if (loader.ValueKind == JsonValueKind.String && loader.GetString() is { } name) loaders.Add(name);
                }
            }

            projects.Add(new ModrinthProject(id, slug, GetString(item, "title") ?? slug, loaders));
        }

        return projects;
    }

    public async Task DownloadFileAsync(string downloadUrl, string targetFilePath, IProgress<double>? progress = null, CancellationToken ct = default, string? expectedSha512 = null, string? expectedSha1 = null)
    {
        string dir = Path.GetDirectoryName(targetFilePath) ?? "";
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        string tempPath = $"{targetFilePath}.download";

        bool useSha512 = !string.IsNullOrWhiteSpace(expectedSha512);
        string? expectedHash = useSha512 ? expectedSha512 : string.IsNullOrWhiteSpace(expectedSha1) ? null : expectedSha1;
        using var hasher = expectedHash == null ? null
            : IncrementalHash.CreateHash(useSha512 ? HashAlgorithmName.SHA512 : HashAlgorithmName.SHA1);

        using var stallCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        try
        {
            using var response = await _httpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            long? totalBytes = response.Content.Headers.ContentLength;

            using (var contentStream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            using (var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
            {
                byte[] buffer = new byte[81920];
                long totalRead = 0;

                while (true)
                {
                    int read;
                    try
                    {
                        // Re-armed for every chunk, so it only fires when no data arrives for the whole period
                        stallCts.CancelAfter(DownloadStallTimeout);
                        read = await contentStream.ReadAsync(buffer.AsMemory(), stallCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested && stallCts.IsCancellationRequested)
                    {
                        throw new TimeoutException(
                            $"The download of '{Path.GetFileName(targetFilePath)}' stalled (no data for {DownloadStallTimeout.TotalSeconds:0} seconds) – check your connection and try again.");
                    }

                    if (read == 0)
                        break;

                    await fileStream.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    hasher?.AppendData(buffer, 0, read);
                    totalRead += read;

                    if (totalBytes.HasValue && totalBytes.Value > 0)
                    {
                        double pct = (double)totalRead / totalBytes.Value * 100.0;
                        progress?.Report(pct);
                    }
                }
            }

            if (hasher != null && !Convert.ToHexString(hasher.GetHashAndReset()).Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Checksum mismatch for '{Path.GetFileName(targetFilePath)}' – the download is corrupted, please try again.");
            }

            try
            {
                // A single replacing rename: if it fails, the old file is still in place
                File.Move(tempPath, targetFilePath, overwrite: true);
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

    /// <summary>GETs a JSON document. Returns null for 404, throws for every other failure.</summary>
    private async Task<JsonDocument?> GetJsonAsync(string url, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(MetadataTimeout);

        try
        {
            using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return null;

            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"Modrinth answered HTTP {(int)response.StatusCode} ({response.ReasonPhrase})", null, response.StatusCode);

            using var stream = await response.Content.ReadAsStreamAsync(timeoutCts.Token).ConfigureAwait(false);
            return await JsonDocument.ParseAsync(stream, default, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Either our metadata timeout or the shared HttpClient's own Timeout fired
            throw new TimeoutException(timeoutCts.IsCancellationRequested
                ? $"Modrinth did not respond within {MetadataTimeout.TotalSeconds:0} seconds"
                : $"Modrinth did not respond within the HTTP client timeout of {_httpClient.Timeout.TotalSeconds:0} seconds");
        }
    }

    private static JsonElement? ChooseJar(JsonElement files)
    {
        JsonElement? fallback = null;

        // Prefer the primary jar, ignoring sources, dev, and javadoc jars
        foreach (var file in files.EnumerateArray())
        {
            string fn = GetString(file, "filename") ?? "";
            if (!fn.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) || GetString(file, "url") == null)
                continue;

            if (fn.EndsWith("-sources.jar", StringComparison.OrdinalIgnoreCase) ||
                fn.EndsWith("-dev.jar", StringComparison.OrdinalIgnoreCase) ||
                fn.EndsWith("-javadoc.jar", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (file.TryGetProperty("primary", out var prim) && prim.ValueKind == JsonValueKind.True)
                return file;

            fallback ??= file;
        }

        return fallback;
    }

    private static ModrinthVersionFile BuildVersionFile(JsonElement version, JsonElement file)
    {
        string? sha1 = null, sha512 = null;
        if (file.TryGetProperty("hashes", out var hashes) && hashes.ValueKind == JsonValueKind.Object)
        {
            sha1 = GetString(hashes, "sha1");
            sha512 = GetString(hashes, "sha512");
        }

        var dependencies = new List<ModrinthDependency>();
        if (version.TryGetProperty("dependencies", out var deps) && deps.ValueKind == JsonValueKind.Array)
        {
            foreach (var dep in deps.EnumerateArray())
            {
                if (GetString(dep, "dependency_type") is { } type)
                {
                    dependencies.Add(new ModrinthDependency(GetString(dep, "project_id"), GetString(dep, "version_id"), type));
                }
            }
        }

        return new ModrinthVersionFile
        {
            Url = GetString(file, "url")!,
            Filename = GetString(file, "filename")!,
            Sha1 = sha1,
            Sha512 = sha512,
            ProjectId = GetString(version, "project_id"),
            VersionId = GetString(version, "id"),
            VersionNumber = GetString(version, "version_number"),
            Dependencies = dependencies,
        };
    }

    private static string? GetString(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(propertyName, out var prop) &&
        prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;
}
