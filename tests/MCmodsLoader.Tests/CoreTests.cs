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

        var validCategories = new HashSet<string> { "Performance", "Quality of Life", "Library" };

        foreach (var mod in mods)
        {
            Assert.False(string.IsNullOrWhiteSpace(mod.Slug), "Slug should not be empty");
            Assert.False(string.IsNullOrWhiteSpace(mod.Name), "Name should not be empty");
            Assert.False(string.IsNullOrWhiteSpace(mod.Description), "Description should not be empty");
            Assert.False(string.IsNullOrWhiteSpace(mod.FabricModId), "FabricModId should not be empty");
            Assert.Contains(mod.Category, validCategories);

            // Ensure descriptions are in English (no common German words)
            string desc = mod.Description.ToLowerInvariant();
            Assert.DoesNotContain("für", desc);
            Assert.DoesNotContain(" und ", desc);
            Assert.DoesNotContain(" ist ", desc);
            Assert.DoesNotContain(" der ", desc);
            Assert.DoesNotContain(" die ", desc);
            Assert.DoesNotContain(" das ", desc);
            Assert.DoesNotContain("bibliothek", mod.Category.ToLowerInvariant());
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
        Assert.Equal("Fixes minor issue", update.ReleaseNotes);
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
    public async Task CheckForUpdateAsync_LiveGitHubRelease_ReturnsValidReleaseInfo()
    {
        using var client = new HttpClient();
        var service = new UpdateService(client);
        var update = await service.CheckForUpdateAsync("SecretLUL/MCmodsLoader");

        // Gracefully handle unauthenticated GitHub API rate limits or offline environments
        if (update == null)
            return;

        Assert.True(Version.TryParse(update.LatestVersion, out _));
        Assert.NotNull(update.DownloadUrl);
        Assert.EndsWith("MCmodsLoader.exe", update.DownloadUrl, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SecretLUL/MCmodsLoader/releases", update.DownloadUrl);
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
            Assert.Equal("release", v.Type);
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

    [Fact]
    public void GetInstalledVersions_OnUserMachine_Returns262AsLatestRelease()
    {
        string userMc = @"C:\Users\AMMAR-PC\AppData\Roaming\.minecraft";
        if (!Directory.Exists(userMc))
            return;

        var service = new MinecraftService();
        var installed = service.GetInstalledVersions(userMc);

        Assert.NotEmpty(installed);
        Assert.Equal("26.2", installed[0]);
        Assert.DoesNotContain(installed, v => v.Contains("snapshot") || v.Contains("pre") || v.Contains("rc"));
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

    [Fact]
    public void ScanModsDirectory_DetectsInstalledJar()
    {
        string jarPath = Path.Combine(_tempDir, "sodium-fabric-0.5.8.jar");
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
    public void ScanModsDirectory_WithUserModsFolder_All15ModsAreDetected()
    {
        string userMods = @"C:\Users\AMMAR-PC\AppData\Roaming\.minecraft\mods";
        if (!Directory.Exists(userMods))
            return;

        var mockModrinth = new MockModrinthService();
        var manager = new ModManagerService(mockModrinth);

        var mods = manager.ScanModsDirectory(userMods);
        Assert.Equal(15, mods.Count);
        Assert.DoesNotContain(mods, m => m.Slug == "polytone" || m.FabricModId == "polytone");

        var undetected = mods.Where(m => !m.IsInstalled).Select(m => $"{m.Name} (slug: {m.Slug}, id: {m.FabricModId})").ToList();
        Assert.True(undetected.Count == 0, $"The following mods were not detected in the user folder: {string.Join(", ", undetected)}");
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
                Description = "Fast graphics",
                FabricModId = "sodium",
                Category = "Performance",
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
                Description = "Fast graphics",
                FabricModId = "sodium",
                Category = "Performance",
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

    [Fact]
    public async Task CheckFabricStatusAsync_OnUserMachine_DetectsFabricFor262()
    {
        string userMc = @"C:\Users\AMMAR-PC\AppData\Roaming\.minecraft";
        if (!Directory.Exists(userMc))
            return;

        using var client = new HttpClient();
        var service = new FabricService(client);
        var status = await service.CheckFabricStatusAsync(userMc, "26.2");

        Assert.True(status.IsInstalled);
        Assert.Equal("0.19.5", status.InstalledLoaderVersion);
        Assert.False(string.IsNullOrWhiteSpace(status.ProfileName));
    }

    [Fact]
    public async Task CheckFabricStatusAsync_OnUserMachine_DetectsFabricFor12111()
    {
        string userMc = @"C:\Users\AMMAR-PC\AppData\Roaming\.minecraft";
        if (!Directory.Exists(userMc))
            return;

        using var client = new HttpClient();
        var service = new FabricService(client);
        var status = await service.CheckFabricStatusAsync(userMc, "1.21.11");

        Assert.True(status.IsInstalled);
        Assert.Equal("0.18.3", status.InstalledLoaderVersion);
        Assert.False(string.IsNullOrWhiteSpace(status.ProfileName));
    }

    [Fact]
    public async Task CheckFabricStatusAsync_WithMalformedDirectory_DoesNotThrow()
    {
        string versionsDir = Path.Combine(_tempDir, "versions");
        Directory.CreateDirectory(versionsDir);

        // Directory that matches prefix and suffix but has no loader version in between
        Directory.CreateDirectory(Path.Combine(versionsDir, "fabric-loader-1.21.4"));

        using var client = new HttpClient();
        var service = new FabricService(client);

        var status = await service.CheckFabricStatusAsync(_tempDir, "1.21.4");
        Assert.NotNull(status);
        Assert.False(status.IsInstalled);
    }
}
