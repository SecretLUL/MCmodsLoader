using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using MCmodsLoader.Core.Models;
using MCmodsLoader.Core.Services;
using MCmodsLoader.Core.Utils;

namespace MCmodsLoader.Tests;

public class ModManagementTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _modsDir;

    public ModManagementTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"ModMgmtTest_{Guid.NewGuid():N}");
        _modsDir = Path.Combine(_tempDir, "mods");
        Directory.CreateDirectory(_modsDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, true); } catch { }
        }
    }

    // ---- Helpers ---------------------------------------------------------

    private static byte[] JarBytes(string modId, string version, string? mcDependency = null)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("fabric.mod.json");
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            string depends = mcDependency == null ? "" : $@", ""depends"": {{ ""minecraft"": ""{mcDependency}"" }}";
            writer.Write($@"{{ ""schemaVersion"": 1, ""id"": ""{modId}"", ""version"": ""{version}"", ""name"": ""{modId}""{depends} }}");
        }
        return buffer.ToArray();
    }

    /// <summary>A mod jar that embeds other mods the Fabric way: fabric.mod.json "jars" plus META-INF/jars/*.jar.</summary>
    private static byte[] JarBytesWithNested(string modId, string version, string? mcDependency, params string[] nestedModIds)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            string jars = string.Join(", ", nestedModIds.Select(id => $@"{{ ""file"": ""META-INF/jars/{id}-1.0.0.jar"" }}"));
            string depends = mcDependency == null ? "" : $@", ""depends"": {{ ""minecraft"": ""{mcDependency}"" }}";
            using (var writer = new StreamWriter(archive.CreateEntry("fabric.mod.json").Open(), new UTF8Encoding(false)))
            {
                writer.Write($@"{{ ""schemaVersion"": 1, ""id"": ""{modId}"", ""version"": ""{version}"", ""name"": ""{modId}"", ""jars"": [ {jars} ]{depends} }}");
            }

            foreach (var id in nestedModIds)
            {
                using var nested = archive.CreateEntry($"META-INF/jars/{id}-1.0.0.jar").Open();
                nested.Write(JarBytes(id, "1.0.0"));
            }
        }
        return buffer.ToArray();
    }

    private string WriteJar(string fileName, string modId, string version, string? mcDependency = null)
    {
        string path = Path.Combine(_modsDir, fileName);
        File.WriteAllBytes(path, JarBytes(modId, version, mcDependency));
        return path;
    }

    /// <summary>A jar without fabric.mod.json, so only the file name can identify it.</summary>
    private string WriteJarWithoutMetadata(string fileName)
    {
        string path = Path.Combine(_modsDir, fileName);
        using var stream = new FileStream(path, FileMode.Create);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        archive.CreateEntry("META-INF/MANIFEST.MF");
        return path;
    }

    private static List<ModDefinition> Pack(params (string Slug, string Name, string FabricModId)[] mods) =>
        mods.Select(m => new ModDefinition { Slug = m.Slug, Name = m.Name, FabricModId = m.FabricModId }).ToList();

    private static readonly (string, string, string) Sodium = ("sodium", "Sodium", "sodium");
    private static readonly (string, string, string) Lithium = ("lithium", "Lithium", "lithium");

    private string[] JarFiles() => Directory.GetFiles(_modsDir).Select(Path.GetFileName).OrderBy(n => n).ToArray()!;

    private static string Hex(byte[] hash) => Convert.ToHexString(hash).ToLowerInvariant();

    // ---- Strict file name predicate --------------------------------------

    [Theory]
    [InlineData("sodium-fabric-0.6.0+mc1.21.4.jar", "sodium", "sodium", true)]
    [InlineData("sodium-0.5.8.jar", "sodium", "sodium", true)]
    [InlineData("Zoomify+2.14.6.jar", "zoomify", "zoomify", true)]
    [InlineData("xaerominimap-fabric-1.21.4-25.2.0.jar", "xaerominimap", "xaeros-minimap", true)]
    [InlineData("appleskin-fabric-mc26.2-3.0.10.jar", "appleskin", "appleskin", true)]
    [InlineData("ImmediatelyFast-Fabric-1.16.4+26.2.jar", "immediatelyfast", "immediatelyfast", true)]
    [InlineData("modmenu-v20.0.1.jar", "modmenu", "modmenu", true)]
    [InlineData("yet_another_config_lib_v3-3.9.6+26.2-fabric.jar", "yet_another_config_lib_v3", "yacl", true)]
    [InlineData("ferrite-core_7.0.0.jar", "ferritecore", "ferrite-core", true)]
    [InlineData("sodium-extra-fabric-0.6.0+mc1.21.4.jar", "sodium", "sodium", false)]
    [InlineData("sodium-neoforge-0.6.0+mc1.21.4.jar", "sodium", "sodium", false)]
    [InlineData("xaerominimapfair-fabric-1.21.4-25.2.0.jar", "xaerominimap", "xaeros-minimap", false)]
    [InlineData("sodium.jar", "sodium", "sodium", false)]
    [InlineData("sodium-.jar", "sodium", "sodium", false)]
    [InlineData("lithium-fabric-0.15.0.jar", "sodium", "sodium", false)]
    public void FileNameMatchesMod_UsesStrictBoundary(string fileName, string fabricModId, string slug, bool expected)
    {
        Assert.Equal(expected, ModManagerService.FileNameMatchesMod(fileName, fabricModId, slug));
    }

    [Fact]
    public void IsJarOfMod_PrefersMetadataOverFileName()
    {
        var other = new FabricModMetadata("sodium-extra", "Sodium Extra", "0.6.0");
        var own = new FabricModMetadata("sodium", "Sodium", "0.6.0");

        // The name would match, but the metadata says it's a different mod
        Assert.False(ModManagerService.IsJarOfMod("sodium-0.6.0.jar", other, "sodium", "sodium"));
        // The name doesn't match the pattern, but the metadata id does
        Assert.True(ModManagerService.IsJarOfMod("renamed-by-user.jar", own, "sodium", "sodium"));
        Assert.True(ModManagerService.IsJarOfMod("sodium-0.6.0.jar", null, "sodium", "sodium"));
    }

    [Fact]
    public void JarInspector_ReadsNestedModIds_OnlyWhenAsked()
    {
        string path = Path.Combine(_modsDir, "cullleaves-fabric-4.1.1+1.21.4.jar");
        File.WriteAllBytes(path, JarBytesWithNested("cullleaves", "4.1.1+1.21.4", null, "midnightlib", "fabric-api-base"));

        Assert.Null(JarInspector.ReadFabricModJson(path)!.NestedModIds);

        var meta = JarInspector.ReadFabricModJson(path, includeNestedJars: true);
        Assert.NotNull(meta);
        Assert.Equal("cullleaves", meta.Id);
        Assert.Equal(new[] { "midnightlib", "fabric-api-base" }, meta.NestedModIds);

        // A jar without "jars" has no nested mods
        File.WriteAllBytes(path, JarBytes("cullleaves", "4.1.1+1.21.4"));
        Assert.Empty(JarInspector.ReadFabricModJson(path, includeNestedJars: true)!.NestedModIds!);
    }

    [Fact]
    public void JarInspector_SkipsDuplicateAndOversizedNestedJars()
    {
        // A nested jar over the cap: its fabric.mod.json plus incompressible filler, stored uncompressed
        byte[] bigNested;
        using (var buffer = new MemoryStream())
        {
            using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            {
                using (var writer = new StreamWriter(archive.CreateEntry("fabric.mod.json").Open(), new UTF8Encoding(false)))
                {
                    writer.Write(@"{ ""schemaVersion"": 1, ""id"": ""biglib"", ""version"": ""1.0.0"" }");
                }

                var filler = new byte[JarInspector.MaxNestedJarSize + 1024];
                new Random(42).NextBytes(filler);
                using var fillerStream = archive.CreateEntry("filler.bin", CompressionLevel.NoCompression).Open();
                fillerStream.Write(filler);
            }
            bigNested = buffer.ToArray();
        }

        string path = Path.Combine(_modsDir, "outer-1.0.0.jar");
        using (var stream = new FileStream(path, FileMode.Create))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            using (var writer = new StreamWriter(archive.CreateEntry("fabric.mod.json").Open(), new UTF8Encoding(false)))
            {
                writer.Write(@"{ ""schemaVersion"": 1, ""id"": ""outer"", ""version"": ""1.0.0"", ""jars"": [
                    { ""file"": ""META-INF/jars/liba.jar"" }, { ""file"": ""META-INF/jars/liba.jar"" }, { ""file"": ""META-INF/jars/big.jar"" } ] }");
            }

            using (var nested = archive.CreateEntry("META-INF/jars/liba.jar").Open())
            {
                nested.Write(JarBytes("liba", "1.0.0"));
            }

            using (var nested = archive.CreateEntry("META-INF/jars/big.jar", CompressionLevel.NoCompression).Open())
            {
                nested.Write(bigNested);
            }
        }

        var meta = JarInspector.ReadFabricModJson(path, includeNestedJars: true);

        Assert.NotNull(meta);
        Assert.Equal(new[] { "liba" }, meta.NestedModIds);
    }

    // ---- Bug 4: scan must not claim other mods' jars ---------------------

    [Fact]
    public void ScanModsDirectory_DoesNotClaimJarsOfOtherModsWithMetadata()
    {
        WriteJar("sodium-extra-fabric-0.6.0+mc1.21.4.jar", "sodium-extra", "0.6.0+mc1.21.4");
        WriteJar("xaerominimapfair-fabric-1.21.4-25.2.0.jar", "xaerominimapfair", "25.2.0");

        var manager = new ModManagerService(new FakeModrinthService());
        var mods = manager.ScanModsDirectory(_modsDir, Pack(Sodium, ("xaeros-minimap", "Xaero's Minimap", "xaerominimap")), "1.21.4");

        Assert.All(mods, m =>
        {
            Assert.Null(m.InstalledFileName);
            Assert.Equal("Missing", m.Status);
        });
    }

    [Fact]
    public void ScanModsDirectory_FileNameFallbackOnlyForJarsWithoutMetadata_AndIsStrict()
    {
        WriteJarWithoutMetadata("sodium-extra-fabric-0.6.0+mc1.21.4.jar");
        WriteJarWithoutMetadata("xaerominimap-fabric-1.21.4-25.2.0.jar");

        var manager = new ModManagerService(new FakeModrinthService());
        var mods = manager.ScanModsDirectory(_modsDir, Pack(Sodium, ("xaeros-minimap", "Xaero's Minimap", "xaerominimap")), "1.21.4");

        Assert.Null(mods.Single(m => m.Slug == "sodium").InstalledFileName);
        var minimap = mods.Single(m => m.Slug == "xaeros-minimap");
        Assert.Equal("xaerominimap-fabric-1.21.4-25.2.0.jar", minimap.InstalledFileName);
        Assert.True(minimap.IsInstalled);
    }

    // ---- Duplicates ------------------------------------------------------

    [Fact]
    public void ScanModsDirectory_WithDuplicates_PicksCompatibleNewestAndRemembersTheRest()
    {
        WriteJar("sodium-fabric-0.5.0+mc1.21.4.jar", "sodium", "0.5.0+mc1.21.4", "1.21.4");
        WriteJar("sodium-fabric-0.6.0+mc1.21.4.jar", "sodium", "0.6.0+mc1.21.4", "1.21.4");
        WriteJar("sodium-fabric-0.7.0+mc26.2.jar", "sodium", "0.7.0+mc26.2", "26.2");

        var template = Pack(Sodium);
        var manager = new ModManagerService(new FakeModrinthService());
        var sodium = manager.ScanModsDirectory(_modsDir, template, "1.21.4").Single();

        Assert.Equal("sodium-fabric-0.6.0+mc1.21.4.jar", sodium.InstalledFileName);
        Assert.Equal("Installed", sodium.Status);
        Assert.Equal(new[] { "sodium-fabric-0.5.0+mc1.21.4.jar", "sodium-fabric-0.7.0+mc26.2.jar" }, sodium.DuplicateFileNames.OrderBy(n => n));

        // The template the copies were made from stays untouched
        Assert.Empty(template[0].DuplicateFileNames);
        Assert.Null(template[0].InstalledFileName);
    }

    [Fact]
    public async Task InstallOrUpdate_RemovesDuplicates_EvenWhenChosenJarIsUpToDate()
    {
        var latest = JarBytes("sodium", "0.6.0+mc1.21.4", "1.21.4");
        File.WriteAllBytes(Path.Combine(_modsDir, "sodium-fabric-0.6.0+mc1.21.4.jar"), latest);
        WriteJar("sodium-fabric-0.5.0+mc1.21.4.jar", "sodium", "0.5.0+mc1.21.4", "1.21.4");
        WriteJar("lithium-fabric-0.15.0+mc1.21.4.jar", "lithium", "0.15.0+mc1.21.4", "1.21.4");

        var fake = new FakeModrinthService().Add("sodium", "sodium-fabric-0.6.0+mc1.21.4.jar", latest);
        var manager = new ModManagerService(fake);
        var mods = manager.ScanModsDirectory(_modsDir, Pack(Sodium), "1.21.4");

        var result = await manager.InstallOrUpdateModsAsync(_modsDir, "1.21.4", mods);

        Assert.Equal(1, result.AlreadyUpToDateCount);
        Assert.Equal(0, result.InstalledOrUpdatedCount);
        Assert.Empty(fake.DownloadedUrls);
        Assert.Equal(new[] { "lithium-fabric-0.15.0+mc1.21.4.jar", "sodium-fabric-0.6.0+mc1.21.4.jar" }, JarFiles());
        Assert.Empty(mods[0].DuplicateFileNames);
    }

    // ---- Bug 1: updating must not delete unrelated jars ------------------

    [Fact]
    public async Task InstallOrUpdate_UpdatingSodium_KeepsJarsOfOtherMods()
    {
        WriteJar("sodium-fabric-0.6.0+mc1.21.4.jar", "sodium", "0.6.0+mc1.21.4");
        WriteJar("sodium-extra-fabric-0.6.0+mc1.21.4.jar", "sodium-extra", "0.6.0+mc1.21.4");
        // Its name passes the strict pattern, but its metadata says it's another mod
        WriteJar("sodium-1.0-addon.jar", "sodiumaddon", "1.0");

        var fake = new FakeModrinthService().Add("sodium", "sodium-fabric-0.6.1+mc1.21.4.jar", JarBytes("sodium", "0.6.1+mc1.21.4"));
        var manager = new ModManagerService(fake);
        var mods = manager.ScanModsDirectory(_modsDir, Pack(Sodium), "1.21.4");

        var result = await manager.InstallOrUpdateModsAsync(_modsDir, "1.21.4", mods);

        Assert.Equal(1, result.InstalledOrUpdatedCount);
        Assert.False(result.HasIssues);
        Assert.Equal(
            new[] { "sodium-1.0-addon.jar", "sodium-extra-fabric-0.6.0+mc1.21.4.jar", "sodium-fabric-0.6.1+mc1.21.4.jar" },
            JarFiles());
        Assert.Equal("0.6.1+mc1.21.4", mods[0].InstalledVersion);
    }

    // ---- Bug 2: updates must be detected when everything is installed ----

    [Fact]
    public async Task InstallOrUpdate_WhenAllInstalled_StillLooksUpAndAppliesUpdates()
    {
        WriteJar("sodium-fabric-0.6.0+mc1.21.4.jar", "sodium", "0.6.0+mc1.21.4");
        WriteJar("lithium-fabric-0.14.0+mc1.21.4.jar", "lithium", "0.14.0+mc1.21.4");

        var fake = new FakeModrinthService()
            .Add("sodium", "sodium-fabric-0.6.1+mc1.21.4.jar", JarBytes("sodium", "0.6.1+mc1.21.4"))
            .Add("lithium", "lithium-fabric-0.15.0+mc1.21.4.jar", JarBytes("lithium", "0.15.0+mc1.21.4"));
        var manager = new ModManagerService(fake);
        var mods = manager.ScanModsDirectory(_modsDir, Pack(Sodium, Lithium), "1.21.4");
        Assert.All(mods, m => Assert.True(m.IsInstalled));

        var result = await manager.InstallOrUpdateModsAsync(_modsDir, "1.21.4", mods);

        Assert.Equal(2, fake.VersionQueries);
        Assert.Equal(2, result.InstalledOrUpdatedCount);
        Assert.Equal(0, result.AlreadyUpToDateCount);
        Assert.Equal(new[] { "lithium-fabric-0.15.0+mc1.21.4.jar", "sodium-fabric-0.6.1+mc1.21.4.jar" }, JarFiles());
    }

    [Fact]
    public async Task InstallOrUpdate_UpToDateIsDecidedByHash()
    {
        // Same content as the latest release but renamed: up to date, no download
        var sodiumLatest = JarBytes("sodium", "0.6.1+mc1.21.4");
        File.WriteAllBytes(Path.Combine(_modsDir, "sodium-renamed-by-user.jar"), sodiumLatest);

        // Same name as the latest release but different content (e.g. re-uploaded): re-download
        WriteJar("lithium-fabric-0.15.0+mc1.21.4.jar", "lithium", "0.15.0-local");
        var lithiumLatest = JarBytes("lithium", "0.15.0+mc1.21.4");

        var fake = new FakeModrinthService()
            .Add("sodium", "sodium-fabric-0.6.1+mc1.21.4.jar", sodiumLatest)
            .Add("lithium", "lithium-fabric-0.15.0+mc1.21.4.jar", lithiumLatest);
        var manager = new ModManagerService(fake);
        var mods = manager.ScanModsDirectory(_modsDir, Pack(Sodium, Lithium), "1.21.4");

        var result = await manager.InstallOrUpdateModsAsync(_modsDir, "1.21.4", mods);

        Assert.Equal(1, result.AlreadyUpToDateCount);
        Assert.Equal(1, result.InstalledOrUpdatedCount);
        Assert.Equal("Up to date", mods.Single(m => m.Slug == "sodium").Status);
        Assert.Single(fake.DownloadedUrls);
        Assert.Equal(lithiumLatest, File.ReadAllBytes(Path.Combine(_modsDir, "lithium-fabric-0.15.0+mc1.21.4.jar")));
        Assert.True(File.Exists(Path.Combine(_modsDir, "sodium-renamed-by-user.jar")));
    }

    // ---- Bug 3: unavailable mods must be reported ------------------------

    [Fact]
    public async Task InstallOrUpdate_UnavailableMissingMod_IsReported()
    {
        var fake = new FakeModrinthService().Add("sodium", "sodium-fabric-0.7.0+mc26.2.jar", JarBytes("sodium", "0.7.0+mc26.2"));
        var manager = new ModManagerService(fake);
        var mods = manager.ScanModsDirectory(_modsDir, Pack(Sodium, Lithium), "26.2");

        var result = await manager.InstallOrUpdateModsAsync(_modsDir, "26.2", mods);

        Assert.Equal(2, result.TotalCount);
        Assert.Equal(1, result.InstalledOrUpdatedCount);
        Assert.Equal(0, result.FailedCount);
        Assert.Equal(new[] { "Lithium" }, result.UnavailableModNames);
        Assert.True(result.HasIssues);
        Assert.Equal("Not available for this version", mods.Single(m => m.Slug == "lithium").Status);
    }

    [Fact]
    public async Task InstallOrUpdate_UnavailableAndIncompatible_DisablesTheJar()
    {
        WriteJar("lithium-fabric-0.14.0+mc1.21.4.jar", "lithium", "0.14.0+mc1.21.4", "1.21.4");

        var fake = new FakeModrinthService().Add("sodium", "sodium-fabric-0.7.0+mc26.2.jar", JarBytes("sodium", "0.7.0+mc26.2"));
        var manager = new ModManagerService(fake);
        var mods = manager.ScanModsDirectory(_modsDir, Pack(Sodium, Lithium), "26.2");

        var result = await manager.InstallOrUpdateModsAsync(_modsDir, "26.2", mods);

        Assert.Equal(new[] { "Lithium" }, result.UnavailableModNames);
        Assert.Equal(new[] { "lithium-fabric-0.14.0+mc1.21.4.jar.disabled" }, result.DisabledFileNames);
        Assert.Equal(new[] { "lithium-fabric-0.14.0+mc1.21.4.jar.disabled", "sodium-fabric-0.7.0+mc26.2.jar" }, JarFiles());

        var lithium = mods.Single(m => m.Slug == "lithium");
        Assert.Null(lithium.InstalledFileName);
        Assert.Equal("Not available for this version", lithium.Status);
    }

    [Fact]
    public async Task InstallOrUpdate_UnavailableButCompatible_KeepsTheJar()
    {
        WriteJar("lithium-fabric-0.14.0+mc26.2.jar", "lithium", "0.14.0+mc26.2", "26.2");

        var manager = new ModManagerService(new FakeModrinthService());
        var mods = manager.ScanModsDirectory(_modsDir, Pack(Lithium), "26.2");

        var result = await manager.InstallOrUpdateModsAsync(_modsDir, "26.2", mods);

        Assert.Equal(1, result.AlreadyUpToDateCount);
        Assert.Empty(result.UnavailableModNames);
        Assert.Empty(result.DisabledFileNames);
        Assert.Equal(new[] { "lithium-fabric-0.14.0+mc26.2.jar" }, JarFiles());
    }

    [Fact]
    public async Task InstallOrUpdate_UnavailableWithTwoCompatibleJars_RemovesTheDuplicate()
    {
        WriteJar("lithium-fabric-0.13.0+mc26.2.jar", "lithium", "0.13.0+mc26.2", "26.2");
        WriteJar("lithium-fabric-0.14.0+mc26.2.jar", "lithium", "0.14.0+mc26.2", "26.2");

        var manager = new ModManagerService(new FakeModrinthService());
        var mods = manager.ScanModsDirectory(_modsDir, Pack(Lithium), "26.2");

        var result = await manager.InstallOrUpdateModsAsync(_modsDir, "26.2", mods);

        Assert.Equal(1, result.AlreadyUpToDateCount);
        Assert.False(result.HasIssues);
        Assert.Equal(new[] { "lithium-fabric-0.14.0+mc26.2.jar" }, JarFiles());
    }

    [Fact]
    public async Task InstallOrUpdate_LookupFailedWithCompatibleJar_StillRemovesTheDuplicate()
    {
        WriteJar("lithium-fabric-0.13.0+mc26.2.jar", "lithium", "0.13.0+mc26.2", "26.2");
        WriteJar("lithium-fabric-0.14.0+mc26.2.jar", "lithium", "0.14.0+mc26.2", "26.2");

        var fake = new FakeModrinthService();
        fake.LookupErrors["lithium"] = new TimeoutException("Modrinth did not respond within 20 seconds");
        var manager = new ModManagerService(fake);
        var mods = manager.ScanModsDirectory(_modsDir, Pack(Lithium), "26.2");

        var result = await manager.InstallOrUpdateModsAsync(_modsDir, "26.2", mods);

        Assert.Contains("did not respond", Assert.Single(result.Failures).Reason);
        Assert.Equal(new[] { "lithium-fabric-0.14.0+mc26.2.jar" }, JarFiles());
    }

    [Fact]
    public async Task InstallOrUpdate_DisablingOverwritesAnExistingDisabledFile()
    {
        WriteJar("lithium-fabric-0.14.0+mc1.21.4.jar", "lithium", "0.14.0+mc1.21.4", "1.21.4");
        File.WriteAllText(Path.Combine(_modsDir, "lithium-fabric-0.14.0+mc1.21.4.jar.disabled"), "disabled in an earlier run");

        var manager = new ModManagerService(new FakeModrinthService());
        var mods = manager.ScanModsDirectory(_modsDir, Pack(Lithium), "26.2");

        var result = await manager.InstallOrUpdateModsAsync(_modsDir, "26.2", mods);

        Assert.Empty(result.Failures);
        Assert.Equal(new[] { "lithium-fabric-0.14.0+mc1.21.4.jar.disabled" }, result.DisabledFileNames);
        Assert.Equal(new[] { "lithium-fabric-0.14.0+mc1.21.4.jar.disabled" }, JarFiles());
        Assert.Equal("lithium", JarInspector.ReadFabricModJson(Path.Combine(_modsDir, "lithium-fabric-0.14.0+mc1.21.4.jar.disabled"))?.Id);
    }

    // ---- Lookup errors ---------------------------------------------------

    [Fact]
    public async Task InstallOrUpdate_LookupError_IsReportedAsFailure_AndNeverDisables()
    {
        WriteJar("sodium-fabric-0.6.0+mc1.21.4.jar", "sodium", "0.6.0+mc1.21.4", "1.21.4");

        var fake = new FakeModrinthService().Add("lithium", "lithium-fabric-0.15.0+mc26.2.jar", JarBytes("lithium", "0.15.0+mc26.2"));
        fake.LookupErrors["sodium"] = new HttpRequestException("Modrinth answered HTTP 429 (Too Many Requests)");
        var manager = new ModManagerService(fake);
        var mods = manager.ScanModsDirectory(_modsDir, Pack(Sodium, Lithium), "26.2");

        var result = await manager.InstallOrUpdateModsAsync(_modsDir, "26.2", mods);

        var failure = Assert.Single(result.Failures);
        Assert.Equal("Sodium", failure.ModName);
        Assert.Contains("429", failure.Reason);
        Assert.Equal(new[] { "Sodium" }, result.FailedModNames);
        Assert.Equal(1, result.InstalledOrUpdatedCount);
        Assert.Empty(result.UnavailableModNames);
        Assert.Empty(result.DisabledFileNames);
        Assert.True(File.Exists(Path.Combine(_modsDir, "sodium-fabric-0.6.0+mc1.21.4.jar")));
    }

    // ---- Bug 5: locked old jar -------------------------------------------

    // FileShare.Read | FileShare.Write is how Java holds a loaded jar: readable, but not renamable or deletable
    [Theory]
    [InlineData(FileShare.None)]
    [InlineData(FileShare.Read | FileShare.Write)]
    public async Task InstallOrUpdate_WhenOldJarIsLocked_RollsBackAndReportsFailure(FileShare lockShare)
    {
        string oldJar = WriteJar("sodium-fabric-0.6.0+mc1.21.4.jar", "sodium", "0.6.0+mc1.21.4");

        var fake = new FakeModrinthService().Add("sodium", "sodium-fabric-0.6.1+mc1.21.4.jar", JarBytes("sodium", "0.6.1+mc1.21.4"));
        var manager = new ModManagerService(fake);
        var mods = manager.ScanModsDirectory(_modsDir, Pack(Sodium), "1.21.4");

        ModInstallResult result;
        using (new FileStream(oldJar, FileMode.Open, FileAccess.Read, lockShare))
        {
            result = await manager.InstallOrUpdateModsAsync(_modsDir, "1.21.4", mods);
        }

        var failure = Assert.Single(result.Failures);
        Assert.Equal("Sodium", failure.ModName);
        Assert.Contains("in use", failure.Reason);
        Assert.Equal(0, result.InstalledOrUpdatedCount);
        Assert.Single(fake.DownloadedUrls);
        Assert.Equal(new[] { "sodium-fabric-0.6.0+mc1.21.4.jar" }, JarFiles());
    }

    [Fact]
    public async Task InstallOrUpdate_WhenOneOfSeveralOldJarsIsLocked_RemovesNoneOfThem()
    {
        // Removed in name order: 0.5.0 would go first, then 0.6.0 is locked
        WriteJar("sodium-fabric-0.5.0+mc1.21.4.jar", "sodium", "0.5.0+mc1.21.4");
        string locked = WriteJar("sodium-fabric-0.6.0+mc1.21.4.jar", "sodium", "0.6.0+mc1.21.4");

        var fake = new FakeModrinthService().Add("sodium", "sodium-fabric-0.6.1+mc1.21.4.jar", JarBytes("sodium", "0.6.1+mc1.21.4"));
        var manager = new ModManagerService(fake);
        var mods = manager.ScanModsDirectory(_modsDir, Pack(Sodium), "1.21.4");

        ModInstallResult result;
        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Write))
        {
            result = await manager.InstallOrUpdateModsAsync(_modsDir, "1.21.4", mods);
        }

        Assert.Contains("'sodium-fabric-0.6.0+mc1.21.4.jar' is in use", Assert.Single(result.Failures).Reason);
        Assert.Equal(new[] { "sodium-fabric-0.5.0+mc1.21.4.jar", "sodium-fabric-0.6.0+mc1.21.4.jar" }, JarFiles());
    }

    [Fact]
    public async Task InstallOrUpdate_ReadOnlyOldJar_IsReplaced_AndLeftoversOfEarlierRunsAreRemoved()
    {
        string oldJar = WriteJar("sodium-fabric-0.6.0+mc1.21.4.jar", "sodium", "0.6.0+mc1.21.4");
        File.SetAttributes(oldJar, FileAttributes.ReadOnly);
        File.WriteAllText(Path.Combine(_modsDir, "lithium-fabric-0.1.0.jar.mcmodsloader-old"), "leftover");
        File.WriteAllText(Path.Combine(_modsDir, "lithium-fabric-0.2.0.jar.mcmodsloader-new"), "unfinished");

        var fake = new FakeModrinthService().Add("sodium", "sodium-fabric-0.6.1+mc1.21.4.jar", JarBytes("sodium", "0.6.1+mc1.21.4"));
        var manager = new ModManagerService(fake);
        var mods = manager.ScanModsDirectory(_modsDir, Pack(Sodium), "1.21.4");

        var result = await manager.InstallOrUpdateModsAsync(_modsDir, "1.21.4", mods);

        Assert.Empty(result.Failures);
        Assert.Equal(1, result.InstalledOrUpdatedCount);
        Assert.Equal(new[] { "sodium-fabric-0.6.1+mc1.21.4.jar" }, JarFiles());
    }

    [Fact]
    public async Task InstallOrUpdate_ReadOnlyJarWithTheTargetName_IsReplaced()
    {
        string sameName = WriteJar("sodium-fabric-0.6.1+mc1.21.4.jar", "sodium", "0.6.1-local");
        File.SetAttributes(sameName, FileAttributes.ReadOnly);
        var latest = JarBytes("sodium", "0.6.1+mc1.21.4");

        var manager = new ModManagerService(new FakeModrinthService().Add("sodium", "sodium-fabric-0.6.1+mc1.21.4.jar", latest));
        var mods = manager.ScanModsDirectory(_modsDir, Pack(Sodium), "1.21.4");

        var result = await manager.InstallOrUpdateModsAsync(_modsDir, "1.21.4", mods);

        Assert.Empty(result.Failures);
        Assert.Equal(new[] { "sodium-fabric-0.6.1+mc1.21.4.jar" }, JarFiles());
        Assert.Equal(latest, File.ReadAllBytes(sameName));
    }

    [Fact]
    public async Task InstallOrUpdate_SameNameTargetPlusLockedSecondJar_LeavesTheDiskUnchanged()
    {
        // The jar with the target name is moved away first (name order), then the locked one fails:
        // the first has to come back exactly as it was, read-only flag included
        string sameName = WriteJar("sodium-fabric-0.6.1+mc1.21.4.jar", "sodium", "0.6.1-local");
        byte[] sameNameContent = File.ReadAllBytes(sameName);
        File.SetAttributes(sameName, FileAttributes.ReadOnly);
        string locked = WriteJar("sodium-fabric-0.7.0-beta+mc1.21.4.jar", "sodium", "0.7.0-beta+mc1.21.4");

        var fake = new FakeModrinthService().Add("sodium", "sodium-fabric-0.6.1+mc1.21.4.jar", JarBytes("sodium", "0.6.1+mc1.21.4"));
        var manager = new ModManagerService(fake);
        var mods = manager.ScanModsDirectory(_modsDir, Pack(Sodium), "1.21.4");

        ModInstallResult result;
        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Write))
        {
            result = await manager.InstallOrUpdateModsAsync(_modsDir, "1.21.4", mods);
        }

        try
        {
            Assert.Contains("'sodium-fabric-0.7.0-beta+mc1.21.4.jar' is in use", Assert.Single(result.Failures).Reason);
            Assert.Single(fake.DownloadedUrls);
            Assert.Equal(new[] { "sodium-fabric-0.6.1+mc1.21.4.jar", "sodium-fabric-0.7.0-beta+mc1.21.4.jar" }, JarFiles());
            Assert.Equal(sameNameContent, File.ReadAllBytes(sameName));
            Assert.True(File.GetAttributes(sameName).HasFlag(FileAttributes.ReadOnly));
        }
        finally
        {
            File.SetAttributes(sameName, FileAttributes.Normal);
        }
    }

    [Fact]
    public async Task InstallOrUpdate_WhenTheOldJarCantBeRenamedBack_CompletesTheSwapInstead()
    {
        WriteJar("sodium-fabric-0.6.0+mc1.21.4.jar", "sodium", "0.6.0+mc1.21.4");
        var fake = new FakeModrinthService().Add("sodium", "sodium-fabric-0.6.1+mc1.21.4.jar", JarBytes("sodium", "0.6.1+mc1.21.4"));

        // The new jar's first move fails, renaming the old one back fails too; the retry of the new one works
        bool firstStagingMove = true;
        var manager = new ModManagerService(fake)
        {
            MoveFile = (from, to, overwrite) =>
            {
                if (from.EndsWith(".mcmodsloader-new") && firstStagingMove)
                {
                    firstStagingMove = false;
                    throw new IOException("simulated");
                }
                if (from.EndsWith(".mcmodsloader-old"))
                    throw new IOException("simulated");
                File.Move(from, to, overwrite);
            }
        };
        var mods = manager.ScanModsDirectory(_modsDir, Pack(Sodium), "1.21.4");

        var result = await manager.InstallOrUpdateModsAsync(_modsDir, "1.21.4", mods);

        Assert.Empty(result.Failures);
        Assert.Equal(1, result.InstalledOrUpdatedCount);
        Assert.Equal(new[] { "sodium-fabric-0.6.1+mc1.21.4.jar" }, JarFiles());
    }

    [Fact]
    public async Task InstallOrUpdate_WhenNeitherJarCanGoLive_TheOldOneComesBackOnTheNextRun()
    {
        WriteJar("sodium-fabric-0.6.0+mc1.21.4.jar", "sodium", "0.6.0+mc1.21.4");
        var fake = new FakeModrinthService().Add("sodium", "sodium-fabric-0.6.1+mc1.21.4.jar", JarBytes("sodium", "0.6.1+mc1.21.4"));
        var failing = new ModManagerService(fake)
        {
            MoveFile = (from, to, overwrite) =>
            {
                if (from.EndsWith(".mcmodsloader-new") || from.EndsWith(".mcmodsloader-old"))
                    throw new IOException("simulated");
                File.Move(from, to, overwrite);
            }
        };

        var first = await failing.InstallOrUpdateModsAsync(_modsDir, "1.21.4", failing.ScanModsDirectory(_modsDir, Pack(Sodium), "1.21.4"));

        Assert.Contains("put back automatically on the next run", Assert.Single(first.Failures).Reason);
        Assert.Equal(new[] { "sodium-fabric-0.6.0+mc1.21.4.jar.mcmodsloader-old" }, JarFiles());

        // Next run (Sodium has no version this time, so nothing is replaced): the old jar is back
        var manager = new ModManagerService(new FakeModrinthService());
        await manager.InstallOrUpdateModsAsync(_modsDir, "1.21.4", manager.ScanModsDirectory(_modsDir, Pack(Sodium), "1.21.4"));

        Assert.Equal(new[] { "sodium-fabric-0.6.0+mc1.21.4.jar" }, JarFiles());
    }

    [Fact]
    public async Task InstallOrUpdate_AfterACrashMidReplacement_RestoresTheOldJarAndDropsTheUnfinishedOne()
    {
        // A crash between renaming the old jar away and putting the new one in place
        string leftover = Path.Combine(_modsDir, "lithium-fabric-0.14.0+mc26.2.jar.mcmodsloader-old");
        File.WriteAllBytes(leftover, JarBytes("lithium", "0.14.0+mc26.2", "26.2"));
        File.SetAttributes(leftover, FileAttributes.ReadOnly);
        File.WriteAllBytes(Path.Combine(_modsDir, "lithium-fabric-0.15.0+mc26.2.jar.mcmodsloader-new"), JarBytes("lithium", "0.15.0+mc26.2", "26.2"));

        // Lithium has no version now, so the restored jar is all there is
        var manager = new ModManagerService(new FakeModrinthService());
        await manager.InstallOrUpdateModsAsync(_modsDir, "26.2", manager.ScanModsDirectory(_modsDir, Pack(Lithium), "26.2"));

        string restored = Path.Combine(_modsDir, "lithium-fabric-0.14.0+mc26.2.jar");
        try
        {
            Assert.Equal(new[] { "lithium-fabric-0.14.0+mc26.2.jar" }, JarFiles());
            Assert.True(File.GetAttributes(restored).HasFlag(FileAttributes.ReadOnly));
        }
        finally
        {
            if (File.Exists(restored)) File.SetAttributes(restored, FileAttributes.Normal);
        }
    }

    [Fact]
    public async Task InstallOrUpdate_LeftoverOfAModWithALiveJar_IsDeleted()
    {
        WriteJar("lithium-fabric-0.15.0+mc26.2.jar", "lithium", "0.15.0+mc26.2", "26.2");
        File.WriteAllBytes(Path.Combine(_modsDir, "lithium-fabric-0.14.0+mc26.2.jar.mcmodsloader-old"), JarBytes("lithium", "0.14.0+mc26.2", "26.2"));
        // Without metadata a leftover's mod can't be told, so it isn't restored
        File.WriteAllText(Path.Combine(_modsDir, "unknown-1.0.0.jar.mcmodsloader-old"), "not a jar");

        var manager = new ModManagerService(new FakeModrinthService());
        var result = await manager.InstallOrUpdateModsAsync(_modsDir, "26.2", manager.ScanModsDirectory(_modsDir, Pack(Lithium), "26.2"));

        Assert.Equal(1, result.AlreadyUpToDateCount);
        Assert.Equal(new[] { "lithium-fabric-0.15.0+mc26.2.jar" }, JarFiles());
    }

    // ---- Cancellation ----------------------------------------------------

    [Fact]
    public async Task InstallOrUpdate_WhenCancelled_ThrowsAndKeepsTheOldJar()
    {
        WriteJar("sodium-fabric-0.6.0+mc1.21.4.jar", "sodium", "0.6.0+mc1.21.4");
        var fake = new FakeModrinthService { BlockDownloads = true }
            .Add("sodium", "sodium-fabric-0.6.1+mc1.21.4.jar", JarBytes("sodium", "0.6.1+mc1.21.4"));
        var manager = new ModManagerService(fake);
        var mods = manager.ScanModsDirectory(_modsDir, Pack(Sodium), "1.21.4");

        using var cts = new CancellationTokenSource();
        var run = manager.InstallOrUpdateModsAsync(_modsDir, "1.21.4", mods, ct: cts.Token);
        await fake.DownloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Equal(new[] { "sodium-fabric-0.6.0+mc1.21.4.jar" }, JarFiles());

        // A token that is already cancelled throws right away
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manager.InstallOrUpdateModsAsync(_modsDir, "1.21.4", mods, ct: cts.Token));
    }

    // ---- Download integrity and safety -----------------------------------

    [Fact]
    public async Task InstallOrUpdate_WithMaliciousFileName_RejectsIt()
    {
        var fake = new FakeModrinthService().Add("sodium", "..\\evil.jar", JarBytes("sodium", "0.6.1"));
        var manager = new ModManagerService(fake);
        var mods = manager.ScanModsDirectory(_modsDir, Pack(Sodium), "1.21.4");

        var result = await manager.InstallOrUpdateModsAsync(_modsDir, "1.21.4", mods);

        var failure = Assert.Single(result.Failures);
        Assert.Contains("invalid file name", failure.Reason);
        Assert.Empty(fake.DownloadedUrls);
        Assert.False(File.Exists(Path.Combine(_tempDir, "evil.jar")));
        Assert.Empty(JarFiles());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InstallOrUpdate_WithRealModrinthService_VerifiesTheDownloadHash(bool hashMatches)
    {
        WriteJar("sodium-fabric-0.6.0+mc1.21.4.jar", "sodium", "0.6.0+mc1.21.4");
        var served = JarBytes("sodium", "0.6.1+mc1.21.4");
        var advertised = hashMatches ? served : JarBytes("sodium", "something-else");
        string json = $@"[{{
            ""project_id"": ""AANobbMI"", ""version_number"": ""mc1.21.4-0.6.1"",
            ""files"": [{{
                ""url"": ""https://cdn.modrinth.com/data/AANobbMI/sodium-fabric-0.6.1%2Bmc1.21.4.jar"",
                ""filename"": ""sodium-fabric-0.6.1+mc1.21.4.jar"", ""primary"": true,
                ""hashes"": {{ ""sha1"": ""{Hex(SHA1.HashData(advertised))}"", ""sha512"": ""{Hex(SHA512.HashData(advertised))}"" }}
            }}],
            ""dependencies"": []
        }}]";

        var handler = new RoutingHandler(request => request.RequestUri!.Host == "cdn.modrinth.com"
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(served) }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        var manager = new ModManagerService(new ModrinthService(new HttpClient(handler)));
        var mods = manager.ScanModsDirectory(_modsDir, Pack(Sodium), "1.21.4");

        var result = await manager.InstallOrUpdateModsAsync(_modsDir, "1.21.4", mods);

        if (hashMatches)
        {
            Assert.Equal(1, result.InstalledOrUpdatedCount);
            Assert.Equal(new[] { "sodium-fabric-0.6.1+mc1.21.4.jar" }, JarFiles());
        }
        else
        {
            var failure = Assert.Single(result.Failures);
            Assert.Contains("Checksum mismatch", failure.Reason);
            Assert.Equal(new[] { "sodium-fabric-0.6.0+mc1.21.4.jar" }, JarFiles());
        }
    }

    [Fact]
    public async Task InstallOrUpdate_WithRealModrinthService_OverwritesAJarWithTheSameNameButOtherContent()
    {
        // Same file name as the latest release, different content: replaced in place
        WriteJar("sodium-fabric-0.6.1+mc1.21.4.jar", "sodium", "0.6.1-local");
        var served = JarBytes("sodium", "0.6.1+mc1.21.4");
        var handler = new RoutingHandler(request => request.RequestUri!.Host == "cdn.modrinth.com"
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(served) }
            : Json(VersionJson("sodium-fabric-0.6.1+mc1.21.4.jar", served)));
        var manager = new ModManagerService(new ModrinthService(new HttpClient(handler)));
        var mods = manager.ScanModsDirectory(_modsDir, Pack(Sodium), "1.21.4");

        var result = await manager.InstallOrUpdateModsAsync(_modsDir, "1.21.4", mods);

        Assert.Equal(1, result.InstalledOrUpdatedCount);
        Assert.Equal(new[] { "sodium-fabric-0.6.1+mc1.21.4.jar" }, JarFiles());
        Assert.Equal(served, File.ReadAllBytes(Path.Combine(_modsDir, "sodium-fabric-0.6.1+mc1.21.4.jar")));
    }

    private static string VersionJson(string fileName, byte[] content, string dependencies = "") => $@"[{{
        ""project_id"": ""AANobbMI"", ""version_number"": ""0.6.1"",
        ""files"": [{{
            ""url"": ""https://cdn.modrinth.com/data/AANobbMI/{Uri.EscapeDataString(fileName)}"",
            ""filename"": ""{fileName}"", ""primary"": true,
            ""hashes"": {{ ""sha1"": ""{Hex(SHA1.HashData(content))}"", ""sha512"": ""{Hex(SHA512.HashData(content))}"" }}
        }}],
        ""dependencies"": [{dependencies}]
    }}]";

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    // ---- Dependencies ----------------------------------------------------

    private const string ZoomifyId = "w7ThoJFB";
    private const string YaclId = "1eAoo2KR";
    private const string FabricApiId = "P7dR8mSH";
    private const string MidnightLibId = "codAaoxh";
    private const string KotlinLangForgeId = "1vrSzlao";

    // Real ids, slugs and loaders as returned by GET /v2/projects
    private static readonly ModrinthProject YaclProject = new(YaclId, "yacl", "YetAnotherConfigLib (YACL)", new[] { "fabric", "neoforge", "quilt" });
    private static readonly ModrinthProject MidnightLibProject = new(MidnightLibId, "midnightlib", "MidnightLib", new[] { "fabric", "forge", "neoforge", "quilt" });
    private static readonly ModrinthProject KotlinLangForgeProject = new(KotlinLangForgeId, "kotlin-lang-forge", "KotlinLangForge", new[] { "forge", "neoforge" });

    private static readonly (string, string, string) CullLeaves = ("cull-leaves", "Cull Leaves", "cullleaves");

    private static List<ModDefinition> DependencyPack() => Pack(
        ("zoomify", "Zoomify", "zoomify"),
        ("yacl", "YetAnotherConfigLib", "yet_another_config_lib_v3"),
        ("fabric-api", "Fabric API", "fabric-api"),
        ("zoomify-addon", "Zoomify Addon", "zoomifyaddon"));

    private static FakeModrinthService DependencyFake(bool yaclAvailable)
    {
        var fake = new FakeModrinthService()
            .Add("zoomify", "zoomify-2.15.2+1.21.4.jar", JarBytes("zoomify", "2.15.2+1.21.4"), ZoomifyId,
                new ModrinthDependency(YaclId, null, "required"),
                new ModrinthDependency(FabricApiId, null, "required"),
                new ModrinthDependency("mOgUt4GM", null, "optional"))
            .Add("fabric-api", "fabric-api-0.119.0+1.21.4.jar", JarBytes("fabric-api", "0.119.0+1.21.4"), FabricApiId)
            .Add("zoomify-addon", "zoomifyaddon-1.0.0.jar", JarBytes("zoomifyaddon", "1.0.0"), "AddOn0001",
                new ModrinthDependency(ZoomifyId, null, "required"));
        if (yaclAvailable)
            fake.Add("yacl", "yet_another_config_lib_v3-3.6.6+1.21.4-fabric.jar", JarBytes("yet_another_config_lib_v3", "3.6.6+1.21.4-fabric"), YaclId);

        fake.Projects.Add(YaclProject);
        return fake;
    }

    [Fact]
    public async Task InstallOrUpdate_SkipsModsWhoseRequiredCuratedDependencyIsMissing()
    {
        var fake = DependencyFake(yaclAvailable: false);
        var manager = new ModManagerService(fake);
        var mods = manager.ScanModsDirectory(_modsDir, DependencyPack(), "1.21.4");

        var result = await manager.InstallOrUpdateModsAsync(_modsDir, "1.21.4", mods);

        Assert.Equal(
            new[] { new ModIssue("Zoomify", "requires YetAnotherConfigLib"), new ModIssue("Zoomify Addon", "requires Zoomify") },
            result.Skipped);
        Assert.Equal(new[] { "YetAnotherConfigLib" }, result.UnavailableModNames);
        Assert.Equal(1, result.InstalledOrUpdatedCount);
        Assert.Equal(new[] { "fabric-api-0.119.0+1.21.4.jar" }, JarFiles());
        Assert.Empty(result.Warnings);
        Assert.Equal(4, result.TotalCount);
    }

    [Fact]
    public async Task InstallOrUpdate_InstallsEverything_WhenCuratedDependenciesArePresent()
    {
        var fake = DependencyFake(yaclAvailable: true);
        var manager = new ModManagerService(fake);
        var mods = manager.ScanModsDirectory(_modsDir, DependencyPack(), "1.21.4");

        var result = await manager.InstallOrUpdateModsAsync(_modsDir, "1.21.4", mods);

        Assert.Empty(result.Skipped);
        Assert.Equal(4, result.InstalledOrUpdatedCount);
    }

    [Fact]
    public async Task InstallOrUpdate_SkippedMod_GetsItsIncompatibleJarDisabled()
    {
        WriteJar("zoomify-2.14.0+1.21.1.jar", "zoomify", "2.14.0+1.21.1", "1.21.1");

        var manager = new ModManagerService(DependencyFake(yaclAvailable: false));
        var mods = manager.ScanModsDirectory(_modsDir, DependencyPack(), "1.21.4");

        var result = await manager.InstallOrUpdateModsAsync(_modsDir, "1.21.4", mods);

        Assert.Contains(new ModIssue("Zoomify", "requires YetAnotherConfigLib"), result.Skipped);
        Assert.Equal(new[] { "zoomify-2.14.0+1.21.1.jar.disabled" }, result.DisabledFileNames);
        Assert.Equal(new[] { "fabric-api-0.119.0+1.21.4.jar", "zoomify-2.14.0+1.21.1.jar.disabled" }, JarFiles());
    }

    [Fact]
    public async Task InstallOrUpdate_WhenProjectsLookupFails_WarnsAndChecksOnlyKnownProjects()
    {
        var fake = DependencyFake(yaclAvailable: false);
        fake.ProjectsError = new HttpRequestException("Modrinth answered HTTP 503 (Service Unavailable)");
        var manager = new ModManagerService(fake);
        var mods = manager.ScanModsDirectory(_modsDir, DependencyPack(), "1.21.4");

        var result = await manager.InstallOrUpdateModsAsync(_modsDir, "1.21.4", mods);

        // YACL has no version here, so its project id isn't known and Zoomify can't be judged
        Assert.Empty(result.Skipped);
        Assert.Empty(result.Failures);
        Assert.Equal(3, result.InstalledOrUpdatedCount);
        Assert.Contains(result.Warnings, w => w.Contains("Could not check mod dependencies"));
    }

    [Fact]
    public async Task InstallOrUpdate_WhenProjectsLookupFails_StillSkipsByProjectIdsLearnedEarlier()
    {
        var fake = DependencyFake(yaclAvailable: true);
        var manager = new ModManagerService(fake);
        var first = await manager.InstallOrUpdateModsAsync(_modsDir, "1.21.4", manager.ScanModsDirectory(_modsDir, DependencyPack(), "1.21.4"));
        Assert.Equal(4, first.InstalledOrUpdatedCount);

        // Another Minecraft version: YACL isn't available, and the projects lookup is down
        string otherModsDir = Path.Combine(_tempDir, "mods-other");
        fake.Remove("yacl");
        fake.ProjectsError = new HttpRequestException("Modrinth answered HTTP 503 (Service Unavailable)");

        var result = await manager.InstallOrUpdateModsAsync(otherModsDir, "1.21.4", manager.ScanModsDirectory(otherModsDir, DependencyPack(), "1.21.4"));

        Assert.Equal(
            new[] { new ModIssue("Zoomify", "requires YetAnotherConfigLib"), new ModIssue("Zoomify Addon", "requires Zoomify") },
            result.Skipped);
        Assert.Contains(result.Warnings, w => w.Contains("Could not check mod dependencies"));
    }

    [Fact]
    public async Task InstallOrUpdate_IgnoresRequiredDependenciesOnProjectsForOtherLoaders()
    {
        // Zoomify 2.17.0+mc26.2 is a Fabric + NeoForge jar and lists the Forge-only KotlinLangForge as required
        var fake = new FakeModrinthService().Add("zoomify", "zoomify-2.17.0+mc26.2.jar", JarBytes("zoomify", "2.17.0+mc26.2"), ZoomifyId,
            new ModrinthDependency(KotlinLangForgeId, null, "required"));
        fake.Projects.Add(KotlinLangForgeProject);
        var manager = new ModManagerService(fake);
        var mods = manager.ScanModsDirectory(_modsDir, Pack(("zoomify", "Zoomify", "zoomify")), "26.2");

        var result = await manager.InstallOrUpdateModsAsync(_modsDir, "26.2", mods);

        Assert.Equal(1, result.InstalledOrUpdatedCount);
        Assert.False(result.HasIssues);
    }

    [Fact]
    public async Task InstallOrUpdate_MissingNonCuratedDependency_InstallsAnywayAndWarns()
    {
        WriteJar("cullleaves-fabric-3.4.0+1.21.1.jar", "cullleaves", "3.4.0+1.21.1", "1.21.1");
        var fake = new FakeModrinthService().Add("cull-leaves", "cullleaves-fabric-4.1.1+1.21.4.jar", JarBytes("cullleaves", "4.1.1+1.21.4"), "CuLLeave",
            new ModrinthDependency(MidnightLibId, null, "required"));
        fake.Projects.Add(MidnightLibProject);
        var manager = new ModManagerService(fake);
        var mods = manager.ScanModsDirectory(_modsDir, Pack(CullLeaves), "1.21.4");

        var result = await manager.InstallOrUpdateModsAsync(_modsDir, "1.21.4", mods);

        Assert.Empty(result.Skipped);
        Assert.Equal(1, result.InstalledOrUpdatedCount);
        Assert.Equal(new[] { "cullleaves-fabric-4.1.1+1.21.4.jar" }, JarFiles());
        Assert.Equal(
            new[] { "Cull Leaves requires MidnightLib, which is not part of the mod pack – the game may not start until MidnightLib is installed" },
            result.Warnings);
    }

    [Fact]
    public async Task InstallOrUpdate_NonCuratedDependencyEmbeddedInTheDownloadedJar_IsSatisfied()
    {
        // Real case: Cull Leaves lists MidnightLib as required on Modrinth but ships it as jar-in-jar
        var fake = new FakeModrinthService().Add("cull-leaves", "cullleaves-fabric-4.1.1+1.21.4.jar",
            JarBytesWithNested("cullleaves", "4.1.1+1.21.4", null, "midnightlib"), "CuLLeave",
            new ModrinthDependency(MidnightLibId, null, "required"));
        fake.Projects.Add(MidnightLibProject);
        var manager = new ModManagerService(fake);
        var mods = manager.ScanModsDirectory(_modsDir, Pack(CullLeaves), "1.21.4");

        var result = await manager.InstallOrUpdateModsAsync(_modsDir, "1.21.4", mods);

        Assert.Equal(1, result.InstalledOrUpdatedCount);
        Assert.False(result.HasIssues);
    }

    [Fact]
    public async Task InstallOrUpdate_CuratedDependencyEmbeddedInTheInstalledJar_DoesNotCauseASkip()
    {
        // The installed Zoomify ships YACL inside; YACL itself has no version for this Minecraft version
        File.WriteAllBytes(Path.Combine(_modsDir, "zoomify-2.15.0+1.21.4.jar"),
            JarBytesWithNested("zoomify", "2.15.0+1.21.4", "1.21.4", "yet_another_config_lib_v3"));
        var fake = DependencyFake(yaclAvailable: false);
        fake.Add("zoomify", "zoomify-2.15.2+1.21.4.jar", JarBytesWithNested("zoomify", "2.15.2+1.21.4", null, "yet_another_config_lib_v3"), ZoomifyId,
            new ModrinthDependency(YaclId, null, "required"),
            new ModrinthDependency(FabricApiId, null, "required"));
        var manager = new ModManagerService(fake);
        var mods = manager.ScanModsDirectory(_modsDir, DependencyPack(), "1.21.4");

        var result = await manager.InstallOrUpdateModsAsync(_modsDir, "1.21.4", mods);

        Assert.Empty(result.Skipped);
        Assert.Empty(result.Warnings);
        Assert.Equal(3, result.InstalledOrUpdatedCount);
        Assert.Equal(new[] { "YetAnotherConfigLib" }, result.UnavailableModNames);
    }

    [Fact]
    public async Task InstallOrUpdate_SkippedModWhoseCompatibleJarStays_StillCountsAsPresent()
    {
        // YACL's update is skipped (Fabric API unavailable), but YACL 3.6.0 stays: Zoomify must not be skipped too
        WriteJar("yet_another_config_lib_v3-3.6.0+1.21.4-fabric.jar", "yet_another_config_lib_v3", "3.6.0+1.21.4-fabric", "1.21.4");
        var fake = new FakeModrinthService()
            .Add("zoomify", "zoomify-2.15.2+1.21.4.jar", JarBytes("zoomify", "2.15.2+1.21.4"), ZoomifyId,
                new ModrinthDependency(YaclId, null, "required"))
            .Add("yacl", "yet_another_config_lib_v3-3.6.6+1.21.4-fabric.jar", JarBytes("yet_another_config_lib_v3", "3.6.6+1.21.4-fabric"), YaclId,
                new ModrinthDependency(FabricApiId, null, "required"));
        fake.Projects.Add(new ModrinthProject(FabricApiId, "fabric-api", "Fabric API", new[] { "fabric" }));
        var manager = new ModManagerService(fake);
        var mods = manager.ScanModsDirectory(_modsDir, Pack(
            ("zoomify", "Zoomify", "zoomify"),
            ("yacl", "YetAnotherConfigLib", "yet_another_config_lib_v3"),
            ("fabric-api", "Fabric API", "fabric-api")), "1.21.4");

        var result = await manager.InstallOrUpdateModsAsync(_modsDir, "1.21.4", mods);

        Assert.Equal(new[] { new ModIssue("YetAnotherConfigLib", "requires Fabric API") }, result.Skipped);
        Assert.Equal(new[] { "Fabric API" }, result.UnavailableModNames);
        Assert.Equal(1, result.InstalledOrUpdatedCount);
        Assert.Empty(result.Warnings);
        Assert.Equal(new[] { "yet_another_config_lib_v3-3.6.0+1.21.4-fabric.jar", "zoomify-2.15.2+1.21.4.jar" }, JarFiles());
    }

    [Theory]
    [InlineData("midnightlib", "midnightlib")]
    [InlineData("midnight-lib", "midnight_lib")] // '-' and '_' count as the same
    public async Task InstallOrUpdate_NonCuratedDependencyInTheModsFolder_IsSatisfied(string projectSlug, string jarModId)
    {
        WriteJar("midnightlib-1.6.9-fabric.jar", jarModId, "1.6.9");
        var fake = new FakeModrinthService().Add("cull-leaves", "cullleaves-fabric-4.1.1+1.21.4.jar", JarBytes("cullleaves", "4.1.1+1.21.4"), "CuLLeave",
            new ModrinthDependency(MidnightLibId, null, "required"));
        fake.Projects.Add(MidnightLibProject with { Slug = projectSlug });
        var manager = new ModManagerService(fake);
        var mods = manager.ScanModsDirectory(_modsDir, Pack(CullLeaves), "1.21.4");

        var result = await manager.InstallOrUpdateModsAsync(_modsDir, "1.21.4", mods);

        Assert.Equal(1, result.InstalledOrUpdatedCount);
        Assert.False(result.HasIssues);
    }

    [Fact]
    public async Task InstallOrUpdate_MissingNonCuratedDependencyOfAnUpToDateMod_IsAWarningEveryRun()
    {
        var latest = JarBytes("cullleaves", "4.1.1+1.21.4");
        File.WriteAllBytes(Path.Combine(_modsDir, "cullleaves-fabric-4.1.1+1.21.4.jar"), latest);
        var fake = new FakeModrinthService().Add("cull-leaves", "cullleaves-fabric-4.1.1+1.21.4.jar", latest, "CuLLeave",
            new ModrinthDependency(MidnightLibId, null, "required"));
        fake.Projects.Add(MidnightLibProject);
        var manager = new ModManagerService(fake);

        for (int run = 0; run < 2; run++)
        {
            var mods = manager.ScanModsDirectory(_modsDir, Pack(CullLeaves), "1.21.4");
            var result = await manager.InstallOrUpdateModsAsync(_modsDir, "1.21.4", mods);

            Assert.Equal(1, result.AlreadyUpToDateCount);
            Assert.Empty(result.Skipped);
            var warning = Assert.Single(result.Warnings);
            Assert.Contains("Cull Leaves requires MidnightLib", warning);
            Assert.Contains("may not start until MidnightLib is installed", warning);
        }
    }

    // ---- ModrinthService parsing -----------------------------------------

    [Fact]
    public async Task ModrinthService_ParsesHashesProjectAndDependencies_AndSkipsVersionsWithoutJar()
    {
        const string json = @"[
            { ""project_id"": ""w7ThoJFB"", ""version_number"": ""9.9.9"", ""files"": [ { ""url"": ""https://cdn/x.zip"", ""filename"": ""x.zip"", ""primary"": true } ], ""dependencies"": [] },
            { ""project_id"": ""w7ThoJFB"", ""version_number"": ""2.15.2+1.21.5"",
              ""files"": [
                { ""url"": ""https://cdn/zoomify-sources.jar"", ""filename"": ""zoomify-2.15.2-sources.jar"", ""primary"": false, ""hashes"": {} },
                { ""url"": ""https://cdn/zoomify.jar"", ""filename"": ""zoomify-2.15.2+1.21.5.jar"", ""primary"": true,
                  ""hashes"": { ""sha512"": ""abc512"", ""sha1"": ""abc1"" } } ],
              ""dependencies"": [
                { ""version_id"": null, ""project_id"": ""1eAoo2KR"", ""file_name"": null, ""dependency_type"": ""required"" },
                { ""version_id"": null, ""project_id"": null, ""file_name"": ""lib.jar"", ""dependency_type"": ""embedded"" } ] }
        ]";

        var service = new ModrinthService(new HttpClient(new RoutingHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") })));

        var file = await service.GetLatestVersionFileAsync("zoomify", "1.21.4");

        Assert.NotNull(file);
        Assert.Equal("zoomify-2.15.2+1.21.5.jar", file.Filename);
        Assert.Equal("https://cdn/zoomify.jar", file.Url);
        Assert.Equal("abc1", file.Sha1);
        Assert.Equal("abc512", file.Sha512);
        Assert.Equal("w7ThoJFB", file.ProjectId);
        Assert.Equal("2.15.2+1.21.5", file.VersionNumber);
        Assert.Equal(
            new[] { new ModrinthDependency("1eAoo2KR", null, "required"), new ModrinthDependency(null, null, "embedded") },
            file.Dependencies);
    }

    [Fact]
    public async Task ModrinthService_NotFoundMeansUnavailable_OtherErrorsThrow()
    {
        var notFound = new ModrinthService(new HttpClient(new RoutingHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound))));
        Assert.Null(await notFound.GetLatestVersionFileAsync("does-not-exist", "1.21.4"));

        var empty = new ModrinthService(new HttpClient(new RoutingHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") })));
        Assert.Null(await empty.GetLatestVersionFileAsync("sodium", "1.21.4"));

        var rateLimited = new ModrinthService(new HttpClient(new RoutingHandler(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests))));
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => rateLimited.GetLatestVersionFileAsync("sodium", "1.21.4"));
        Assert.Contains("429", ex.Message);
    }

    [Fact]
    public async Task ModrinthService_GetProjectsAsync_SendsJsonArrayAndParsesResult()
    {
        Uri? requested = null;
        const string json = @"[ { ""id"": ""1eAoo2KR"", ""slug"": ""yacl"", ""title"": ""YetAnotherConfigLib (YACL)"", ""loaders"": [""fabric"", ""quilt""] },
                               { ""id"": ""AANobbMI"", ""slug"": ""sodium"", ""title"": ""Sodium"" } ]";
        var service = new ModrinthService(new HttpClient(new RoutingHandler(request =>
        {
            requested = request.RequestUri;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        })));

        var projects = await service.GetProjectsAsync(new[] { "sodium", "yacl" });

        Assert.NotNull(requested);
        Assert.Equal("https://api.modrinth.com/v2/projects?ids=%5B%22sodium%22%2C%22yacl%22%5D", requested.AbsoluteUri);
        Assert.Equal(new[] { ("1eAoo2KR", "yacl", "YetAnotherConfigLib (YACL)"), ("AANobbMI", "sodium", "Sodium") },
            projects.Select(p => (p.Id, p.Slug, p.Title)));
        Assert.Equal(new[] { "fabric", "quilt" }, projects[0].Loaders);
        Assert.Empty(projects[1].Loaders!);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ModrinthService_RequiredDependencyWithOnlyAVersionId_GetsItsProjectId(bool versionKnown)
    {
        var served = JarBytes("zoomify", "2.15.2");
        string versions = VersionJson("zoomify-2.15.2.jar", served,
            @"{ ""version_id"": ""Ver12345"", ""project_id"": null, ""dependency_type"": ""required"" },
              { ""version_id"": ""Ver99999"", ""project_id"": null, ""dependency_type"": ""optional"" }");
        var service = new ModrinthService(new HttpClient(new RoutingHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/v2/version/Ver12345" => versionKnown ? Json(@"{ ""id"": ""Ver12345"", ""project_id"": ""1eAoo2KR"" }") : new HttpResponseMessage(HttpStatusCode.NotFound),
            "/v2/project/zoomify/version" => Json(versions),
            _ => throw new InvalidOperationException($"Unexpected request {request.RequestUri}"),
        })));

        var file = await service.GetLatestVersionFileAsync("zoomify", "1.21.4");

        Assert.NotNull(file);
        Assert.Equal(
            new[] { new ModrinthDependency(versionKnown ? "1eAoo2KR" : null, "Ver12345", "required"), new ModrinthDependency(null, "Ver99999", "optional") },
            file.Dependencies);
    }

    [Fact]
    public async Task ModrinthService_TimeoutsAreReportedAccurately()
    {
        static Task<HttpResponseMessage> Hang(HttpRequestMessage _, CancellationToken token) =>
            Task.Delay(Timeout.Infinite, token).ContinueWith(_ => new HttpResponseMessage(HttpStatusCode.OK), token);

        var ownTimeout = new ModrinthService(new HttpClient(new RoutingHandler(Hang))) { MetadataTimeout = TimeSpan.FromMilliseconds(200) };
        var ex1 = await Assert.ThrowsAsync<TimeoutException>(() => ownTimeout.GetLatestVersionFileAsync("sodium", "1.21.4"));
        Assert.StartsWith("Modrinth did not respond within", ex1.Message);
        Assert.DoesNotContain("HTTP client", ex1.Message);

        var clientTimeout = new ModrinthService(new HttpClient(new RoutingHandler(Hang)) { Timeout = TimeSpan.FromMilliseconds(200) });
        var ex2 = await Assert.ThrowsAsync<TimeoutException>(() => clientTimeout.GetProjectsAsync(new[] { "sodium" }));
        Assert.Contains("HTTP client timeout", ex2.Message);

        // A real cancellation stays a cancellation
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ModrinthService(new HttpClient(new RoutingHandler(Hang))).GetLatestVersionFileAsync("sodium", "1.21.4", cts.Token));
    }

    [Fact]
    public async Task ModrinthService_StalledDownload_FailsWithClearReason()
    {
        var service = new ModrinthService(new HttpClient(new RoutingHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StallingStream()) })))
        {
            DownloadStallTimeout = TimeSpan.FromMilliseconds(200)
        };
        string target = Path.Combine(_modsDir, "sodium.jar");

        var ex = await Assert.ThrowsAsync<TimeoutException>(() => service.DownloadFileAsync("https://cdn.modrinth.com/sodium.jar", target));

        Assert.Contains("stalled", ex.Message);
        Assert.Empty(Directory.GetFiles(_modsDir));
    }

    // ---- Release channel and version pins ----------------------------------

    private const string SodiumId = "AANobbMI";
    private const string IrisId = "YL57xq9U";

    private static string VersionEntry(string id, string type, string fileName) => $@"{{
        ""id"": ""{id}"", ""project_id"": ""{SodiumId}"", ""version_number"": ""{id}"", ""version_type"": ""{type}"",
        ""game_versions"": [""26.3""], ""loaders"": [""fabric""],
        ""files"": [{{ ""url"": ""https://cdn.modrinth.com/data/{SodiumId}/{fileName}"", ""filename"": ""{fileName}"", ""primary"": true }}],
        ""dependencies"": []
    }}";

    [Theory]
    // A newer alpha or beta does not win over a release for the same Minecraft version...
    [InlineData("alpha,release", "release")]
    [InlineData("beta,alpha,release,release", "release")]
    // ...but without any release the newest pre-release is taken.
    [InlineData("beta,alpha", "beta")]
    public async Task ModrinthService_PrefersReleasesOverNewerPreReleases(string typesNewestFirst, string expectedType)
    {
        string[] types = typesNewestFirst.Split(',');
        string json = "[" + string.Join(",", types.Select((t, i) => VersionEntry($"v{i}-{t}", t, $"sodium-{i}.jar"))) + "]";
        var service = new ModrinthService(new HttpClient(new RoutingHandler(_ => Json(json))));

        var file = await service.GetLatestVersionFileAsync("sodium", "26.3");

        Assert.NotNull(file);
        Assert.Equal($"v{Array.IndexOf(types, expectedType)}-{expectedType}", file.VersionId);
    }

    [Theory]
    [InlineData("26.3", "fabric", true)]
    [InlineData("26.2", "fabric", false)]
    [InlineData("26.3", "neoforge", false)]
    public async Task ModrinthService_GetVersionFileAsync_OnlyReturnsAFabricBuildForTheVersion(string mcVersion, string loader, bool expectFile)
    {
        string json = $@"{{ ""id"": ""bAZQdGpg"", ""project_id"": ""{SodiumId}"", ""version_number"": ""mc26.3-0.9.2-fabric"",
            ""version_type"": ""release"", ""game_versions"": [""26.3""], ""loaders"": [""{loader}""],
            ""files"": [{{ ""url"": ""https://cdn.modrinth.com/data/{SodiumId}/sodium.jar"", ""filename"": ""sodium-fabric-0.9.2+mc26.3.jar"", ""primary"": true }}],
            ""dependencies"": [] }}";
        Uri? requested = null;
        var service = new ModrinthService(new HttpClient(new RoutingHandler(request =>
        {
            requested = request.RequestUri;
            return Json(json);
        })));

        var file = await service.GetVersionFileAsync("bAZQdGpg", mcVersion);

        Assert.Equal("/v2/version/bAZQdGpg", requested!.AbsolutePath);
        Assert.Equal(expectFile, file != null);
        if (expectFile) Assert.Equal("bAZQdGpg", file!.VersionId);
    }

    private static List<ModDefinition> IrisPack(params (string, string, string)[] more) =>
        Pack(new[] { Sodium, ("iris", "Iris Shaders", "iris") }.Concat(more).ToArray());

    // As on Modrinth for 26.3: the newest Sodium is newer than the build Iris was made for.
    private static FakeModrinthService IrisFake(string pinnedSodiumVersionId) => new FakeModrinthService()
        .Add("sodium", "sodium-fabric-0.9.3+mc26.3.jar", JarBytes("sodium", "0.9.3+mc26.3"), SodiumId)
        .Add("iris", "iris-fabric-1.11.7+mc26.3.jar", JarBytes("iris", "1.11.7+mc26.3"), IrisId,
            new ModrinthDependency(SodiumId, pinnedSodiumVersionId, "required"));

    [Fact]
    public async Task InstallOrUpdate_InstallsTheSodiumBuildIrisPins_InsteadOfTheNewest()
    {
        var fake = IrisFake("bAZQdGpg")
            .AddVersion("bAZQdGpg", "sodium-fabric-0.9.2+mc26.3.jar", JarBytes("sodium", "0.9.2+mc26.3"), SodiumId);
        var manager = new ModManagerService(fake);
        var mods = manager.ScanModsDirectory(_modsDir, IrisPack(), "26.3");

        var result = await manager.InstallOrUpdateModsAsync(_modsDir, "26.3", mods);

        Assert.Equal(new[] { "iris-fabric-1.11.7+mc26.3.jar", "sodium-fabric-0.9.2+mc26.3.jar" }, JarFiles());
        Assert.Equal(2, result.InstalledOrUpdatedCount);
        Assert.False(result.HasIssues);
    }

    [Fact]
    public async Task InstallOrUpdate_ReplacesANewerInstalledSodium_WithTheBuildIrisPins()
    {
        WriteJar("sodium-fabric-0.9.3+mc26.3.jar", "sodium", "0.9.3+mc26.3");
        var fake = IrisFake("bAZQdGpg")
            .AddVersion("bAZQdGpg", "sodium-fabric-0.9.2+mc26.3.jar", JarBytes("sodium", "0.9.2+mc26.3"), SodiumId);
        var manager = new ModManagerService(fake);
        var mods = manager.ScanModsDirectory(_modsDir, IrisPack(), "26.3");

        var result = await manager.InstallOrUpdateModsAsync(_modsDir, "26.3", mods);

        Assert.Equal(new[] { "iris-fabric-1.11.7+mc26.3.jar", "sodium-fabric-0.9.2+mc26.3.jar" }, JarFiles());
        Assert.False(result.HasIssues);
    }

    [Fact]
    public async Task InstallOrUpdate_WhenThePinMatchesTheNewest_InstallsItWithoutAnotherLookup()
    {
        // Without AddVersion a lookup of the pin would come back empty and add a warning.
        var manager = new ModManagerService(IrisFake("sodium-latest"));
        var mods = manager.ScanModsDirectory(_modsDir, IrisPack(), "26.3");

        var result = await manager.InstallOrUpdateModsAsync(_modsDir, "26.3", mods);

        Assert.Equal(new[] { "iris-fabric-1.11.7+mc26.3.jar", "sodium-fabric-0.9.3+mc26.3.jar" }, JarFiles());
        Assert.False(result.HasIssues);
    }

    [Fact]
    public async Task InstallOrUpdate_WhenThePinnedBuildIsNotAvailable_KeepsTheNewestAndWarns()
    {
        var manager = new ModManagerService(IrisFake("NoSuchVer"));
        var mods = manager.ScanModsDirectory(_modsDir, IrisPack(), "26.3");

        var result = await manager.InstallOrUpdateModsAsync(_modsDir, "26.3", mods);

        Assert.Contains("sodium-fabric-0.9.3+mc26.3.jar", JarFiles());
        Assert.Equal(new[] { "The Sodium build that Iris Shaders requires is not available for Minecraft 26.3; the newest one is installed" },
            result.Warnings);
    }

    [Fact]
    public async Task InstallOrUpdate_WhenTwoModsPinDifferentBuilds_KeepsTheNewestAndWarns()
    {
        var fake = IrisFake("PinA0001")
            .Add("sodium-addon", "sodiumaddon-1.0.0.jar", JarBytes("sodiumaddon", "1.0.0"), "AddOn0002",
                new ModrinthDependency(SodiumId, "PinB0002", "required"))
            .AddVersion("PinA0001", "sodium-fabric-0.9.2+mc26.3.jar", JarBytes("sodium", "0.9.2+mc26.3"), SodiumId)
            .AddVersion("PinB0002", "sodium-fabric-0.9.1+mc26.3.jar", JarBytes("sodium", "0.9.1+mc26.3"), SodiumId);
        var manager = new ModManagerService(fake);
        var mods = manager.ScanModsDirectory(_modsDir, IrisPack(("sodium-addon", "Sodium Addon", "sodiumaddon")), "26.3");

        var result = await manager.InstallOrUpdateModsAsync(_modsDir, "26.3", mods);

        Assert.Contains("sodium-fabric-0.9.3+mc26.3.jar", JarFiles());
        Assert.Equal(new[] { "Iris Shaders, Sodium Addon require different builds of Sodium; the newest one is installed" },
            result.Warnings);
    }

    // ---- Fakes -----------------------------------------------------------

    private sealed class FakeModrinthService : IModrinthService
    {
        private readonly Dictionary<string, ModrinthVersionFile> _latest = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, ModrinthVersionFile> _byVersionId = new(StringComparer.Ordinal);
        private readonly Dictionary<string, byte[]> _content = new(StringComparer.Ordinal);
        private int _versionQueries;

        public Dictionary<string, Exception> LookupErrors { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<ModrinthProject> Projects { get; } = new();
        public Exception? ProjectsError { get; set; }
        public ConcurrentBag<string> DownloadedUrls { get; } = new();
        public int VersionQueries => _versionQueries;

        public FakeModrinthService Add(string slug, string fileName, byte[] content, string? projectId = null, params ModrinthDependency[] dependencies)
        {
            string url = $"https://cdn.invalid/{slug}/{Uri.EscapeDataString(fileName)}";
            _latest[slug] = new ModrinthVersionFile
            {
                Url = url,
                Filename = fileName,
                Sha1 = Hex(SHA1.HashData(content)),
                Sha512 = Hex(SHA512.HashData(content)),
                ProjectId = projectId,
                VersionId = $"{slug}-latest",
                VersionNumber = JarInspectorVersion(content),
                Dependencies = dependencies,
            };
            _content[url] = content;
            return this;
        }

        /// <summary>A specific build, e.g. an older one another mod pins; only <see cref="GetVersionFileAsync"/> returns it.</summary>
        public FakeModrinthService AddVersion(string versionId, string fileName, byte[] content, string? projectId = null)
        {
            string url = $"https://cdn.invalid/versions/{versionId}/{Uri.EscapeDataString(fileName)}";
            _byVersionId[versionId] = new ModrinthVersionFile
            {
                Url = url,
                Filename = fileName,
                Sha1 = Hex(SHA1.HashData(content)),
                Sha512 = Hex(SHA512.HashData(content)),
                ProjectId = projectId,
                VersionId = versionId,
                VersionNumber = JarInspectorVersion(content),
            };
            _content[url] = content;
            return this;
        }

        public Task<ModrinthVersionFile?> GetVersionFileAsync(string versionId, string mcVersion, CancellationToken ct = default)
        {
            return Task.FromResult(_byVersionId.TryGetValue(versionId, out var file) ? file : null);
        }

        private static string? JarInspectorVersion(byte[] content)
        {
            string path = Path.Combine(Path.GetTempPath(), $"fake_{Guid.NewGuid():N}.jar");
            try
            {
                File.WriteAllBytes(path, content);
                return JarInspector.ReadFabricModJson(path)?.Version;
            }
            finally
            {
                File.Delete(path);
            }
        }

        public Task<ModrinthVersionFile?> GetLatestVersionFileAsync(string projectSlugOrId, string mcVersion, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _versionQueries);
            if (LookupErrors.TryGetValue(projectSlugOrId, out var error))
                return Task.FromException<ModrinthVersionFile?>(error);

            return Task.FromResult(_latest.TryGetValue(projectSlugOrId, out var file) ? file : null);
        }

        public Task<IReadOnlyList<ModrinthProject>> GetProjectsAsync(IEnumerable<string> idsOrSlugs, CancellationToken ct = default)
        {
            if (ProjectsError != null)
                return Task.FromException<IReadOnlyList<ModrinthProject>>(ProjectsError);

            var wanted = idsOrSlugs.ToHashSet(StringComparer.OrdinalIgnoreCase);
            return Task.FromResult<IReadOnlyList<ModrinthProject>>(Projects.Where(p => wanted.Contains(p.Id) || wanted.Contains(p.Slug)).ToList());
        }

        public async Task DownloadFileAsync(string downloadUrl, string targetFilePath, IProgress<double>? progress = null, CancellationToken ct = default, string? expectedSha512 = null, string? expectedSha1 = null)
        {
            DownloadedUrls.Add(downloadUrl);
            if (BlockDownloads)
            {
                DownloadStarted.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
            }

            File.WriteAllBytes(targetFilePath, _content[downloadUrl]);
        }

        /// <summary>Downloads wait until the run is cancelled.</summary>
        public bool BlockDownloads { get; init; }
        public TaskCompletionSource DownloadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Remove(string slug) => _latest.Remove(slug);
    }

    private sealed class RoutingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _route;

        public RoutingHandler(Func<HttpRequestMessage, HttpResponseMessage> route)
        {
            _route = (request, _) => Task.FromResult(route(request));
        }

        public RoutingHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> route)
        {
            _route = route;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            _route(request, cancellationToken);
    }

    /// <summary>A response body that never delivers a byte.</summary>
    private sealed class StallingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
