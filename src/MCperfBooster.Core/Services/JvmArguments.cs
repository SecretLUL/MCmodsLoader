namespace MCperfBooster.Core.Services;

/// <summary>What the JVM arguments are tuned to: the PC's memory and its logical processors.</summary>
public sealed record SystemProfile(long TotalMemoryBytes, int LogicalProcessors);

/// <summary>
/// The JVM arguments for the launcher profile. Every set was run on real GraalVM 17, 21
/// and 25 (exit code 0, no warnings); see <see cref="GraalVmService.TestArgumentsAsync"/>
/// for the check that guards a PC where one of them still fails.
/// </summary>
public static class JvmArguments
{
    // G1 tuned for short pauses. The experimental unlock has to come first, the tuning
    // flags below depend on it.
    private static readonly string[] CommonFlags =
    {
        "-XX:+UnlockExperimentalVMOptions", "-XX:+UseG1GC", "-XX:MaxGCPauseMillis=37", "-XX:G1NewSizePercent=23",
        "-XX:G1ReservePercent=20", "-XX:G1HeapRegionSize=16M", "-XX:SurvivorRatio=32", "-XX:G1MixedGCCountTarget=3",
        "-XX:G1HeapWastePercent=20", "-XX:MaxTenuringThreshold=1", "-XX:G1RSetUpdatingPauseTimePercent=0",
        "-XX:G1SATBBufferEnqueueingThresholdPercent=30", "-XX:G1ConcMarkStepDurationMillis=5.0", "-XX:GCTimeRatio=99",
        "-XX:+DisableExplicitGC", "-XX:+PerfDisableSharedMem",
    };

    // Worth their memory only where there is plenty: a pre-touched heap and a larger code cache.
    private static readonly string[] LargePcFlags =
    {
        "-XX:+AlwaysPreTouch", "-XX:ReservedCodeCacheSize=400M", "-XX:NonNMethodCodeHeapSize=12M",
        "-XX:ProfiledCodeHeapSize=194M", "-XX:NonProfiledCodeHeapSize=194M",
    };

    // For a Java this code has never been run on: only what every HotSpot accepts.
    private static readonly string[] UnknownJavaFlags = { "-XX:+UseG1GC", "-XX:MaxGCPauseMillis=37", "-XX:+DisableExplicitGC" };

    private const int ManyCoresForConcurrentMarking = 8;
    private const int ManyCoresForInliner = 12;

    private readonly record struct Heap(int InitialMegabytes, int MaxMegabytes, bool LargePc);

    public static SystemProfile DetectSystem() =>
        new(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes, Environment.ProcessorCount);

    /// <summary>
    /// The full argument set for a Java major version, space separated. <paramref name="graalVm"/> adds
    /// the one option only Graal understands; <paramref name="distantHorizons"/> asks for more heap.
    /// </summary>
    public static string Build(int javaMajor, bool graalVm, SystemProfile system, bool distantHorizons)
    {
        Heap heap = PickHeap(system, distantHorizons);
        var args = new List<string> { $"-Xms{Size(heap.InitialMegabytes)}", $"-Xmx{Size(heap.MaxMegabytes)}" };

        if (javaMajor is not (17 or 21 or 25))
        {
            args.AddRange(UnknownJavaFlags);
            return string.Join(' ', args);
        }

        args.AddRange(CommonFlags);
        if (heap.LargePc)
            args.AddRange(LargePcFlags);
        if (system.LogicalProcessors >= ManyCoresForConcurrentMarking)
            args.Add("-XX:InitiatingHeapOccupancyPercent=10");

        // Java 17 and 21 read the property as graal.*, 25 as jdk.graal.*. Each ignores the
        // other spelling, except that 25 still accepts the old one with a deprecation warning.
        if (graalVm && system.LogicalProcessors >= ManyCoresForInliner)
            args.Add($"-D{(javaMajor == 25 ? "jdk.graal" : "graal")}.TuneInlinerExploration=1");

        return string.Join(' ', args);
    }

    /// <summary>
    /// Heap and G1 only: the fallback when the full set fails the self-test on a PC, so Minecraft still
    /// gets its memory without any flag that could stop the JVM from starting.
    /// </summary>
    public static string BuildMinimal(SystemProfile system, bool distantHorizons)
    {
        Heap heap = PickHeap(system, distantHorizons);
        return $"-Xms{Size(heap.InitialMegabytes)} -Xmx{Size(heap.MaxMegabytes)} -XX:+UseG1GC";
    }

    private static Heap PickHeap(SystemProfile system, bool distantHorizons)
    {
        double gibibytes = system.TotalMemoryBytes / (1024.0 * 1024.0 * 1024.0);
        return gibibytes switch
        {
            < 6 => new Heap(1024, 2048, LargePc: false),
            < 12 => distantHorizons ? new Heap(2048, 4096, LargePc: false) : new Heap(1536, 3072, LargePc: false),
            < 24 => distantHorizons ? new Heap(6144, 6144, LargePc: true) : new Heap(4096, 4096, LargePc: true),
            _ => distantHorizons ? new Heap(8192, 8192, LargePc: true) : new Heap(6144, 6144, LargePc: true),
        };
    }

    private static string Size(int megabytes) => megabytes % 1024 == 0 ? $"{megabytes / 1024}G" : $"{megabytes}M";
}
