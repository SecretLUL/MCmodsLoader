using System.Text.Json;
using MCperfBooster.Core.Services;

namespace MCperfBooster.Tests;

/// <summary>
/// A GraalVM service that answers from the test and writes down what it was asked, in order:
/// "ensure 21", "test &lt;arguments&gt;", "remove-all". Anything the test did not set up fails loudly.
/// </summary>
internal sealed class FakeGraalVmService : IGraalVmService
{
    public FakeGraalVmService(string runtimesRoot) => RuntimesRoot = runtimesRoot;

    public string RuntimesRoot { get; }

    public List<string> Calls { get; } = new();

    public Func<int, IProgress<RuntimeDownloadProgress>?, CancellationToken, Task<RuntimeInstallResult>> OnEnsure { get; set; } =
        (major, _, _) => throw new InvalidOperationException($"The test did not expect EnsureInstalledAsync({major}).");

    public Func<InstalledRuntime, string, ArgumentTestResult> OnTest { get; set; } =
        (_, args) => throw new InvalidOperationException($"The test did not expect TestArgumentsAsync({args}).");

    public Action OnRemoveAll { get; set; } = () => { };

    public InstalledRuntime? GetInstalled(int javaMajor)
    {
        Calls.Add($"installed {javaMajor}");
        return null;
    }

    public Task<RuntimeInstallResult> EnsureInstalledAsync(int javaMajor, IProgress<RuntimeDownloadProgress>? progress = null, CancellationToken ct = default)
    {
        Calls.Add($"ensure {javaMajor}");
        return OnEnsure(javaMajor, progress, ct);
    }

    public Task<ArgumentTestResult> TestArgumentsAsync(InstalledRuntime runtime, string javaArgs, CancellationToken ct = default)
    {
        Calls.Add($"test {javaArgs}");
        return Task.FromResult(OnTest(runtime, javaArgs));
    }

    public void RemoveAll()
    {
        Calls.Add("remove-all");
        OnRemoveAll();
    }
}

public class JavaSetupServiceTests : IDisposable
{
    private const long Gib = 1024L * 1024 * 1024;
    private const string McVersion = "1.21.4";
    private const string NoProfileProblem =
        "No Fabric profile of Minecraft 1.21.4 that uses this .minecraft folder was found, so its Java settings were not changed.";

    // 16 GiB give Minecraft 4 GB, and 16 logical processors switch every optional flag on.
    private static readonly SystemProfile Pc = new(16 * Gib, 16);
    private static readonly ArgumentTestResult Ok = new(true, 0, "openjdk version \"21.0.12\"");

    private readonly string _base = TempFolders.Create("javasetup");
    private readonly string _mc;
    private readonly FakeGraalVmService _graalVm;
    private readonly FakeGpuPreferenceStore _gpu = new();

    public JavaSetupServiceTests()
    {
        _mc = Path.Combine(_base, ".minecraft");
        Directory.CreateDirectory(_mc);
        _graalVm = new FakeGraalVmService(Path.Combine(_base, "runtimes"));
    }

    public void Dispose() => TempFolders.Delete(_base);

    private string ProfilesFile => Path.Combine(_mc, "launcher_profiles.json");

    private static string Esc(string value) => JsonEncodedText.Encode(value).ToString();

    private static string Profile(string key, string lastVersionId, string? javaDir = null, string? javaArgs = null, string? gameDir = null)
    {
        var fields = new List<string> { $"\"lastVersionId\": \"{Esc(lastVersionId)}\"" };
        if (javaDir != null) fields.Add($"\"javaDir\": \"{Esc(javaDir)}\"");
        if (javaArgs != null) fields.Add($"\"javaArgs\": \"{Esc(javaArgs)}\"");
        if (gameDir != null) fields.Add($"\"gameDir\": \"{Esc(gameDir)}\"");
        return $"\"{Esc(key)}\": {{ {string.Join(", ", fields)} }}";
    }

    private void WriteProfiles(params string[] profiles) =>
        File.WriteAllText(ProfilesFile, "{ \"profiles\": { " + string.Join(", ", profiles) + " }, \"version\": 3 }");

    private JsonElement ReadProfile(string key)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(ProfilesFile));
        return doc.RootElement.GetProperty("profiles").GetProperty(key).Clone();
    }

    private static string? Str(JsonElement profile, string property) =>
        profile.TryGetProperty(property, out var value) ? value.GetString() : null;

    private const string FabricKey = "fabric-loader-1.21.4";

    /// <summary>The profile the app registers for the game version, with the launcher's modest default arguments.</summary>
    private void WriteDefaultProfile(string? javaDir = null, string? javaArgs = "-Xmx2G") =>
        WriteProfiles(Profile(FabricKey, "fabric-loader-0.17.2-1.21.4", javaDir, javaArgs));

    private InstalledRuntime Runtime(int major, string version)
    {
        string home = Path.Combine(_graalVm.RuntimesRoot, $"graalvm-{major}");
        return new InstalledRuntime(major, home, Path.Combine(home, "bin", "java.exe"), Path.Combine(home, "bin", "javaw.exe"), version, "sha");
    }

    private static Task<RuntimeInstallResult> Installed(InstalledRuntime runtime, string? error = null, string? notice = null) =>
        Task.FromResult(new RuntimeInstallResult(runtime, Downloaded: false, error, notice));

    private static Task<RuntimeInstallResult> NotInstalled(string error) =>
        Task.FromResult(new RuntimeInstallResult(null, Downloaded: false, error, null));

    private Task<JavaSetupOutcome> SetUp(bool useGraalVm, bool distantHorizons = false, string mcVersion = McVersion,
        SystemProfile? system = null, IProgress<RuntimeDownloadProgress>? progress = null, CancellationToken ct = default)
    {
        var service = new JavaSetupService(_graalVm, _gpu, () => system ?? Pc);
        return service.SetUpAsync(_mc, mcVersion, useGraalVm, distantHorizons, progress, ct);
    }

    private static string GraalArgs(bool distantHorizons = false, SystemProfile? system = null) =>
        JvmArguments.Build(21, graalVm: true, system ?? Pc, distantHorizons);

    private static string HotSpotArgs(int javaMajor = 21, bool distantHorizons = false, SystemProfile? system = null) =>
        JvmArguments.Build(javaMajor, graalVm: false, system ?? Pc, distantHorizons);

    // ---- GraalVM on ------------------------------------------------------

    [Fact]
    public async Task SetUpAsync_WithGraalVm_PointsTheProfileAtTheRuntimeWithTheTunedArguments()
    {
        WriteDefaultProfile();
        var runtime = Runtime(21, "21.0.12");
        _graalVm.OnEnsure = (_, _, _) => Task.FromResult(new RuntimeInstallResult(runtime, Downloaded: true, null, null));
        _graalVm.OnTest = (_, _) => Ok;

        JavaSetupOutcome outcome = await SetUp(useGraalVm: true);

        Assert.Equal(new JavaSetupOutcome("Oracle GraalVM 21.0.12, 4 GB of memory for Minecraft", null, null), outcome);
        var profile = ReadProfile(FabricKey);
        Assert.Equal(runtime.JavawExe, Str(profile, "javaDir"));
        Assert.Equal(GraalArgs(), Str(profile, "javaArgs"));
        // The set that was tried is the Graal one, and nothing is deleted or detached on the way.
        Assert.Contains("-Dgraal.TuneInlinerExploration=1", GraalArgs());
        Assert.Equal(new[] { "ensure 21", $"test {GraalArgs()}" }, _graalVm.Calls);
    }

    [Fact]
    public async Task SetUpAsync_WithGraalVm_TestsTheRuntimeThatWasReturned()
    {
        WriteDefaultProfile();
        var runtime = Runtime(21, "21.0.12");
        InstalledRuntime? tested = null;
        _graalVm.OnEnsure = (_, _, _) => Installed(runtime);
        _graalVm.OnTest = (candidate, _) =>
        {
            tested = candidate;
            return Ok;
        };

        await SetUp(useGraalVm: true);

        Assert.Same(runtime, tested);
    }

    [Theory]
    [InlineData("1.16.5", 17)]
    [InlineData("1.20.4", 17)]
    [InlineData("1.20.5", 21)]
    [InlineData("1.21.4", 21)]
    [InlineData("26.1", 25)]
    public async Task SetUpAsync_AsksForTheJavaOfTheGameVersion(string mcVersion, int javaMajor)
    {
        WriteProfiles(Profile("fabric", $"fabric-loader-0.17.2-{mcVersion}"));
        _graalVm.OnEnsure = (major, _, _) => Installed(Runtime(major, $"{major}.0.1"));
        _graalVm.OnTest = (_, _) => Ok;

        JavaSetupOutcome outcome = await SetUp(useGraalVm: true, mcVersion: mcVersion);

        Assert.Equal($"Oracle GraalVM {javaMajor}.0.1, 4 GB of memory for Minecraft", outcome.Summary);
        Assert.Equal($"ensure {javaMajor}", _graalVm.Calls[0]);
        Assert.Equal(JvmArguments.Build(javaMajor, graalVm: true, Pc, distantHorizons: false), Str(ReadProfile("fabric"), "javaArgs"));
    }

    [Fact]
    public async Task SetUpAsync_WithGraalVm_PassesTheProgressAndTheTokenOn()
    {
        WriteDefaultProfile();
        IProgress<RuntimeDownloadProgress>? receivedProgress = null;
        CancellationToken receivedToken = default;
        _graalVm.OnEnsure = (_, progress, ct) =>
        {
            receivedProgress = progress;
            receivedToken = ct;
            return Installed(Runtime(21, "21.0.12"));
        };
        _graalVm.OnTest = (_, _) => Ok;
        var givenProgress = new Progress<RuntimeDownloadProgress>();
        using var cts = new CancellationTokenSource();

        await SetUp(useGraalVm: true, progress: givenProgress, ct: cts.Token);

        Assert.Same(givenProgress, receivedProgress);
        Assert.Equal(cts.Token, receivedToken);
    }

    [Fact]
    public async Task SetUpAsync_WhenTheDownloadFailsAndNothingIsInstalled_FallsBackToTheLauncherJava()
    {
        // The profile still points at a GraalVM from an earlier run.
        string staleJava = Path.Combine(_graalVm.RuntimesRoot, "graalvm-21", "bin", "javaw.exe");
        WriteDefaultProfile(javaDir: staleJava, javaArgs: GraalArgs());
        _graalVm.OnEnsure = (_, _, _) => NotInstalled("Could not download GraalVM: the server answered HTTP 503 (Service Unavailable).");

        JavaSetupOutcome outcome = await SetUp(useGraalVm: true);

        Assert.Equal("the launcher's own Java, 4 GB of memory for Minecraft", outcome.Summary);
        Assert.Equal(
            "GraalVM could not be set up: Could not download GraalVM: the server answered HTTP 503 (Service Unavailable). " +
            "Minecraft uses the launcher's own Java for now; the next injection tries again.",
            outcome.Problem);
        Assert.Null(outcome.Note);
        var profile = ReadProfile(FabricKey);
        Assert.Null(Str(profile, "javaDir"));
        Assert.Equal(HotSpotArgs(), Str(profile, "javaArgs"));
        Assert.Equal(new[] { "ensure 21" }, _graalVm.Calls);
    }

    [Fact]
    public async Task SetUpAsync_WhenAnUpdateFailsButARuntimeIsInstalled_KeepsTheInstalledOneAndSaysSo()
    {
        WriteDefaultProfile();
        var old = Runtime(21, "21.0.5");
        _graalVm.OnEnsure = (_, _, _) => Installed(old, error: "Could not download GraalVM: the server did not answer in time.");
        _graalVm.OnTest = (_, _) => Ok;

        JavaSetupOutcome outcome = await SetUp(useGraalVm: true);

        Assert.Equal("Oracle GraalVM 21.0.5, 4 GB of memory for Minecraft", outcome.Summary);
        Assert.Null(outcome.Problem);
        Assert.Equal("Could not download GraalVM: the server did not answer in time. The installed GraalVM 21.0.5 is used.", outcome.Note);
        var profile = ReadProfile(FabricKey);
        Assert.Equal(old.JavawExe, Str(profile, "javaDir"));
        Assert.Equal(GraalArgs(), Str(profile, "javaArgs"));
    }

    [Fact]
    public async Task SetUpAsync_PassesANoticeOfTheInstallOnAsANote()
    {
        WriteDefaultProfile();
        _graalVm.OnEnsure = (_, _, _) => Installed(Runtime(21, "21.0.12"), notice: "Minecraft is running, so the update waits. Keeping the installed GraalVM 21.0.12.");
        _graalVm.OnTest = (_, _) => Ok;

        JavaSetupOutcome outcome = await SetUp(useGraalVm: true);

        Assert.Equal("Oracle GraalVM 21.0.12, 4 GB of memory for Minecraft", outcome.Summary);
        Assert.Null(outcome.Problem);
        Assert.Equal("Minecraft is running, so the update waits. Keeping the installed GraalVM 21.0.12.", outcome.Note);
    }

    [Fact]
    public async Task SetUpAsync_WhenTheTunedArgumentsFailButTheMinimalOnesStart_UsesTheMinimalSet()
    {
        WriteDefaultProfile();
        var runtime = Runtime(21, "21.0.12");
        string tuned = GraalArgs();
        string minimal = JvmArguments.BuildMinimal(Pc, distantHorizons: false);
        _graalVm.OnEnsure = (_, _, _) => Installed(runtime);
        _graalVm.OnTest = (_, args) => args == tuned ? new ArgumentTestResult(false, 1, "Unrecognized VM option 'G1Nope'") : Ok;

        JavaSetupOutcome outcome = await SetUp(useGraalVm: true);

        Assert.Equal("Oracle GraalVM 21.0.12, 4 GB of memory for Minecraft", outcome.Summary);
        Assert.Null(outcome.Problem);
        Assert.Equal(
            "The tuned Java arguments did not start on this PC (Unrecognized VM option 'G1Nope'); a minimal safe set is used.",
            outcome.Note);
        var profile = ReadProfile(FabricKey);
        Assert.Equal(runtime.JavawExe, Str(profile, "javaDir"));
        Assert.Equal(minimal, Str(profile, "javaArgs"));
        Assert.Equal(new[] { "ensure 21", $"test {tuned}", $"test {minimal}" }, _graalVm.Calls);
    }

    [Fact]
    public async Task SetUpAsync_KeepsBothNotesWhenTheInstallHadOneAndTheTunedArgumentsFailed()
    {
        WriteDefaultProfile();
        string tuned = GraalArgs();
        _graalVm.OnEnsure = (_, _, _) => Installed(Runtime(21, "21.0.12"), notice: "Offline.");
        _graalVm.OnTest = (_, args) => args == tuned ? new ArgumentTestResult(false, 1, "boom") : Ok;

        JavaSetupOutcome outcome = await SetUp(useGraalVm: true);

        Assert.Equal("Offline. The tuned Java arguments did not start on this PC (boom); a minimal safe set is used.", outcome.Note);
    }

    [Fact]
    public async Task SetUpAsync_WhenGraalVmDoesNotStartAtAll_FallsBackToTheLauncherJava()
    {
        var runtime = Runtime(21, "21.0.12");
        WriteDefaultProfile(javaDir: runtime.JavawExe, javaArgs: GraalArgs());
        string tuned = GraalArgs();
        string minimal = JvmArguments.BuildMinimal(Pc, distantHorizons: false);
        _graalVm.OnEnsure = (_, _, _) => Installed(runtime);
        _graalVm.OnTest = (_, args) => args == tuned
            ? new ArgumentTestResult(false, 1, "Unrecognized VM option 'G1Nope'")
            : new ArgumentTestResult(false, 1, "Error occurred during initialization of VM");

        JavaSetupOutcome outcome = await SetUp(useGraalVm: true);

        Assert.Equal("the launcher's own Java, 4 GB of memory for Minecraft", outcome.Summary);
        // The output named is the minimal set's: it shows that GraalVM itself is what does not run.
        Assert.Equal(
            "GraalVM 21.0.12 did not start on this PC (Error occurred during initialization of VM). Minecraft uses the launcher's own Java.",
            outcome.Problem);
        Assert.Null(outcome.Note);
        var profile = ReadProfile(FabricKey);
        Assert.Null(Str(profile, "javaDir"));
        Assert.Equal(HotSpotArgs(), Str(profile, "javaArgs"));
        Assert.Equal(new[] { "ensure 21", $"test {tuned}", $"test {minimal}" }, _graalVm.Calls);
    }

    [Fact]
    public async Task SetUpAsync_WhenJavaFailsSilently_NamesItsExitCodeInstead()
    {
        WriteDefaultProfile();
        _graalVm.OnEnsure = (_, _, _) => Installed(Runtime(21, "21.0.12"));
        _graalVm.OnTest = (_, _) => new ArgumentTestResult(false, -1073741515, "  ");

        JavaSetupOutcome outcome = await SetUp(useGraalVm: true);

        Assert.Equal(
            "GraalVM 21.0.12 did not start on this PC (exit code -1073741515). Minecraft uses the launcher's own Java.",
            outcome.Problem);
    }

    [Fact]
    public async Task SetUpAsync_WhenTheRuntimeIsNotUsedAfterAll_LeavesOutTheNotesAboutIt()
    {
        WriteDefaultProfile();
        _graalVm.OnEnsure = (_, _, _) => Installed(Runtime(21, "21.0.5"), error: "Could not download GraalVM: offline.", notice: "Offline.");
        _graalVm.OnTest = (_, _) => new ArgumentTestResult(false, 1, "no JVM");

        JavaSetupOutcome outcome = await SetUp(useGraalVm: true);

        Assert.NotNull(outcome.Problem);
        Assert.Null(outcome.Note);
    }

    // ---- GraalVM off -----------------------------------------------------

    [Fact]
    public async Task SetUpAsync_WithGraalVmSwitchedOff_DetachesTheProfilesBeforeDeletingTheRuntimes()
    {
        string java21 = Path.Combine(_graalVm.RuntimesRoot, "graalvm-21", "bin", "javaw.exe");
        string java17 = Path.Combine(_graalVm.RuntimesRoot, "graalvm-17", "bin", "javaw.exe");
        string myJava = @"C:\Program Files\Java\jdk-21\bin\javaw.exe";
        WriteProfiles(
            Profile(FabricKey, "fabric-loader-0.17.2-1.21.4", java21, "graal-args"),
            Profile("older", "fabric-loader-0.17.2-1.20.4", java17, "graal-args"),
            Profile("mine", "fabric-loader-0.17.2-1.21.1", myJava, "my-args"));
        string? olderJavaDirWhenRemoving = "RemoveAll was not called";
        _graalVm.OnRemoveAll = () => olderJavaDirWhenRemoving = Str(ReadProfile("older"), "javaDir");

        JavaSetupOutcome outcome = await SetUp(useGraalVm: false);

        Assert.Equal(
            new JavaSetupOutcome("the launcher's own Java (GraalVM is switched off), 4 GB of memory for Minecraft", null, null),
            outcome);
        Assert.Equal(new[] { "remove-all" }, _graalVm.Calls);
        // The profile ApplyJavaSettings does not touch was taken off its runtime before the runtimes went: so was this one.
        Assert.Null(olderJavaDirWhenRemoving);

        var current = ReadProfile(FabricKey);
        Assert.Null(Str(current, "javaDir"));
        Assert.Equal(HotSpotArgs(21), Str(current, "javaArgs"));
        var older = ReadProfile("older");
        Assert.Null(Str(older, "javaDir"));
        Assert.Equal(HotSpotArgs(17), Str(older, "javaArgs"));
        // Another Java than ours is the user's business.
        var mine = ReadProfile("mine");
        Assert.Equal(myJava, Str(mine, "javaDir"));
        Assert.Equal("my-args", Str(mine, "javaArgs"));
    }

    [Fact]
    public async Task SetUpAsync_WithGraalVmSwitchedOff_NeverAsksForADownload()
    {
        WriteDefaultProfile();

        await SetUp(useGraalVm: false);

        Assert.DoesNotContain(_graalVm.Calls, call => call.StartsWith("ensure") || call.StartsWith("test") || call.StartsWith("installed"));
    }

    // ---- Games without a Java setup --------------------------------------

    [Fact]
    public async Task SetUpAsync_ForAGameBefore1_16_5_ChangesNothing()
    {
        string java = Path.Combine(_graalVm.RuntimesRoot, "graalvm-17", "bin", "javaw.exe");
        WriteProfiles(Profile("old", "fabric-loader-0.17.2-1.16.4", java, "-Xmx1G"));
        string before = File.ReadAllText(ProfilesFile);
        var oldTime = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(ProfilesFile, oldTime);

        JavaSetupOutcome outcome = await SetUp(useGraalVm: true, mcVersion: "1.16.4");

        Assert.Equal(new JavaSetupOutcome("unchanged (Java is only set up for Minecraft 1.16.5 and newer)", null, null), outcome);
        Assert.Equal(before, File.ReadAllText(ProfilesFile));
        Assert.Equal(oldTime, File.GetLastWriteTimeUtc(ProfilesFile));
        Assert.Empty(_graalVm.Calls);
    }

    [Fact]
    public async Task SetUpAsync_SwitchedOffWhileAnOldGameIsSelected_StillRemovesGraalVm()
    {
        // The switch says GraalVM goes on the next injection, whichever version that is for.
        string java21 = Path.Combine(_graalVm.RuntimesRoot, "graalvm-21", "bin", "javaw.exe");
        WriteProfiles(
            Profile("old", "fabric-loader-0.17.2-1.12.2", javaArgs: "-Xmx1G"),
            Profile(FabricKey, "fabric-loader-0.17.2-1.21.4", java21, "graal-args"));

        JavaSetupOutcome outcome = await SetUp(useGraalVm: false, mcVersion: "1.12.2");

        Assert.Equal(new JavaSetupOutcome("unchanged (Java is only set up for Minecraft 1.16.5 and newer)", null, null), outcome);
        Assert.Equal(new[] { "remove-all" }, _graalVm.Calls);
        Assert.Null(Str(ReadProfile(FabricKey), "javaDir"));
        Assert.Equal(HotSpotArgs(21), Str(ReadProfile(FabricKey), "javaArgs"));
        Assert.Equal("-Xmx1G", Str(ReadProfile("old"), "javaArgs"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SetUpAsync_WhenTheGameNeedsANewerJavaThanAnyGraalVm_ChangesNothing(bool useGraalVm)
    {
        WriteDefaultProfile(javaArgs: "-Xmx1G");
        string versionDir = Path.Combine(_mc, "versions", McVersion);
        Directory.CreateDirectory(versionDir);
        File.WriteAllText(Path.Combine(versionDir, $"{McVersion}.json"), """{ "id": "1.21.4", "javaVersion": { "component": "x", "majorVersion": 29 } }""");
        string before = File.ReadAllText(ProfilesFile);
        var oldTime = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(ProfilesFile, oldTime);

        JavaSetupOutcome outcome = await SetUp(useGraalVm);

        Assert.Equal(new JavaSetupOutcome("unchanged (this Minecraft version needs a newer Java than any GraalVM)", null, null), outcome);
        Assert.Equal(before, File.ReadAllText(ProfilesFile));
        Assert.Equal(oldTime, File.GetLastWriteTimeUtc(ProfilesFile));
        // Switched off, the downloaded runtimes still go; no profile pointed at them here.
        Assert.Equal(useGraalVm ? Array.Empty<string>() : new[] { "remove-all" }, _graalVm.Calls);
    }

    [Fact]
    public async Task SetUpAsync_WhenTheGameNeedsANewerJavaThatHasABuild_UsesThatJava()
    {
        WriteDefaultProfile();
        string versionDir = Path.Combine(_mc, "versions", McVersion);
        Directory.CreateDirectory(versionDir);
        File.WriteAllText(Path.Combine(versionDir, $"{McVersion}.json"), """{ "javaVersion": { "majorVersion": 25 } }""");
        _graalVm.OnEnsure = (major, _, _) => Installed(Runtime(major, "25.0.1"));
        _graalVm.OnTest = (_, _) => Ok;

        JavaSetupOutcome outcome = await SetUp(useGraalVm: true);

        Assert.Equal("Oracle GraalVM 25.0.1, 4 GB of memory for Minecraft", outcome.Summary);
        Assert.Equal("ensure 25", _graalVm.Calls[0]);
        Assert.Equal(JvmArguments.Build(25, graalVm: true, Pc, distantHorizons: false), Str(ReadProfile(FabricKey), "javaArgs"));
    }

    // ---- No profile to write to ------------------------------------------

    [Fact]
    public async Task SetUpAsync_WithGraalVmAndNoMatchingProfile_ReportsThatNothingWasWritten()
    {
        WriteProfiles(
            Profile("own-pack", "fabric-loader-0.17.2-1.21.4", gameDir: @"C:\Packs\Create"),
            Profile("other-version", "fabric-loader-0.17.2-1.20.4"),
            Profile("vanilla", "1.21.4"));
        string before = File.ReadAllText(ProfilesFile);
        _graalVm.OnEnsure = (_, _, _) => Installed(Runtime(21, "21.0.12"));
        _graalVm.OnTest = (_, _) => Ok;

        JavaSetupOutcome outcome = await SetUp(useGraalVm: true);

        Assert.Equal(NoProfileProblem, outcome.Problem);
        Assert.Equal("Oracle GraalVM 21.0.12, 4 GB of memory for Minecraft", outcome.Summary);
        Assert.Null(outcome.Note);
        Assert.Equal(before, File.ReadAllText(ProfilesFile));
    }

    [Fact]
    public async Task SetUpAsync_WithoutAnyProfileFile_ReportsThatNothingWasWritten()
    {
        _graalVm.OnEnsure = (_, _, _) => Installed(Runtime(21, "21.0.12"));
        _graalVm.OnTest = (_, _) => Ok;

        JavaSetupOutcome outcome = await SetUp(useGraalVm: true);

        Assert.Equal(NoProfileProblem, outcome.Problem);
        Assert.False(File.Exists(ProfilesFile));
    }

    [Fact]
    public async Task SetUpAsync_WithGraalVmSwitchedOffAndNoMatchingProfile_ReportsThatNothingWasWritten()
    {
        WriteProfiles(Profile("other-version", "fabric-loader-0.17.2-1.20.4"));

        JavaSetupOutcome outcome = await SetUp(useGraalVm: false);

        Assert.Equal(NoProfileProblem, outcome.Problem);
        Assert.Equal("the launcher's own Java (GraalVM is switched off), 4 GB of memory for Minecraft", outcome.Summary);
        // Switching off is still carried out: the runtimes go whether or not this version has a profile.
        Assert.Equal(new[] { "remove-all" }, _graalVm.Calls);
    }

    [Fact]
    public async Task SetUpAsync_WhenTheFallbackHasNoProfileEither_ReportsBothProblems()
    {
        WriteProfiles(Profile("other-version", "fabric-loader-0.17.2-1.20.4"));
        _graalVm.OnEnsure = (_, _, _) => NotInstalled("Not enough free disk space for GraalVM on C:\\.");

        JavaSetupOutcome outcome = await SetUp(useGraalVm: true);

        Assert.Equal(
            "GraalVM could not be set up: Not enough free disk space for GraalVM on C:\\. " +
            "Minecraft uses the launcher's own Java for now; the next injection tries again. " + NoProfileProblem,
            outcome.Problem);
    }

    [Fact]
    public async Task SetUpAsync_WhenTheProfileIsRightAlready_ReportsNoProblemAndDoesNotRewriteTheFile()
    {
        WriteDefaultProfile();
        _graalVm.OnEnsure = (_, _, _) => Installed(Runtime(21, "21.0.12"));
        _graalVm.OnTest = (_, _) => Ok;
        JavaSetupOutcome first = await SetUp(useGraalVm: true);
        Assert.Null(first.Problem);
        string written = File.ReadAllText(ProfilesFile);
        var oldTime = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(ProfilesFile, oldTime);

        JavaSetupOutcome second = await SetUp(useGraalVm: true);

        Assert.Equal(first, second);
        Assert.Null(second.Problem);
        Assert.Equal(written, File.ReadAllText(ProfilesFile));
        Assert.Equal(oldTime, File.GetLastWriteTimeUtc(ProfilesFile));
    }

    [Fact]
    public async Task SetUpAsync_SwitchedOffTwice_ReportsNoProblemTheSecondTime()
    {
        WriteDefaultProfile();
        await SetUp(useGraalVm: false);

        JavaSetupOutcome second = await SetUp(useGraalVm: false);

        Assert.Null(second.Problem);
        Assert.Equal("the launcher's own Java (GraalVM is switched off), 4 GB of memory for Minecraft", second.Summary);
    }

    // ---- Memory ----------------------------------------------------------

    [Theory]
    [InlineData(4, false, "2 GB")]
    [InlineData(8, false, "3 GB")]
    [InlineData(8, true, "4 GB")]
    [InlineData(16, false, "4 GB")]
    [InlineData(16, true, "6 GB")]
    [InlineData(32, false, "6 GB")]
    [InlineData(32, true, "8 GB")]
    public async Task SetUpAsync_NamesTheMemoryOfTheArgumentsItWrote(int ramGib, bool distantHorizons, string heap)
    {
        var system = new SystemProfile(ramGib * Gib, 8);
        WriteDefaultProfile();
        _graalVm.OnEnsure = (_, _, _) => Installed(Runtime(21, "21.0.12"));
        _graalVm.OnTest = (_, _) => Ok;

        JavaSetupOutcome outcome = await SetUp(useGraalVm: true, distantHorizons, system: system);

        Assert.Equal($"Oracle GraalVM 21.0.12, {heap} of memory for Minecraft", outcome.Summary);
        Assert.Equal(GraalArgs(distantHorizons, system), Str(ReadProfile(FabricKey), "javaArgs"));
        Assert.Contains($"-Xmx{heap.Replace(" GB", "G")}", GraalArgs(distantHorizons, system));
    }

    [Fact]
    public async Task SetUpAsync_WithDistantHorizons_AlsoSizesTheLauncherJavaForIt()
    {
        var system = new SystemProfile(16 * Gib, 8);
        WriteDefaultProfile();

        JavaSetupOutcome outcome = await SetUp(useGraalVm: false, distantHorizons: true, system: system);

        Assert.Equal("the launcher's own Java (GraalVM is switched off), 6 GB of memory for Minecraft", outcome.Summary);
        Assert.Equal(HotSpotArgs(21, distantHorizons: true, system), Str(ReadProfile(FabricKey), "javaArgs"));
    }

    [Theory]
    [InlineData("-Xms1G -Xmx4G -XX:+UseG1GC", "4 GB")]
    [InlineData("-Xmx3072M", "3 GB")]
    [InlineData("-Xms1536M -Xmx1536M", "1536 MB")]
    [InlineData("-Xmx2g", "2 GB")]
    [InlineData("-Xmx2048m", "2 GB")]
    [InlineData("-Xmx1G -XX:+UseG1GC -Xmx6G", "6 GB")]
    [InlineData("-Xms4G", null)]
    [InlineData("-Xmx", null)]
    [InlineData("-XmxG", null)]
    [InlineData("-Xmx4", null)]
    [InlineData("-Xmx4X", null)]
    [InlineData("-Xmx-4G", null)]
    [InlineData("-XX:MaxHeapSize=4G", null)]
    [InlineData("", null)]
    public void DescribeHeap_ReadsTheLastMaximumHeapOption(string javaArgs, string? expected)
    {
        Assert.Equal(expected, JavaSetupService.DescribeHeap(javaArgs));
    }

    // ---- Things going wrong ----------------------------------------------

    [Fact]
    public async Task SetUpAsync_WhenSomethingUnexpectedThrows_ReturnsAProblemInsteadOfThrowing()
    {
        WriteDefaultProfile();
        _graalVm.OnEnsure = (_, _, _) => throw new InvalidOperationException("the disk caught fire");

        JavaSetupOutcome outcome = await SetUp(useGraalVm: true);

        Assert.Equal("Setting up Java failed: the disk caught fire", outcome.Problem);
        Assert.Equal("could not be set up", outcome.Summary);
        Assert.Null(outcome.Note);
    }

    [Fact]
    public async Task SetUpAsync_WhenTheSystemCannotBeDetected_ReturnsAProblemInsteadOfThrowing()
    {
        var service = new JavaSetupService(_graalVm, _gpu, () => throw new InvalidOperationException("no memory info"));

        JavaSetupOutcome outcome = await service.SetUpAsync(_mc, McVersion, useGraalVm: true, distantHorizons: false);

        Assert.Equal("Setting up Java failed: no memory info", outcome.Problem);
    }

    [Fact]
    public async Task SetUpAsync_WhenACancellationNobodyAskedForSurfaces_ReturnsAProblem()
    {
        WriteDefaultProfile();
        _graalVm.OnEnsure = (_, _, _) => throw new OperationCanceledException("a timeout deep down");

        JavaSetupOutcome outcome = await SetUp(useGraalVm: true);

        Assert.Equal("Setting up Java failed: a timeout deep down", outcome.Problem);
    }

    [Fact]
    public async Task SetUpAsync_WhenCancelledWhileDownloading_ThrowsOperationCanceled()
    {
        WriteDefaultProfile(javaArgs: "-Xmx2G");
        string before = File.ReadAllText(ProfilesFile);
        using var cts = new CancellationTokenSource();
        _graalVm.OnEnsure = async (_, _, ct) =>
        {
            cts.Cancel();
            await Task.Delay(Timeout.Infinite, ct);
            return new RuntimeInstallResult(null, false, "unreachable", null);
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SetUp(useGraalVm: true, ct: cts.Token));

        Assert.Equal(before, File.ReadAllText(ProfilesFile));
    }

    [Fact]
    public async Task SetUpAsync_WhenCancelledBeforeItStarts_DoesNothing()
    {
        WriteDefaultProfile();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SetUp(useGraalVm: false, ct: cts.Token));

        Assert.Empty(_graalVm.Calls);
    }

    // ---- The launcher's own Java ----------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SetUpAsync_For1_16_5OnTheLaunchersJava_WritesArgumentsJava8Accepts(bool graalVmFails)
    {
        // The launcher starts 1.16.5 on Java 8, which refuses the code heap options a big PC gets for Java 17.
        WriteProfiles(Profile("fabric-loader-1.16.5", "fabric-loader-0.17.2-1.16.5", javaArgs: "-Xmx2G"));
        var bigPc = new SystemProfile(32 * Gib, 16);
        if (graalVmFails)
            _graalVm.OnEnsure = (_, _, _) => NotInstalled("Could not download GraalVM: offline.");

        JavaSetupOutcome outcome = await SetUp(useGraalVm: graalVmFails, mcVersion: "1.16.5", system: bigPc);

        string javaArgs = Str(ReadProfile("fabric-loader-1.16.5"), "javaArgs")!;
        Assert.Equal(JvmArguments.Build(8, graalVm: false, bigPc, distantHorizons: false), javaArgs);
        Assert.Equal("-Xms6G -Xmx6G -XX:+UseG1GC -XX:MaxGCPauseMillis=37 -XX:+DisableExplicitGC", javaArgs);
        Assert.DoesNotContain("CodeHeapSize", javaArgs);
        Assert.StartsWith("the launcher's own Java", outcome.Summary);
    }

    [Fact]
    public async Task SetUpAsync_For1_16_5OnGraalVm_UsesTheFullSetForJava17()
    {
        WriteProfiles(Profile("fabric-loader-1.16.5", "fabric-loader-0.17.2-1.16.5"));
        _graalVm.OnEnsure = (major, _, _) => Installed(Runtime(major, "17.0.12"));
        _graalVm.OnTest = (_, _) => Ok;

        JavaSetupOutcome outcome = await SetUp(useGraalVm: true, mcVersion: "1.16.5");

        Assert.Equal("ensure 17", _graalVm.Calls[0]);
        Assert.Equal(JvmArguments.Build(17, graalVm: true, Pc, distantHorizons: false), Str(ReadProfile("fabric-loader-1.16.5"), "javaArgs"));
        Assert.Equal("Oracle GraalVM 17.0.12, 4 GB of memory for Minecraft", outcome.Summary);
    }

    [Fact]
    public async Task SetUpAsync_SwitchedOff_GivesAProfileOnABetaLoaderTheArgumentsOfItsGame()
    {
        // The loader's own dash ("0.20.0-beta.2") must not be taken for the start of the game version.
        string java25 = Path.Combine(_graalVm.RuntimesRoot, "graalvm-25", "bin", "javaw.exe");
        WriteProfiles(
            Profile(FabricKey, "fabric-loader-0.17.2-1.21.4", javaArgs: "-Xmx2G"),
            Profile("beta", "fabric-loader-0.20.0-beta.2-26.3", java25, "graal-args"));

        await SetUp(useGraalVm: false);

        Assert.Null(Str(ReadProfile("beta"), "javaDir"));
        Assert.Equal(HotSpotArgs(25), Str(ReadProfile("beta"), "javaArgs"));
    }

    // ---- Profile files that cannot be written ----------------------------

    [Fact]
    public async Task SetUpAsync_WithABrokenSecondProfileFile_WritesTheFirstAndOnlyNotesTheOther()
    {
        // Without any GraalVM on the disk there is nothing that could have been "kept".
        WriteDefaultProfile();
        File.WriteAllText(Path.Combine(_mc, "launcher_profiles_microsoft_store.json"), "{ not json");

        JavaSetupOutcome outcome = await SetUp(useGraalVm: false);

        Assert.Null(outcome.Problem);
        Assert.StartsWith("One launcher profile file could not be read or written and was left as it was (launcher_profiles_microsoft_store.json: ", outcome.Note);
        Assert.Equal(HotSpotArgs(21), Str(ReadProfile(FabricKey), "javaArgs"));
    }

    [Fact]
    public async Task SetUpAsync_SwitchedOffWithAReadOnlyProfileFile_KeepsGraalVm()
    {
        // Some players make launcher_profiles.json read-only so the launcher cannot change it. The profile keeps
        // pointing at GraalVM then, so deleting GraalVM would leave it with a Java that is gone.
        string java21 = Path.Combine(_graalVm.RuntimesRoot, "graalvm-21", "bin", "javaw.exe");
        WriteDefaultProfile(javaDir: java21, javaArgs: "graal-args");
        Directory.CreateDirectory(Path.GetDirectoryName(java21)!);
        File.SetAttributes(ProfilesFile, FileAttributes.ReadOnly);
        try
        {
            JavaSetupOutcome outcome = await SetUp(useGraalVm: false);

            Assert.DoesNotContain("remove-all", _graalVm.Calls);
            Assert.StartsWith("GraalVM was kept, because the launcher profiles could not all be switched back", outcome.Problem);
            Assert.Contains("launcher_profiles.json", outcome.Problem);
            Assert.Equal(java21, Str(ReadProfile(FabricKey), "javaDir"));
        }
        finally
        {
            File.SetAttributes(ProfilesFile, FileAttributes.Normal);
        }
    }

    [Fact]
    public async Task SetUpAsync_WithAReadOnlyProfileFile_SaysTheFileCouldNotBeWritten()
    {
        WriteDefaultProfile();
        _graalVm.OnEnsure = (major, _, _) => Installed(Runtime(major, "21.0.12"));
        _graalVm.OnTest = (_, _) => Ok;
        File.SetAttributes(ProfilesFile, FileAttributes.ReadOnly);
        try
        {
            JavaSetupOutcome outcome = await SetUp(useGraalVm: true);

            Assert.StartsWith("The Java settings could not be written to the launcher profiles (launcher_profiles.json: ", outcome.Problem);
            Assert.DoesNotContain("No Fabric profile", outcome.Problem);
        }
        finally
        {
            File.SetAttributes(ProfilesFile, FileAttributes.Normal);
        }
    }

    // ---- The graphics card -----------------------------------------------

    private const string HighPerformance = "GpuPreference=2;";
    private const string CarriedOverNote = "Windows' graphics preference for the launcher's Java (high performance) now applies to GraalVM too.";

    private string GpuRecord => Path.Combine(_graalVm.RuntimesRoot, GpuPreferenceService.RecordFileName);

    private string LauncherJava(string component) =>
        Path.Combine(_mc, "runtime", component, "windows-x64", component, "bin", "javaw.exe");

    /// <summary>GraalVM 21 installs and starts; the launcher's Java 21 has the strong card picked in Windows.</summary>
    private InstalledRuntime GraalVmWithACardToCarryOver()
    {
        WriteDefaultProfile();
        var runtime = Runtime(21, "21.0.12");
        _graalVm.OnEnsure = (_, _, _) => Installed(runtime);
        _graalVm.OnTest = (_, _) => Ok;
        _gpu.Settings[LauncherJava("java-runtime-delta")] = HighPerformance;
        return runtime;
    }

    [Fact]
    public async Task SetUpAsync_WithGraalVm_GivesItTheGraphicsCardOfTheLaunchersJava()
    {
        var runtime = GraalVmWithACardToCarryOver();

        JavaSetupOutcome outcome = await SetUp(useGraalVm: true);

        Assert.Equal(new JavaSetupOutcome("Oracle GraalVM 21.0.12, 4 GB of memory for Minecraft", null, CarriedOverNote), outcome);
        Assert.Equal(HighPerformance, _gpu.Settings[runtime.JavawExe]);
        Assert.Equal(new[] { $"set {runtime.JavawExe}" }, _gpu.Writes);
        // The exe that got the card is exactly the one the profile starts.
        Assert.Equal(runtime.JavawExe, Str(ReadProfile(FabricKey), "javaDir"));
        Assert.True(File.Exists(GpuRecord));
    }

    [Fact]
    public async Task SetUpAsync_PrefersTheCardOfTheJavaTheGameRanOnSoFar()
    {
        // Newest first would pick Java 25's runtime; the version json names the one 1.21.4 ran on.
        var runtime = GraalVmWithACardToCarryOver();
        WriteVersionJson("""{ "javaVersion": { "component": "java-runtime-delta", "majorVersion": 21 } }""");
        _gpu.Settings[LauncherJava("java-runtime-epsilon")] = "GpuPreference=1;";

        await SetUp(useGraalVm: true);

        Assert.Equal(HighPerformance, _gpu.Settings[runtime.JavawExe]);
    }

    [Fact]
    public async Task SetUpAsync_WhenTheGamesOwnJavaLeavesTheCardToWindows_CarriesNothingOver()
    {
        // The game ran on "let Windows decide" until now; another Java's card would be forced on it.
        GraalVmWithACardToCarryOver();
        WriteVersionJson("""{ "javaVersion": { "component": "java-runtime-delta", "majorVersion": 21 } }""");
        _gpu.Settings[LauncherJava("java-runtime-delta")] = "GpuPreference=0;";
        _gpu.Settings[LauncherJava("java-runtime-epsilon")] = HighPerformance;

        JavaSetupOutcome outcome = await SetUp(useGraalVm: true);

        Assert.Empty(_gpu.Writes);
        Assert.Null(outcome.Note);
    }

    [Fact]
    public async Task SetUpAsync_WithoutTheGamesOwnCard_TakesTheNewestLauncherJavasCard()
    {
        var runtime = GraalVmWithACardToCarryOver();
        _gpu.Settings.Clear();
        _gpu.Settings[LauncherJava("java-runtime-gamma")] = "GpuPreference=1;";

        JavaSetupOutcome outcome = await SetUp(useGraalVm: true);

        Assert.Equal("GpuPreference=1;", _gpu.Settings[runtime.JavawExe]);
        Assert.Equal("Windows' graphics preference for the launcher's Java (power saving) now applies to GraalVM too.", outcome.Note);
    }

    [Fact]
    public async Task SetUpAsync_FindsTheCardOfTheMicrosoftStoreLaunchersJava()
    {
        var runtime = GraalVmWithACardToCarryOver();
        _gpu.Settings.Clear();

        // That launcher keeps its runtimes in its package folder, not in .minecraft.
        string storeRoot = FabricService.GetMinecraftRuntimeRoots(_mc).ElementAt(1);
        Assert.Contains("Microsoft.4297127D64EC6_8wekyb3d8bbwe", storeRoot);
        _gpu.Settings[Path.Combine(storeRoot, "java-runtime-delta", "windows-x64", "java-runtime-delta", "bin", "javaw.exe")] = HighPerformance;

        JavaSetupOutcome outcome = await SetUp(useGraalVm: true);

        Assert.Equal(HighPerformance, _gpu.Settings[runtime.JavawExe]);
        Assert.Equal(CarriedOverNote, outcome.Note);
    }

    [Fact]
    public async Task SetUpAsync_WithoutACardPickedForTheLaunchersJava_LeavesWindowsAlone()
    {
        GraalVmWithACardToCarryOver();
        _gpu.Settings[LauncherJava("java-runtime-delta")] = "AppStatus=4;";

        JavaSetupOutcome outcome = await SetUp(useGraalVm: true);

        Assert.Empty(_gpu.Writes);
        Assert.Null(outcome.Note);
        Assert.False(File.Exists(GpuRecord));
    }

    [Fact]
    public async Task SetUpAsync_KeepsACardPickedForGraalVmItself()
    {
        var runtime = GraalVmWithACardToCarryOver();
        _gpu.Settings[runtime.JavawExe] = "GpuPreference=1;";

        JavaSetupOutcome outcome = await SetUp(useGraalVm: true);

        Assert.Equal("GpuPreference=1;", _gpu.Settings[runtime.JavawExe]);
        Assert.Empty(_gpu.Writes);
        Assert.Null(outcome.Note);
    }

    [Fact]
    public async Task SetUpAsync_CarriesTheCardOverOnlyOnce()
    {
        GraalVmWithACardToCarryOver();
        await SetUp(useGraalVm: true);

        JavaSetupOutcome second = await SetUp(useGraalVm: true);

        Assert.Null(second.Note);
        Assert.Single(_gpu.Writes);
    }

    [Fact]
    public async Task SetUpAsync_WhenNoProfileRunsOnGraalVm_CarriesNoCardOver()
    {
        GraalVmWithACardToCarryOver();
        WriteProfiles(Profile("other-version", "fabric-loader-0.17.2-1.20.4"));

        JavaSetupOutcome outcome = await SetUp(useGraalVm: true);

        Assert.Equal(NoProfileProblem, outcome.Problem);
        Assert.Null(outcome.Note);
        Assert.Empty(_gpu.Writes);
    }

    [Fact]
    public async Task SetUpAsync_WhenGraalVmIsNotUsed_CarriesNoCardOver()
    {
        GraalVmWithACardToCarryOver();
        _graalVm.OnEnsure = (_, _, _) => NotInstalled("Could not download GraalVM: offline.");

        await SetUp(useGraalVm: true);

        Assert.Empty(_gpu.Writes);
    }

    [Fact]
    public async Task SetUpAsync_WhenTheCardCannotBeCarriedOver_SaysSoButStillUsesGraalVm()
    {
        var runtime = GraalVmWithACardToCarryOver();
        _gpu.FailSet = _ => new UnauthorizedAccessException("Access to the registry key is denied.");

        JavaSetupOutcome outcome = await SetUp(useGraalVm: true);

        Assert.Equal("Oracle GraalVM 21.0.12, 4 GB of memory for Minecraft", outcome.Summary);
        Assert.Null(outcome.Problem);
        Assert.Equal(
            "Windows' graphics preference for the launcher's Java could not be carried over to GraalVM (Access to the registry key is denied.). " +
            $"If Minecraft runs on the wrong graphics card, pick one for {runtime.JavawExe} under Settings > System > Display > Graphics.",
            outcome.Note);
        Assert.Equal(runtime.JavawExe, Str(ReadProfile(FabricKey), "javaDir"));
    }

    [Fact]
    public async Task SetUpAsync_PutsTheCardNoteAfterTheOtherNotes()
    {
        var runtime = GraalVmWithACardToCarryOver();
        _graalVm.OnEnsure = (_, _, _) => Installed(runtime, notice: "Offline.");

        JavaSetupOutcome outcome = await SetUp(useGraalVm: true);

        Assert.Equal($"Offline. {CarriedOverNote}", outcome.Note);
    }

    [Fact]
    public async Task SetUpAsync_SwitchedOff_TakesTheCardAwayWithGraalVm()
    {
        var runtime = GraalVmWithACardToCarryOver();
        await SetUp(useGraalVm: true);

        JavaSetupOutcome outcome = await SetUp(useGraalVm: false);

        Assert.False(_gpu.Settings.ContainsKey(runtime.JavawExe));
        Assert.Equal(HighPerformance, _gpu.Settings[LauncherJava("java-runtime-delta")]);
        Assert.Null(outcome.Note);
        Assert.False(File.Exists(GpuRecord));
        Assert.Contains("remove-all", _graalVm.Calls);
    }

    [Fact]
    public async Task SetUpAsync_SwitchedOff_LeavesACardTheUserPickedForGraalVmSince()
    {
        var runtime = GraalVmWithACardToCarryOver();
        await SetUp(useGraalVm: true);
        _gpu.Settings[runtime.JavawExe] = "GpuPreference=1;";

        await SetUp(useGraalVm: false);

        Assert.Equal("GpuPreference=1;", _gpu.Settings[runtime.JavawExe]);
    }

    [Fact]
    public async Task SetUpAsync_SwitchedOffButGraalVmKept_KeepsItsCard()
    {
        // A read-only profile file keeps pointing at GraalVM, so GraalVM stays, and so does the card it runs on.
        var runtime = GraalVmWithACardToCarryOver();
        await SetUp(useGraalVm: true);
        Directory.CreateDirectory(Path.GetDirectoryName(runtime.JavawExe)!);
        File.SetAttributes(ProfilesFile, FileAttributes.ReadOnly);
        try
        {
            JavaSetupOutcome outcome = await SetUp(useGraalVm: false);

            Assert.StartsWith("GraalVM was kept", outcome.Problem);
            Assert.Equal(HighPerformance, _gpu.Settings[runtime.JavawExe]);
            Assert.True(File.Exists(GpuRecord));
        }
        finally
        {
            File.SetAttributes(ProfilesFile, FileAttributes.Normal);
        }
    }

    [Fact]
    public async Task SetUpAsync_SwitchedOffWithAnUnreadableProfileFileButNoGraalVmLeft_StillTakesTheCardAway()
    {
        // Nothing of GraalVM is on the disk any more, so nothing was kept that the card could still be for.
        var runtime = GraalVmWithACardToCarryOver();
        await SetUp(useGraalVm: true);
        File.WriteAllText(Path.Combine(_mc, "launcher_profiles_microsoft_store.json"), "{ not json");

        JavaSetupOutcome outcome = await SetUp(useGraalVm: false);

        Assert.Null(outcome.Problem);
        Assert.False(_gpu.Settings.ContainsKey(runtime.JavawExe));
        Assert.False(File.Exists(GpuRecord));
    }

    private void WriteVersionJson(string json)
    {
        string versionDir = Path.Combine(_mc, "versions", McVersion);
        Directory.CreateDirectory(versionDir);
        File.WriteAllText(Path.Combine(versionDir, $"{McVersion}.json"), json);
    }
}
