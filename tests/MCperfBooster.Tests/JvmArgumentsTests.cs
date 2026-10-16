using MCperfBooster.Core.Services;

namespace MCperfBooster.Tests;

public class JvmArgumentsTests
{
    private const long Gib = 1024L * 1024 * 1024;

    // The pieces of the argument sets, written out here independently of the code under test.
    private const string Common =
        "-XX:+UnlockExperimentalVMOptions -XX:+UseG1GC -XX:MaxGCPauseMillis=37 -XX:G1NewSizePercent=23 -XX:G1ReservePercent=20 " +
        "-XX:G1HeapRegionSize=16M -XX:SurvivorRatio=32 -XX:G1MixedGCCountTarget=3 -XX:G1HeapWastePercent=20 -XX:MaxTenuringThreshold=1 " +
        "-XX:G1RSetUpdatingPauseTimePercent=0 -XX:G1SATBBufferEnqueueingThresholdPercent=30 -XX:G1ConcMarkStepDurationMillis=5.0 " +
        "-XX:GCTimeRatio=99 -XX:+DisableExplicitGC -XX:+PerfDisableSharedMem";

    private const string LargePc =
        "-XX:+AlwaysPreTouch -XX:ReservedCodeCacheSize=400M -XX:NonNMethodCodeHeapSize=12M -XX:ProfiledCodeHeapSize=194M -XX:NonProfiledCodeHeapSize=194M";

    private const string ConcurrentMarking = "-XX:InitiatingHeapOccupancyPercent=10";

    private static readonly string[] ForbiddenOptions =
    {
        "G1ConcRSHotCardLimit", "G1ConcRefinementServiceIntervalMillis", "NmethodSweepActivity", "OptWriteMotion",
        "EnableJVMCI", "UseJVMCICompiler", "IgnoreUnrecognizedVMOptions", "ZGC",
    };

    public static IEnumerable<object[]> Matrix()
    {
        foreach (int java in new[] { 17, 21, 25 })
        foreach (int ramGib in new[] { 4, 8, 16, 32 })
        foreach (bool distantHorizons in new[] { false, true })
        foreach (int cpus in new[] { 4, 8, 16 })
        {
            yield return new object[] { java, ramGib, distantHorizons, cpus };
        }
    }

    private static string ExpectedHeap(int ramGib, bool distantHorizons) => (ramGib, distantHorizons) switch
    {
        (4, _) => "-Xms1G -Xmx2G",
        (8, false) => "-Xms1536M -Xmx3G",
        (8, true) => "-Xms2G -Xmx4G",
        (16, false) => "-Xms4G -Xmx4G",
        (16, true) => "-Xms6G -Xmx6G",
        (32, false) => "-Xms6G -Xmx6G",
        (32, true) => "-Xms8G -Xmx8G",
        _ => throw new ArgumentOutOfRangeException(nameof(ramGib)),
    };

    private static string Expected(int java, bool graal, int ramGib, bool distantHorizons, int cpus)
    {
        var parts = new List<string> { ExpectedHeap(ramGib, distantHorizons), Common };
        if (ramGib >= 16)
            parts.Add(LargePc);
        if (cpus >= 8)
            parts.Add(ConcurrentMarking);
        if (graal && cpus >= 12)
            parts.Add(java == 25 ? "-Djdk.graal.TuneInlinerExploration=1" : "-Dgraal.TuneInlinerExploration=1");
        return string.Join(' ', parts);
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public void Build_ForGraalVm_GivesTheExactSetForEveryMemoryClassAndCoreCount(int java, int ramGib, bool distantHorizons, int cpus)
    {
        var system = new SystemProfile(ramGib * Gib, cpus);

        Assert.Equal(Expected(java, graal: true, ramGib, distantHorizons, cpus), JvmArguments.Build(java, graalVm: true, system, distantHorizons));
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public void Build_ForHotSpot_IsTheSameSetWithoutTheGraalOption(int java, int ramGib, bool distantHorizons, int cpus)
    {
        var system = new SystemProfile(ramGib * Gib, cpus);

        string args = JvmArguments.Build(java, graalVm: false, system, distantHorizons);

        Assert.Equal(Expected(java, graal: false, ramGib, distantHorizons, cpus), args);
        Assert.DoesNotContain("TuneInlinerExploration", args);
    }

    [Fact]
    public void Build_WritesTheWholeSetOutInOrder()
    {
        // The biggest PC on the newest Java, as one literal string: heap, G1, large-PC flags, cores, Graal.
        Assert.Equal(
            "-Xms8G -Xmx8G -XX:+UnlockExperimentalVMOptions -XX:+UseG1GC -XX:MaxGCPauseMillis=37 -XX:G1NewSizePercent=23 " +
            "-XX:G1ReservePercent=20 -XX:G1HeapRegionSize=16M -XX:SurvivorRatio=32 -XX:G1MixedGCCountTarget=3 -XX:G1HeapWastePercent=20 " +
            "-XX:MaxTenuringThreshold=1 -XX:G1RSetUpdatingPauseTimePercent=0 -XX:G1SATBBufferEnqueueingThresholdPercent=30 " +
            "-XX:G1ConcMarkStepDurationMillis=5.0 -XX:GCTimeRatio=99 -XX:+DisableExplicitGC -XX:+PerfDisableSharedMem " +
            "-XX:+AlwaysPreTouch -XX:ReservedCodeCacheSize=400M -XX:NonNMethodCodeHeapSize=12M -XX:ProfiledCodeHeapSize=194M " +
            "-XX:NonProfiledCodeHeapSize=194M -XX:InitiatingHeapOccupancyPercent=10 -Djdk.graal.TuneInlinerExploration=1",
            JvmArguments.Build(25, graalVm: true, new SystemProfile(32 * Gib, 16), distantHorizons: true));

        // The smallest: just the heap and the G1 set.
        Assert.Equal(
            "-Xms1G -Xmx2G -XX:+UnlockExperimentalVMOptions -XX:+UseG1GC -XX:MaxGCPauseMillis=37 -XX:G1NewSizePercent=23 " +
            "-XX:G1ReservePercent=20 -XX:G1HeapRegionSize=16M -XX:SurvivorRatio=32 -XX:G1MixedGCCountTarget=3 -XX:G1HeapWastePercent=20 " +
            "-XX:MaxTenuringThreshold=1 -XX:G1RSetUpdatingPauseTimePercent=0 -XX:G1SATBBufferEnqueueingThresholdPercent=30 " +
            "-XX:G1ConcMarkStepDurationMillis=5.0 -XX:GCTimeRatio=99 -XX:+DisableExplicitGC -XX:+PerfDisableSharedMem",
            JvmArguments.Build(17, graalVm: true, new SystemProfile(4 * Gib, 4), distantHorizons: false));
    }

    [Theory]
    // Below 6 GiB Distant Horizons changes nothing.
    [InlineData(0L, false, "-Xms1G -Xmx2G")]
    [InlineData(6 * Gib - 1, true, "-Xms1G -Xmx2G")]
    // 6 to under 12 GiB.
    [InlineData(6 * Gib, false, "-Xms1536M -Xmx3G")]
    [InlineData(6 * Gib, true, "-Xms2G -Xmx4G")]
    [InlineData(12 * Gib - 1, false, "-Xms1536M -Xmx3G")]
    // 12 to under 24 GiB.
    [InlineData(12 * Gib, false, "-Xms4G -Xmx4G")]
    [InlineData(12 * Gib, true, "-Xms6G -Xmx6G")]
    [InlineData(24 * Gib - 1, false, "-Xms4G -Xmx4G")]
    // 24 GiB and up.
    [InlineData(24 * Gib, false, "-Xms6G -Xmx6G")]
    [InlineData(24 * Gib, true, "-Xms8G -Xmx8G")]
    [InlineData(256 * Gib, false, "-Xms6G -Xmx6G")]
    public void Build_PicksTheMemoryClassAtItsBoundaries(long totalBytes, bool distantHorizons, string expectedHeap)
    {
        string args = JvmArguments.Build(21, graalVm: true, new SystemProfile(totalBytes, 4), distantHorizons);

        Assert.StartsWith(expectedHeap + " -XX:+UnlockExperimentalVMOptions", args);
    }

    [Theory]
    [InlineData(12 * Gib - 1, false)]
    [InlineData(12 * Gib, true)]
    [InlineData(24 * Gib, true)]
    [InlineData(6 * Gib, false)]
    public void Build_AddsTheLargePcFlagsFromTwelveGibOn(long totalBytes, bool expected)
    {
        string args = JvmArguments.Build(21, graalVm: true, new SystemProfile(totalBytes, 4), distantHorizons: false);

        Assert.Equal(expected, args.Contains(LargePc, StringComparison.Ordinal));
        Assert.Equal(expected, args.Contains("-XX:+AlwaysPreTouch", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(1, false, false)]
    [InlineData(7, false, false)]
    [InlineData(8, true, false)]
    [InlineData(11, true, false)]
    [InlineData(12, true, true)]
    [InlineData(32, true, true)]
    public void Build_SwitchesOnTheCoreDependentOptionsAtEightAndTwelve(int cpus, bool hasConcurrentMarking, bool hasInliner)
    {
        var system = new SystemProfile(16 * Gib, cpus);

        string graal = JvmArguments.Build(21, graalVm: true, system, distantHorizons: false);
        string hotSpot = JvmArguments.Build(21, graalVm: false, system, distantHorizons: false);

        Assert.Equal(hasConcurrentMarking, graal.Contains(ConcurrentMarking, StringComparison.Ordinal));
        Assert.Equal(hasInliner, graal.Contains("-Dgraal.TuneInlinerExploration=1", StringComparison.Ordinal));
        Assert.Equal(hasConcurrentMarking, hotSpot.Contains(ConcurrentMarking, StringComparison.Ordinal));
        Assert.DoesNotContain("TuneInlinerExploration", hotSpot);
    }

    [Theory]
    [InlineData(17, "-Dgraal.TuneInlinerExploration=1", "jdk.graal")]
    [InlineData(21, "-Dgraal.TuneInlinerExploration=1", "jdk.graal")]
    [InlineData(25, "-Djdk.graal.TuneInlinerExploration=1", "-Dgraal.")]
    public void Build_UsesThePropertyNameTheJavaVersionUnderstands(int java, string expectedOption, string unwanted)
    {
        string args = JvmArguments.Build(java, graalVm: true, new SystemProfile(16 * Gib, 16), distantHorizons: false);

        Assert.Contains(expectedOption, args.Split(' '));
        Assert.DoesNotContain(unwanted, args);
    }

    [Theory]
    [InlineData(8)]
    [InlineData(11)]
    [InlineData(16)]
    [InlineData(22)]
    [InlineData(29)]
    [InlineData(0)]
    public void Build_ForAnUnknownJava_StaysWithWhatEveryHotSpotAccepts(int java)
    {
        foreach (bool graal in new[] { false, true })
        {
            Assert.Equal("-Xms4G -Xmx4G -XX:+UseG1GC -XX:MaxGCPauseMillis=37 -XX:+DisableExplicitGC",
                JvmArguments.Build(java, graal, new SystemProfile(16 * Gib, 16), distantHorizons: false));
            Assert.Equal("-Xms1G -Xmx2G -XX:+UseG1GC -XX:MaxGCPauseMillis=37 -XX:+DisableExplicitGC",
                JvmArguments.Build(java, graal, new SystemProfile(4 * Gib, 4), distantHorizons: true));
        }
    }

    [Theory]
    [InlineData(4, false, "-Xms1G -Xmx2G -XX:+UseG1GC")]
    [InlineData(4, true, "-Xms1G -Xmx2G -XX:+UseG1GC")]
    [InlineData(8, false, "-Xms1536M -Xmx3G -XX:+UseG1GC")]
    [InlineData(8, true, "-Xms2G -Xmx4G -XX:+UseG1GC")]
    [InlineData(16, false, "-Xms4G -Xmx4G -XX:+UseG1GC")]
    [InlineData(16, true, "-Xms6G -Xmx6G -XX:+UseG1GC")]
    [InlineData(32, false, "-Xms6G -Xmx6G -XX:+UseG1GC")]
    [InlineData(32, true, "-Xms8G -Xmx8G -XX:+UseG1GC")]
    public void BuildMinimal_IsJustTheHeapAndG1(int ramGib, bool distantHorizons, string expected)
    {
        Assert.Equal(expected, JvmArguments.BuildMinimal(new SystemProfile(ramGib * Gib, 16), distantHorizons));
    }

    [Fact]
    public void NoSetEverContainsAnOptionThatBreaksOrDoesNothing()
    {
        var all = new List<string>();
        foreach (int java in new[] { 8, 17, 21, 25, 99 })
        foreach (bool graal in new[] { false, true })
        foreach (long ram in new[] { 0L, 4 * Gib, 8 * Gib, 16 * Gib, 64 * Gib })
        foreach (int cpus in new[] { 1, 8, 16, 64 })
        foreach (bool distantHorizons in new[] { false, true })
        {
            var system = new SystemProfile(ram, cpus);
            all.Add(JvmArguments.Build(java, graal, system, distantHorizons));
            all.Add(JvmArguments.BuildMinimal(system, distantHorizons));
        }

        foreach (string args in all)
        {
            foreach (string forbidden in ForbiddenOptions)
                Assert.DoesNotContain(forbidden, args);
        }
    }

    [Fact]
    public void EverySetIsCleanlySeparatedAndHasNoDuplicates()
    {
        foreach (object[] row in Matrix())
        {
            var system = new SystemProfile((int)row[1] * Gib, (int)row[3]);
            string args = JvmArguments.Build((int)row[0], graalVm: true, system, (bool)row[2]);

            Assert.Equal(args.Trim(), args);
            Assert.DoesNotContain("  ", args);
            string[] tokens = args.Split(' ');
            Assert.Equal(tokens.Length, tokens.Distinct().Count());
            Assert.Equal(2, tokens.Count(t => t.StartsWith("-Xms") || t.StartsWith("-Xmx")));
        }
    }

    [Fact]
    public void DetectSystem_ReportsThisPc()
    {
        SystemProfile system = JvmArguments.DetectSystem();

        Assert.True(system.TotalMemoryBytes > 256L * 1024 * 1024, $"Detected only {system.TotalMemoryBytes} bytes");
        Assert.Equal(Environment.ProcessorCount, system.LogicalProcessors);
    }
}
