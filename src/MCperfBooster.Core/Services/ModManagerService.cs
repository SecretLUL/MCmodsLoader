using System.Collections.Concurrent;
using System.Security.Cryptography;
using MCperfBooster.Core.Constants;
using MCperfBooster.Core.Models;
using MCperfBooster.Core.Utils;

namespace MCperfBooster.Core.Services;

/// <summary>
/// Outcome of an install run. Every mod passed in ends up in exactly one bucket: installed/updated,
/// already up to date, failed, unavailable for the target version, or skipped.
/// </summary>
public record ModInstallResult
{
    public int SuccessCount => InstalledOrUpdatedCount + AlreadyUpToDateCount;
    public int InstalledOrUpdatedCount { get; init; }
    public int AlreadyUpToDateCount { get; init; }
    public int TotalCount { get; init; }
    public int FailedCount => Failures.Count;
    public List<string> FailedModNames => Failures.Select(f => f.ModName).ToList();
    public List<ModIssue> Failures { get; init; } = new();

    /// <summary>Mods Modrinth has no Fabric version of for the target Minecraft version (and which aren't installed compatibly).</summary>
    public List<string> UnavailableModNames { get; init; } = new();

    /// <summary>Mods not installed because a required curated dependency won't be present.</summary>
    public List<ModIssue> Skipped { get; init; } = new();

    /// <summary>Incompatible jars of unavailable mods that were renamed to "*.jar.disabled".</summary>
    public List<string> DisabledFileNames { get; init; } = new();

    public List<string> Warnings { get; init; } = new();

    public bool HasIssues =>
        Failures.Count > 0 || UnavailableModNames.Count > 0 || Skipped.Count > 0 || DisabledFileNames.Count > 0 || Warnings.Count > 0;
}

public interface IModManagerService
{
    List<ModDefinition> ScanModsDirectory(string modsDirectory, IReadOnlyList<ModDefinition>? template = null, string? mcVersion = null);
    Task<ModInstallResult> InstallOrUpdateModsAsync(string modsDirectory, string mcVersion, List<ModDefinition> mods, IProgress<string>? statusProgress = null, IProgress<double>? percentageProgress = null, CancellationToken ct = default);
}

public class ModManagerService : IModManagerService
{
    private const string DisabledSuffix = ".disabled";

    // Old jars are renamed to this (not "*.jar", so Fabric ignores them) before they are deleted
    private const string RemovedSuffix = ".mcmodsloader-old";

    // A verified download waits under this name until the old jars are out of the way
    private const string StagingSuffix = ".mcmodsloader-new";

    private readonly IModrinthService _modrinthService;

    // Project ids of curated mods learned in earlier runs, so the dependency check still works when
    // the projects lookup fails or the mod has no version for the current Minecraft version
    private readonly ConcurrentDictionary<string, string> _knownProjectIds = new(StringComparer.OrdinalIgnoreCase);

    // Every rename of the jar replacement goes through here, so tests can make single renames fail
    internal Action<string, string, bool> MoveFile { get; init; } = File.Move;

    public ModManagerService(IModrinthService modrinthService)
    {
        _modrinthService = modrinthService;
    }

    /// <summary>
    /// Decides whether a jar belongs to a mod: by its fabric.mod.json id if it has one, otherwise
    /// (and only then) by a strict file name match, see <see cref="FileNameMatchesMod"/>.
    /// </summary>
    public static bool IsJarOfMod(string jarFileName, FabricModMetadata? metadata, string fabricModId, string slug)
    {
        if (metadata != null)
        {
            return metadata.Id.Equals(fabricModId, StringComparison.OrdinalIgnoreCase) ||
                   metadata.Id.Equals(slug, StringComparison.OrdinalIgnoreCase);
        }

        return FileNameMatchesMod(jarFileName, fabricModId, slug);
    }

    /// <summary>
    /// True if the file name is the mod id or slug, followed by '-', '_' or '+', an optional "fabric-"/"fabric_"
    /// and/or "mc"/"v", and then a digit. "xaerominimap-fabric-1.21.4-25.2.0.jar" matches "xaerominimap", while
    /// "sodium-extra-…", "sodium-neoforge-…" or "xaerominimapfair-…" never match "sodium" / "xaerominimap".
    /// </summary>
    public static bool FileNameMatchesMod(string jarFileName, string fabricModId, string slug)
    {
        string name = Path.GetFileName(jarFileName).ToLowerInvariant();
        return StartsWithModKey(name, fabricModId) || StartsWithModKey(name, slug);
    }

    private static bool StartsWithModKey(string name, string key)
    {
        if (string.IsNullOrEmpty(key))
            return false;

        key = key.ToLowerInvariant();
        if (name.Length <= key.Length + 1 || !name.StartsWith(key, StringComparison.Ordinal))
            return false;

        if (name[key.Length] is not ('-' or '_' or '+'))
            return false;

        var rest = name.AsSpan(key.Length + 1);
        if (rest.StartsWith("fabric-") || rest.StartsWith("fabric_"))
            rest = rest[7..];

        if (rest.StartsWith("mc"))
            rest = rest[2..];
        else if (rest.StartsWith("v"))
            rest = rest[1..];

        return rest.Length > 0 && char.IsAsciiDigit(rest[0]);
    }

    public List<ModDefinition> ScanModsDirectory(string modsDirectory, IReadOnlyList<ModDefinition>? template = null, string? mcVersion = null)
    {
        var mods = (template ?? ModPresets.GetDefaultFpsModPack())
            .Select(m => m with
            {
                InstalledFileName = null,
                InstalledVersion = null,
                IsCompatible = true,
                TargetVersionNumber = null,
                DownloadUrl = null,
                TargetSha1 = null,
                TargetSha512 = null,
                DuplicateFileNames = Array.Empty<string>(),
                Status = "Missing"
            })
            .ToList();

        if (!Directory.Exists(modsDirectory))
        {
            return mods;
        }

        var jars = ReadJarIndex(modsDirectory);
        var claimed = new bool[jars.Length];

        foreach (var mod in mods)
        {
            var candidates = new List<(string FileName, FabricModMetadata? Meta, bool IsCompatible)>();
            for (int i = 0; i < jars.Length; i++)
            {
                var (fileName, meta) = jars[i];
                if (claimed[i] || !IsJarOfMod(fileName, meta, mod.FabricModId, mod.Slug))
                    continue;

                claimed[i] = true;
                candidates.Add((fileName, meta, ModCompatibilityChecker.IsCompatible(mcVersion, meta?.MinecraftDependency, meta?.Version, fileName)));
            }

            if (candidates.Count == 0)
                continue;

            // Several jars of one mod: keep the best (compatible first, then newest) and remember the rest
            var ordered = candidates
                .OrderByDescending(c => c.IsCompatible)
                .ThenByDescending(c => c.Meta?.Version ?? "", MinecraftVersionComparer.Instance)
                .ThenBy(c => c.FileName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var best = ordered[0];
            mod.InstalledFileName = best.FileName;
            mod.InstalledVersion = best.Meta?.Version ?? "present";
            mod.IsCompatible = best.IsCompatible;
            mod.Status = best.IsCompatible ? "Installed" : "Incompatible (needs update)";
            mod.DuplicateFileNames = ordered.Skip(1).Select(c => c.FileName).ToArray();
        }

        return mods;
    }

    public Task<ModInstallResult> InstallOrUpdateModsAsync(
        string modsDirectory,
        string mcVersion,
        List<ModDefinition> mods,
        IProgress<string>? statusProgress = null,
        IProgress<double>? percentageProgress = null,
        CancellationToken ct = default)
    {
        // Run on the thread pool so jar hashing and file I/O never block the caller's (UI) thread;
        // progress still reaches the caller through its IProgress<T> implementations.
        return Task.Run(() => InstallOrUpdateCoreAsync(modsDirectory, mcVersion, mods, statusProgress, percentageProgress, ct), ct);
    }

    private async Task<ModInstallResult> InstallOrUpdateCoreAsync(
        string modsDirectory,
        string mcVersion,
        List<ModDefinition> mods,
        IProgress<string>? statusProgress,
        IProgress<double>? percentageProgress,
        CancellationToken ct)
    {
        Directory.CreateDirectory(modsDirectory);
        RecoverLeftovers(modsDirectory);

        // Snapshot of the jars before anything changes. Cleanup works from it, so no task reads
        // a jar while another one deletes it.
        var jarIndex = new Dictionary<string, FabricModMetadata?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (fileName, meta) in ReadJarIndex(modsDirectory, includeNestedJars: true))
        {
            jarIndex[fileName] = meta;
        }

        var plans = mods.Select(m => new ModPlan(m)).ToList();
        var warnings = new List<string>();

        statusProgress?.Report("Resolving compatible mod versions on Modrinth...");
        await ResolveAsync(plans, modsDirectory, mcVersion, statusProgress, ct).ConfigureAwait(false);

        foreach (var plan in plans.Where(p => p.Outcome == PlanOutcome.Unavailable))
        {
            if (!DisableIncompatibleJars(plan, modsDirectory, mcVersion, jarIndex, $"not available for Minecraft {mcVersion}"))
                continue;

            if (plan.Mod.IsInstalled)
            {
                // A compatible jar is present and nothing newer exists for this version: keep it
                plan.Outcome = PlanOutcome.Kept;
            }
            else
            {
                plan.Mod.Status = "Not available for this version";
            }
        }

        await ApplyVersionPinsAsync(plans, modsDirectory, mcVersion, warnings, ct).ConfigureAwait(false);

        var dependencyInfo = await AnalyzeDependenciesAsync(plans, jarIndex, warnings, ct).ConfigureAwait(false);

        // A skipped mod's incompatible jar would stop Fabric from starting just like an unavailable one's
        foreach (var plan in plans.Where(p => p.Outcome == PlanOutcome.Skipped))
        {
            DisableIncompatibleJars(plan, modsDirectory, mcVersion, jarIndex, $"skipped ({plan.Reason})");
        }

        // Besides the installs, every mod whose chosen jar is compatible sheds its remaining duplicates
        // (incompatible ones of unavailable/skipped mods were disabled above)
        var work = plans
            .Where(p => p.Outcome == PlanOutcome.Install ||
                        (p.Outcome is PlanOutcome.UpToDate or PlanOutcome.Kept or PlanOutcome.LookupFailed or PlanOutcome.Skipped &&
                         p.Mod.IsInstalled &&
                         GetJarsOfMod(p.Mod, modsDirectory, jarIndex, p.Mod.InstalledFileName).Count > 0))
            .ToList();

        using var throttle = new SemaphoreSlim(3);
        int completed = 0;

        var tasks = work.Select(async plan =>
        {
            await throttle.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                ct.ThrowIfCancellationRequested();

                if (plan.Outcome == PlanOutcome.Install)
                    await InstallAsync(plan, modsDirectory, jarIndex, ct).ConfigureAwait(false);
                else
                    RemoveDuplicates(plan, modsDirectory, jarIndex);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                plan.Fail(ex.Message);
            }
            finally
            {
                throttle.Release();
                int finished = Interlocked.Increment(ref completed);
                percentageProgress?.Report((double)finished / work.Count * 100.0);
                statusProgress?.Report($"Downloading {plan.Mod.Name}... ({finished}/{work.Count})");
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);

        if (dependencyInfo != null)
            WarnAboutMissingDependencies(plans, dependencyInfo, modsDirectory, warnings);

        var result = new ModInstallResult
        {
            InstalledOrUpdatedCount = plans.Count(p => p.Outcome == PlanOutcome.Installed),
            AlreadyUpToDateCount = plans.Count(p => p.Outcome is PlanOutcome.UpToDate or PlanOutcome.Kept),
            TotalCount = mods.Count,
            Failures = plans.Where(p => p.Outcome is PlanOutcome.Failed or PlanOutcome.LookupFailed)
                .Select(p => new ModIssue(p.Mod.Name, p.Reason ?? "unknown error"))
                .OrderBy(i => i.ModName, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            UnavailableModNames = plans.Where(p => p.Outcome == PlanOutcome.Unavailable)
                .Select(p => p.Mod.Name)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            Skipped = plans.Where(p => p.Outcome == PlanOutcome.Skipped)
                .Select(p => new ModIssue(p.Mod.Name, p.Reason ?? "skipped"))
                .OrderBy(i => i.ModName, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            DisabledFileNames = plans.SelectMany(p => p.DisabledFiles).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList(),
            Warnings = warnings,
        };

        percentageProgress?.Report(100.0);
        statusProgress?.Report(
            $"Injection complete: {result.InstalledOrUpdatedCount} installed/updated, {result.AlreadyUpToDateCount} already up to date, " +
            $"{result.FailedCount} failed, {result.UnavailableModNames.Count} not available, {result.Skipped.Count} skipped.");

        return result;
    }

    /// <summary>
    /// Looks up every mod that doesn't carry a download URL yet and decides whether it needs installing.
    /// A pre-set <see cref="ModDefinition.DownloadUrl"/> means the caller already resolved the mod.
    /// </summary>
    private async Task ResolveAsync(List<ModPlan> plans, string modsDirectory, string mcVersion, IProgress<string>? progress, CancellationToken ct)
    {
        using var throttle = new SemaphoreSlim(4);
        int completed = 0;

        var tasks = plans.Select(async plan =>
        {
            var mod = plan.Mod;
            await throttle.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (string.IsNullOrEmpty(mod.DownloadUrl) || string.IsNullOrEmpty(mod.TargetVersionNumber))
                {
                    ModrinthVersionFile? file;
                    try
                    {
                        file = await _modrinthService.GetLatestVersionFileAsync(mod.Slug, mcVersion, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                    {
                        plan.Outcome = PlanOutcome.LookupFailed;
                        plan.Reason = $"Modrinth lookup failed: {ex.Message}";
                        mod.Status = $"Error: {plan.Reason}";
                        return;
                    }

                    if (file == null)
                    {
                        mod.DownloadUrl = null;
                        mod.TargetVersionNumber = null;
                        plan.Outcome = PlanOutcome.Unavailable;
                        return;
                    }

                    mod.DownloadUrl = file.Url;
                    mod.TargetVersionNumber = file.Filename;
                    mod.TargetSha1 = file.Sha1;
                    mod.TargetSha512 = file.Sha512;
                    plan.ProjectId = file.ProjectId;
                    plan.VersionId = file.VersionId;
                    plan.VersionNumber = file.VersionNumber;
                    plan.Dependencies = file.Dependencies;
                }

                if (mod.IsInstalled && await IsUpToDateAsync(mod, modsDirectory, ct).ConfigureAwait(false))
                {
                    plan.Outcome = PlanOutcome.UpToDate;
                    mod.Status = "Up to date";
                }
                else
                {
                    plan.Outcome = PlanOutcome.Install;
                    if (mod.IsInstalled)
                        mod.Status = "Update available";
                }
            }
            finally
            {
                throttle.Release();
                int current = Interlocked.Increment(ref completed);
                progress?.Report($"Resolved {current}/{plans.Count} mods on Modrinth...");
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    /// <summary>Up to date = SHA-1 of the installed jar equals the latest file's; without a hash the file names are compared.</summary>
    private static async Task<bool> IsUpToDateAsync(ModDefinition mod, string modsDirectory, CancellationToken ct)
    {
        string installedPath = PathInDirectory(modsDirectory, mod.InstalledFileName!);
        if (!string.IsNullOrEmpty(mod.TargetSha1) && File.Exists(installedPath))
        {
            try
            {
                var stream = new FileStream(installedPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, true);
                await using (stream.ConfigureAwait(false))
                {
                    byte[] hash = await SHA1.HashDataAsync(stream, ct).ConfigureAwait(false);
                    return Convert.ToHexString(hash).Equals(mod.TargetSha1, StringComparison.OrdinalIgnoreCase);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Unreadable (e.g. locked): fall back to the file name
            }
        }

        return string.Equals(mod.InstalledFileName, mod.TargetVersionNumber, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Fabric only loads "*.jar", so incompatible jars (incl. duplicates) of a mod that won't be updated in this
    /// run are renamed to "*.jar.disabled". Compatible jars are left alone. Returns false, and fails the plan,
    /// if a jar couldn't be renamed.
    /// </summary>
    private static bool DisableIncompatibleJars(ModPlan plan, string modsDirectory, string mcVersion, Dictionary<string, FabricModMetadata?> jarIndex, string context)
    {
        var mod = plan.Mod;
        foreach (var fileName in GetJarsOfMod(mod, modsDirectory, jarIndex, keepFileName: null))
        {
            bool isCompatible = fileName.Equals(mod.InstalledFileName, StringComparison.OrdinalIgnoreCase)
                ? mod.IsCompatible
                : ModCompatibilityChecker.IsCompatible(mcVersion, jarIndex[fileName]?.MinecraftDependency, jarIndex[fileName]?.Version, fileName);
            if (isCompatible)
                continue;

            try
            {
                string path = PathInDirectory(modsDirectory, fileName);
                File.Move(path, path + DisabledSuffix, overwrite: true);
                plan.DisabledFiles.Add(fileName + DisabledSuffix);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                plan.Fail($"{context}, and its incompatible file '{fileName}' could not be disabled: it {DescribeFileError(ex)}");
                return false;
            }

            if (fileName.Equals(mod.InstalledFileName, StringComparison.OrdinalIgnoreCase))
            {
                mod.InstalledFileName = null;
                mod.InstalledVersion = null;
                mod.IsCompatible = true;
            }
        }

        mod.DuplicateFileNames = mod.DuplicateFileNames
            .Where(d => !plan.DisabledFiles.Contains(d + DisabledSuffix, StringComparer.OrdinalIgnoreCase))
            .ToArray();
        return true;
    }

    /// <summary>
    /// Some mods are built against one exact build of another mod in the pack: Iris names the Sodium version it
    /// supports. When a mod that will be present pins a version of another resolved mod, that version is installed
    /// instead of the newest one, even if that means going back a version. A pin that can't be satisfied, or pins
    /// that disagree, keep the newest version and add a warning.
    /// </summary>
    private async Task ApplyVersionPinsAsync(List<ModPlan> plans, string modsDirectory, string mcVersion, List<string> warnings, CancellationToken ct)
    {
        var resolvedByProject = new Dictionary<string, ModPlan>(StringComparer.Ordinal);
        foreach (var plan in plans.Where(p => p.ProjectId != null && p.Outcome is PlanOutcome.Install or PlanOutcome.UpToDate))
            resolvedByProject.TryAdd(plan.ProjectId!, plan);

        var pinsByTarget = plans
            .Where(p => p.Outcome is PlanOutcome.Install or PlanOutcome.UpToDate)
            .SelectMany(p => p.Dependencies
                .Where(d => IsRequired(d) && d.VersionId != null &&
                            resolvedByProject.TryGetValue(d.ProjectId!, out var target) && target != p)
                .Select(d => (Pinner: p, Target: resolvedByProject[d.ProjectId!], VersionId: d.VersionId!)))
            .GroupBy(pin => pin.Target);

        foreach (var pins in pinsByTarget)
        {
            var target = pins.Key;
            var mod = target.Mod;
            string pinners = string.Join(", ", pins.Select(pin => pin.Pinner.Mod.Name).Distinct());
            var versionIds = pins.Select(pin => pin.VersionId).Distinct(StringComparer.Ordinal).ToList();

            if (versionIds.Count > 1)
            {
                warnings.Add($"{pinners} require different builds of {mod.Name}; the newest one is installed");
                continue;
            }

            if (string.Equals(versionIds[0], target.VersionId, StringComparison.Ordinal))
                continue;

            ModrinthVersionFile? file;
            try
            {
                file = await _modrinthService.GetVersionFileAsync(versionIds[0], mcVersion, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                warnings.Add($"Could not look up the {mod.Name} build that {pinners} requires: {ex.Message}");
                continue;
            }

            if (file == null)
            {
                warnings.Add($"The {mod.Name} build that {pinners} requires is not available for Minecraft {mcVersion}; the newest one is installed");
                continue;
            }

            mod.DownloadUrl = file.Url;
            mod.TargetVersionNumber = file.Filename;
            mod.TargetSha1 = file.Sha1;
            mod.TargetSha512 = file.Sha512;
            target.VersionId = file.VersionId;
            target.VersionNumber = file.VersionNumber;
            target.Dependencies = file.Dependencies;

            if (mod.IsInstalled && await IsUpToDateAsync(mod, modsDirectory, ct).ConfigureAwait(false))
            {
                target.Outcome = PlanOutcome.UpToDate;
                mod.Status = "Up to date";
            }
            else
            {
                target.Outcome = PlanOutcome.Install;
                if (mod.IsInstalled)
                    mod.Status = "Update available";
            }
        }
    }

    /// <summary>What the dependency check learned before the downloads; reused for the final check.</summary>
    private sealed class DependencyInfo
    {
        public Dictionary<string, ModrinthProject> ProjectsById { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, ModPlan> CuratedByProjectId { get; } = new(StringComparer.Ordinal);
    }

    private static bool IsRequired(ModrinthDependency d) =>
        d.ProjectId != null && d.DependencyType.Equals("required", StringComparison.OrdinalIgnoreCase);

    // Modrinth lists dependencies per version, not per loader: a multi-loader jar may require
    // e.g. a Forge-only library, which doesn't concern Fabric. Unknown projects are assumed to.
    private static bool ConcernsFabric(ModrinthDependency dep, DependencyInfo info) =>
        !info.ProjectsById.TryGetValue(dep.ProjectId!, out var project) ||
        project.Loaders is not { Count: > 0 } loaders ||
        loaders.Any(l => l.Equals("fabric", StringComparison.OrdinalIgnoreCase) || l.Equals("quilt", StringComparison.OrdinalIgnoreCase));

    // Fabric mod ids, with '-' and '_' treated alike
    private static string NormalizeModId(string id) => id.ToLowerInvariant().Replace('_', '-');

    private static bool IsModIdOf(string modId, ModDefinition mod) =>
        NormalizeModId(modId) == NormalizeModId(mod.FabricModId) || NormalizeModId(modId) == NormalizeModId(mod.Slug);

    /// <summary>
    /// Before the downloads: skips every mod about to be installed whose required curated dependency won't be
    /// present after this run, repeating until nothing changes. Returns null if no resolved mod has required
    /// dependencies. Never fails the run.
    /// </summary>
    private async Task<DependencyInfo?> AnalyzeDependenciesAsync(List<ModPlan> plans, Dictionary<string, FabricModMetadata?> jarIndex, List<string> warnings, CancellationToken ct)
    {
        // Only mods resolved in this run know their dependencies. Required deps that name only a version
        // already got their project id in ModrinthService; the rest can't be checked and are ignored.
        var requiredIds = plans
            .Where(p => p.Outcome is PlanOutcome.Install or PlanOutcome.UpToDate)
            .SelectMany(p => p.Dependencies.Where(IsRequired))
            .Select(d => d.ProjectId!)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (requiredIds.Count == 0)
            return null;

        foreach (var plan in plans)
        {
            if (plan.ProjectId == null && _knownProjectIds.TryGetValue(plan.Mod.Slug, out var known))
                plan.ProjectId = known;
        }

        // The lookup supplies titles, slugs and loaders of the dependencies plus the ids of curated mods not known yet
        var info = new DependencyInfo();
        var lookupIds = requiredIds.Concat(plans.Where(p => p.ProjectId == null).Select(p => p.Mod.Slug)).Distinct(StringComparer.Ordinal);
        try
        {
            foreach (var project in await _modrinthService.GetProjectsAsync(lookupIds, ct).ConfigureAwait(false))
            {
                info.ProjectsById.TryAdd(project.Id, project);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Carry on with the project ids already known
            warnings.Add($"Could not check mod dependencies: {ex.Message}");
        }

        foreach (var plan in plans)
        {
            plan.ProjectId ??= info.ProjectsById.Values.FirstOrDefault(pr =>
                pr.Slug.Equals(plan.Mod.Slug, StringComparison.OrdinalIgnoreCase) ||
                pr.Id.Equals(plan.Mod.Slug, StringComparison.Ordinal))?.Id;

            if (plan.ProjectId != null)
            {
                info.CuratedByProjectId.TryAdd(plan.ProjectId, plan);
                _knownProjectIds[plan.Mod.Slug] = plan.ProjectId;
            }
        }

        static bool IsPresentAfterRun(ModPlan p) =>
            p.Outcome is PlanOutcome.Install or PlanOutcome.UpToDate or PlanOutcome.Kept ||
            // Skipped and failed mods keep their old jar, which still counts if it's compatible
            (p.Outcome is PlanOutcome.Skipped or PlanOutcome.Failed or PlanOutcome.LookupFailed && p.Mod.IsInstalled);

        // A dependency the mod embeds itself. Judged by the currently installed jar: the new one can only be
        // inspected after its download, so a library that only the new version embeds isn't seen here.
        bool EmbedsMod(ModPlan plan, ModDefinition dependency) =>
            plan.Mod.InstalledFileName != null &&
            jarIndex.TryGetValue(plan.Mod.InstalledFileName, out var meta) &&
            meta?.NestedModIds is { } nested &&
            nested.Any(id => IsModIdOf(id, dependency));

        bool changed;
        do
        {
            changed = false;
            foreach (var plan in plans.Where(p => p.Outcome == PlanOutcome.Install))
            {
                foreach (var dep in plan.Dependencies.Where(d => IsRequired(d) && ConcernsFabric(d, info)))
                {
                    if (info.CuratedByProjectId.TryGetValue(dep.ProjectId!, out var target) &&
                        target != plan && !IsPresentAfterRun(target) && !EmbedsMod(plan, target.Mod))
                    {
                        plan.Outcome = PlanOutcome.Skipped;
                        plan.Reason = $"requires {target.Mod.Name}";
                        plan.Mod.Status = $"Skipped: {plan.Reason}";
                        changed = true;
                        break;
                    }
                }
            }
        }
        while (changed);

        return info;
    }

    /// <summary>
    /// After the downloads: warns about required dependencies of installed mods that are missing from the final
    /// mods folder, where a mod counts as present if any jar is it or embeds it (fabric.mod.json "jars").
    /// Dependencies outside the pack never cause a skip, since mods often embed them.
    /// </summary>
    private static void WarnAboutMissingDependencies(List<ModPlan> plans, DependencyInfo info, string modsDirectory, List<string> warnings)
    {
        var presentIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (_, meta) in ReadJarIndex(modsDirectory, includeNestedJars: true))
        {
            if (meta == null)
                continue;

            presentIds.Add(NormalizeModId(meta.Id));
            foreach (var nestedId in meta.NestedModIds ?? Array.Empty<string>())
                presentIds.Add(NormalizeModId(nestedId));
        }

        foreach (var plan in plans.Where(p => p.Outcome is PlanOutcome.Installed or PlanOutcome.UpToDate))
        {
            foreach (var dep in plan.Dependencies.Where(d => IsRequired(d) && ConcernsFabric(d, info)))
            {
                string title;
                bool isPresent;
                bool isExternal = false;

                if (info.CuratedByProjectId.TryGetValue(dep.ProjectId!, out var target))
                {
                    if (target == plan)
                        continue;

                    title = target.Mod.Name;
                    isPresent = presentIds.Contains(NormalizeModId(target.Mod.FabricModId)) || presentIds.Contains(NormalizeModId(target.Mod.Slug));
                }
                else if (info.ProjectsById.TryGetValue(dep.ProjectId!, out var project))
                {
                    // Matched by Modrinth slug against Fabric mod ids. They usually agree but not always
                    // (e.g. "architectury-api" vs. "architectury"), one more reason this is only a warning.
                    title = project.Title;
                    isPresent = presentIds.Contains(NormalizeModId(project.Slug));
                    isExternal = true;
                }
                else
                {
                    // Unknown project (e.g. the lookup failed): can't be judged
                    continue;
                }

                if (!isPresent)
                {
                    string notInPack = isExternal ? ", which is not part of the mod pack" : "";
                    warnings.Add($"{plan.Mod.Name} requires {title}{notInPack} – the game may not start until {title} is installed");
                }
            }
        }
    }

    private async Task InstallAsync(ModPlan plan, string modsDirectory, Dictionary<string, FabricModMetadata?> jarIndex, CancellationToken ct)
    {
        var mod = plan.Mod;

        string? fileName = SanitizeJarFileName(mod.TargetVersionNumber);
        if (fileName == null)
        {
            plan.Fail($"Modrinth returned an invalid file name '{mod.TargetVersionNumber}'");
            return;
        }

        string targetFilePath = Path.Combine(modsDirectory, fileName);
        string stagingPath = targetFilePath + StagingSuffix;

        // 1. Download and verify next to the target; nothing Fabric loads changes yet
        try
        {
            await _modrinthService.DownloadFileAsync(mod.DownloadUrl!, stagingPath, null, ct, mod.TargetSha512, mod.TargetSha1).ConfigureAwait(false);
        }
        catch
        {
            TryDelete(stagingPath);
            throw;
        }

        // 2. Every old jar of the mod moves out of the way, including a file that already has the target name
        var oldJars = GetJarsOfMod(mod, modsDirectory, jarIndex, keepFileName: null);
        if (File.Exists(targetFilePath) && !oldJars.Contains(fileName, StringComparer.OrdinalIgnoreCase))
            oldJars.Add(fileName);

        // On failure the jar that couldn't be renamed is still live, so the mod keeps a jar either way
        var (moved, error) = MoveJarsAway(modsDirectory, oldJars, "old file");
        if (error != null)
        {
            TryDelete(stagingPath);
            plan.Fail(error);
            return;
        }

        // 3. The new jar goes live; if it can't, the old ones come back
        try
        {
            MoveFile(stagingPath, targetFilePath, false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            string reason = $"new file '{fileName}' {DescribeFileError(ex)}";
            var (allRestored, note) = RestoreJars(moved);
            if (allRestored || moved.Any(j => File.Exists(j.Path)))
            {
                // An old jar is live again; any that stayed renamed are cleaned up on the next run
                TryDelete(stagingPath);
                plan.Fail(reason + note);
                return;
            }

            // None of the old jars came back: rather complete the swap than leave the mod without a jar
            try
            {
                MoveFile(stagingPath, targetFilePath, false);
            }
            catch (Exception retryEx) when (retryEx is IOException or UnauthorizedAccessException)
            {
                TryDelete(stagingPath);
                plan.Fail($"{reason}{note}; the old file is put back automatically on the next run");
                return;
            }
        }

        // 4. Only now are the old jars deleted
        DeleteMovedJars(moved);

        mod.InstalledFileName = fileName;
        mod.InstalledVersion = plan.VersionNumber ?? "Latest";
        mod.IsCompatible = true;
        mod.DuplicateFileNames = Array.Empty<string>();
        mod.Status = "Installed";
        plan.Outcome = PlanOutcome.Installed;
    }

    private void RemoveDuplicates(ModPlan plan, string modsDirectory, Dictionary<string, FabricModMetadata?> jarIndex)
    {
        var duplicates = GetJarsOfMod(plan.Mod, modsDirectory, jarIndex, keepFileName: plan.Mod.InstalledFileName);
        var (moved, error) = MoveJarsAway(modsDirectory, duplicates, "duplicate file");
        if (error != null)
        {
            plan.Fail(error);
            return;
        }

        DeleteMovedJars(moved);
        plan.Mod.DuplicateFileNames = Array.Empty<string>();
    }

    /// <summary>
    /// Jars of this mod that existed when the run started (previous install, duplicates, metadata id matches,
    /// strict file name matches for jars without metadata), except <paramref name="keepFileName"/>.
    /// A jar whose metadata names a different mod is never included.
    /// </summary>
    private static List<string> GetJarsOfMod(ModDefinition mod, string modsDirectory, Dictionary<string, FabricModMetadata?> jarIndex, string? keepFileName)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(mod.InstalledFileName))
            names.Add(Path.GetFileName(mod.InstalledFileName));
        foreach (var duplicate in mod.DuplicateFileNames)
            names.Add(Path.GetFileName(duplicate));
        foreach (var (fileName, meta) in jarIndex)
        {
            if (IsJarOfMod(fileName, meta, mod.FabricModId, mod.Slug))
                names.Add(fileName);
        }

        if (keepFileName != null)
            names.Remove(keepFileName);

        return names
            .Where(n => jarIndex.TryGetValue(n, out var meta) &&
                        (meta == null || IsJarOfMod(n, meta, mod.FabricModId, mod.Slug)) &&
                        File.Exists(PathInDirectory(modsDirectory, n)))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private readonly record struct MovedJar(string Path, string TempPath, FileAttributes Attributes);

    /// <summary>
    /// All or nothing: renames every jar out of Fabric's way (to "*.mcmodsloader-old"; a read-only flag is cleared
    /// for the rename and put back on the renamed file). If one can't be renamed, the others are renamed back
    /// and the reason is returned.
    /// </summary>
    private (List<MovedJar> Moved, string? Error) MoveJarsAway(string modsDirectory, IReadOnlyList<string> fileNames, string description)
    {
        var moved = new List<MovedJar>();
        foreach (var fileName in fileNames)
        {
            string path = PathInDirectory(modsDirectory, fileName);
            string tempPath = path + RemovedSuffix;
            FileAttributes attributes = default;
            try
            {
                attributes = File.GetAttributes(path);
                if (attributes.HasFlag(FileAttributes.ReadOnly))
                    File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);

                MoveFile(path, tempPath, true);
                moved.Add(new MovedJar(path, tempPath, attributes));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // This one stays where it is; put its original attributes back too
                TrySetAttributes(path, attributes);
                return (moved, $"{description} '{fileName}' {DescribeFileError(ex)}{RestoreJars(moved).Note}");
            }

            // Keeps the flag if the renamed file has to be restored later, even by the next run's recovery
            TrySetAttributes(tempPath, attributes);
        }

        return (moved, null);
    }

    /// <summary>
    /// Renames moved jars back with their original attributes. AllRestored is false if any jar couldn't be
    /// renamed back; Note describes such problems.
    /// </summary>
    private (bool AllRestored, string Note) RestoreJars(List<MovedJar> moved)
    {
        bool allRestored = true;
        var notes = new List<string>();
        foreach (var jar in moved)
        {
            try
            {
                MoveFile(jar.TempPath, jar.Path, false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                allRestored = false;
                notes.Add($"'{Path.GetFileName(jar.Path)}' could not be renamed back and is left as '{Path.GetFileName(jar.TempPath)}'");
                continue;
            }

            if (!TrySetAttributes(jar.Path, jar.Attributes))
                notes.Add($"the read-only flag of '{Path.GetFileName(jar.Path)}' could not be restored");
        }

        return (allRestored, notes.Count == 0 ? "" : $" ({string.Join("; ", notes)})");
    }

    private static bool TrySetAttributes(string path, FileAttributes attributes)
    {
        if (attributes == default)
            return true;

        try
        {
            if (File.GetAttributes(path) != attributes)
                File.SetAttributes(path, attributes);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Deletes renamed jars. A failure is harmless (Fabric ignores them) and retried on the next run.</summary>
    private static void DeleteMovedJars(List<MovedJar> moved)
    {
        foreach (var jar in moved)
        {
            TryDelete(jar.TempPath);
        }
    }

    /// <summary>
    /// Deals with what an earlier run left in the mods folder (top level only). Unfinished downloads
    /// ("*.mcmodsloader-new") are deleted. A renamed old jar ("*.mcmodsloader-old") is renamed back if its mod
    /// (by its fabric.mod.json id) has no live jar and the original name is free — e.g. after a crash in the
    /// middle of a replacement — and deleted otherwise. Without readable metadata its mod can't be identified,
    /// so it is deleted rather than risk a duplicate.
    /// </summary>
    private void RecoverLeftovers(string modsDirectory)
    {
        foreach (var staging in Directory.GetFiles(modsDirectory, "*" + StagingSuffix))
        {
            TryDelete(staging);
        }

        var leftovers = Directory.GetFiles(modsDirectory, "*" + RemovedSuffix)
            .Select(path => (Path: path, Meta: JarInspector.ReadFabricModJson(path)))
            .OrderByDescending(l => l.Meta?.Version ?? "", MinecraftVersionComparer.Instance)
            .ToList();
        if (leftovers.Count == 0)
            return;

        var liveJars = ReadJarIndex(modsDirectory).ToList();

        foreach (var (path, meta) in leftovers)
        {
            string originalPath = path[..^RemovedSuffix.Length];
            if (meta != null &&
                originalPath.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) &&
                !liveJars.Any(j => IsJarOfMod(j.FileName, j.Meta, meta.Id, meta.Id)) &&
                !File.Exists(originalPath))
            {
                try
                {
                    // Attributes travel with the file, so a read-only flag comes back too
                    MoveFile(path, originalPath, false);
                    liveJars.Add((Path.GetFileName(originalPath), meta));
                    continue;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Left as it is; tried again on the next run
                    continue;
                }
            }

            TryDelete(path);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var attributes = File.GetAttributes(path);
                if (attributes.HasFlag(FileAttributes.ReadOnly))
                    File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Still locked: retried on the next run
        }
    }

    private static string DescribeFileError(Exception ex) => ex is UnauthorizedAccessException
        ? "can't be changed (access denied) – check the file's permissions"
        : "is in use – close Minecraft and try again";

    /// <summary>Accepts only a plain "*.jar" file name, so a hostile name can't escape the mods directory.</summary>
    private static string? SanitizeJarFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) ||
            fileName != fileName.Trim() ||
            fileName.IndexOfAny(['/', '\\']) >= 0 ||
            fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            Path.GetFileName(fileName) != fileName ||
            fileName.StartsWith('.') ||
            fileName.Length <= ".jar".Length ||
            !fileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return fileName;
    }

    private static string PathInDirectory(string modsDirectory, string fileName) =>
        Path.Combine(modsDirectory, Path.GetFileName(fileName));

    private static (string FileName, FabricModMetadata? Meta)[] ReadJarIndex(string modsDirectory, bool includeNestedJars = false)
    {
        var jarFiles = Directory.GetFiles(modsDirectory, "*.jar")
            .Where(f => f.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        var jars = new (string FileName, FabricModMetadata? Meta)[jarFiles.Length];
        Parallel.For(0, jarFiles.Length, i =>
        {
            jars[i] = (Path.GetFileName(jarFiles[i]), JarInspector.ReadFabricModJson(jarFiles[i], includeNestedJars));
        });

        return jars;
    }

    private enum PlanOutcome
    {
        Pending,
        Install,
        Installed,
        UpToDate,
        Kept,
        Unavailable,
        LookupFailed,
        Skipped,
        Failed
    }

    /// <summary>Per-mod state of one install run. Only the task working on the mod touches it.</summary>
    private sealed class ModPlan
    {
        public ModPlan(ModDefinition mod)
        {
            Mod = mod;
        }

        public ModDefinition Mod { get; }
        public PlanOutcome Outcome { get; set; } = PlanOutcome.Pending;
        public string? Reason { get; set; }
        public string? ProjectId { get; set; }
        public string? VersionId { get; set; }
        public string? VersionNumber { get; set; }
        public IReadOnlyList<ModrinthDependency> Dependencies { get; set; } = Array.Empty<ModrinthDependency>();
        public List<string> DisabledFiles { get; } = new();

        public void Fail(string reason)
        {
            // A second problem (e.g. duplicate cleanup after a failed lookup) doesn't hide the first one
            Reason = Outcome is PlanOutcome.Failed or PlanOutcome.LookupFailed && Reason != null ? $"{Reason}; {reason}" : reason;
            Outcome = PlanOutcome.Failed;
            Mod.Status = $"Error: {Reason}";
        }
    }
}
