using System.IO.Compression;
using System.Text;
using System.Text.Json;
using MCmodsLoader.Core.Constants;
using MCmodsLoader.Core.Models;
using MCmodsLoader.Core.Services;
using MCmodsLoader.Core.Utils;

namespace MCmodsLoader.Tests;

public class ModPresetsTests
{
    [Fact]
    public void GetDefaultFpsModPack_ReturnsExpectedMods()
    {
        var mods = ModPresets.GetDefaultFpsModPack();
        Assert.NotNull(mods);
        Assert.Equal(15, mods.Count);
        Assert.DoesNotContain(mods, m => m.Slug == "polytone" || m.FabricModId == "polytone");

        foreach (var mod in mods)
        {
            Assert.False(string.IsNullOrWhiteSpace(mod.Slug), "Slug should not be empty");
            Assert.False(string.IsNullOrWhiteSpace(mod.Name), "Name should not be empty");
            Assert.False(string.IsNullOrWhiteSpace(mod.FabricModId), "FabricModId should not be empty");
        }
    }

    [Fact]
    public void GetDefaultFpsModPack_ContainsCrucialPerformanceMods()
    {
        var mods = ModPresets.GetDefaultFpsModPack();
        var slugs = mods.Select(m => m.Slug).ToList();

        Assert.Contains("sodium", slugs);
        Assert.Contains("lithium", slugs);
        Assert.Contains("ferrite-core", slugs);
        Assert.Contains("entityculling", slugs);
        Assert.Contains("immediatelyfast", slugs);
        Assert.Contains("lambdynamiclights", slugs);
        Assert.Contains("fabric-api", slugs);
    }
}

public class UpdateServiceTests
{
    [Theory]
    [InlineData("1.1.0", "1.0.0", true)]
    [InlineData("1.0.1", "1.0.0", true)]
    [InlineData("2.0.0", "1.9.9", true)]
    [InlineData("v1.1.0", "1.0.0", true)]
    [InlineData("v2.0.0", "v1.9.9", true)]
    [InlineData("1.0.0", "1.0.0", false)]
    [InlineData("0.9.0", "1.0.0", false)]
    [InlineData("1.0.0", "1.1.0", false)]
    [InlineData("1.2.3.4", "1.2.3.0", true)]
    public void IsNewerVersion_ComparesCorrectly(string latest, string current, bool expected)
    {
        bool result = UpdateService.IsNewerVersion(latest, current);
        Assert.Equal(expected, result);
    }

    [Fact]
    public async Task CheckForUpdateAsync_ParsesGitHubReleaseAndDetectsUpdate()
    {
        // HasUpdate is measured against this build's own version, which the build
        // resolves from the newest Git tag. The fixture therefore has to name a
        // release far beyond anything that will ever ship, otherwise the test starts
        // failing the moment the real version catches up with the number used here.
        var jsonResponse = @"{
            ""tag_name"": ""v99.0.0"",
            ""name"": ""v99.0.0 - Bugfix Release"",
            ""body"": ""Fixes minor issue"",
            ""html_url"": ""https://github.com/SecretLUL/MCmodsLoader/releases/tag/v99.0.0"",
            ""assets"": [
                {
                    ""name"": ""MCmodsLoader.exe"",
                    ""browser_download_url"": ""https://github.com/SecretLUL/MCmodsLoader/releases/download/v99.0.0/MCmodsLoader.exe""
                }
            ]
        }";

        var handler = new MockHttpHandler(jsonResponse);
        using var client = new HttpClient(handler);
        var service = new UpdateService(client);

        var update = await service.CheckForUpdateAsync("SecretLUL/MCmodsLoader");

        Assert.NotNull(update);
        Assert.True(update.HasUpdate);
        Assert.Equal("99.0.0", update.LatestVersion);
        Assert.Equal("https://github.com/SecretLUL/MCmodsLoader/releases/download/v99.0.0/MCmodsLoader.exe", update.DownloadUrl);
        Assert.Equal("v99.0.0 - Bugfix Release", update.ReleaseName);
    }

    [Fact]
    public async Task CheckForUpdateAsync_WhenCurrentIsUpToDate_HasUpdateIsFalse()
    {
        var jsonResponse = @"{
            ""tag_name"": ""v1.0.0"",
            ""name"": ""v1.0.0 Initial Release"",
            ""assets"": [
                {
                    ""name"": ""MCmodsLoader.exe"",
                    ""browser_download_url"": ""https://github.com/SecretLUL/MCmodsLoader/releases/download/v1.0.0/MCmodsLoader.exe""
                }
            ]
        }";

        var handler = new MockHttpHandler(jsonResponse);
        using var client = new HttpClient(handler);
        var service = new UpdateService(client);

        var update = await service.CheckForUpdateAsync("SecretLUL/MCmodsLoader");

        Assert.NotNull(update);
        Assert.False(update.HasUpdate);
        Assert.Equal("1.0.0", update.LatestVersion);
    }

    [Fact]
    public async Task DownloadAndApplyUpdateAsync_DownloadsAndCreatesUpdaterScript()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"UpdateTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            string fakeTargetExe = Path.Combine(tempDir, "MCmodsLoader.exe");
            await File.WriteAllTextAsync(fakeTargetExe, "Original Content v1.0.0");

            byte[] newBytes = Encoding.UTF8.GetBytes("New Binary Content v2.0.0");
            var handler = new MockBinaryHttpHandler(newBytes);
            using var client = new HttpClient(handler);
            var service = new UpdateService(client);

            double lastProgress = 0;
            var progress = new Progress<double>(p => lastProgress = p);

            bool ok = await service.DownloadAndApplyUpdateAsync(
                "https://dummy.url/MCmodsLoader.exe",
                progress,
                targetExePath: fakeTargetExe,
                launchAndExit: false,
                startExecutable: false);

            Assert.True(ok);

            string newExePath = Path.Combine(tempDir, "MCmodsLoader.new.exe");
            Assert.True(File.Exists(newExePath));
            string downloadedContent = await File.ReadAllTextAsync(newExePath);
            Assert.Equal("New Binary Content v2.0.0", downloadedContent);

            string batPath = Path.Combine(tempDir, "update_restart.bat");
            Assert.True(File.Exists(batPath));
            string batContent = await File.ReadAllTextAsync(batPath);
            Assert.Contains(Environment.ProcessId.ToString(), batContent);
            Assert.Contains("MCmodsLoader.new.exe", batContent);
            Assert.Contains("MCmodsLoader.exe", batContent);
            Assert.Contains("MOVE_RETRY", batContent);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }

    [Fact]
    public async Task UpdateRestartScript_ExecutesAndReplacesTargetExecutableSuccessfully()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"BatTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            string fakeTargetExe = Path.Combine(tempDir, "MCmodsLoader.exe");
            await File.WriteAllTextAsync(fakeTargetExe, "OLD_VERSION_100");

            byte[] newBytes = Encoding.UTF8.GetBytes("NEW_VERSION_200");
            var handler = new MockBinaryHttpHandler(newBytes);
            using var client = new HttpClient(handler);
            var service = new UpdateService(client);

            bool ok = await service.DownloadAndApplyUpdateAsync(
                "https://dummy.url/MCmodsLoader.exe",
                targetExePath: fakeTargetExe,
                launchAndExit: false,
                startExecutable: false,
                processIdToWait: 0);

            Assert.True(ok);

            string batPath = Path.Combine(tempDir, "update_restart.bat");
            string newExePath = Path.Combine(tempDir, "MCmodsLoader.new.exe");
            Assert.True(File.Exists(batPath));
            Assert.True(File.Exists(newExePath));

            // Execute the generated batch script via cmd.exe
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"{batPath}\"",
                WorkingDirectory = tempDir,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = System.Diagnostics.Process.Start(psi);
            Assert.NotNull(proc);
            bool exited = proc.WaitForExit(10000);
            Assert.True(exited, "Batch script execution should finish within 10 seconds");

            // Verify file replacement
            string updatedContent = await File.ReadAllTextAsync(fakeTargetExe);
            Assert.Equal("NEW_VERSION_200", updatedContent);

            // Verify new file was moved
            Assert.False(File.Exists(newExePath), "MCmodsLoader.new.exe should no longer exist after move");
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }
}

public class MockHttpHandler : HttpMessageHandler
{
    private readonly string _responseContent;
    private readonly System.Net.HttpStatusCode _statusCode;

    public MockHttpHandler(string responseContent, System.Net.HttpStatusCode statusCode = System.Net.HttpStatusCode.OK)
    {
        _responseContent = responseContent;
        _statusCode = statusCode;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = new HttpResponseMessage(_statusCode)
        {
            Content = new StringContent(_responseContent, Encoding.UTF8, "application/json")
        };
        return Task.FromResult(response);
    }
}

public class MockBinaryHttpHandler : HttpMessageHandler
{
    private readonly byte[] _binaryData;
    private readonly System.Net.HttpStatusCode _statusCode;

    public MockBinaryHttpHandler(byte[] binaryData, System.Net.HttpStatusCode statusCode = System.Net.HttpStatusCode.OK)
    {
        _binaryData = binaryData;
        _statusCode = statusCode;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = new HttpResponseMessage(_statusCode)
        {
            Content = new ByteArrayContent(_binaryData)
        };
        return Task.FromResult(response);
    }
}

public class MinecraftVersionComparerTests
{
    [Theory]
    [InlineData("1.21.11", "1.21.4", 1)]
    [InlineData("1.21.4", "1.9.4", 1)]
    [InlineData("26.2", "1.21.4", 1)]
    [InlineData("1.21.4", "1.21.4", 0)]
    [InlineData("1.9.4", "1.21.4", -1)]
    [InlineData("1.21.1", "1.21", 1)]
    [InlineData("26.3-pre-2", "26.3-pre-1", 1)]
    public void Compare_OrdersMinecraftVersionsCorrectly(string v1, string v2, int expectedSign)
    {
        int result = MinecraftVersionComparer.Instance.Compare(v1, v2);
        int sign = Math.Sign(result);
        Assert.Equal(expectedSign, sign);
    }
}

public class JarInspectorTests : IDisposable
{
    private readonly string _tempDir;

    public JarInspectorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"JarTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, true); } catch { }
        }
    }

    [Fact]
    public void ReadFabricModJson_ReturnsMetadata_ForValidJar()
    {
        string jarPath = Path.Combine(_tempDir, "testmod-1.0.0.jar");

        // Create a zip with fabric.mod.json
        using (var zipStream = new FileStream(jarPath, FileMode.Create))
        using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("fabric.mod.json");
            using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
            writer.Write(@"{
                ""schemaVersion"": 1,
                ""id"": ""sodium"",
                ""version"": ""0.5.8"",
                ""name"": ""Sodium""
            }");
        }

        var meta = JarInspector.ReadFabricModJson(jarPath);
        Assert.NotNull(meta);
        Assert.Equal("sodium", meta.Id);
        Assert.Equal("Sodium", meta.Name);
        Assert.Equal("0.5.8", meta.Version);
    }

    [Fact]
    public void ReadFabricModJson_ReturnsNull_ForMissingFile()
    {
        var meta = JarInspector.ReadFabricModJson(Path.Combine(_tempDir, "nonexistent.jar"));
        Assert.Null(meta);
    }

    [Fact]
    public void ReadFabricModJson_ReturnsNull_WhenFabricModJsonMissing()
    {
        string jarPath = Path.Combine(_tempDir, "empty.jar");
        using (var zipStream = new FileStream(jarPath, FileMode.Create))
        using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create))
        {
            archive.CreateEntry("dummy.txt");
        }

        var meta = JarInspector.ReadFabricModJson(jarPath);
        Assert.Null(meta);
    }

    [Fact]
    public void ReadFabricModJson_HandlesJsonWithCommentsAndTrailingCommas()
    {
        string jarPath = Path.Combine(_tempDir, "mod-with-comments.jar");

        using (var zipStream = new FileStream(jarPath, FileMode.Create))
        using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("fabric.mod.json");
            using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
            writer.Write(@"{
                // Fabric mod config
                ""schemaVersion"": 1,
                ""id"": ""lithium"",
                ""version"": ""0.15.0"",
                ""name"": ""Lithium"",
            }");
        }

        var meta = JarInspector.ReadFabricModJson(jarPath);
        Assert.NotNull(meta);
        Assert.Equal("lithium", meta.Id);
        Assert.Equal("Lithium", meta.Name);
    }
}

public class MinecraftServiceTests
{
    [Fact]
    public void GetDefaultMinecraftDirectory_ReturnsValidPathEndingWithMinecraft()
    {
        var service = new MinecraftService();
        string defaultPath = service.GetDefaultMinecraftDirectory();

        Assert.False(string.IsNullOrWhiteSpace(defaultPath));
        Assert.EndsWith(".minecraft", defaultPath);
    }

    [Fact]
    public void GetModsDirectory_CreatesAndReturnsModsFolder()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"McDirTest_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempRoot);
            var service = new MinecraftService();
            string modsDir = service.GetModsDirectory(tempRoot);

            Assert.True(Directory.Exists(modsDir));
            Assert.Equal(Path.Combine(tempRoot, "mods"), modsDir);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                try { Directory.Delete(tempRoot, true); } catch { }
            }
        }
    }

    [Theory]
    [InlineData("1.21.4", false)]
    [InlineData("1.21.1", false)]
    [InlineData("26.2", false)]
    [InlineData("26.1", false)]
    [InlineData("26.1.2", false)]
    [InlineData("1.21.11", false)]
    [InlineData("1.14.4", false)]
    [InlineData("26.3-snapshot-3", true)]
    [InlineData("26.3-pre-2", true)]
    [InlineData("26.1-rc-2", true)]
    [InlineData("26.1-snapshot-1", true)]
    [InlineData("24w14a", true)]
    [InlineData("26w14a", true)]
    [InlineData("1.21.11_unobfuscated", true)]
    [InlineData("1.14 Pre-Release 1", true)]
    [InlineData("1.18_experimental-snapshot-1", true)]
    public void IsSnapshot_DetectsSnapshotsAccurately(string version, bool expectedSnapshot)
    {
        bool isSnap = MinecraftService.IsSnapshot(version);
        Assert.Equal(expectedSnapshot, isSnap);
    }

    [Theory]
    [InlineData("1.21.4", true)]
    [InlineData("1.21.1", true)]
    [InlineData("26.2", true)]
    [InlineData("26.1", true)]
    [InlineData("26.1.2", true)]
    [InlineData("1.21.11", true)]
    [InlineData("26.3-snapshot-3", false)]
    [InlineData("26.3-pre-2", false)]
    [InlineData("26.1-rc-2", false)]
    [InlineData("24w14a", false)]
    public void IsOfficialRelease_OnlyMatchesOfficialReleases(string version, bool expectedOfficial)
    {
        bool isOfficial = MinecraftService.IsOfficialRelease(version);
        Assert.Equal(expectedOfficial, isOfficial);
    }

    [Fact]
    public void GetInstalledVersions_ExcludesSnapshotsAndOrdersByVersionDescending()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"McInstTest_{Guid.NewGuid():N}");
        try
        {
            string vDir = Path.Combine(tempDir, "versions");
            Directory.CreateDirectory(vDir);

            // Create fake version dirs: official and snapshots
            Directory.CreateDirectory(Path.Combine(vDir, "26.2"));
            File.WriteAllText(Path.Combine(vDir, "26.2", "26.2.json"), "{}");

            Directory.CreateDirectory(Path.Combine(vDir, "26.1"));
            File.WriteAllText(Path.Combine(vDir, "26.1", "26.1.json"), "{}");

            Directory.CreateDirectory(Path.Combine(vDir, "26.3-snapshot-3"));
            File.WriteAllText(Path.Combine(vDir, "26.3-snapshot-3", "26.3-snapshot-3.json"), "{}");

            Directory.CreateDirectory(Path.Combine(vDir, "26.1-rc-2"));
            File.WriteAllText(Path.Combine(vDir, "26.1-rc-2", "26.1-rc-2.json"), "{}");

            Directory.CreateDirectory(Path.Combine(vDir, "fabric-loader-0.19.5-26.2"));
            File.WriteAllText(Path.Combine(vDir, "fabric-loader-0.19.5-26.2", "fabric-loader-0.19.5-26.2.json"), "{}");

            var service = new MinecraftService();
            var versions = service.GetInstalledVersions(tempDir);

            Assert.Contains("26.2", versions);
            Assert.Contains("26.1", versions);
            Assert.DoesNotContain("26.3-snapshot-3", versions);
            Assert.DoesNotContain("26.1-rc-2", versions);
            Assert.Equal("26.2", versions[0]);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }

    [Fact]
    public async Task GetAllAvailableVersionsAsync_ExcludesSnapshotsFromApi()
    {
        string mockApiResponse = @"[
            { ""version"": ""26.3-pre-2"", ""stable"": false },
            { ""version"": ""26.3-snapshot-3"", ""stable"": false },
            { ""version"": ""26.2"", ""stable"": true },
            { ""version"": ""26.1.2"", ""stable"": true },
            { ""version"": ""26.1-rc-2"", ""stable"": false },
            { ""version"": ""26.1"", ""stable"": true },
            { ""version"": ""1.21.11"", ""stable"": true }
        ]";

        var handler = new MockHttpHandler(mockApiResponse);
        using var client = new HttpClient(handler);
        var service = new MinecraftService(client);

        var versions = await service.GetAllAvailableVersionsAsync(Path.GetTempPath());

        Assert.NotNull(versions);
        Assert.All(versions, v =>
        {
            Assert.False(MinecraftService.IsSnapshot(v.VersionId), $"Version {v.VersionId} should not be a snapshot");
            Assert.True(MinecraftService.IsOfficialRelease(v.VersionId), $"Version {v.VersionId} should be an official release");
        });

        Assert.Contains(versions, v => v.VersionId == "26.2");
        Assert.Contains(versions, v => v.VersionId == "26.1.2");
        Assert.Contains(versions, v => v.VersionId == "26.1");
        Assert.DoesNotContain(versions, v => v.VersionId == "26.3-snapshot-3");
        Assert.DoesNotContain(versions, v => v.VersionId == "26.3-pre-2");
        Assert.DoesNotContain(versions, v => v.VersionId == "26.1-rc-2");

        // Verify strictly descending version ordering
        Assert.Equal("26.2", versions[0].VersionId);
        Assert.Equal("26.1.2", versions[1].VersionId);
        Assert.Equal("26.1", versions[2].VersionId);
        Assert.Equal("1.21.11", versions[3].VersionId);
    }

    /// <summary>
    /// The version folders a real launcher leaves behind: official releases, the
    /// snapshot, pre-release and release-candidate builds that have to be filtered
    /// out, and Fabric loader entries whose base game version has to be recovered
    /// from the folder name.
    /// </summary>
    private static readonly string[] RealisticVersionFolders =
    {
        "1.21.11",
        "26.1",
        "26.1-rc-2",
        "26.1-snapshot-1",
        "26.2",
        "26.3-pre-2",
        "26.3-snapshot-3",
        "fabric-loader-0.18.3-1.21.11",
        "fabric-loader-0.19.5-26.2",

        // Every folder above mirrors a real install, where each Fabric entry sits
        // next to the plain folder of the same game version. That hides whether the
        // base version is really being recovered from the loader folder name, so one
        // Fabric entry here has no plain folder and appears nowhere else.
        "fabric-loader-0.18.3-1.21.9",
    };

    /// <summary>
    /// A launcher_profiles.json in the shape the vanilla launcher writes it: a pinned
    /// version, the two "latest-*" placeholders that are not version ids at all, and
    /// Fabric profiles naming a loader build rather than a game version.
    /// </summary>
    private const string RealisticLauncherProfiles = @"{
        ""profiles"": {
            ""068ff6c6fb1a852de3c9b2864cc99bdc"": { ""lastVersionId"": ""1.21.11"" },
            ""3e1f9c5314cb27903785f4a04ff651c6"": { ""lastVersionId"": ""latest-release"" },
            ""8cc7cf69ee0a4022e3c795f4f475ddc3"": { ""lastVersionId"": ""latest-snapshot"" },
            ""fabric-loader-1.21.11"": { ""lastVersionId"": ""fabric-loader-0.18.3-1.21.11"" },
            ""fabric-loader-26.2"": { ""lastVersionId"": ""fabric-loader-0.19.5-26.2"" },

            ""a1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6"": { ""lastVersionId"": ""1.21.4"" },
            ""fabric-loader-1.20.6"": { ""lastVersionId"": ""fabric-loader-0.16.0-1.20.6"" }
        }
    }";
    // The last two profiles have no folder on disk at all -- a profile still pinned to
    // a version whose folder was removed. Without them the profile list would be
    // shadowed entirely by the version folders and could stop being read unnoticed.

    [Fact]
    public void GetInstalledVersions_WithRealisticLauncherLayout_MergesEverySourceAndExcludesSnapshots()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"McLayoutTest_{Guid.NewGuid():N}");
        try
        {
            string versionsDir = Path.Combine(tempDir, "versions");
            foreach (string folder in RealisticVersionFolders)
            {
                string dir = Path.Combine(versionsDir, folder);
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, $"{folder}.json"), "{}");
            }

            File.WriteAllText(Path.Combine(tempDir, "launcher_profiles.json"), RealisticLauncherProfiles);

            var service = new MinecraftService();
            var installed = service.GetInstalledVersions(tempDir);

            Assert.DoesNotContain(installed, MinecraftService.IsSnapshot);
            Assert.DoesNotContain("latest-release", installed);
            Assert.DoesNotContain("latest-snapshot", installed);

            // Newest first, each release listed once even though 26.2 and 1.21.11 are
            // reachable three ways: a version folder, a Fabric loader folder and a
            // launcher profile. 1.21.9 can only come from a Fabric loader folder, and
            // 1.21.4 and 1.20.6 only from the launcher profiles.
            Assert.Equal(new[] { "26.2", "26.1", "1.21.11", "1.21.9", "1.21.4", "1.20.6" }, installed);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }
}

public class ModManagerServiceTests : IDisposable
{
    private readonly string _tempDir;

    public ModManagerServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"ModMgrTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, true); } catch { }
        }
    }

    [Fact]
    public void ScanModsDirectory_WhenEmpty_AllModsAreMissing()
    {
        var mockModrinth = new MockModrinthService();
        var manager = new ModManagerService(mockModrinth);

        var mods = manager.ScanModsDirectory(_tempDir);

        Assert.Equal(15, mods.Count);
        Assert.All(mods, m =>
        {
            Assert.Equal("Missing", m.Status);
            Assert.Null(m.InstalledFileName);
        });
    }

    /// <summary>
    /// A snapshot of a fully populated mods folder, taken from a real install: the
    /// file names and fabric.mod.json ids these mods actually ship with, plus one
    /// mod that is not part of the curated pack.
    ///
    /// Several entries only match through a non-obvious route, which is why the
    /// fixture is worth keeping literal: LambDynamicLights declares the id
    /// "lambdynlights", YACL's jar is named after its mod id rather than its
    /// Modrinth slug ("yacl"), and Xaero's two maps use ids that match neither.
    /// </summary>
    private static readonly (string Jar, string ModId, string Version)[] PopulatedModsFolder =
    {
        ("appleskin-fabric-mc26.2-3.0.10.jar", "appleskin", "3.0.10+mc26.2"),
        ("entityculling-fabric-1.10.5-mc26.2.jar", "entityculling", "1.10.5"),
        ("fabric-api-0.160.0+26.2.jar", "fabric-api", "0.160.0+26.2"),
        ("fabric-language-kotlin-1.14.1+kotlin.2.4.20.jar", "fabric-language-kotlin", "1.14.1+kotlin.2.4.20"),
        ("ferritecore-9.0.0-fabric.jar", "ferritecore", "9.0.0"),
        ("ImmediatelyFast-Fabric-1.16.4+26.2.jar", "immediatelyfast", "1.16.4+26.2"),
        ("lambdynamiclights-4.12.4+26.2.jar", "lambdynlights", "4.12.4+26.2"),
        ("lithium-fabric-0.25.3+mc26.2.jar", "lithium", "0.25.3+mc26.2"),
        ("modmenu-20.0.1.jar", "modmenu", "20.0.1"),
        ("placeholder-api-3.1.0-beta.1+26.2.jar", "placeholder-api", "3.1.0-beta.1+26.2"),
        ("sodium-fabric-0.9.2-beta.1+mc26.2.jar", "sodium", "0.9.2-beta.1+mc26.2"),
        ("xaerominimap-fabric-26.2-26.4.2.jar", "xaerominimap", "26.4.2"),
        ("xaeroworldmap-fabric-26.2-1.45.0.jar", "xaeroworldmap", "1.45.0"),
        ("yet_another_config_lib_v3-3.9.6+26.2-fabric.jar", "yet_another_config_lib_v3", "3.9.6+26.2-fabric"),
        ("zoomify-2.16.1+26.2.jar", "zoomify", "2.16.1+26.2"),

        // Not part of the curated pack: it has to be left alone rather than claimed
        // by one of the presets.
        ("polytone-26.2-6.5.0-fabric.jar", "polytone", "26.2-6.5.0"),
    };

    /// <summary>Writes a minimal but realistic Fabric mod jar into <paramref name="directory"/>.</summary>
    private static void CreateModJar(string directory, string fileName, string modId, string version)
    {
        using var zipStream = new FileStream(Path.Combine(directory, fileName), FileMode.Create);
        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Create);

        var entry = archive.CreateEntry("fabric.mod.json");
        using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
        writer.Write($@"{{
            ""schemaVersion"": 1,
            ""id"": ""{modId}"",
            ""version"": ""{version}"",
            ""name"": ""{modId}""
        }}");
    }

    [Fact]
    public void ScanModsDirectory_DetectsInstalledJar()
    {
        CreateModJar(_tempDir, "sodium-fabric-0.5.8.jar", "sodium", "0.5.8");

        var mockModrinth = new MockModrinthService();
        var manager = new ModManagerService(mockModrinth);

        var mods = manager.ScanModsDirectory(_tempDir);
        var sodium = mods.FirstOrDefault(m => m.Slug == "sodium");

        Assert.NotNull(sodium);
        Assert.Equal("Installed", sodium.Status);
        Assert.Equal("sodium-fabric-0.5.8.jar", sodium.InstalledFileName);
        Assert.Equal("0.5.8", sodium.InstalledVersion);
    }

    [Fact]
    public void ScanModsDirectory_WithPopulatedFolder_DetectsEveryCuratedModAndIgnoresTheRest()
    {
        foreach (var (jar, modId, version) in PopulatedModsFolder)
            CreateModJar(_tempDir, jar, modId, version);

        var mockModrinth = new MockModrinthService();
        var manager = new ModManagerService(mockModrinth);

        var mods = manager.ScanModsDirectory(_tempDir);

        Assert.Equal(15, mods.Count);
        Assert.DoesNotContain(mods, m => m.Slug == "polytone" || m.FabricModId == "polytone");

        var undetected = mods.Where(m => !m.IsInstalled).Select(m => $"{m.Name} (slug: {m.Slug}, id: {m.FabricModId})").ToList();
        Assert.True(undetected.Count == 0, $"The following mods were not detected: {string.Join(", ", undetected)}");

        // Every preset has to be paired with its own jar, not merely with some jar:
        // a sloppy prefix match could otherwise hand one mod's file to another.
        //
        // The version is asserted as well because it says which route did the
        // matching. Reading fabric.mod.json yields the real version, while the
        // file-name fallback only records "present" -- and since every jar here
        // also happens to be named after its mod id, that fallback would quietly
        // cover for a broken metadata pass if the version were not checked.
        foreach (var mod in mods)
        {
            var fixtureEntry = PopulatedModsFolder.SingleOrDefault(f => f.ModId == mod.FabricModId);
            Assert.True(fixtureEntry.Jar is not null,
                $"No jar in the fixture declares the id '{mod.FabricModId}' ({mod.Name}), so the fixture and ModPresets have drifted apart.");
            Assert.Equal(fixtureEntry.Jar, mod.InstalledFileName);
            Assert.Equal(fixtureEntry.Version, mod.InstalledVersion);
        }
    }

    [Fact]
    public async Task InstallOrUpdateModsAsync_WhenDownloadFails_DoesNotDeleteExistingFile()
    {
        string existingJar = Path.Combine(_tempDir, "sodium-0.5.0.jar");
        File.WriteAllText(existingJar, "dummy jar content");

        var failingModrinth = new FailingDownloadModrinthService();
        var manager = new ModManagerService(failingModrinth);

        var testMods = new List<ModDefinition>
        {
            new()
            {
                Slug = "sodium",
                Name = "Sodium",
                FabricModId = "sodium",
                InstalledFileName = "sodium-0.5.0.jar",
                TargetVersionNumber = "sodium-0.5.8.jar",
                DownloadUrl = "https://example.com/sodium.jar",
                Status = "Update available"
            }
        };

        var result = await manager.InstallOrUpdateModsAsync(_tempDir, "1.21.1", testMods);

        Assert.Equal(1, result.FailedCount);
        Assert.True(File.Exists(existingJar), "Existing jar file must not be deleted if download failed!");
    }

    [Fact]
    public async Task InstallOrUpdateModsAsync_WhenAllUpToDate_ReturnsAccurateMetrics()
    {
        var mockModrinth = new MockModrinthService();
        var manager = new ModManagerService(mockModrinth);

        var testMods = new List<ModDefinition>
        {
            new()
            {
                Slug = "sodium",
                Name = "Sodium",
                FabricModId = "sodium",
                InstalledFileName = "sodium-0.5.8.jar",
                TargetVersionNumber = "sodium-0.5.8.jar",
                DownloadUrl = "https://example.com/sodium.jar",
                Status = "Up to date"
            }
        };

        var result = await manager.InstallOrUpdateModsAsync(_tempDir, "1.21.1", testMods);

        Assert.Equal(0, result.InstalledOrUpdatedCount);
        Assert.Equal(1, result.AlreadyUpToDateCount);
        Assert.Equal(1, result.SuccessCount);
        Assert.Equal(0, result.FailedCount);
    }

    private class MockModrinthService : IModrinthService
    {
        public Task<ModrinthVersionFile?> GetLatestVersionFileAsync(string projectSlugOrId, string mcVersion)
        {
            return Task.FromResult<ModrinthVersionFile?>(null);
        }

        public Task DownloadFileAsync(string downloadUrl, string targetFilePath, IProgress<double>? progress = null, CancellationToken ct = default)
        {
            return Task.CompletedTask;
        }
    }

    private class FailingDownloadModrinthService : IModrinthService
    {
        public Task<ModrinthVersionFile?> GetLatestVersionFileAsync(string projectSlugOrId, string mcVersion)
        {
            return Task.FromResult<ModrinthVersionFile?>(null);
        }

        public Task DownloadFileAsync(string downloadUrl, string targetFilePath, IProgress<double>? progress = null, CancellationToken ct = default)
        {
            throw new HttpRequestException("Simulated download failure");
        }
    }
}

public class FabricServiceTests : IDisposable
{
    private readonly string _tempDir;

    public FabricServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"FabricTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, true); } catch { }
        }
    }

    [Fact]
    public void RegisterProfileInLauncher_CreatesProfilesJsonWhenMissing()
    {
        string mcVersion = "1.21.1";
        string versionId = "fabric-loader-0.16.10-1.21.1";

        FabricService.RegisterProfileInLauncher(_tempDir, mcVersion, versionId);

        string profilesFile = Path.Combine(_tempDir, "launcher_profiles.json");
        Assert.True(File.Exists(profilesFile));

        string json = File.ReadAllText(profilesFile);
        Assert.Contains("fabric-loader-1.21.1", json);
        Assert.Contains(versionId, json);
        Assert.Contains("-Xmx4G", json);
    }

    [Fact]
    public void RegisterProfileInLauncher_PreservesCustomJavaArgs()
    {
        string mcVersion = "1.21.1";
        string versionId = "fabric-loader-0.16.10-1.21.1";
        string profilesFile = Path.Combine(_tempDir, "launcher_profiles.json");

        string initialJson = @"{
            ""profiles"": {
                ""fabric-loader-1.21.1"": {
                    ""name"": ""My Custom Fabric Profile"",
                    ""lastVersionId"": ""fabric-loader-0.15.0-1.21.1"",
                    ""javaArgs"": ""-Xmx16G -XX:+CustomFlag""
                }
            }
        }";
        File.WriteAllText(profilesFile, initialJson);

        FabricService.RegisterProfileInLauncher(_tempDir, mcVersion, versionId);

        string json = File.ReadAllText(profilesFile);
        Assert.Contains(versionId, json);
        Assert.Contains("-Xmx16G -XX:+CustomFlag", json);
        Assert.Contains("My Custom Fabric Profile", json);
    }

    /// <summary>
    /// Lays out a .minecraft directory with Fabric installed for two game versions,
    /// mirroring what the Fabric installer leaves behind: a version folder per loader
    /// build plus a matching launcher profile. 1.21.11 deliberately carries two loader
    /// builds, which a single-loader install never exercises.
    /// </summary>
    private void GivenFabricInstallation()
    {
        string versionsDir = Path.Combine(_tempDir, "versions");
        foreach (string folder in new[]
                 {
                     "26.2",
                     "1.21.11",
                     "fabric-loader-0.19.5-26.2",
                     "fabric-loader-0.18.3-1.21.11",
                     "fabric-loader-0.17.2-1.21.11",
                 })
        {
            Directory.CreateDirectory(Path.Combine(versionsDir, folder));
        }

        File.WriteAllText(Path.Combine(_tempDir, "launcher_profiles.json"), @"{
            ""profiles"": {
                ""fabric-loader-1.21.11"": {
                    ""name"": ""Fabric"",
                    ""lastVersionId"": ""fabric-loader-0.18.3-1.21.11""
                },
                ""fabric-loader-26.2"": {
                    ""name"": ""fabric-loader-26.2"",
                    ""lastVersionId"": ""fabric-loader-0.19.5-26.2""
                },
                ""vanilla"": {
                    ""name"": ""Latest Release"",
                    ""lastVersionId"": ""latest-release""
                }
            }
        }");
    }

    [Theory]
    [InlineData("26.2", "0.19.5", "fabric-loader-26.2")]
    // Two loader builds are installed for 1.21.11; the newer one has to win, and the
    // profile name is the launcher's own label rather than the profile key.
    [InlineData("1.21.11", "0.18.3", "Fabric")]
    public async Task CheckFabricStatusAsync_WithFabricInstalled_ReportsNewestLoaderAndProfile(
        string mcVersion, string expectedLoader, string expectedProfileName)
    {
        GivenFabricInstallation();

        var service = new FabricService();
        var status = await service.CheckFabricStatusAsync(_tempDir, mcVersion);

        Assert.True(status.IsInstalled);
        Assert.Equal(expectedLoader, status.InstalledLoaderVersion);
        Assert.Equal(expectedProfileName, status.ProfileName);
    }

    [Fact]
    public async Task CheckFabricStatusAsync_ForAGameVersionWithoutFabric_ReportsNotInstalled()
    {
        GivenFabricInstallation();

        var service = new FabricService();
        var status = await service.CheckFabricStatusAsync(_tempDir, "26.1");

        Assert.False(status.IsInstalled);
        Assert.Null(status.InstalledLoaderVersion);
    }

    [Fact]
    public async Task CheckFabricStatusAsync_WithMalformedDirectory_DoesNotThrow()
    {
        string versionsDir = Path.Combine(_tempDir, "versions");
        Directory.CreateDirectory(versionsDir);

        // Directory that matches prefix and suffix but has no loader version in between
        Directory.CreateDirectory(Path.Combine(versionsDir, "fabric-loader-1.21.4"));

        var service = new FabricService();

        var status = await service.CheckFabricStatusAsync(_tempDir, "1.21.4");
        Assert.NotNull(status);
        Assert.False(status.IsInstalled);
    }
}
