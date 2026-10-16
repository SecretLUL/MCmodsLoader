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

    // What a build from before the rename kept that is worth carrying over; everything else there is a cache.
    private static readonly string[] LegacyFiles = { "settings.json", "mods.v2.json" };

    /// <summary>
    /// Renames the data folder of a build from before the rename, so its settings and mod
    /// choices carry over. Where that cannot be done (a file in it is locked, or a folder under
    /// the new name exists already because an earlier attempt failed), the files the new folder
    /// lacks are copied instead; nothing in the new folder is ever overwritten. Never throws:
    /// without it the app simply starts with fresh settings.
    /// </summary>
    public static void MigrateLegacyDataDirectory() => MigrateLegacyDataDirectory(LocalAppData);

    internal static bool MigrateLegacyDataDirectory(string localAppData)
    {
        string legacy = Path.Combine(localAppData, LegacyAppName);
        string current = Path.Combine(localAppData, AppName);
        if (!Directory.Exists(legacy))
            return false;

        if (!Directory.Exists(current))
        {
            try
            {
                Directory.Move(legacy, current);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Copied below instead.
            }
        }

        bool copied = false;
        foreach (string name in LegacyFiles)
        {
            try
            {
                string from = Path.Combine(legacy, name);
                string to = Path.Combine(current, name);
                if (!File.Exists(from) || File.Exists(to))
                    continue;

                Directory.CreateDirectory(current);
                File.Copy(from, to);
                copied = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Tried again on the next start, while the new folder still lacks the file.
            }
        }

        return copied;
    }
}
