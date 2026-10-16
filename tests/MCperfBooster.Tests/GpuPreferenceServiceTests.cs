using System.Security;
using System.Text.Json;
using MCperfBooster.Core.Services;

namespace MCperfBooster.Tests;

/// <summary>
/// Windows' graphics card settings as a table, so no test touches the registry. Names compare without regard to
/// case, as registry value names do. Writes are listed in order ("set X", "remove X"), and any call can be made to fail.
/// </summary>
internal sealed class FakeGpuPreferenceStore : IGpuPreferenceStore
{
    public Dictionary<string, string> Settings { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<string> Writes { get; } = new();

    public Func<string, Exception?> FailSet { get; set; } = _ => null;

    public Func<string, Exception?> FailRemove { get; set; } = _ => null;

    public string? Get(string exePath) => Settings.TryGetValue(exePath, out string? setting) ? setting : null;

    public void Set(string exePath, string setting)
    {
        if (FailSet(exePath) is { } error)
            throw error;

        Writes.Add($"set {exePath}");
        Settings[exePath] = setting;
    }

    public void Remove(string exePath)
    {
        if (FailRemove(exePath) is { } error)
            throw error;

        Writes.Add($"remove {exePath}");
        Settings.Remove(exePath);
    }
}

public class GpuPreferenceServiceTests : IDisposable
{
    private const string HighPerformance = "GpuPreference=2;";
    private const string PowerSaving = "GpuPreference=1;";
    private const string SpecificCard = "SpecificAdapter=10DE&2520&0A6F1028;GpuPreference=1073741824;";

    private readonly string _base = TempFolders.Create("gpu");
    private readonly FakeGpuPreferenceStore _store = new();
    private readonly string _recordFile;
    private readonly GpuPreferenceService _service;

    // Only the names matter: Windows keeps its settings by path, and nothing here reads the files.
    private readonly string _mc;
    private readonly string _graal21;
    private readonly string _graal17;
    private readonly string _delta;
    private readonly string _gamma;

    public GpuPreferenceServiceTests()
    {
        string runtimes = Path.Combine(_base, "runtimes");
        _recordFile = Path.Combine(runtimes, GpuPreferenceService.RecordFileName);
        _service = new GpuPreferenceService(_store, _recordFile);
        _mc = Path.Combine(_base, ".minecraft");
        _graal21 = Path.Combine(runtimes, "graalvm-21", "bin", "javaw.exe");
        _graal17 = Path.Combine(runtimes, "graalvm-17", "bin", "javaw.exe");
        _delta = LauncherJava("java-runtime-delta");
        _gamma = LauncherJava("java-runtime-gamma");
    }

    public void Dispose() => TempFolders.Delete(_base);

    private string LauncherJava(string component) =>
        Path.Combine(_mc, "runtime", component, "windows-x64", component, "bin", "javaw.exe");

    private string? CarryOverTo21() => _service.CarryOver(_graal21, new[] { _delta, _gamma });

    private Dictionary<string, string> ReadRecord()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(_recordFile));
        return doc.RootElement.EnumerateObject().ToDictionary(entry => entry.Name, entry => entry.Value.GetString()!);
    }

    // ---- Carrying the card over ------------------------------------------

    [Fact]
    public void CarryOver_GivesGraalVmTheCardOfTheFirstLauncherJavaThatHasOne()
    {
        _store.Settings[_delta] = HighPerformance;
        _store.Settings[_gamma] = PowerSaving;

        string? carried = CarryOverTo21();

        Assert.Equal(HighPerformance, carried);
        Assert.Equal(HighPerformance, _store.Settings[_graal21]);
        Assert.Equal(new[] { $"set {_graal21}" }, _store.Writes);
        // The launcher's own settings stay as they were.
        Assert.Equal(HighPerformance, _store.Settings[_delta]);
        Assert.Equal(PowerSaving, _store.Settings[_gamma]);
    }

    [Theory]
    [InlineData("AppStatus=4;")]
    [InlineData(";SwapEffectUpgradeEnable=1;")]
    [InlineData("AutoHDREnable=2097;")]
    [InlineData("")]
    [InlineData("not a setting")]
    public void CarryOver_PassesOverALauncherJavaWithoutACardSetting(string setting)
    {
        _store.Settings[_delta] = setting;
        _store.Settings[_gamma] = PowerSaving;

        Assert.Equal(PowerSaving, CarryOverTo21());
        Assert.Equal(PowerSaving, _store.Settings[_graal21]);
    }

    [Theory]
    [InlineData("GpuPreference=0;")]
    [InlineData("AppStatus=4;GpuPreference=0;")]
    [InlineData("GpuPreference=;")]
    [InlineData("GpuPreference=fast;")]
    [InlineData("SpecificAdapter=10DE&2520&0A6F1028;")]
    public void CarryOver_StopsAtALauncherJavaThatLeavesTheCardToWindows(string setting)
    {
        // The first Java with a card setting decides: the game ran on "let Windows decide" until now, and so it stays.
        _store.Settings[_delta] = setting;
        _store.Settings[_gamma] = HighPerformance;

        Assert.Null(CarryOverTo21());

        Assert.Empty(_store.Writes);
        Assert.False(_store.Settings.ContainsKey(_graal21));
        Assert.False(File.Exists(_recordFile));
    }

    [Theory]
    [InlineData("AppStatus=1;AutoHDREnable=2097;GpuPreference=2;", HighPerformance)]
    [InlineData(SpecificCard + "AutoHDREnable=1;", SpecificCard)]
    [InlineData(" GpuPreference = 2 ", HighPerformance)]
    public void CarryOver_CopiesOnlyThePartsThatPickTheCard(string launcherSetting, string expected)
    {
        _store.Settings[_delta] = launcherSetting;

        Assert.Equal(expected, CarryOverTo21());
        Assert.Equal(expected, _store.Settings[_graal21]);
    }

    [Theory]
    [InlineData("AppStatus=4;", "AppStatus=4;GpuPreference=2;")]
    [InlineData(";SwapEffectUpgradeEnable=1", ";SwapEffectUpgradeEnable=1;GpuPreference=2;")]
    [InlineData("AutoHDREnable=1; ", "AutoHDREnable=1;GpuPreference=2;")]
    [InlineData("", HighPerformance)]
    public void CarryOver_KeepsWhatWindowsHasForGraalVmAlready(string graalSetting, string expected)
    {
        _store.Settings[_graal21] = graalSetting;
        _store.Settings[_delta] = HighPerformance;

        Assert.Equal(HighPerformance, CarryOverTo21());
        Assert.Equal(expected, _store.Settings[_graal21]);
    }

    [Theory]
    [InlineData(PowerSaving)]
    [InlineData("GpuPreference=0;")]
    [InlineData("AppStatus=4;GpuPreference=0;")]
    [InlineData("SpecificAdapter=1002&744C&0E3B1EAE;")]
    // Not text, or not a setting: whatever it is, it is not this app's to change.
    [InlineData("(DWord)")]
    [InlineData("on")]
    public void CarryOver_LeavesWhatGraalVmHasOfItsOwn(string graalSetting)
    {
        _store.Settings[_graal21] = graalSetting;
        _store.Settings[_delta] = HighPerformance;

        Assert.Null(CarryOverTo21());

        Assert.Equal(graalSetting, _store.Settings[_graal21]);
        Assert.Empty(_store.Writes);
        Assert.False(File.Exists(_recordFile));
    }

    [Fact]
    public void CarryOver_WithoutACardPickedAnywhere_WritesNothing()
    {
        _store.Settings[_delta] = "GpuPreference=0;";
        _store.Settings["DirectXUserGlobalSettings"] = "AutoHDREnable=1;SwapEffectUpgradeEnable=1;";

        Assert.Null(CarryOverTo21());

        Assert.Empty(_store.Writes);
        Assert.False(_store.Settings.ContainsKey(_graal21));
        Assert.False(File.Exists(_recordFile));
    }

    [Fact]
    public void CarryOver_RecordsWhatItWroteForEachRuntime()
    {
        _store.Settings[_delta] = "AppStatus=4;GpuPreference=2;";
        _store.Settings[_gamma] = PowerSaving;

        CarryOverTo21();
        _service.CarryOver(_graal17, new[] { _gamma });

        Assert.Equal(new Dictionary<string, string> { [_graal21] = HighPerformance, [_graal17] = PowerSaving }, ReadRecord());
    }

    [Fact]
    public void CarryOver_TheSecondTime_ChangesNothing()
    {
        _store.Settings[_delta] = HighPerformance;
        CarryOverTo21();

        Assert.Null(CarryOverTo21());

        Assert.Single(_store.Writes);
    }

    [Fact]
    public void CarryOver_DoesNotBringBackACardTheUserTookAway()
    {
        _store.Settings[_delta] = HighPerformance;
        CarryOverTo21();

        // The user removed GraalVM from the list in Windows' settings.
        _store.Settings.Remove(_graal21);

        Assert.Null(CarryOverTo21());
        Assert.False(_store.Settings.ContainsKey(_graal21));
    }

    [Fact]
    public void CarryOver_WhenWindowsRefusesTheWrite_ThrowsAndTriesAgainNextTime()
    {
        _store.Settings[_delta] = HighPerformance;
        _store.FailSet = _ => new UnauthorizedAccessException("Access to the registry key is denied.");

        Assert.Throws<UnauthorizedAccessException>(() => CarryOverTo21());
        Assert.False(File.Exists(_recordFile));

        _store.FailSet = _ => null;
        Assert.Equal(HighPerformance, CarryOverTo21());
        Assert.Equal(HighPerformance, _store.Settings[_graal21]);
    }

    [Fact]
    public void CarryOver_WhenTheRecordCannotBeWritten_LeavesWindowsAlone()
    {
        _store.Settings[_delta] = HighPerformance;

        // A folder where the record should go.
        Directory.CreateDirectory(_recordFile);

        Exception error = Assert.ThrowsAny<Exception>(() => CarryOverTo21());
        Assert.True(error is IOException or UnauthorizedAccessException, error.ToString());
        Assert.Empty(_store.Writes);
    }

    // ---- Taking it back --------------------------------------------------

    [Fact]
    public void RemoveCarriedOver_TakesBackWhatItWrote()
    {
        _store.Settings[_delta] = HighPerformance;
        CarryOverTo21();

        _service.RemoveCarriedOver();

        Assert.False(_store.Settings.ContainsKey(_graal21));
        Assert.Equal(HighPerformance, _store.Settings[_delta]);
        Assert.False(File.Exists(_recordFile));
    }

    [Fact]
    public void RemoveCarriedOver_LeavesWhatWindowsKeepsBesideTheCard()
    {
        _store.Settings[_graal21] = "AppStatus=4;";
        _store.Settings[_delta] = SpecificCard;
        CarryOverTo21();

        // Windows adds notes of its own while the game runs.
        _store.Settings[_graal21] = "AppStatus=4;" + SpecificCard + "AutoHDREnable=1;";

        _service.RemoveCarriedOver();

        Assert.Equal("AppStatus=4;AutoHDREnable=1;", _store.Settings[_graal21]);
    }

    [Theory]
    [InlineData(PowerSaving)]
    [InlineData("GpuPreference=0;")]
    [InlineData(SpecificCard)]
    public void RemoveCarriedOver_LeavesACardTheUserPickedSince(string pickedSince)
    {
        _store.Settings[_delta] = HighPerformance;
        CarryOverTo21();
        _store.Settings[_graal21] = pickedSince;

        _service.RemoveCarriedOver();

        Assert.Equal(pickedSince, _store.Settings[_graal21]);
        Assert.False(File.Exists(_recordFile));
    }

    [Fact]
    public void RemoveCarriedOver_WithoutARecord_TouchesNothing()
    {
        // Picked by the user for GraalVM by hand.
        _store.Settings[_graal21] = HighPerformance;
        _store.Settings["DirectXUserGlobalSettings"] = "AutoHDREnable=1;SwapEffectUpgradeEnable=1;";

        _service.RemoveCarriedOver();

        Assert.Empty(_store.Writes);
        Assert.Equal(HighPerformance, _store.Settings[_graal21]);
    }

    [Fact]
    public void RemoveCarriedOver_WhenWindowsRefuses_KeepsItOnRecordAndTriesAgainNextTime()
    {
        _store.Settings[_delta] = HighPerformance;
        _store.Settings[_gamma] = PowerSaving;
        CarryOverTo21();
        _service.CarryOver(_graal17, new[] { _gamma });
        _store.FailRemove = exe => exe == _graal21 ? new SecurityException("Requested registry access is not allowed.") : null;

        _service.RemoveCarriedOver();

        Assert.Equal(HighPerformance, _store.Settings[_graal21]);
        Assert.False(_store.Settings.ContainsKey(_graal17));
        Assert.Equal(new Dictionary<string, string> { [_graal21] = HighPerformance }, ReadRecord());

        _store.FailRemove = _ => null;
        _service.RemoveCarriedOver();

        Assert.False(_store.Settings.ContainsKey(_graal21));
        Assert.False(File.Exists(_recordFile));
    }

    [Theory]
    [InlineData("{ not json")]
    // Valid JSON, but text no .NET string can hold: a lone half of a surrogate pair.
    [InlineData("{ \"C:\\\\x\\\\javaw.exe\": \"\\ud800\" }")]
    [InlineData("{ \"\\ud800\": \"GpuPreference=2;\" }")]
    public void RemoveCarriedOver_WithADamagedRecord_TouchesNothing(string record)
    {
        _store.Settings[_graal21] = HighPerformance;
        Directory.CreateDirectory(Path.GetDirectoryName(_recordFile)!);
        File.WriteAllText(_recordFile, record);

        _service.RemoveCarriedOver();

        Assert.Empty(_store.Writes);
        Assert.Equal(HighPerformance, _store.Settings[_graal21]);
    }

    [Fact]
    public void CarryOver_WithADamagedRecord_StartsAFreshOne()
    {
        _store.Settings[_delta] = HighPerformance;
        Directory.CreateDirectory(Path.GetDirectoryName(_recordFile)!);
        File.WriteAllText(_recordFile, "{ \"C:\\\\x\\\\javaw.exe\": \"\\ud800\" }");

        Assert.Equal(HighPerformance, CarryOverTo21());

        Assert.Equal(new Dictionary<string, string> { [_graal21] = HighPerformance }, ReadRecord());
    }

    [Fact]
    public void CarryOver_AfterTheCardWasTakenBack_CarriesItOverAgain()
    {
        // GraalVM switched off and on again: a fresh start.
        _store.Settings[_delta] = HighPerformance;
        CarryOverTo21();
        _service.RemoveCarriedOver();

        Assert.Equal(HighPerformance, CarryOverTo21());
        Assert.Equal(HighPerformance, _store.Settings[_graal21]);
    }

    // ---- Reading a setting -----------------------------------------------

    [Theory]
    [InlineData(HighPerformance, HighPerformance)]
    [InlineData(PowerSaving, PowerSaving)]
    [InlineData("gpupreference=2", "gpupreference=2;")]
    [InlineData("AppStatus=4;GpuPreference=2;AutoHDREnable=1;", HighPerformance)]
    [InlineData(SpecificCard, SpecificCard)]
    [InlineData("GpuPreference=0;", null)]
    [InlineData("GpuPreference=;", null)]
    [InlineData("GpuPreference=-1;", null)]
    [InlineData("SpecificAdapter=10DE&2520&0A6F1028;", null)]
    [InlineData("AppStatus=4;", null)]
    [InlineData(";SwapEffectUpgradeEnable=1;", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("(DWord)", null)]
    [InlineData("GpuPreference=2;garbage", null)]
    [InlineData("=2;GpuPreference=2;", null)]
    public void GraphicsCardChoice_ReadsThePartsThatPickTheCard(string? setting, string? expected)
    {
        Assert.Equal(expected, GpuPreferenceService.GraphicsCardChoice(setting));
    }

    [Theory]
    [InlineData(HighPerformance, "high performance")]
    [InlineData(PowerSaving, "power saving")]
    [InlineData(SpecificCard, "a specific graphics card")]
    [InlineData("GpuPreference=3;", "a chosen graphics card")]
    public void Describe_NamesTheCardInWords(string choice, string expected)
    {
        Assert.Equal(expected, GpuPreferenceService.Describe(choice));
    }

    // ---- Where the launcher's Java sits ----------------------------------

    [Fact]
    public void GetLauncherJavawPaths_ListsTheGamesOwnJavaFirstThenTheOthersNewestFirst()
    {
        string[] roots = FabricService.GetMinecraftRuntimeRoots(_mc).Select(Path.GetFullPath).ToArray();
        string[] order = { "java-runtime-gamma", "java-runtime-epsilon", "java-runtime-delta", "java-runtime-beta", "java-runtime-alpha", "jre-legacy" };
        var expected = order.SelectMany(component => roots.SelectMany(root => new[] { "windows-x64", "windows" }
            .Select(platform => Path.Combine(root, component, platform, component, "bin", "javaw.exe"))));

        string[] paths = FabricService.GetLauncherJavawPaths(_mc, "java-runtime-gamma").ToArray();

        Assert.Equal(expected, paths);
        Assert.Equal(_gamma, paths[0]);
        Assert.Contains(paths, path => path.Contains("Microsoft.4297127D64EC6_8wekyb3d8bbwe", StringComparison.Ordinal));
    }

    [Fact]
    public void GetLauncherJavawPaths_WithoutTheGamesJava_StartsWithTheNewest()
    {
        Assert.Equal(LauncherJava("java-runtime-epsilon"), FabricService.GetLauncherJavawPaths(_mc).First());
    }

    [Fact]
    public void GetLauncherJavawPaths_PutsAJavaItDoesNotKnowFirstAndKeepsAllOthers()
    {
        string[] paths = FabricService.GetLauncherJavawPaths(_mc, "java-runtime-zeta").ToArray();

        Assert.Equal(LauncherJava("java-runtime-zeta"), paths[0]);
        Assert.Equal(7 * FabricService.GetMinecraftRuntimeRoots(_mc).Count() * 2, paths.Length);
    }
}
