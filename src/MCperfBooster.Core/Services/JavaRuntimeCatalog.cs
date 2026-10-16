using System.Text.Json;
using MCperfBooster.Core.Models;
using MCperfBooster.Core.Utils;

namespace MCperfBooster.Core.Services;

/// <summary>
/// Which Java a Minecraft version should run on and where to get an Oracle GraalVM JDK
/// of that major version (Windows x64, GFTC license).
/// </summary>
public static class JavaRuntimeCatalog
{
    // 17 and 21 are frozen builds, so their checksums can be pinned. 25 follows "latest":
    // its .sha256 file changes whenever Oracle ships an update. Sizes are in the megabytes
    // Windows and the progress bar count (1024 x 1024 bytes).
    private static readonly GraalVmBuild Graal17 = new(
        17,
        "https://download.oracle.com/graalvm/17/archive/graalvm-jdk-17.0.12_windows-x64_bin.zip",
        "8b978e56dddc0edc60db99794b56975740d9c52293b31549cfc3f7516fc18b43",
        ChecksumUrl: null,
        FixedVersion: "17.0.12",
        ApproximateDownloadMegabytes: 303);

    private static readonly GraalVmBuild Graal21 = new(
        21,
        "https://download.oracle.com/graalvm/21/archive/graalvm-jdk-21.0.12_windows-x64_bin.zip",
        "7560971e52b236ddba7a22e7a682a9a185e56a2f6ee9cd6c4ecbb800e9643990",
        ChecksumUrl: null,
        FixedVersion: "21.0.12",
        ApproximateDownloadMegabytes: 309);

    private static readonly GraalVmBuild Graal25 = new(
        25,
        "https://download.oracle.com/graalvm/25/latest/graalvm-jdk-25_windows-x64_bin.zip",
        PinnedSha256: null,
        ChecksumUrl: "https://download.oracle.com/graalvm/25/latest/graalvm-jdk-25_windows-x64_bin.zip.sha256",
        FixedVersion: null,
        ApproximateDownloadMegabytes: 345);

    private static readonly JsonDocumentOptions LenientJson = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// <summary>The GraalVM build for a Java major version, or null when there is none.</summary>
    public static GraalVmBuild? GetBuild(int javaMajor) => javaMajor switch
    {
        17 => Graal17,
        21 => Graal21,
        25 => Graal25,
        _ => null,
    };

    /// <summary>
    /// The Java major version to run a game version on, or null to leave Java alone
    /// (versions older than 1.16.5). 1.16.5 to 1.20.4 deliberately get 17 even though
    /// Mojang names 8 and 16 for the oldest of them.
    /// </summary>
    public static int? GetJavaMajor(string mcVersion)
    {
        if (string.IsNullOrWhiteSpace(mcVersion))
            return null;

        var compare = MinecraftVersionComparer.Instance;
        if (compare.Compare(mcVersion, "1.16.5") < 0)
            return null;
        if (compare.Compare(mcVersion, "1.20.5") < 0)
            return 17;

        // The year-based versions (26.1 and on) start at 26.
        return compare.Compare(mcVersion, "26") < 0 ? 21 : 25;
    }

    /// <summary>
    /// <see cref="GetJavaMajor(string)"/>, but a newer Java demanded by the installed
    /// version json wins. When no GraalVM build exists for that Java the result is
    /// null: better to leave the launcher's own Java in place than to start the game
    /// on one that is too old.
    /// </summary>
    public static int? GetJavaMajor(string mcVersion, string minecraftPath)
    {
        int? fromTable = GetJavaMajor(mcVersion);
        if (fromTable is null)
            return null;

        // Smaller values are ignored: 1.16.5 names 8, and the table says 17 on purpose.
        if (ReadDeclaredJavaMajor(mcVersion, minecraftPath) is int declared && declared > fromTable.Value)
            return GetBuild(declared) is null ? null : declared;

        return fromTable;
    }

    /// <summary>
    /// The Java the official launcher itself starts a game version with: what the installed version json
    /// names, otherwise Mojang's table (Java 8 up to 1.16.5, 16 for 1.17, 17 up to 1.20.4, 21 up to 1.21.x,
    /// 25 from 26.1). Arguments for the launcher's own Java must suit this one, not the GraalVM that
    /// <see cref="GetJavaMajor(string)"/> picks: Java 8 refuses the code heap options Java 17 knows.
    /// </summary>
    public static int GetLauncherJavaMajor(string mcVersion, string? minecraftPath = null)
    {
        if (minecraftPath != null && ReadDeclaredJavaMajor(mcVersion, minecraftPath) is int declared)
            return declared;

        var compare = MinecraftVersionComparer.Instance;
        if (compare.Compare(mcVersion, "1.17") < 0)
            return 8;
        if (compare.Compare(mcVersion, "1.18") < 0)
            return 16;
        if (compare.Compare(mcVersion, "1.20.5") < 0)
            return 17;

        return compare.Compare(mcVersion, "26") < 0 ? 21 : 25;
    }

    private static int? ReadDeclaredJavaMajor(string mcVersion, string minecraftPath)
    {
        if (string.IsNullOrWhiteSpace(minecraftPath))
            return null;

        try
        {
            string path = Path.Combine(minecraftPath, "versions", mcVersion, $"{mcVersion}.json");
            if (!File.Exists(path))
                return null;

            using var stream = File.OpenRead(path);
            using var doc = JsonDocument.Parse(stream, LenientJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("javaVersion", out var javaVersion) && javaVersion.ValueKind == JsonValueKind.Object &&
                javaVersion.TryGetProperty("majorVersion", out var major) && major.ValueKind == JsonValueKind.Number &&
                major.TryGetInt32(out int value))
            {
                return value;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            // A broken or unreadable file just means the table decides.
            return null;
        }

        return null;
    }
}
