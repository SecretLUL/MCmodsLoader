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
        Assert.Equal(14, mods.Count);

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
        var jsonResponse = @"{
            ""tag_name"": ""v1.1.0"",
            ""name"": ""v1.1.0 - Bugfix Release"",
            ""body"": ""Fixes minor issue"",
            ""html_url"": ""https://github.com/SecretLUL/MCmodsLoader/releases/tag/v1.1.0"",
            ""assets"": [
                {
                    ""name"": ""MCmodsLoader.exe"",
                    ""browser_download_url"": ""https://github.com/SecretLUL/MCmodsLoader/releases/download/v1.1.0/MCmodsLoader.exe""
                }
            ]
        }";

        var handler = new MockHttpHandler(jsonResponse);
        using var client = new HttpClient(handler);
        var service = new UpdateService(client);

        var update = await service.CheckForUpdateAsync("SecretLUL/MCmodsLoader");

        Assert.NotNull(update);
        Assert.True(update.HasUpdate);
        Assert.Equal("1.1.0", update.LatestVersion);
        Assert.Equal("https://github.com/SecretLUL/MCmodsLoader/releases/download/v1.1.0/MCmodsLoader.exe", update.DownloadUrl);
        Assert.Equal("v1.1.0 - Bugfix Release", update.ReleaseName);
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

        Assert.NotNull(update);
        Assert.Equal("1.0.0", update.LatestVersion);
        Assert.False(update.HasUpdate);
        Assert.NotNull(update.DownloadUrl);
        Assert.EndsWith("MCmodsLoader.exe", update.DownloadUrl);
        Assert.Contains("v1.0.0", update.DownloadUrl);
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

        Assert.Equal(14, mods.Count);
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
    public void ScanModsDirectory_WithUserModsFolder_All14ModsAreDetected()
    {
        string userMods = @"C:\Users\AMMAR-PC\AppData\Roaming\.minecraft\mods";
        if (!Directory.Exists(userMods))
            return;

        var mockModrinth = new MockModrinthService();
        var manager = new ModManagerService(mockModrinth);

        var mods = manager.ScanModsDirectory(userMods);
        Assert.Equal(14, mods.Count);

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
}
