using System.Globalization;

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
/// GraalVM is switched off or cannot be used here. Never throws, cancelling through the token aside: whatever goes
/// wrong comes back in the outcome, so a finished injection is never reported as failed because of Java.
/// </summary>
public sealed class JavaSetupService
{
    private readonly IGraalVmService _graalVm;
    private readonly Func<SystemProfile> _detectSystem;

    /// <param name="detectSystem">What the arguments are sized for; replaceable so tests do not depend on the PC they run on.</param>
    public JavaSetupService(IGraalVmService graalVm, Func<SystemProfile>? detectSystem = null)
    {
        _graalVm = graalVm;
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
        if (JavaRuntimeCatalog.GetJavaMajor(mcVersion, minecraftPath) is not int javaMajor)
        {
            // Nothing is written: the profile keeps whatever its owner chose.
            return new JavaSetupOutcome(
                JavaRuntimeCatalog.GetJavaMajor(mcVersion) is null
                    ? "unchanged (Java is only set up for Minecraft 1.16.5 and newer)"
                    : "unchanged (this Minecraft version needs a newer Java than any GraalVM)",
                null, null);
        }

        SystemProfile system = _detectSystem();
        string hotSpotArgs = JvmArguments.Build(javaMajor, graalVm: false, system, distantHorizons);

        if (!useGraalVm)
        {
            // Profiles of every game version leave the runtimes before these are deleted, or one of them would
            // be left pointing at a Java that is gone. They get the plain arguments for their own game version.
            FabricService.DetachRuntimes(minecraftPath, _graalVm.RuntimesRoot,
                gameVersion => JavaRuntimeCatalog.GetJavaMajor(gameVersion) is int major
                    ? JvmArguments.Build(major, graalVm: false, system, distantHorizons)
                    : null);
            _graalVm.RemoveAll();

            return Apply(minecraftPath, mcVersion, null, hotSpotArgs, "the launcher's own Java (GraalVM is switched off)", null, null);
        }

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

    /// <summary>The way out of every GraalVM failure: the launcher's own Java, with the same memory.</summary>
    private static JavaSetupOutcome UseLauncherJava(string minecraftPath, string mcVersion, string hotSpotArgs, string problem) =>
        Apply(minecraftPath, mcVersion, null, hotSpotArgs, "the launcher's own Java", problem, null);

    /// <summary>
    /// Writes the settings to the profiles and sums up what Minecraft runs on. No profile to write them to is a
    /// problem of its own, added to the one there may be already; a profile that needed no change is not.
    /// </summary>
    private static JavaSetupOutcome Apply(string minecraftPath, string mcVersion, string? javawExe, string javaArgs,
        string runsOn, string? problem, string? note)
    {
        ProfileJavaUpdate update = FabricService.ApplyJavaSettings(minecraftPath, mcVersion, javawExe, javaArgs);
        if (update.Matched == 0)
        {
            problem = Join(problem,
                $"No Fabric profile of Minecraft {mcVersion} that uses this .minecraft folder was found, so its Java settings were not changed.");
        }

        string summary = DescribeHeap(javaArgs) is { } heap ? $"{runsOn}, {heap} of memory for Minecraft" : runsOn;
        return new JavaSetupOutcome(summary, problem, note);
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
