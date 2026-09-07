using MCmodsLoader.Core.Constants;
using MCmodsLoader.Core.Models;
using MCmodsLoader.Core.Utils;

namespace MCmodsLoader.Core.Services;

public record ModInstallResult
{
    public int SuccessCount => InstalledOrUpdatedCount + AlreadyUpToDateCount;
    public int InstalledOrUpdatedCount { get; init; }
    public int AlreadyUpToDateCount { get; init; }
    public int TotalCount { get; init; }
    public int FailedCount { get; init; }
    public List<string> FailedModNames { get; init; } = new();
}

public interface IModManagerService
{
    List<ModDefinition> ScanModsDirectory(string modsDirectory, IReadOnlyList<ModDefinition>? template = null);
    Task PrepareModrinthMetadataAsync(string mcVersion, List<ModDefinition> mods, IProgress<string>? progress = null);
    Task<ModInstallResult> InstallOrUpdateModsAsync(string modsDirectory, string mcVersion, List<ModDefinition> mods, IProgress<string>? statusProgress = null, IProgress<double>? percentageProgress = null, CancellationToken ct = default);
}

public class ModManagerService : IModManagerService
{
    private readonly IModrinthService _modrinthService;

    public ModManagerService(IModrinthService modrinthService)
    {
        _modrinthService = modrinthService;
    }

    public List<ModDefinition> ScanModsDirectory(string modsDirectory, IReadOnlyList<ModDefinition>? template = null)
    {
        var mods = (template ?? ModPresets.GetDefaultFpsModPack())
            .Select(m => m with
            {
                InstalledFileName = null,
                InstalledVersion = null,
                Status = "Missing"
            })
            .ToList();

        if (!Directory.Exists(modsDirectory))
        {
            return mods;
        }

        var jarFiles = Directory.GetFiles(modsDirectory, "*.jar");
        var matchedJars = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Pass 1: Parse fabric.mod.json inside each jar
        foreach (var jar in jarFiles)
        {
            var meta = JarInspector.ReadFabricModJson(jar);
            if (meta != null)
            {
                var mod = mods.FirstOrDefault(m =>
                    m.FabricModId.Equals(meta.Id, StringComparison.OrdinalIgnoreCase) ||
                    m.Slug.Equals(meta.Id, StringComparison.OrdinalIgnoreCase));

                if (mod != null && mod.InstalledFileName == null)
                {
                    mod.InstalledFileName = Path.GetFileName(jar);
                    mod.InstalledVersion = meta.Version;
                    mod.Status = "Installed";
                    matchedJars.Add(jar);
                }
            }
        }

        // Pass 2: Fallback matching by filename pattern for jars without standard fabric.mod.json
        foreach (var jar in jarFiles)
        {
            if (matchedJars.Contains(jar))
                continue;

            string fn = Path.GetFileNameWithoutExtension(jar).ToLowerInvariant();
            var mod = mods.FirstOrDefault(m =>
                m.InstalledFileName == null &&
                (fn.StartsWith(m.FabricModId.ToLowerInvariant()) || fn.StartsWith(m.Slug.ToLowerInvariant())));

            if (mod != null)
            {
                mod.InstalledFileName = Path.GetFileName(jar);
                mod.InstalledVersion = "present";
                mod.Status = "Installed";
                matchedJars.Add(jar);
            }
        }

        return mods;
    }

    public async Task PrepareModrinthMetadataAsync(string mcVersion, List<ModDefinition> mods, IProgress<string>? progress = null)
    {
        using var throttle = new SemaphoreSlim(4);
        int completed = 0;

        var tasks = mods.Select(async mod =>
        {
            await throttle.WaitAsync();
            try
            {
                var versionFile = await _modrinthService.GetLatestVersionFileAsync(mod.Slug, mcVersion);
                if (versionFile != null)
                {
                    mod.DownloadUrl = versionFile.Url;
                    mod.TargetVersionNumber = versionFile.Filename;
                    mod.FileSize = versionFile.Size;

                    if (mod.IsInstalled)
                    {
                        if (!string.Equals(mod.InstalledFileName, versionFile.Filename, StringComparison.OrdinalIgnoreCase))
                        {
                            mod.Status = "Update available";
                        }
                        else
                        {
                            mod.Status = "Up to date";
                        }
                    }
                    else
                    {
                        mod.Status = "Missing";
                    }
                }
                else
                {
                    mod.DownloadUrl = null;
                    mod.TargetVersionNumber = null;
                    if (!mod.IsInstalled)
                    {
                        mod.Status = "Not available for this version";
                    }
                }
            }
            finally
            {
                throttle.Release();
                int current = Interlocked.Increment(ref completed);
                progress?.Report($"Resolved {current}/{mods.Count} mods on Modrinth...");
            }
        });

        await Task.WhenAll(tasks);
    }

    public async Task<ModInstallResult> InstallOrUpdateModsAsync(
        string modsDirectory,
        string mcVersion,
        List<ModDefinition> mods,
        IProgress<string>? statusProgress = null,
        IProgress<double>? percentageProgress = null,
        CancellationToken ct = default)
    {
        if (!Directory.Exists(modsDirectory))
        {
            Directory.CreateDirectory(modsDirectory);
        }

        // Always ensure metadata is populated for target mcVersion
        bool hasMissingMetadata = mods.Any(m => m.DownloadUrl == null && (m.Status == "Missing" || m.Status == "Pending" || !m.IsInstalled));
        if (hasMissingMetadata)
        {
            statusProgress?.Report("Resolving compatible mod versions on Modrinth...");
            await PrepareModrinthMetadataAsync(mcVersion, mods, statusProgress);
        }

        var toInstall = mods.Where(m => m.DownloadUrl != null && (!m.IsInstalled || m.Status == "Update available" || m.Status == "Missing")).ToList();
        int alreadyUpToDate = mods.Count(m => m.IsInstalled && m.Status == "Up to date");

        int installedCount = 0;
        int failedCount = 0;
        var failedList = new List<string>();

        if (toInstall.Count == 0)
        {
            statusProgress?.Report($"All {mods.Count} performance mods are already up to date!");
            percentageProgress?.Report(100.0);
            return new ModInstallResult
            {
                InstalledOrUpdatedCount = 0,
                AlreadyUpToDateCount = alreadyUpToDate > 0 ? alreadyUpToDate : mods.Count(m => m.IsInstalled),
                TotalCount = mods.Count,
                FailedCount = 0
            };
        }

        for (int i = 0; i < toInstall.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var mod = toInstall[i];

            statusProgress?.Report($"Downloading {mod.Name}... ({i + 1}/{toInstall.Count})");

            try
            {
                if (string.IsNullOrEmpty(mod.DownloadUrl) || string.IsNullOrEmpty(mod.TargetVersionNumber))
                {
                    var file = await _modrinthService.GetLatestVersionFileAsync(mod.Slug, mcVersion);
                    if (file == null)
                    {
                        failedCount++;
                        failedList.Add(mod.Name);
                        mod.Status = "Not available for this version";
                        continue;
                    }
                    mod.DownloadUrl = file.Url;
                    mod.TargetVersionNumber = file.Filename;
                }

                string targetFilePath = Path.Combine(modsDirectory, mod.TargetVersionNumber);

                // Download safely first before touching existing files
                await _modrinthService.DownloadFileAsync(mod.DownloadUrl, targetFilePath, null, ct);

                // After download succeeds, clean up any previous/conflicting version of this mod
                if (!string.IsNullOrEmpty(mod.InstalledFileName) && !mod.InstalledFileName.Equals(mod.TargetVersionNumber, StringComparison.OrdinalIgnoreCase))
                {
                    string oldPath = Path.Combine(modsDirectory, mod.InstalledFileName);
                    if (File.Exists(oldPath))
                    {
                        try { File.Delete(oldPath); } catch { }
                    }
                }

                foreach (var existing in Directory.GetFiles(modsDirectory, "*.jar"))
                {
                    if (existing.Equals(targetFilePath, StringComparison.OrdinalIgnoreCase))
                        continue;

                    string fn = Path.GetFileName(existing).ToLowerInvariant();
                    if (fn.StartsWith(mod.FabricModId.ToLowerInvariant() + "-") ||
                        fn.StartsWith(mod.Slug.ToLowerInvariant() + "-") ||
                        fn.StartsWith(mod.FabricModId.ToLowerInvariant() + "_") ||
                        fn.StartsWith(mod.Slug.ToLowerInvariant() + "_"))
                    {
                        try { File.Delete(existing); } catch { }
                    }
                }

                mod.InstalledFileName = mod.TargetVersionNumber;
                mod.InstalledVersion = "Latest";
                mod.Status = "Installed";
                installedCount++;
            }
            catch (Exception ex)
            {
                failedCount++;
                failedList.Add(mod.Name);
                mod.Status = $"Error: {ex.Message}";
            }

            double pct = (double)(i + 1) / toInstall.Count * 100.0;
            percentageProgress?.Report(pct);
        }

        statusProgress?.Report($"Injection complete: {installedCount} installed/updated, {alreadyUpToDate} already up to date, {failedCount} failed.");
        return new ModInstallResult
        {
            InstalledOrUpdatedCount = installedCount,
            AlreadyUpToDateCount = alreadyUpToDate,
            TotalCount = mods.Count,
            FailedCount = failedCount,
            FailedModNames = failedList
        };
    }
}
