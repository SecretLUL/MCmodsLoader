namespace MCperfBooster.Core.Utils;

/// <summary>
/// Where the app keeps its own files (settings, the cached mod list, downloaded Java
/// runtimes): %LOCALAPPDATA%\MCperfBooster. Builds from before the rename used
/// %LOCALAPPDATA%\MCmodsLoader, which <see cref="MigrateLegacyDataDirectory()"/> takes over.
/// </summary>
public static class AppPaths
{
    public const string AppName = "MCperfBooster";

    internal const string LegacyAppName = "MCmodsLoader";

    public static string DataDirectory => Path.Combine(LocalAppData, AppName);

    private static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    /// <summary>
    /// Renames the data folder of a build from before the rename, so its settings and mod
    /// choices carry over. Only while no folder under the new name exists yet, so it runs
    /// once. Never throws: without it the app simply starts with fresh settings.
    /// </summary>
    public static void MigrateLegacyDataDirectory() => MigrateLegacyDataDirectory(LocalAppData);

    internal static bool MigrateLegacyDataDirectory(string localAppData)
    {
        try
        {
            string legacy = Path.Combine(localAppData, LegacyAppName);
            string current = Path.Combine(localAppData, AppName);
            if (!Directory.Exists(legacy) || Directory.Exists(current))
                return false;

            Directory.Move(legacy, current);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
