using System.Runtime.CompilerServices;
using MCperfBooster.Core.Constants;

namespace MCperfBooster.Tests;

/// <summary>
/// Points machine-wide state at a per-run temp directory before any test runs, so the
/// suite neither reads nor overwrites the developer's real %LOCALAPPDATA%\MCperfBooster.
/// </summary>
internal static class TestEnvironment
{
    public static string RunDirectory { get; private set; } = "";

#pragma warning disable CA2255 // A module initializer is exactly what a test assembly needs here.
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Initialize()
    {
        RunDirectory = Path.Combine(Path.GetTempPath(), $"MCperfBoosterTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(RunDirectory);

        ModPresets.LocalCacheFilePath = Path.Combine(RunDirectory, "mods.json");

        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { Directory.Delete(RunDirectory, true); } catch { }
        };
    }
}
