using System.ComponentModel;
using System.Net;
using System.Text.Json;
using MCperfBooster.Core.Models;
using MCperfBooster.Core.Services;
using MCperfBooster.Core.Utils;

namespace MCperfBooster.Tests;

public class GraalVmServiceTests : IDisposable
{
    private const string Top21 = "graalvm-jdk-21.0.12+7.1";
    private const string Zip21Url = "https://download.oracle.com/graalvm/21/archive/graalvm-jdk-21.0.12_windows-x64_bin.zip";
    private const string Zip25Url = "https://download.oracle.com/graalvm/25/latest/graalvm-jdk-25_windows-x64_bin.zip";
    private const string Sha25Url = Zip25Url + ".sha256";
    private const string MarkerFileName = "mcperfbooster-runtime.json";

    private readonly string _base = TempFolders.Create("graalvm");

    private string Root => Path.Combine(_base, "runtimes");
    private string Home(int major) => Path.Combine(Root, $"graalvm-{major}");

    public void Dispose() => TempFolders.Delete(_base);

    private GraalVmService CreateService(FakeDownloadServer server, Func<int, GraalVmBuild?> catalog) =>
        new(new HttpClient(server), Root, catalog)
        {
            GetFreeSpace = _ => long.MaxValue,
            MoveRetryDelay = TimeSpan.Zero,
        };

    private static GraalVmBuild Pinned21(byte[] zip, string? sha256 = null) =>
        new(21, Zip21Url, sha256 ?? JdkZip.Sha256(zip), null, "21.0.12", 1);

    private static GraalVmBuild Latest25 { get; } = new(25, Zip25Url, null, Sha25Url, null, 1);

    private static Func<int, GraalVmBuild?> CatalogOf(GraalVmBuild build) => major => major == build.JavaMajor ? build : null;

    private GraalVmService ServiceFor21(byte[] zip, out FakeDownloadServer server)
    {
        server = new FakeDownloadServer().Serve(Zip21Url, zip);
        return CreateService(server, CatalogOf(Pinned21(zip)));
    }

    private static byte[] Zip25(string version, string top, string tag) => JdkZip.Create(top, version, tag);

    private static string[] RelativeFiles(string folder) =>
        Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(folder, f).Replace('\\', '/'))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToArray();

    [Fact]
    public async Task EnsureInstalledAsync_DownloadsVerifiesAndUnpacksTheRuntime()
    {
        byte[] zip = JdkZip.Create(Top21, "21.0.12");
        var service = ServiceFor21(zip, out var server);

        RuntimeInstallResult result = await service.EnsureInstalledAsync(21, null, CancellationToken.None);

        Assert.Null(result.Error);
        Assert.Null(result.Notice);
        Assert.True(result.Downloaded);
        var runtime = result.Runtime;
        Assert.NotNull(runtime);
        Assert.Equal(21, runtime.JavaMajor);
        Assert.Equal("21.0.12", runtime.Version);
        Assert.Equal(JdkZip.Sha256(zip), runtime.Sha256);
        Assert.Equal(Home(21), runtime.HomeDirectory);
        Assert.Equal(Path.Combine(Home(21), "bin", "java.exe"), runtime.JavaExe);
        Assert.Equal(Path.Combine(Home(21), "bin", "javaw.exe"), runtime.JavawExe);
        Assert.Equal(1, server.Count(Zip21Url));

        // The top folder is gone, the rest is in place, and the excluded parts were never written.
        Assert.Equal(
            new[] { "bin/java.exe", "bin/javaw.exe", "lib/modules", MarkerFileName, "release" }.OrderBy(f => f, StringComparer.Ordinal),
            RelativeFiles(Home(21)));
        Assert.Equal("javaw ", File.ReadAllText(runtime.JavawExe));
        Assert.False(Directory.Exists(Path.Combine(Home(21), "jmods")));
        Assert.False(Directory.Exists(Path.Combine(Home(21), "lib", "static")));
        Assert.False(Directory.Exists(Path.Combine(Home(21), "lib", "svm")));
        Assert.False(File.Exists(Path.Combine(Home(21), "lib", "src.zip")));
        Assert.False(Directory.Exists(Path.Combine(Home(21), Top21)));

        // Nothing but the runtime is left in the runtimes folder: no zip, staging folder or lock.
        Assert.Equal(new[] { Home(21) }, Directory.GetFileSystemEntries(Root));
        Assert.Equal(runtime, service.GetInstalled(21));
    }

    [Fact]
    public async Task EnsureInstalledAsync_WritesTheMarkerFile()
    {
        byte[] zip = JdkZip.Create(Top21, "21.0.12");
        var service = ServiceFor21(zip, out _);

        await service.EnsureInstalledAsync(21, null, CancellationToken.None);

        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Home(21), MarkerFileName)));
        Assert.Equal(21, doc.RootElement.GetProperty("javaMajor").GetInt32());
        Assert.Equal("21.0.12", doc.RootElement.GetProperty("version").GetString());
        Assert.Equal(JdkZip.Sha256(zip), doc.RootElement.GetProperty("sha256").GetString());
        Assert.Equal(Zip21Url, doc.RootElement.GetProperty("source").GetString());
    }

    [Fact]
    public async Task EnsureInstalledAsync_LeavesOutTheExcludedPathsWhateverTheCaseAndSeparator()
    {
        byte[] zip = JdkZip.Create(Top21, "21.0.12", extra: new[]
        {
            ("{top}/JMODS/upper.jmod", "x"),
            ("{top}/Lib/Static/mixed.lib", "x"),
            ("{top}\\lib\\SVM\\backslash.jar", "x"),
            ("{top}/LIB/SRC.ZIP", "x"),
            // Lookalikes that must be unpacked: the exclusions are exact folders at the top of the runtime.
            ("{top}/lib/static-libs/keep.txt", "keep"),
            ("{top}/lib/svm.txt", "keep"),
            ("{top}/legal/jmods/keep.txt", "keep"),
            ("{top}/lib/src.zip.txt", "keep"),
        });
        var service = ServiceFor21(zip, out _);

        RuntimeInstallResult result = await service.EnsureInstalledAsync(21, null, CancellationToken.None);

        Assert.Null(result.Error);
        Assert.Equal(
            new[] { "bin/java.exe", "bin/javaw.exe", "legal/jmods/keep.txt", "lib/modules", "lib/src.zip.txt", "lib/static-libs/keep.txt", "lib/svm.txt", MarkerFileName, "release" }
                .OrderBy(f => f, StringComparer.Ordinal),
            RelativeFiles(Home(21)));
    }

    [Fact]
    public async Task EnsureInstalledAsync_ReportsProgressForTheDownloadAndTheUnpacking()
    {
        byte[] zip = JdkZip.Create(Top21, "21.0.12");
        var service = ServiceFor21(zip, out _);
        var reports = new List<RuntimeDownloadProgress>();

        await service.EnsureInstalledAsync(21, new SyncProgress<RuntimeDownloadProgress>(reports.Add), CancellationToken.None);

        var download = reports.Where(r => r.Phase == RuntimeInstallPhase.Downloading).ToList();
        Assert.True(download.Count >= 2);
        Assert.Equal(0, download[0].BytesDone);
        Assert.Equal(zip.Length, download[^1].BytesDone);
        Assert.All(download, r => Assert.Equal(zip.Length, r.TotalBytes));

        long unpackedBytes = RelativeFiles(Home(21))
            .Where(f => f != MarkerFileName)
            .Sum(f => new FileInfo(Path.Combine(Home(21), f)).Length);
        var extract = reports.Where(r => r.Phase == RuntimeInstallPhase.Extracting).ToList();
        Assert.True(extract.Count >= 2);
        Assert.Equal(0, extract[0].BytesDone);
        Assert.Equal(unpackedBytes, extract[^1].BytesDone);
        Assert.All(extract, r => Assert.Equal(unpackedBytes, r.TotalBytes));

        // Downloading comes first, and the counters never run backwards.
        Assert.True(reports.TakeWhile(r => r.Phase == RuntimeInstallPhase.Downloading).Count() == download.Count);
        Assert.Equal(download.Select(r => r.BytesDone).OrderBy(b => b), download.Select(r => r.BytesDone));
        Assert.Equal(extract.Select(r => r.BytesDone).OrderBy(b => b), extract.Select(r => r.BytesDone));
    }

    [Fact]
    public async Task EnsureInstalledAsync_WhenAlreadyInstalled_DoesNotDownloadAgain()
    {
        byte[] zip = JdkZip.Create(Top21, "21.0.12");
        var service = ServiceFor21(zip, out var server);
        var first = await service.EnsureInstalledAsync(21, null, CancellationToken.None);
        File.WriteAllText(Path.Combine(Home(21), "kept.txt"), "still here");

        RuntimeInstallResult second = await service.EnsureInstalledAsync(21, null, CancellationToken.None);

        Assert.Null(second.Error);
        Assert.Null(second.Notice);
        Assert.False(second.Downloaded);
        Assert.Equal(first.Runtime, second.Runtime);
        Assert.Equal(1, server.Count(Zip21Url));
        Assert.Equal(1, server.TotalRequests);
        Assert.Equal("still here", File.ReadAllText(Path.Combine(Home(21), "kept.txt")));
    }

    [Fact]
    public async Task EnsureInstalledAsync_WithAWrongChecksum_FailsAndLeavesNothingBehind()
    {
        byte[] zip = JdkZip.Create(Top21, "21.0.12");
        var server = new FakeDownloadServer().Serve(Zip21Url, zip);
        var service = CreateService(server, CatalogOf(Pinned21(zip, sha256: new string('0', 64))));

        RuntimeInstallResult result = await service.EnsureInstalledAsync(21, null, CancellationToken.None);

        Assert.Null(result.Runtime);
        Assert.False(result.Downloaded);
        Assert.Contains("checksum", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, server.Count(Zip21Url));
        Assert.Empty(Directory.GetFileSystemEntries(Root));
    }

    [Fact]
    public async Task EnsureInstalledAsync_AcceptsTheChecksumInUpperCase()
    {
        byte[] zip = JdkZip.Create(Top21, "21.0.12");
        var server = new FakeDownloadServer().Serve(Zip21Url, zip);
        var service = CreateService(server, CatalogOf(Pinned21(zip, sha256: JdkZip.Sha256(zip).ToUpperInvariant())));

        RuntimeInstallResult result = await service.EnsureInstalledAsync(21, null, CancellationToken.None);

        Assert.Null(result.Error);
        Assert.Equal(JdkZip.Sha256(zip), result.Runtime!.Sha256);
    }

    [Fact]
    public async Task EnsureInstalledAsync_WithAMalformedPinnedChecksum_FailsWithoutDownloading()
    {
        byte[] zip = JdkZip.Create(Top21, "21.0.12");
        var server = new FakeDownloadServer().Serve(Zip21Url, zip);
        var service = CreateService(server, CatalogOf(Pinned21(zip, sha256: "not-a-hash")));

        RuntimeInstallResult result = await service.EnsureInstalledAsync(21, null, CancellationToken.None);

        Assert.Null(result.Runtime);
        Assert.Contains("SHA-256", result.Error);
        Assert.Equal(0, server.TotalRequests);
    }

    [Theory]
    // Leaves the folder at the top of the zip.
    [InlineData("../evil.txt")]
    // Leaves it from inside the top folder.
    [InlineData("{top}/../../evil.txt")]
    // Lands inside the staging folder but outside the runtime once the top folder is cut off.
    [InlineData("{top}/../evil.txt")]
    [InlineData("{top}/bin/../../../evil.txt")]
    // Absolute paths (a file in the test's own folder, so a failure cannot hurt anything else).
    [InlineData("{absolute}")]
    [InlineData("{absolute-with-slashes}")]
    public async Task EnsureInstalledAsync_WithAnEntryThatEscapesTheTargetFolder_IsRejectedAndLeavesNothing(string entryName)
    {
        string absolute = Path.Combine(_base, "evil-absolute.txt");
        entryName = entryName.Replace("{absolute-with-slashes}", absolute.Replace('\\', '/')).Replace("{absolute}", absolute);
        byte[] zip = JdkZip.Create(Top21, "21.0.12", extra: new[] { (entryName, "evil") });
        var service = ServiceFor21(zip, out _);

        RuntimeInstallResult result = await service.EnsureInstalledAsync(21, null, CancellationToken.None);

        Assert.Null(result.Runtime);
        Assert.Contains("outside", result.Error);
        Assert.Empty(Directory.GetFileSystemEntries(Root));
        Assert.False(File.Exists(absolute));
        Assert.False(File.Exists(Path.Combine(Root, "evil.txt")));
        Assert.False(File.Exists(Path.Combine(_base, "evil.txt")));
        Assert.False(File.Exists(Path.Combine(TestEnvironment.RunDirectory, "evil.txt")));
    }

    [Fact]
    public async Task EnsureInstalledAsync_WithMoreThanOneTopFolder_Fails()
    {
        byte[] zip = JdkZip.Create(Top21, "21.0.12", extra: new[] { ("other-folder/readme.txt", "x") });
        var service = ServiceFor21(zip, out _);

        RuntimeInstallResult result = await service.EnsureInstalledAsync(21, null, CancellationToken.None);

        Assert.Null(result.Runtime);
        Assert.Contains("top-level folders", result.Error);
        Assert.Empty(Directory.GetFileSystemEntries(Root));
    }

    [Fact]
    public async Task EnsureInstalledAsync_WithAFileAtTheTopOfTheZip_Fails()
    {
        byte[] zip = JdkZip.Create(Top21, "21.0.12", extra: new[] { ("readme.txt", "x") });
        var service = ServiceFor21(zip, out _);

        RuntimeInstallResult result = await service.EnsureInstalledAsync(21, null, CancellationToken.None);

        Assert.Null(result.Runtime);
        Assert.Contains("not inside a folder", result.Error);
        Assert.Empty(Directory.GetFileSystemEntries(Root));
    }

    [Fact]
    public async Task EnsureInstalledAsync_WithoutJavaw_FailsAndLeavesNothing()
    {
        byte[] zip = JdkZip.Create(Top21, "21.0.12", withJavaw: false);
        var service = ServiceFor21(zip, out _);

        RuntimeInstallResult result = await service.EnsureInstalledAsync(21, null, CancellationToken.None);

        Assert.Null(result.Runtime);
        Assert.Contains("javaw.exe", result.Error);
        Assert.Empty(Directory.GetFileSystemEntries(Root));
    }

    [Theory]
    [InlineData("JAVA_VERSION=\"17.0.12\"\n", "Java 17.0.12")]
    [InlineData("JAVA_VERSION_DATE=\"2026-07-21\"\nIMPLEMENTOR=\"Oracle\"\n", "release file")]
    [InlineData("", "release file")]
    public async Task EnsureInstalledAsync_WithAReleaseFileOfAnotherJava_Fails(string releaseText, string expectedInError)
    {
        byte[] zip = JdkZip.Create(Top21, "21.0.12", releaseText: releaseText);
        var service = ServiceFor21(zip, out _);

        RuntimeInstallResult result = await service.EnsureInstalledAsync(21, null, CancellationToken.None);

        Assert.Null(result.Runtime);
        Assert.Contains(expectedInError, result.Error);
        Assert.Empty(Directory.GetFileSystemEntries(Root));
    }

    [Fact]
    public async Task EnsureInstalledAsync_WithADownloadThatIsNotAZip_FailsClearly()
    {
        byte[] notAZip = "<html>Access denied</html>"u8.ToArray();
        var server = new FakeDownloadServer().Serve(Zip21Url, notAZip);
        var service = CreateService(server, CatalogOf(Pinned21(notAZip)));

        RuntimeInstallResult result = await service.EnsureInstalledAsync(21, null, CancellationToken.None);

        Assert.Null(result.Runtime);
        Assert.Contains("archive", result.Error);
        Assert.Empty(Directory.GetFileSystemEntries(Root));
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task EnsureInstalledAsync_WhenTheServerRefuses_FailsWithTheStatus(HttpStatusCode status)
    {
        byte[] zip = JdkZip.Create(Top21, "21.0.12");
        var server = new FakeDownloadServer().Fail(Zip21Url, status);
        var service = CreateService(server, CatalogOf(Pinned21(zip)));

        RuntimeInstallResult result = await service.EnsureInstalledAsync(21, null, CancellationToken.None);

        Assert.Null(result.Runtime);
        Assert.Contains($"HTTP {(int)status}", result.Error);
        Assert.Empty(Directory.GetFileSystemEntries(Root));
    }

    [Fact]
    public async Task EnsureInstalledAsync_WhenTheServerIsUnreachable_FailsWithTheReason()
    {
        byte[] zip = JdkZip.Create(Top21, "21.0.12");
        var server = new FakeDownloadServer().Unreachable(Zip21Url);
        var service = CreateService(server, CatalogOf(Pinned21(zip)));

        RuntimeInstallResult result = await service.EnsureInstalledAsync(21, null, CancellationToken.None);

        Assert.Null(result.Runtime);
        Assert.Contains("No such host is known", result.Error);
        Assert.Empty(Directory.GetFileSystemEntries(Root));
    }

    [Fact]
    public async Task EnsureInstalledAsync_ForAJavaWithoutABuild_FailsWithoutAnyRequest()
    {
        var server = new FakeDownloadServer();
        var service = CreateService(server, JavaRuntimeCatalog.GetBuild);

        RuntimeInstallResult result = await service.EnsureInstalledAsync(8, null, CancellationToken.None);

        Assert.Null(result.Runtime);
        Assert.Contains("Java 8", result.Error);
        Assert.Equal(0, server.TotalRequests);
        Assert.False(Directory.Exists(Root));
    }

    [Fact]
    public async Task EnsureInstalledAsync_WithANewChecksum_ReplacesTheInstalledVersion()
    {
        const string oldTop = "graalvm-jdk-25.0.3+9.1";
        const string newTop = "graalvm-jdk-25.0.4+7.1";
        byte[] oldZip = Zip25("25.0.3", oldTop, "old");
        byte[] newZip = Zip25("25.0.4", newTop, "new");
        var server = new FakeDownloadServer().Serve(Zip25Url, oldZip).ServeText(Sha25Url, JdkZip.Sha256(oldZip) + "\n");
        var service = CreateService(server, CatalogOf(Latest25));

        // The version of a "latest" build is only known from the unpacked release file.
        RuntimeInstallResult first = await service.EnsureInstalledAsync(25, null, CancellationToken.None);
        Assert.Null(first.Error);
        Assert.True(first.Downloaded);
        Assert.Equal("25.0.3", first.Runtime!.Version);
        File.WriteAllText(Path.Combine(Home(25), "lib", "only-in-old.txt"), "x");

        // Same checksum: asked again, not downloaded again.
        RuntimeInstallResult unchanged = await service.EnsureInstalledAsync(25, null, CancellationToken.None);
        Assert.False(unchanged.Downloaded);
        Assert.Equal(1, server.Count(Zip25Url));
        Assert.Equal(2, server.Count(Sha25Url));

        // Oracle publishes a new build: the checksum file changes and the version is replaced as a whole.
        server.Serve(Zip25Url, newZip).ServeText(Sha25Url, JdkZip.Sha256(newZip));
        RuntimeInstallResult updated = await service.EnsureInstalledAsync(25, null, CancellationToken.None);

        Assert.Null(updated.Error);
        Assert.Null(updated.Notice);
        Assert.True(updated.Downloaded);
        Assert.Equal("25.0.4", updated.Runtime!.Version);
        Assert.Equal(JdkZip.Sha256(newZip), updated.Runtime.Sha256);
        Assert.Equal(Home(25), updated.Runtime.HomeDirectory);
        Assert.Equal("modules new", File.ReadAllText(Path.Combine(Home(25), "lib", "modules")));
        Assert.False(File.Exists(Path.Combine(Home(25), "lib", "only-in-old.txt")));
        Assert.Equal(updated.Runtime, service.GetInstalled(25));
        Assert.Equal(new[] { Home(25) }, Directory.GetFileSystemEntries(Root));
    }

    [Theory]
    [InlineData("unreachable")]
    [InlineData("503")]
    [InlineData("garbage")]
    public async Task EnsureInstalledAsync_WhenTheChecksumCannotBeFetchedButARuntimeIsInstalled_UsesTheInstalledOne(string failure)
    {
        byte[] zip = Zip25("25.0.3", "graalvm-jdk-25.0.3+9.1", "old");
        var server = new FakeDownloadServer().Serve(Zip25Url, zip).ServeText(Sha25Url, JdkZip.Sha256(zip));
        var service = CreateService(server, CatalogOf(Latest25));
        RuntimeInstallResult installed = await service.EnsureInstalledAsync(25, null, CancellationToken.None);

        switch (failure)
        {
            case "unreachable": server.Unreachable(Sha25Url); break;
            case "503": server.Fail(Sha25Url, HttpStatusCode.ServiceUnavailable); break;
            default: server.ServeText(Sha25Url, "<html>Please log in to the hotspot</html>"); break;
        }

        RuntimeInstallResult result = await service.EnsureInstalledAsync(25, null, CancellationToken.None);

        Assert.Null(result.Error);
        Assert.False(result.Downloaded);
        Assert.Equal(installed.Runtime, result.Runtime);
        Assert.False(string.IsNullOrWhiteSpace(result.Notice));
        Assert.Contains("25.0.3", result.Notice);
        Assert.Equal(1, server.Count(Zip25Url));
    }

    [Fact]
    public async Task EnsureInstalledAsync_WhenTheChecksumCannotBeFetchedAndNothingIsInstalled_Fails()
    {
        var server = new FakeDownloadServer().Unreachable(Sha25Url);
        var service = CreateService(server, CatalogOf(Latest25));

        RuntimeInstallResult result = await service.EnsureInstalledAsync(25, null, CancellationToken.None);

        Assert.Null(result.Runtime);
        Assert.Contains("checksum", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, server.Count(Zip25Url));
        Assert.Empty(Directory.GetFileSystemEntries(Root));
    }

    [Theory]
    [InlineData("{HASH}\n")]
    [InlineData("\uFEFF{hash}")]
    [InlineData("{hash}  graalvm-jdk-25_windows-x64_bin.zip\r\n")]
    public async Task EnsureInstalledAsync_ReadsTheChecksumFileInItsCommonShapes(string shaFileContent)
    {
        // The checksum has to be the test zip's own, so the file content is a template for it.
        byte[] zip = Zip25("25.0.3", "graalvm-jdk-25.0.3+9.1", "old");
        string hash = JdkZip.Sha256(zip);
        string content = shaFileContent.Replace("{hash}", hash).Replace("{HASH}", hash.ToUpperInvariant());
        var server = new FakeDownloadServer().Serve(Zip25Url, zip).ServeText(Sha25Url, content);
        var service = CreateService(server, CatalogOf(Latest25));

        RuntimeInstallResult result = await service.EnsureInstalledAsync(25, null, CancellationToken.None);

        Assert.Null(result.Error);
        Assert.Equal(hash, result.Runtime!.Sha256);
    }

    [Fact]
    public async Task EnsureInstalledAsync_WhenTheInstalledRuntimeIsInUse_KeepsItAndSaysSo()
    {
        byte[] oldZip = Zip25("25.0.3", "graalvm-jdk-25.0.3+9.1", "old");
        byte[] newZip = Zip25("25.0.4", "graalvm-jdk-25.0.4+7.1", "new");
        var server = new FakeDownloadServer().Serve(Zip25Url, oldZip).ServeText(Sha25Url, JdkZip.Sha256(oldZip));
        var service = CreateService(server, CatalogOf(Latest25));
        var installed = (await service.EnsureInstalledAsync(25, null, CancellationToken.None)).Runtime!;
        server.Serve(Zip25Url, newZip).ServeText(Sha25Url, JdkZip.Sha256(newZip));

        RuntimeInstallResult blocked;
        // Minecraft running on the runtime holds javaw.exe like this.
        using (new FileStream(installed.JavawExe, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            blocked = await service.EnsureInstalledAsync(25, null, CancellationToken.None);
        }

        Assert.Null(blocked.Error);
        Assert.False(blocked.Downloaded);
        Assert.Equal(installed, blocked.Runtime);
        Assert.Contains("in use", blocked.Notice);
        Assert.Equal("javaw old", File.ReadAllText(installed.JavawExe));
        Assert.Equal(new[] { Home(25) }, Directory.GetFileSystemEntries(Root));

        // Once Minecraft is closed, the next try goes through.
        RuntimeInstallResult retried = await service.EnsureInstalledAsync(25, null, CancellationToken.None);
        Assert.Null(retried.Error);
        Assert.True(retried.Downloaded);
        Assert.Equal("25.0.4", retried.Runtime!.Version);
        Assert.Equal(new[] { Home(25) }, Directory.GetFileSystemEntries(Root));
    }

    [Fact]
    public async Task EnsureInstalledAsync_ReplacesAFolderThatHasNoMarker()
    {
        Directory.CreateDirectory(Path.Combine(Home(21), "bin"));
        File.WriteAllText(Path.Combine(Home(21), "bin", "javaw.exe"), "half-deleted install");
        byte[] zip = JdkZip.Create(Top21, "21.0.12");
        var service = ServiceFor21(zip, out _);
        Assert.Null(service.GetInstalled(21));

        RuntimeInstallResult result = await service.EnsureInstalledAsync(21, null, CancellationToken.None);

        Assert.Null(result.Error);
        Assert.True(result.Downloaded);
        Assert.Equal("javaw ", File.ReadAllText(result.Runtime!.JavawExe));
        Assert.Equal(new[] { Home(21) }, Directory.GetFileSystemEntries(Root));
    }

    [Fact]
    public async Task EnsureInstalledAsync_SweepsTheLeftoversOfEarlierRuns()
    {
        Directory.CreateDirectory(Path.Combine(Root, ".staging-graalvm-21-abc", "bin"));
        File.WriteAllText(Path.Combine(Root, ".staging-graalvm-21-abc", "bin", "java.exe"), "x");
        Directory.CreateDirectory(Path.Combine(Root, ".old-graalvm-17-def"));
        File.WriteAllText(Path.Combine(Root, ".old-graalvm-17-def", "release"), "x");
        File.WriteAllText(Path.Combine(Root, ".download-graalvm-21.zip"), "half a download");
        File.WriteAllText(Path.Combine(Root, "keep.txt"), "not ours to delete");
        Directory.CreateDirectory(Home(17));
        File.WriteAllText(Path.Combine(Home(17), "release"), "another runtime");
        byte[] zip = JdkZip.Create(Top21, "21.0.12");
        var service = ServiceFor21(zip, out _);

        RuntimeInstallResult result = await service.EnsureInstalledAsync(21, null, CancellationToken.None);

        Assert.Null(result.Error);
        Assert.Equal(
            new[] { Path.Combine(Root, "keep.txt"), Home(17), Home(21) }.OrderBy(p => p, StringComparer.OrdinalIgnoreCase),
            Directory.GetFileSystemEntries(Root).OrderBy(p => p, StringComparer.OrdinalIgnoreCase));
    }

    [Theory]
    // About 10 MB of download plus 700 MB for the unpacked runtime.
    [InlineData(709L, false)]
    [InlineData(710L, true)]
    public async Task EnsureInstalledAsync_NeedsTheDownloadSizePlus700MegabytesFree(long freeMegabytes, bool expectedToInstall)
    {
        byte[] zip = JdkZip.Create(Top21, "21.0.12");
        var server = new FakeDownloadServer().Serve(Zip21Url, zip);
        var build = Pinned21(zip) with { ApproximateDownloadMegabytes = 10 };
        var service = CreateService(server, CatalogOf(build));
        service.GetFreeSpace = _ => freeMegabytes * 1024 * 1024;

        RuntimeInstallResult result = await service.EnsureInstalledAsync(21, null, CancellationToken.None);

        if (expectedToInstall)
        {
            Assert.Null(result.Error);
            Assert.NotNull(result.Runtime);
            return;
        }

        Assert.Null(result.Runtime);
        Assert.Contains("free disk space", result.Error);
        Assert.Contains("710 MB are needed", result.Error);
        Assert.Contains("709 MB are free", result.Error);
        Assert.Equal(0, server.TotalRequests);
        Assert.Empty(Directory.GetFileSystemEntries(Root));
    }

    [Fact]
    public async Task EnsureInstalledAsync_WhenTheFreeSpaceIsUnknown_StillTriesTheInstall()
    {
        byte[] zip = JdkZip.Create(Top21, "21.0.12");
        var service = ServiceFor21(zip, out _);
        service.GetFreeSpace = _ => null;

        RuntimeInstallResult result = await service.EnsureInstalledAsync(21, null, CancellationToken.None);

        Assert.Null(result.Error);
        Assert.NotNull(result.Runtime);
    }

    [Fact]
    public async Task EnsureInstalledAsync_WhenTheDownloadStalls_GivesUpAndCleansUp()
    {
        byte[] zip = JdkZip.Create(Top21, "21.0.12");
        var server = new FakeDownloadServer().Route(Zip21Url, () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new StallingStream(zip.AsSpan(0, 20).ToArray())),
        });
        var service = CreateService(server, CatalogOf(Pinned21(zip)));
        service.IdleTimeout = TimeSpan.FromMilliseconds(300);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        RuntimeInstallResult result = await service.EnsureInstalledAsync(21, null, CancellationToken.None);

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), $"Took {watch.Elapsed}");
        Assert.Null(result.Runtime);
        Assert.Contains("stalled", result.Error);
        Assert.Empty(Directory.GetFileSystemEntries(Root));
    }

    [Fact]
    public async Task EnsureInstalledAsync_WhenCancelledWhileDownloading_ThrowsAndCleansUp()
    {
        byte[] zip = JdkZip.Create(Top21, "21.0.12");
        var server = new FakeDownloadServer().Route(Zip21Url, () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new StallingStream(zip.AsSpan(0, 20).ToArray())),
        });
        var service = CreateService(server, CatalogOf(Pinned21(zip)));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.EnsureInstalledAsync(21, null, cts.Token));

        Assert.Empty(Directory.GetFileSystemEntries(Root));
    }

    [Fact]
    public async Task EnsureInstalledAsync_WhenAlreadyCancelled_ThrowsWithoutTouchingTheNetwork()
    {
        byte[] zip = JdkZip.Create(Top21, "21.0.12");
        var service = ServiceFor21(zip, out var server);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.EnsureInstalledAsync(21, null, cts.Token));

        Assert.Equal(0, server.TotalRequests);
        Assert.Empty(Directory.GetFileSystemEntries(Root));
    }

    [Fact]
    public async Task EnsureInstalledAsync_WhenCancelledWhileUnpacking_ThrowsAndCleansUp()
    {
        byte[] zip = JdkZip.Create(Top21, "21.0.12");
        var service = ServiceFor21(zip, out _);
        using var cts = new CancellationTokenSource();
        var progress = new SyncProgress<RuntimeDownloadProgress>(p =>
        {
            if (p.Phase == RuntimeInstallPhase.Extracting)
                cts.Cancel();
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.EnsureInstalledAsync(21, progress, cts.Token));

        Assert.Empty(Directory.GetFileSystemEntries(Root));
    }

    [Fact]
    public async Task EnsureInstalledAsync_WhileAnotherInstallHoldsTheLock_FailsAndTouchesNothing()
    {
        Directory.CreateDirectory(Root);
        File.WriteAllText(Path.Combine(Root, ".download-graalvm-21.zip"), "the other install's download");
        byte[] zip = JdkZip.Create(Top21, "21.0.12");
        var service = ServiceFor21(zip, out var server);

        RuntimeInstallResult result;
        using (new FileStream(Path.Combine(Root, ".install.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            result = await service.EnsureInstalledAsync(21, null, CancellationToken.None);
        }

        Assert.Null(result.Runtime);
        Assert.Contains("another", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, server.TotalRequests);
        Assert.Equal("the other install's download", File.ReadAllText(Path.Combine(Root, ".download-graalvm-21.zip")));
    }

    [Fact]
    public void GetInstalled_NeedsTheMarkerAndJavaw()
    {
        var service = CreateService(new FakeDownloadServer(), _ => null);
        Assert.Null(service.GetInstalled(21));

        // Javaw without a marker.
        Directory.CreateDirectory(Path.Combine(Home(21), "bin"));
        File.WriteAllText(Path.Combine(Home(21), "bin", "javaw.exe"), "x");
        Assert.Null(service.GetInstalled(21));

        // A marker for another Java, or one that is broken.
        File.WriteAllText(Path.Combine(Home(21), MarkerFileName), """{ "javaMajor": 17, "version": "17.0.12", "sha256": "abc" }""");
        Assert.Null(service.GetInstalled(21));
        File.WriteAllText(Path.Combine(Home(21), MarkerFileName), "{ not json");
        Assert.Null(service.GetInstalled(21));
        File.WriteAllText(Path.Combine(Home(21), MarkerFileName), """{ "javaMajor": 21, "version": "", "sha256": "abc" }""");
        Assert.Null(service.GetInstalled(21));
        File.WriteAllText(Path.Combine(Home(21), MarkerFileName), """{ "javaMajor": 21, "version": "21.0.12" }""");
        Assert.Null(service.GetInstalled(21));

        // A good marker, but no javaw.
        File.WriteAllText(Path.Combine(Home(21), MarkerFileName), """{ "javaMajor": 21, "version": "21.0.12", "sha256": "abc", "source": "x" }""");
        File.Delete(Path.Combine(Home(21), "bin", "javaw.exe"));
        Assert.Null(service.GetInstalled(21));

        File.WriteAllText(Path.Combine(Home(21), "bin", "javaw.exe"), "x");
        var runtime = service.GetInstalled(21);
        Assert.Equal(new InstalledRuntime(21, Home(21), Path.Combine(Home(21), "bin", "java.exe"), Path.Combine(Home(21), "bin", "javaw.exe"), "21.0.12", "abc"), runtime);
    }

    [Fact]
    public void RemoveAll_DeletesTheGraalVmFoldersAndNothingElse()
    {
        var service = CreateService(new FakeDownloadServer(), _ => null);
        foreach (string name in new[] { "graalvm-17", "graalvm-21", "graalvm-25", "other-runtime" })
        {
            Directory.CreateDirectory(Path.Combine(Root, name, "bin"));
            File.WriteAllText(Path.Combine(Root, name, "bin", "javaw.exe"), "x");
        }
        File.WriteAllText(Path.Combine(Home(21), MarkerFileName), """{ "javaMajor": 21, "version": "21.0.12", "sha256": "abc" }""");
        File.WriteAllText(Path.Combine(Root, "notes.txt"), "x");
        Assert.NotNull(service.GetInstalled(21));

        service.RemoveAll();

        Assert.Equal(
            new[] { Path.Combine(Root, "notes.txt"), Path.Combine(Root, "other-runtime") }.OrderBy(p => p),
            Directory.GetFileSystemEntries(Root).OrderBy(p => p));
        Assert.Null(service.GetInstalled(21));
    }

    [Fact]
    public void RemoveAll_WithoutARuntimesFolder_DoesNothing()
    {
        var service = CreateService(new FakeDownloadServer(), _ => null);

        service.RemoveAll();

        Assert.False(Directory.Exists(Root));
    }

    [Fact]
    public void RemoveAll_WhenARuntimeIsInUse_LeavesItNotInstalled()
    {
        var service = CreateService(new FakeDownloadServer(), _ => null);
        Directory.CreateDirectory(Path.Combine(Home(21), "bin"));
        string javaw = Path.Combine(Home(21), "bin", "javaw.exe");
        File.WriteAllText(javaw, "x");
        File.WriteAllText(Path.Combine(Home(21), MarkerFileName), """{ "javaMajor": 21, "version": "21.0.12", "sha256": "abc" }""");

        using (new FileStream(javaw, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            service.RemoveAll();
        }

        // The folder could not be deleted as a whole, but it no longer passes for an installed runtime.
        Assert.Null(service.GetInstalled(21));
    }

    [Fact]
    public void TheDefaultRuntimesFolder_LivesBelowTheAppsDataFolder()
    {
        var service = new GraalVmService(new HttpClient(new FakeDownloadServer()));

        Assert.Equal(Path.Combine(AppPaths.DataDirectory, "runtimes"), service.RuntimesRoot);
    }

    [Theory]
    [InlineData("21.0.12", 21)]
    [InlineData("25", 25)]
    [InlineData("25.0.4", 25)]
    [InlineData("17.0.12+8", 17)]
    [InlineData("1.8.0_402", 8)]
    [InlineData("1", 1)]
    [InlineData("", null)]
    [InlineData("abc", null)]
    [InlineData("1.x", null)]
    public void ParseJavaMajor_ReadsTheFirstNumber(string version, int? expected)
    {
        Assert.Equal(expected, GraalVmService.ParseJavaMajor(version));
    }

    private const string SampleHash = "8b978e56dddc0edc60db99794b56975740d9c52293b31549cfc3f7516fc18b43";

    [Theory]
    [InlineData("{hash}")]
    [InlineData("{HASH}\r\n")]
    [InlineData("  {hash}  file.zip")]
    [InlineData("﻿{hash}\n")]
    public void ParseSha256_ReadsAHashInItsCommonShapesAsLowerCase(string template)
    {
        string text = template.Replace("{hash}", SampleHash).Replace("{HASH}", SampleHash.ToUpperInvariant());

        Assert.Equal(SampleHash, GraalVmService.ParseSha256(text));
    }

    [Fact]
    public void ParseSha256_RejectsAnythingButA64DigitHexHash()
    {
        Assert.Equal(64, SampleHash.Length);
        foreach (string? bad in new[] { SampleHash[..63], SampleHash + "3", "z" + SampleHash[1..], "<html>404</html>", "", "   ", null })
        {
            Assert.Null(GraalVmService.ParseSha256(bad));
        }
    }

    [Theory]
    [InlineData("jmods/a.jmod", true)]
    [InlineData("jmods/", true)]
    [InlineData("JMODS/a.jmod", true)]
    [InlineData("lib/static/x.lib", true)]
    [InlineData("lib\\svm\\x.jar", true)]
    [InlineData("lib/src.zip", true)]
    [InlineData("LIB\\SRC.ZIP", true)]
    [InlineData("lib/modules", false)]
    [InlineData("lib/server/jvm.dll", false)]
    [InlineData("lib/static-libs/x", false)]
    [InlineData("legal/jmods/x", false)]
    [InlineData("bin/java.exe", false)]
    public void IsExcluded_MatchesTheFoldersAtTheTopOfTheRuntime(string path, bool expected)
    {
        Assert.Equal(expected, GraalVmService.IsExcluded(path));
    }

    private static InstalledRuntime FakeRuntime(string javaExe = @"C:\fake\bin\java.exe") =>
        new(21, @"C:\fake", javaExe, @"C:\fake\bin\javaw.exe", "21.0.12", "abc");

    private GraalVmService ServiceWithProcess(Func<string, IReadOnlyList<string>, TimeSpan, CancellationToken, Task<ProcessResult>> run)
    {
        var service = CreateService(new FakeDownloadServer(), _ => null);
        service.RunProcess = run;
        return service;
    }

    [Fact]
    public async Task TestArgumentsAsync_AppendsEagerJvmciAndVersionAndReportsSuccess()
    {
        string? file = null;
        IReadOnlyList<string>? args = null;
        TimeSpan? timeout = null;
        var service = ServiceWithProcess((f, a, t, _) =>
        {
            file = f;
            args = a;
            timeout = t;
            return Task.FromResult(new ProcessResult(0, "", "java version \"21.0.12\" 2026-07-21 LTS\nJava(TM) SE Runtime Environment Oracle GraalVM 21.0.12+7.1\n", false));
        });

        ArgumentTestResult result = await service.TestArgumentsAsync(FakeRuntime(), "-Xms1G  -Xmx2G -XX:+UseG1GC", CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Oracle GraalVM 21.0.12", result.Output);
        Assert.Equal(@"C:\fake\bin\java.exe", file);
        Assert.Equal(new[] { "-Xms1G", "-Xmx2G", "-XX:+UseG1GC", "-XX:+EagerJVMCI", "-version" }, args);
        Assert.Equal(TimeSpan.FromSeconds(60), timeout);
    }

    [Fact]
    public async Task TestArgumentsAsync_WithExitCode1_ReportsFailureAndTheJvmsComplaint()
    {
        var service = ServiceWithProcess((_, _, _, _) => Task.FromResult(new ProcessResult(
            1, "", "Error occurred during initialization of VM\nUnrecognized VM option 'Bogus'\n", false)));

        ArgumentTestResult result = await service.TestArgumentsAsync(FakeRuntime(), "-XX:+Bogus", CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Unrecognized VM option", result.Output);
    }

    [Fact]
    public async Task TestArgumentsAsync_FallsBackToStandardOutputAndShortensLongOutput()
    {
        string noisy = string.Join('\n', Enumerable.Range(1, 50).Select(i => $"line {i} " + new string('x', 100)));
        var service = ServiceWithProcess((_, _, _, _) => Task.FromResult(new ProcessResult(0, noisy, "", false)));

        ArgumentTestResult result = await service.TestArgumentsAsync(FakeRuntime(), "", CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Contains("line 50", result.Output);
        Assert.DoesNotContain("line 1 ", result.Output);
        Assert.True(result.Output.Length <= 403, $"Output was {result.Output.Length} characters");
    }

    [Fact]
    public async Task TestArgumentsAsync_WhenTheJvmHangs_ReportsTheTimeout()
    {
        var service = ServiceWithProcess((_, _, _, _) => Task.FromResult(new ProcessResult(-1, "", "", true)));

        ArgumentTestResult result = await service.TestArgumentsAsync(FakeRuntime(), "-Xmx2G", CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(-1, result.ExitCode);
        Assert.Contains("60 seconds", result.Output);
    }

    [Fact]
    public async Task TestArgumentsAsync_WhenJavaCannotBeStarted_ReportsItInsteadOfThrowing()
    {
        var service = ServiceWithProcess((_, _, _, _) => throw new Win32Exception(2, "The system cannot find the file specified."));

        ArgumentTestResult result = await service.TestArgumentsAsync(FakeRuntime(), "-Xmx2G", CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(-1, result.ExitCode);
        Assert.Contains("cannot find the file", result.Output);
    }

    [Fact]
    public async Task TestArgumentsAsync_HandsTheCancellationTokenOn()
    {
        var service = ServiceWithProcess((_, _, _, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new ProcessResult(0, "", "", false));
        });
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.TestArgumentsAsync(FakeRuntime(), "-Xmx2G", cts.Token));
    }

    [Fact]
    public async Task TestArgumentsAsync_RunsTheRealProcessRunnerByDefault()
    {
        // cmd.exe stands in for java.exe: it echoes the arguments back, so the whole path through the
        // default process start (arguments, output, exit code) is exercised without a Java installed.
        var service = CreateService(new FakeDownloadServer(), _ => null);
        string cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");

        ArgumentTestResult result = await service.TestArgumentsAsync(FakeRuntime(cmd), "/d /c echo hello", CancellationToken.None);

        Assert.True(result.Ok, result.Output);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("hello -XX:+EagerJVMCI -version", result.Output);
    }

    [Fact]
    public async Task TestArgumentsAsync_WhenTheExecutableIsMissing_ReportsItInsteadOfThrowing()
    {
        var service = CreateService(new FakeDownloadServer(), _ => null);

        ArgumentTestResult result = await service.TestArgumentsAsync(FakeRuntime(Path.Combine(_base, "missing", "java.exe")), "-Xmx2G", CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(-1, result.ExitCode);
        Assert.False(string.IsNullOrWhiteSpace(result.Output));
    }
}
