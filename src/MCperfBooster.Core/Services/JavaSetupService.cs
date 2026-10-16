using System.Globalization;
using System.Security;

namespace MCperfBooster.Core.Services;

/// <summary>
/// What <see cref="JavaSetupService.SetUpAsync"/> did. <see cref="Summary"/> says what Minecraft runs on now, as a
/// line for the report; <see cref="Problem"/> is something that went wrong and that the user should know about;
/// <see cref="Note"/> is a remark that is no problem. Both are null when there is nothing to say.
/// </summary>
public sealed record JavaSetupOutcome(string Summary, string? Problem, string? Note);

/// <summary>
/// Puts the Fabric profiles of a game version on the right Java after an injection: the downloaded Oracle GraalVM
/// with tuned arguments, or the launcher's own Java (with the same tuning, minus the one Graal-only option) when
/// GraalVM is switched off or cannot be used here. A GraalVM also gets the graphics card the user picked in Windows
/// for the launcher's Java (<see cref="GpuPreferenceService"/>), and loses it again with the runtimes. Never throws,
/// cancelling through the token aside: whatever goes wrong comes back in the outcome, so a finished injection is
/// never reported as failed because of Java.
/// </summary>
public sealed class JavaSetupService
{
    private readonly IGraalVmService _graalVm;
    private readonly GpuPreferenceService _gpuPreferences;
    private readonly Func<SystemProfile> _detectSystem;

    /// <param name="gpuPreferences">Windows' graphics card settings; replaceable so tests never touch the registry.</param>
    /// <param name="detectSystem">What the arguments are sized for; replaceable so tests do not depend on the PC they run on.</param>
    public JavaSetupService(IGraalVmService graalVm, IGpuPreferenceStore gpuPreferences, Func<SystemProfile>? detectSystem = null)
    {
        _graalVm = graalVm;

        // Next to the runtimes, which are folders of their own: removing them leaves the record in place.
        _gpuPreferences = new GpuPreferenceService(gpuPreferences, Path.Combine(graalVm.RuntimesRoot, GpuPreferenceService.RecordFileName));
        _detectSystem = detectSystem ?? JvmArguments.DetectSystem;
    }

    public async Task<JavaSetupOutcome> SetUpAsync(string minecraftPath, string mcVersion, bool useGraalVm, bool distantHorizons,
        IProgress<RuntimeDownloadProgress>? progress = null, CancellationToken ct = default)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            return await SetUpCoreAsync(minecraftPath, mcVersion, useGraalVm, distantHorizons, progress, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new JavaSetupOutcome("could not be set up", $"Setting up Java failed: {ex.Message}", null);
        }
    }

    private async Task<JavaSetupOutcome> SetUpCoreAsync(string minecraftPath, string mcVersion, bool useGraalVm, bool distantHorizons,
        IProgress<RuntimeDownloadProgress>? progress, CancellationToken ct)
    {
        SystemProfile system = _detectSystem();

        // Switched off, GraalVM goes whichever version is selected: the switch promised that, and the profiles
        // of other versions may be the ones still running on it.
        string? cleanupProblem = useGraalVm ? null : RemoveGraalVm(minecraftPath, system, distantHorizons);

        if (JavaRuntimeCatalog.GetJavaMajor(mcVersion, minecraftPath) is not int javaMajor)
        {
            // Nothing is written: the profile keeps whatever its owner chose.
            return new JavaSetupOutcome(
                JavaRuntimeCatalog.GetJavaMajor(mcVersion) is null
                    ? "unchanged (Java is only set up for Minecraft 1.16.5 and newer)"
                    : "unchanged (this Minecraft version needs a newer Java than any GraalVM)",
                cleanupProblem, null);
        }

        // For the launcher's own Java, which is not always the one GraalVM stands in for: 1.16.5 runs on Java 8 there.
        string hotSpotArgs = LauncherJavaArgs(mcVersion, minecraftPath, system, distantHorizons);

        if (!useGraalVm)
            return Apply(minecraftPath, mcVersion, null, hotSpotArgs, "the launcher's own Java (GraalVM is switched off)", cleanupProblem, null);

        RuntimeInstallResult install = await _graalVm.EnsureInstalledAsync(javaMajor, progress, ct).ConfigureAwait(false);
        if (install.Runtime is not { } runtime)
        {
            return UseLauncherJava(minecraftPath, mcVersion, hotSpotArgs,
                $"GraalVM could not be set up: {install.Error} Minecraft uses the launcher's own Java for now; the next injection tries again.");
        }

        // A runtime next to an error is the one that was installed before: an update failed, the old one still works.
        string? note = install.Notice;
        if (install.Error is { } error)
            note = Join(note, $"{error} The installed GraalVM {runtime.Version} is used.");

        string javaArgs = JvmArguments.Build(javaMajor, graalVm: true, system, distantHorizons);
        ArgumentTestResult test = await _graalVm.TestArgumentsAsync(runtime, javaArgs, ct).ConfigureAwait(false);
        if (!test.Ok)
        {
            // One flag may be what this PC rejects. The minimal set tells that apart from a GraalVM that does not run at all.
            string minimalArgs = JvmArguments.BuildMinimal(system, distantHorizons);
            ArgumentTestResult minimalTest = await _graalVm.TestArgumentsAsync(runtime, minimalArgs, ct).ConfigureAwait(false);
            if (!minimalTest.Ok)
            {
                // The notes above are about a runtime that is not going to be used.
                return UseLauncherJava(minecraftPath, mcVersion, hotSpotArgs,
                    $"GraalVM {runtime.Version} did not start on this PC ({Describe(minimalTest)}). Minecraft uses the launcher's own Java.");
            }

            javaArgs = minimalArgs;
            note = Join(note, $"The tuned Java arguments did not start on this PC ({Describe(test)}); a minimal safe set is used.");
        }

        return Apply(minecraftPath, mcVersion, runtime.JavawExe, javaArgs, $"Oracle GraalVM {runtime.Version}", null, note);
    }

    /// <summary>
    /// Takes every profile off the downloaded runtimes, onto the launcher's own Java with arguments for its game
    /// version, and only then deletes the runtimes and the graphics card they were given. Returns a problem when a
    /// profile file could not be changed: GraalVM then stays, card and all, since a profile may still point at it,
    /// and deleting it would leave that profile with a Java that is gone.
    /// </summary>
    private string? RemoveGraalVm(string minecraftPath, SystemProfile system, bool distantHorizons)
    {
        ProfileJavaUpdate detach = FabricService.DetachRuntimes(minecraftPath, _graalVm.RuntimesRoot,
            gameVersion => LauncherJavaArgs(gameVersion, minecraftPath, system, distantHorizons));
        if (detach.Error is { } error)
        {
            // With no GraalVM on the disk there is nothing that was kept; a profile file that cannot be written
            // is reported anyway where the selected version's settings are written.
            return HasRuntimes()
                ? $"GraalVM was kept, because the launcher profiles could not all be switched back to the launcher's own Java ({error})."
                : null;
        }

        _graalVm.RemoveAll();

        // Never throws; what Windows refuses is tried again on the next injection without GraalVM.
        _gpuPreferences.RemoveCarriedOver();
        return null;
    }

    private bool HasRuntimes()
    {
        try
        {
            return Directory.Exists(_graalVm.RuntimesRoot) && Directory.EnumerateDirectories(_graalVm.RuntimesRoot, "graalvm-*").Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Cannot look: better to say too much than to hide that GraalVM may still be there.
            return true;
        }
    }

    /// <summary>
    /// Arguments for the Java the launcher itself starts a game version with. For Java 8 and 16, which this app
    /// never tested its tuning on, that is only the memory and G1, which every Java takes.
    /// </summary>
    private static string LauncherJavaArgs(string mcVersion, string minecraftPath, SystemProfile system, bool distantHorizons) =>
        JvmArguments.Build(JavaRuntimeCatalog.GetLauncherJavaMajor(mcVersion, minecraftPath), graalVm: false, system, distantHorizons);

    /// <summary>The way out of every GraalVM failure: the launcher's own Java, with the same memory.</summary>
    private JavaSetupOutcome UseLauncherJava(string minecraftPath, string mcVersion, string hotSpotArgs, string problem) =>
        Apply(minecraftPath, mcVersion, null, hotSpotArgs, "the launcher's own Java", problem, null);

    /// <summary>
    /// Writes the settings to the profiles and sums up what Minecraft runs on. A profile file that could not be
    /// written, or no profile to write to, is a problem of its own, added to the one there may be already; a
    /// profile that needed no change is not. Once a profile runs on <paramref name="javawExe"/>, that one gets
    /// the graphics card of the launcher's Java.
    /// </summary>
    private JavaSetupOutcome Apply(string minecraftPath, string mcVersion, string? javawExe, string javaArgs,
        string runsOn, string? problem, string? note)
    {
        ProfileJavaUpdate update = FabricService.ApplyJavaSettings(minecraftPath, mcVersion, javawExe, javaArgs);
        if (update.Error is { } error && update.Matched == 0)
        {
            problem = Join(problem, $"The Java settings could not be written to the launcher profiles ({error}).");
        }
        else if (update.Error is { } otherFileError)
        {
            // The profile the launcher shows was set up; only the other profile file (often an old one of the
            // Microsoft Store launcher) is unreadable or locked.
            note = Join(note, $"One launcher profile file could not be read or written and was left as it was ({otherFileError}).");
        }
        else if (update.Matched == 0)
        {
            problem = Join(problem,
                $"No Fabric profile of Minecraft {mcVersion} that uses this .minecraft folder was found, so its Java settings were not changed.");
        }

        if (javawExe != null && update.Matched > 0 && CarryOverGpuPreference(minecraftPath, mcVersion, javawExe) is { } gpuNote)
            note = Join(note, gpuNote);

        string summary = DescribeHeap(javaArgs) is { } heap ? $"{runsOn}, {heap} of memory for Minecraft" : runsOn;
        return new JavaSetupOutcome(summary, problem, note);
    }

    /// <summary>
    /// Gives <paramref name="javawExe"/> the graphics card the user picked in Windows for the launcher's Java: first
    /// for the one the launcher starts this game version with, which the game ran on until now, then for the others,
    /// newest first. Returns a note for the report when a card was carried over or when that failed; the Java is set
    /// up either way.
    /// </summary>
    private string? CarryOverGpuPreference(string minecraftPath, string mcVersion, string javawExe)
    {
        try
        {
            IEnumerable<string> launcherJavas = FabricService.GetLauncherJavawPaths(
                minecraftPath, JavaRuntimeCatalog.GetLauncherJavaComponent(mcVersion, minecraftPath));

            return _gpuPreferences.CarryOver(javawExe, launcherJavas) is { } choice
                ? $"Windows' graphics preference for the launcher's Java ({GpuPreferenceService.Describe(choice)}) now applies to GraalVM too."
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            return $"Windows' graphics preference for the launcher's Java could not be carried over to GraalVM ({ex.Message}). " +
                   $"If Minecraft runs on the wrong graphics card, pick one for {javawExe} under Settings > System > Display > Graphics.";
        }
    }

    /// <summary>
    /// The memory the last -Xmx names, as text: whole gigabytes as "4 GB" (also when written "3072M"), anything
    /// else in megabytes ("1536 MB"). Null when the arguments name none.
    /// </summary>
    internal static string? DescribeHeap(string javaArgs)
    {
        string? option = javaArgs.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault(token => token.StartsWith("-Xmx", StringComparison.Ordinal));
        if (option is not { Length: > 5 })
            return null;

        char unit = char.ToUpperInvariant(option[^1]);
        if (unit is not ('G' or 'M') ||
            !long.TryParse(option.AsSpan(4, option.Length - 5), NumberStyles.None, CultureInfo.InvariantCulture, out long amount))
            return null;

        long megabytes = unit == 'G' ? amount * 1024 : amount;
        return megabytes % 1024 == 0 ? $"{megabytes / 1024} GB" : $"{megabytes} MB";
    }

    // The JVM's own words, or its exit code when it said nothing, so a message never ends in empty brackets.
    private static string Describe(ArgumentTestResult result) =>
        string.IsNullOrWhiteSpace(result.Output) ? $"exit code {result.ExitCode}" : result.Output.Trim();

    private static string Join(string? first, string second) => first == null ? second : $"{first} {second}";
}
