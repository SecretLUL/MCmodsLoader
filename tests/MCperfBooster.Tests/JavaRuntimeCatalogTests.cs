using MCperfBooster.Core.Services;

namespace MCperfBooster.Tests;

public class JavaRuntimeCatalogTests : IDisposable
{
    private readonly string _minecraftDir = TempFolders.Create("catalog");

    public void Dispose() => TempFolders.Delete(_minecraftDir);

    private void WriteVersionJson(string mcVersion, string json)
    {
        string dir = Path.Combine(_minecraftDir, "versions", mcVersion);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, $"{mcVersion}.json"), json);
    }

    [Theory]
    // Older than 1.16.5: Java stays as it is.
    [InlineData("1.8.9", null)]
    [InlineData("1.14.4", null)]
    [InlineData("1.16.4", null)]
    // 1.16.5 to 1.20.4 all get 17, although Mojang names 8 and 16 for the oldest of them.
    [InlineData("1.16.5", 17)]
    [InlineData("1.17", 17)]
    [InlineData("1.17.1", 17)]
    [InlineData("1.18", 17)]
    [InlineData("1.19.4", 17)]
    [InlineData("1.20", 17)]
    [InlineData("1.20.4", 17)]
    // 1.20.5 up to the year-based versions: 21.
    [InlineData("1.20.5", 21)]
    [InlineData("1.20.6", 21)]
    [InlineData("1.21", 21)]
    [InlineData("1.21.4", 21)]
    [InlineData("1.21.11", 21)]
    // Year-based versions: 25.
    [InlineData("26", 25)]
    [InlineData("26.1", 25)]
    [InlineData("26.1.2", 25)]
    [InlineData("26.3", 25)]
    [InlineData("27.1", 25)]
    // Not versions at all.
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("latest-release", null)]
    public void GetJavaMajor_FollowsTheTable(string mcVersion, int? expected)
    {
        Assert.Equal(expected, JavaRuntimeCatalog.GetJavaMajor(mcVersion));
    }

    [Theory]
    // Newer than the table, with a build: the higher Java wins.
    [InlineData("1.21.5", 25, 25)]
    [InlineData("1.20.4", 21, 21)]
    // Newer than the table and than any build: no GraalVM, the launcher's Java stays.
    [InlineData("1.21.5", 29, null)]
    [InlineData("1.21.5", 22, null)]
    [InlineData("26.1", 29, null)]
    // Equal or smaller values change nothing: 1.16.5 names Java 8, the table says 17 on purpose.
    [InlineData("1.21.5", 21, 21)]
    [InlineData("1.21.5", 17, 21)]
    [InlineData("1.16.5", 8, 17)]
    [InlineData("26.1", 25, 25)]
    // Too old for the table: Java is left alone whatever the json says.
    [InlineData("1.16.4", 21, null)]
    public void GetJavaMajor_WithVersionJson_TakesAHigherJavaButNeverALowerOne(string mcVersion, int declared, int? expected)
    {
        WriteVersionJson(mcVersion, $$"""{ "id": "{{mcVersion}}", "javaVersion": { "component": "x", "majorVersion": {{declared}} } }""");

        Assert.Equal(expected, JavaRuntimeCatalog.GetJavaMajor(mcVersion, _minecraftDir));
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("""{ "id": "1.21.5" }""")]
    [InlineData("""{ "javaVersion": "25" }""")]
    [InlineData("""{ "javaVersion": { "majorVersion": "29" } }""")]
    [InlineData("""{ "javaVersion": { "majorVersion": 2.5 } }""")]
    public void GetJavaMajor_WithUnusableVersionJson_FallsBackToTheTable(string json)
    {
        WriteVersionJson("1.21.5", json);

        Assert.Equal(21, JavaRuntimeCatalog.GetJavaMajor("1.21.5", _minecraftDir));
    }

    [Fact]
    public void GetJavaMajor_WithoutVersionJsonOrFolder_FallsBackToTheTable()
    {
        Assert.Equal(21, JavaRuntimeCatalog.GetJavaMajor("1.21.5", _minecraftDir));
        Assert.Equal(21, JavaRuntimeCatalog.GetJavaMajor("1.21.5", Path.Combine(_minecraftDir, "missing")));
        Assert.Equal(21, JavaRuntimeCatalog.GetJavaMajor("1.21.5", ""));
        Assert.Equal(21, JavaRuntimeCatalog.GetJavaMajor("1.21.5", "\0invalid"));
    }

    [Fact]
    public void GetJavaMajor_ReadsVersionJsonWithCommentsAndTrailingCommas()
    {
        WriteVersionJson("1.21.5", """
            {
                // written by hand
                "javaVersion": { "majorVersion": 25, },
            }
            """);

        Assert.Equal(25, JavaRuntimeCatalog.GetJavaMajor("1.21.5", _minecraftDir));
    }

    [Theory]
    [InlineData(17, "https://download.oracle.com/graalvm/17/archive/graalvm-jdk-17.0.12_windows-x64_bin.zip",
        "8b978e56dddc0edc60db99794b56975740d9c52293b31549cfc3f7516fc18b43", "17.0.12", 318)]
    [InlineData(21, "https://download.oracle.com/graalvm/21/archive/graalvm-jdk-21.0.12_windows-x64_bin.zip",
        "7560971e52b236ddba7a22e7a682a9a185e56a2f6ee9cd6c4ecbb800e9643990", "21.0.12", 325)]
    public void GetBuild_PinsTheFrozenBuilds(int javaMajor, string url, string sha256, string version, int megabytes)
    {
        var build = JavaRuntimeCatalog.GetBuild(javaMajor);

        Assert.NotNull(build);
        Assert.Equal(javaMajor, build.JavaMajor);
        Assert.Equal(url, build.DownloadUrl);
        Assert.Equal(sha256, build.PinnedSha256);
        Assert.Null(build.ChecksumUrl);
        Assert.Equal(version, build.FixedVersion);
        Assert.Equal(megabytes, build.ApproximateDownloadMegabytes);
    }

    [Fact]
    public void GetBuild_ForJava25FollowsLatestAndTakesTheChecksumFromTheSha256File()
    {
        var build = JavaRuntimeCatalog.GetBuild(25);

        Assert.NotNull(build);
        Assert.Equal(25, build.JavaMajor);
        Assert.Equal("https://download.oracle.com/graalvm/25/latest/graalvm-jdk-25_windows-x64_bin.zip", build.DownloadUrl);
        Assert.Equal(build.DownloadUrl + ".sha256", build.ChecksumUrl);
        Assert.Null(build.PinnedSha256);
        Assert.Null(build.FixedVersion);
        Assert.Equal(362, build.ApproximateDownloadMegabytes);
    }

    [Fact]
    public void GetBuild_DownloadsOnlyFromOraclesDownloadServerOverHttps()
    {
        foreach (int major in new[] { 17, 21, 25 })
        {
            var build = JavaRuntimeCatalog.GetBuild(major)!;
            Assert.StartsWith("https://download.oracle.com/", build.DownloadUrl);
            if (build.ChecksumUrl != null)
                Assert.StartsWith("https://download.oracle.com/", build.ChecksumUrl);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    [InlineData(11)]
    [InlineData(16)]
    [InlineData(18)]
    [InlineData(22)]
    [InlineData(24)]
    [InlineData(26)]
    [InlineData(-1)]
    public void GetBuild_KnowsOnlyTheSupportedJavaVersions(int javaMajor)
    {
        Assert.Null(JavaRuntimeCatalog.GetBuild(javaMajor));
    }

    [Fact]
    public void EveryJavaTheTableHandsOutHasABuild()
    {
        foreach (string version in new[] { "1.16.5", "1.17.1", "1.18", "1.20.4", "1.20.5", "1.21.11", "26.1", "27.1" })
        {
            int major = JavaRuntimeCatalog.GetJavaMajor(version)!.Value;
            Assert.NotNull(JavaRuntimeCatalog.GetBuild(major));
        }
    }
}
