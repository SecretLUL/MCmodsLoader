using System.Text;
using System.Text.Json;
using MCperfBooster.Core.Models;
using MCperfBooster.Core.Services;
using Xunit.Abstractions;

namespace MCperfBooster.Tests;

public class FabricJavaSettingsTests : IDisposable
{
    private const long Gib = 1024L * 1024 * 1024;

    // What the launcher itself puts in a new profile; the tests spell it out on purpose.
    private const string LauncherDefaultJavaArgs =
        "-Xmx2G -XX:+UnlockExperimentalVMOptions -XX:+UseG1GC -XX:G1NewSizePercent=20 -XX:G1ReservePercent=20 -XX:MaxGCPauseMillis=50 -XX:G1HeapRegionSize=32M";

    private readonly string _mc = TempFolders.Create("fabricjava");

    public void Dispose() => TempFolders.Delete(_mc);

    private string ProfilesFile => Path.Combine(_mc, "launcher_profiles.json");
    private string StoreProfilesFile => Path.Combine(_mc, "launcher_profiles_microsoft_store.json");

    private static string Esc(string value) => JsonEncodedText.Encode(value).ToString();

    /// <summary>One profile per case that matters for a game version of 1.21.1.</summary>
    private string SampleProfiles() => $$"""
        {
          "profiles": {
            "fabric-loader-1.21.1": {
              "name": "Fabric 1.21.1", "type": "custom", "icon": "data:image/png;base64,AAA",
              "lastVersionId": "fabric-loader-0.16.10-1.21.1",
              "resolution": { "width": 1280, "height": 720 },
              "javaArgs": "-Xmx1G",
              "futureField": [1, 2, 3]
            },
            "same-folder": { "lastVersionId": "fabric-loader-0.15.0-1.21.1", "gameDir": "{{Esc(_mc.ToUpperInvariant() + "\\")}}" },
            "slash-folder": { "lastVersionId": "fabric-loader-0.15.0-1.21.1", "gameDir": "{{Esc(_mc.Replace('\\', '/'))}}" },
            "empty-folder": { "lastVersionId": "fabric-loader-0.15.0-1.21.1", "gameDir": "" },
            "null-folder": { "lastVersionId": "fabric-loader-0.15.0-1.21.1", "gameDir": null },
            "own-pack": { "name": "Pack", "lastVersionId": "fabric-loader-0.16.10-1.21.1", "gameDir": "{{Esc(@"C:\Packs\Create")}}" },
            "other-version": { "lastVersionId": "fabric-loader-0.16.10-1.20.4" },
            "vanilla": { "lastVersionId": "1.21.1" },
            "latest": { "lastVersionId": "latest-release" }
          },
          "settings": { "keepLauncherOpen": true },
          "version": 3
        }
        """;

    private static JsonElement ReadProfile(string file, string key)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        return doc.RootElement.GetProperty("profiles").GetProperty(key).Clone();
    }

    private static string? Str(JsonElement profile, string property) =>
        profile.TryGetProperty(property, out var value) ? value.GetString() : null;

    private const string Javaw = @"C:\Users\x\AppData\Local\MCperfBooster\runtimes\graalvm-21\bin\javaw.exe";

    [Fact]
    public void ApplyJavaSettings_SetsJavaArgsAndJavaDirOnEveryFabricProfileOfThatGameVersion()
    {
        File.WriteAllText(ProfilesFile, SampleProfiles());

        var update = FabricService.ApplyJavaSettings(_mc, "1.21.1", Javaw, "-Xms6G -Xmx6G");

        // The profile of the app, and the ones that use this very game folder (spelled differently or not at all).
        Assert.Equal(new ProfileJavaUpdate(Matched: 5, Changed: 5), update);
        foreach (string key in new[] { "fabric-loader-1.21.1", "same-folder", "slash-folder", "empty-folder", "null-folder" })
        {
            var profile = ReadProfile(ProfilesFile, key);
            Assert.Equal("-Xms6G -Xmx6G", Str(profile, "javaArgs"));
            Assert.Equal(Javaw, Str(profile, "javaDir"));
        }
    }

    [Fact]
    public void ApplyJavaSettings_LeavesOtherPacksOtherVersionsAndEverythingElseAlone()
    {
        File.WriteAllText(ProfilesFile, SampleProfiles());

        FabricService.ApplyJavaSettings(_mc, "1.21.1", Javaw, "-Xms6G -Xmx6G");

        // A profile with a game folder of its own is another modpack's.
        foreach (string key in new[] { "own-pack", "other-version", "vanilla", "latest" })
        {
            var profile = ReadProfile(ProfilesFile, key);
            Assert.False(profile.TryGetProperty("javaArgs", out _), key);
            Assert.False(profile.TryGetProperty("javaDir", out _), key);
        }

        // Fields the app does not know about survive the rewrite.
        var fabric = ReadProfile(ProfilesFile, "fabric-loader-1.21.1");
        Assert.Equal("Fabric 1.21.1", Str(fabric, "name"));
        Assert.Equal("custom", Str(fabric, "type"));
        Assert.Equal("data:image/png;base64,AAA", Str(fabric, "icon"));
        Assert.Equal("fabric-loader-0.16.10-1.21.1", Str(fabric, "lastVersionId"));
        Assert.Equal(1280, fabric.GetProperty("resolution").GetProperty("width").GetInt32());
        Assert.Equal(new[] { 1, 2, 3 }, fabric.GetProperty("futureField").EnumerateArray().Select(e => e.GetInt32()));
        using var doc = JsonDocument.Parse(File.ReadAllText(ProfilesFile));
        Assert.True(doc.RootElement.GetProperty("settings").GetProperty("keepLauncherOpen").GetBoolean());
        Assert.Equal(3, doc.RootElement.GetProperty("version").GetInt32());
        Assert.Equal(new[] { ProfilesFile }, Directory.GetFiles(_mc));
    }

    [Fact]
    public void ApplyJavaSettings_WithoutAJavaExecutable_RemovesJavaDirSoTheLauncherUsesItsOwnJava()
    {
        File.WriteAllText(ProfilesFile, SampleProfiles());
        FabricService.ApplyJavaSettings(_mc, "1.21.1", Javaw, "-Xms6G -Xmx6G");

        // The same arguments: the Java executable going away is a change in itself.
        Assert.Equal(new ProfileJavaUpdate(5, 5), FabricService.ApplyJavaSettings(_mc, "1.21.1", null, "-Xms6G -Xmx6G"));
        var profile = ReadProfile(ProfilesFile, "fabric-loader-1.21.1");
        Assert.False(profile.TryGetProperty("javaDir", out _));
        Assert.Equal("-Xms6G -Xmx6G", Str(profile, "javaArgs"));
        Assert.Equal(new ProfileJavaUpdate(5, 0), FabricService.ApplyJavaSettings(_mc, "1.21.1", null, "-Xms6G -Xmx6G"));

        // And the arguments keep following along without one.
        Assert.Equal(new ProfileJavaUpdate(5, 5), FabricService.ApplyJavaSettings(_mc, "1.21.1", null, "-Xms4G -Xmx4G"));
        Assert.Equal("-Xms4G -Xmx4G", Str(ReadProfile(ProfilesFile, "fabric-loader-1.21.1"), "javaArgs"));
        Assert.False(ReadProfile(ProfilesFile, "own-pack").TryGetProperty("javaArgs", out _));
    }

    [Fact]
    public void ApplyJavaSettings_WhenTheSettingsAreAlreadyThere_ChangesNothingAndDoesNotRewriteTheFile()
    {
        File.WriteAllText(ProfilesFile, SampleProfiles());
        Assert.Equal(new ProfileJavaUpdate(5, 5), FabricService.ApplyJavaSettings(_mc, "1.21.1", Javaw, "-Xms6G -Xmx6G"));
        string before = File.ReadAllText(ProfilesFile);
        var oldTime = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(ProfilesFile, oldTime);

        var again = FabricService.ApplyJavaSettings(_mc, "1.21.1", Javaw, "-Xms6G -Xmx6G");

        // The profiles are still found, they just need nothing: that is not the same as "no profile".
        Assert.Equal(new ProfileJavaUpdate(Matched: 5, Changed: 0), again);
        Assert.Equal(before, File.ReadAllText(ProfilesFile));
        Assert.Equal(oldTime, File.GetLastWriteTimeUtc(ProfilesFile));
    }

    [Fact]
    public void ApplyJavaSettings_WorksOnBothLauncherProfileFiles()
    {
        string profiles = """{ "profiles": { "f": { "lastVersionId": "fabric-loader-0.16.10-1.21.1" } } }""";
        File.WriteAllText(ProfilesFile, profiles);
        File.WriteAllText(StoreProfilesFile, profiles);

        var update = FabricService.ApplyJavaSettings(_mc, "1.21.1", Javaw, "-Xmx4G");

        Assert.Equal(new ProfileJavaUpdate(2, 2), update);
        Assert.Equal(Javaw, Str(ReadProfile(ProfilesFile, "f"), "javaDir"));
        Assert.Equal(Javaw, Str(ReadProfile(StoreProfilesFile, "f"), "javaDir"));
        Assert.Equal(new[] { ProfilesFile, StoreProfilesFile }.OrderBy(p => p), Directory.GetFiles(_mc).OrderBy(p => p));
    }

    [Fact]
    public void ApplyJavaSettings_SkipsAFileItCannotReadAndDoesTheOther()
    {
        File.WriteAllText(ProfilesFile, "{ this is not json");
        File.WriteAllText(StoreProfilesFile, """{ "profiles": { "f": { "lastVersionId": "fabric-loader-0.16.10-1.21.1" } } }""");

        var update = FabricService.ApplyJavaSettings(_mc, "1.21.1", Javaw, "-Xmx4G");

        Assert.Equal((1, 1), (update.Matched, update.Changed));
        Assert.StartsWith("launcher_profiles.json: ", update.Error);
        Assert.Equal("{ this is not json", File.ReadAllText(ProfilesFile));
        Assert.Equal("-Xmx4G", Str(ReadProfile(StoreProfilesFile, "f"), "javaArgs"));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{ "profiles": [] }""")]
    [InlineData("""{ "profiles": { "a": 5, "b": null, "c": "text" } }""")]
    [InlineData("[]")]
    public void ApplyJavaSettings_WithoutUsableProfiles_ChangesNothing(string json)
    {
        File.WriteAllText(ProfilesFile, json);

        Assert.Equal(new ProfileJavaUpdate(0, 0), FabricService.ApplyJavaSettings(_mc, "1.21.1", Javaw, "-Xmx4G"));
        Assert.Equal(json, File.ReadAllText(ProfilesFile));
    }

    [Fact]
    public void ApplyJavaSettings_WithoutProfileFiles_ChangesNothing()
    {
        Assert.Equal(new ProfileJavaUpdate(0, 0), FabricService.ApplyJavaSettings(_mc, "1.21.1", Javaw, "-Xmx4G"));
        Assert.Empty(Directory.GetFiles(_mc));
    }

    [Fact]
    public void ApplyJavaSettings_ReadsProfilesWithCommentsAndTrailingCommas()
    {
        File.WriteAllText(ProfilesFile, """
            {
                // hand-edited
                "profiles": { "f": { "lastVersionId": "fabric-loader-0.16.10-1.21.1", }, },
            }
            """);

        Assert.Equal(new ProfileJavaUpdate(1, 1), FabricService.ApplyJavaSettings(_mc, "1.21.1", Javaw, "-Xmx4G"));
    }

    [Fact]
    public void ApplyJavaSettings_MatchesTheGameVersionExactly()
    {
        File.WriteAllText(ProfilesFile, """
            { "profiles": {
                "a": { "lastVersionId": "fabric-loader-0.16.10-1.21.1" },
                "b": { "lastVersionId": "fabric-loader-0.20.0-beta.2-26.3" }
            } }
            """);

        // A prefix or a tail of the version is another version.
        Assert.Equal(new ProfileJavaUpdate(0, 0), FabricService.ApplyJavaSettings(_mc, "1.21", Javaw, "-Xmx4G"));
        Assert.Equal(new ProfileJavaUpdate(0, 0), FabricService.ApplyJavaSettings(_mc, "21.1", Javaw, "-Xmx4G"));
        Assert.Equal(new ProfileJavaUpdate(0, 0), FabricService.ApplyJavaSettings(_mc, "26", Javaw, "-Xmx4G"));

        // A loader build with a dash in it does not confuse the game version.
        Assert.Equal(new ProfileJavaUpdate(1, 1), FabricService.ApplyJavaSettings(_mc, "26.3", Javaw, "-Xmx4G"));
        Assert.Null(Str(ReadProfile(ProfilesFile, "a"), "javaArgs"));
        Assert.Equal("-Xmx4G", Str(ReadProfile(ProfilesFile, "b"), "javaArgs"));
    }

    [Fact]
    public void ApplyJavaSettings_ForAProfileOfAnotherPackOnly_FindsNoMatch()
    {
        string json = """
            { "profiles": {
                "own-pack": { "lastVersionId": "fabric-loader-0.16.10-1.21.1", "gameDir": "C:\\Packs\\Create" },
                "vanilla": { "lastVersionId": "1.21.1" }
            } }
            """;
        File.WriteAllText(ProfilesFile, json);

        // Not a profile of this folder or not Fabric: the caller can tell "nothing to set it on" from "already set".
        Assert.Equal(new ProfileJavaUpdate(0, 0), FabricService.ApplyJavaSettings(_mc, "1.21.1", Javaw, "-Xmx4G"));
        Assert.Equal(json, File.ReadAllText(ProfilesFile));
    }

    [Fact]
    public void ApplyJavaSettings_CountsMatchesOverBothFilesEvenWhenOnlyOneNeedsAChange()
    {
        string profiles = """{ "profiles": { "f": { "lastVersionId": "fabric-loader-0.16.10-1.21.1", "javaArgs": "-Xmx4G" } } }""";
        File.WriteAllText(ProfilesFile, profiles);
        File.WriteAllText(StoreProfilesFile, profiles.Replace("-Xmx4G", "-Xmx1G"));

        Assert.Equal(new ProfileJavaUpdate(Matched: 2, Changed: 1), FabricService.ApplyJavaSettings(_mc, "1.21.1", null, "-Xmx4G"));
    }

    [Fact]
    public void ApplyJavaSettings_WhenAFileCannotBeWritten_CountsItsProfilesForNeither()
    {
        string json = """{ "profiles": { "f": { "lastVersionId": "fabric-loader-0.16.10-1.21.1" } } }""";
        File.WriteAllText(ProfilesFile, json);

        // Readable but neither replaceable nor renamable, which is how another program holding the file looks.
        using (new FileStream(ProfilesFile, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var update = FabricService.ApplyJavaSettings(_mc, "1.21.1", Javaw, "-Xmx4G");

            // Counting it as matched would let the caller call the Java "set up" while the file is still as it was;
            // the error tells the caller which file it was instead.
            Assert.Equal((0, 0), (update.Matched, update.Changed));
            Assert.StartsWith("launcher_profiles.json: ", update.Error);
        }

        Assert.Equal(json, File.ReadAllText(ProfilesFile));
        Assert.Equal(new[] { ProfilesFile }, Directory.GetFiles(_mc));
    }

    private string RuntimesRoot => Path.Combine(_mc, "runtimes");

    [Fact]
    public void DetachRuntimes_TakesProfilesOffTheRuntimeAndPutsBackTheArgumentsForTheirGameVersion()
    {
        string home21 = Path.Combine(RuntimesRoot, "graalvm-21", "bin", "javaw.exe");
        string home17 = Path.Combine(RuntimesRoot, "graalvm-17", "bin", "javaw.exe");
        string upperHome25 = Path.Combine(RuntimesRoot.ToUpperInvariant(), "GRAALVM-25", "BIN", "JAVAW.EXE");
        string lookalike = Path.Combine(RuntimesRoot + "-evil", "graalvm-21", "bin", "javaw.exe");
        string elsewhere = @"C:\Program Files\Java\jdk-21\bin\javaw.exe";

        File.WriteAllText(ProfilesFile, $$"""
            { "profiles": {
              "a": { "name": "A", "lastVersionId": "fabric-loader-0.16.10-1.21.1", "javaDir": "{{Esc(home21)}}", "javaArgs": "graal-args" },
              "b": { "lastVersionId": "fabric-loader-0.16.10-1.21.1", "javaDir": "{{Esc(elsewhere)}}", "javaArgs": "mine" },
              "c": { "lastVersionId": "fabric-loader-0.20.0-beta.2-26.3", "javaDir": "{{Esc(upperHome25)}}", "javaArgs": "graal-args" },
              "d": { "lastVersionId": "1.21.1", "javaDir": "{{Esc(home21)}}", "javaArgs": "keep" },
              "e": { "lastVersionId": "fabric-loader-0.16.10-1.21.1", "javaDir": "{{Esc(lookalike)}}", "javaArgs": "keep" },
              "f": { "lastVersionId": "fabric-loader-0.16.10-1.21.1", "javaArgs": "keep" },
              "g": { "lastVersionId": "fabric-loader-0.16.10-1.16.4", "javaDir": "{{Esc(home17)}}", "javaArgs": "graal-args" }
            }, "settings": { "keepLauncherOpen": true } }
            """);

        int changed = FabricService.DetachRuntimes(_mc, RuntimesRoot, version => version switch
        {
            "1.21.1" => "hotspot-21",
            "26.3" => "hotspot-25",
            _ => null,
        }).Changed;

        Assert.Equal(4, changed);
        // Detached, with the arguments of their game version.
        Assert.False(ReadProfile(ProfilesFile, "a").TryGetProperty("javaDir", out _));
        Assert.Equal("hotspot-21", Str(ReadProfile(ProfilesFile, "a"), "javaArgs"));
        Assert.Equal("A", Str(ReadProfile(ProfilesFile, "a"), "name"));
        Assert.False(ReadProfile(ProfilesFile, "c").TryGetProperty("javaDir", out _));
        Assert.Equal("hotspot-25", Str(ReadProfile(ProfilesFile, "c"), "javaArgs"));
        // Not a Fabric version id, or no arguments for that game version: only the Java is taken away.
        Assert.False(ReadProfile(ProfilesFile, "d").TryGetProperty("javaDir", out _));
        Assert.Equal("keep", Str(ReadProfile(ProfilesFile, "d"), "javaArgs"));
        Assert.False(ReadProfile(ProfilesFile, "g").TryGetProperty("javaDir", out _));
        Assert.Equal("graal-args", Str(ReadProfile(ProfilesFile, "g"), "javaArgs"));
        // Another Java, a folder that merely starts like the runtimes folder, and no Java at all: untouched.
        Assert.Equal(elsewhere, Str(ReadProfile(ProfilesFile, "b"), "javaDir"));
        Assert.Equal("mine", Str(ReadProfile(ProfilesFile, "b"), "javaArgs"));
        Assert.Equal(lookalike, Str(ReadProfile(ProfilesFile, "e"), "javaDir"));
        Assert.Equal("keep", Str(ReadProfile(ProfilesFile, "e"), "javaArgs"));
        Assert.Equal("keep", Str(ReadProfile(ProfilesFile, "f"), "javaArgs"));
    }

    [Fact]
    public void DetachRuntimes_AsksForTheGameVersionAfterEverySplitOfTheVersionId()
    {
        File.WriteAllText(ProfilesFile, $$"""
            { "profiles": { "a": { "lastVersionId": "fabric-loader-0.20.0-beta.2-26.3-pre-2", "javaDir": "{{Esc(Path.Combine(RuntimesRoot, "graalvm-25", "bin", "javaw.exe"))}}" } } }
            """);
        var asked = new List<string>();

        FabricService.DetachRuntimes(_mc, RuntimesRoot, version =>
        {
            asked.Add(version);
            return version == "26.3-pre-2" ? "args" : null;
        });

        // "beta.2-26.3-pre-2" starts with a letter: the end of the loader's version, not a game version.
        Assert.Equal(new[] { "26.3-pre-2" }, asked);
        Assert.Equal("args", Str(ReadProfile(ProfilesFile, "a"), "javaArgs"));
    }

    [Fact]
    public void DetachRuntimes_WorksOnBothFilesAndIgnoresProfilesWithoutAJava()
    {
        string profiles = $$"""
            { "profiles": {
              "a": { "lastVersionId": "fabric-loader-0.16.10-1.21.1", "javaDir": "{{Esc(Path.Combine(RuntimesRoot, "graalvm-21", "bin", "javaw.exe"))}}" },
              "b": { "lastVersionId": "fabric-loader-0.16.10-1.21.1" }
            } }
            """;
        File.WriteAllText(ProfilesFile, profiles);
        File.WriteAllText(StoreProfilesFile, profiles);

        int changed = FabricService.DetachRuntimes(_mc, RuntimesRoot, _ => "hotspot").Changed;

        Assert.Equal(2, changed);
        foreach (string file in new[] { ProfilesFile, StoreProfilesFile })
        {
            Assert.False(ReadProfile(file, "a").TryGetProperty("javaDir", out _));
            Assert.Equal("hotspot", Str(ReadProfile(file, "a"), "javaArgs"));
            Assert.False(ReadProfile(file, "b").TryGetProperty("javaArgs", out _));
        }

        Assert.Equal(new ProfileJavaUpdate(0, 0), FabricService.DetachRuntimes(_mc, RuntimesRoot, _ => "hotspot"));
    }

    [Fact]
    public void DetachRuntimes_WithoutProfileFiles_ChangesNothing()
    {
        Assert.Equal(new ProfileJavaUpdate(0, 0), FabricService.DetachRuntimes(_mc, RuntimesRoot, _ => "hotspot"));
        Assert.Empty(Directory.GetFiles(_mc));
    }

    private string NewProfileJavaArgs(string mcVersion, SystemProfile system)
    {
        FabricService.RegisterProfileInLauncher(_mc, mcVersion, $"fabric-loader-0.16.10-{mcVersion}", system);
        return Str(ReadProfile(ProfilesFile, $"fabric-loader-{mcVersion}"), "javaArgs")!;
    }

    [Theory]
    [InlineData(4, 4)]
    [InlineData(8, 8)]
    [InlineData(16, 8)]
    [InlineData(16, 16)]
    [InlineData(32, 16)]
    public void RegisterProfileInLauncher_GivesANewProfileTheHotSpotArgumentsForThisPc(int ramGib, int cpus)
    {
        var system = new SystemProfile(ramGib * Gib, cpus);

        string args = NewProfileJavaArgs("1.21.1", system);

        Assert.Equal(JvmArguments.Build(21, graalVm: false, system, distantHorizons: false), args);
        Assert.DoesNotContain("TuneInlinerExploration", args);
    }

    [Fact]
    public void RegisterProfileInLauncher_SizesTheMemoryByTheRamOfThePc()
    {
        Assert.StartsWith("-Xms1G -Xmx2G -XX:+UnlockExperimentalVMOptions", NewProfileJavaArgs("1.21.1", new SystemProfile(4 * Gib, 4)));
        File.Delete(ProfilesFile);
        Assert.StartsWith("-Xms1536M -Xmx3G -XX:+UnlockExperimentalVMOptions", NewProfileJavaArgs("1.21.1", new SystemProfile(8 * Gib, 4)));
        File.Delete(ProfilesFile);
        Assert.StartsWith("-Xms4G -Xmx4G -XX:+UnlockExperimentalVMOptions", NewProfileJavaArgs("1.21.1", new SystemProfile(16 * Gib, 4)));
        File.Delete(ProfilesFile);
        Assert.StartsWith("-Xms6G -Xmx6G -XX:+UnlockExperimentalVMOptions", NewProfileJavaArgs("1.21.1", new SystemProfile(32 * Gib, 4)));
    }

    [Theory]
    // A new profile starts on the launcher's own Java: 8 for 1.16.5 and 16 for 1.17, which only get memory and G1.
    [InlineData("1.16.5", 8)]
    [InlineData("1.17.1", 16)]
    [InlineData("1.18", 17)]
    [InlineData("1.20.4", 17)]
    [InlineData("1.20.5", 21)]
    [InlineData("1.21.11", 21)]
    [InlineData("26.1", 25)]
    public void RegisterProfileInLauncher_PicksTheArgumentsOfTheLaunchersJava(string mcVersion, int javaMajor)
    {
        var system = new SystemProfile(16 * Gib, 16);

        Assert.Equal(JvmArguments.Build(javaMajor, graalVm: false, system, distantHorizons: false), NewProfileJavaArgs(mcVersion, system));
    }

    [Fact]
    public void RegisterProfileInLauncher_For1_16_5_LeavesOutWhatJava8Refuses()
    {
        string args = NewProfileJavaArgs("1.16.5", new SystemProfile(32 * Gib, 16));

        Assert.Equal("-Xms6G -Xmx6G -XX:+UseG1GC -XX:MaxGCPauseMillis=37 -XX:+DisableExplicitGC", args);
    }

    [Fact]
    public void ApplyJavaSettings_WithAReadOnlyProfileFile_NamesItInsteadOfFindingNoProfile()
    {
        File.WriteAllText(ProfilesFile, """{ "profiles": { "a": { "lastVersionId": "fabric-loader-0.16.10-1.21.1" } } }""");
        File.SetAttributes(ProfilesFile, FileAttributes.ReadOnly);
        try
        {
            ProfileJavaUpdate update = FabricService.ApplyJavaSettings(_mc, "1.21.1", @"C:\x\javaw.exe", "-Xmx2G");

            Assert.Equal(0, update.Matched);
            Assert.Equal(0, update.Changed);
            Assert.StartsWith("launcher_profiles.json: ", update.Error);
        }
        finally
        {
            File.SetAttributes(ProfilesFile, FileAttributes.Normal);
        }
    }

    [Fact]
    public void DetachRuntimes_WithAReadOnlyProfileFile_ReportsItAndLeavesTheProfileAsItWas()
    {
        string javaw = Path.Combine(RuntimesRoot, "graalvm-21", "bin", "javaw.exe");
        File.WriteAllText(ProfilesFile, $$"""
            { "profiles": { "a": { "lastVersionId": "fabric-loader-0.16.10-1.21.1", "javaDir": "{{Esc(javaw)}}" } } }
            """);
        File.SetAttributes(ProfilesFile, FileAttributes.ReadOnly);
        try
        {
            ProfileJavaUpdate update = FabricService.DetachRuntimes(_mc, RuntimesRoot, _ => "hotspot");

            Assert.Equal(0, update.Changed);
            Assert.NotNull(update.Error);
            Assert.Equal(javaw, Str(ReadProfile(ProfilesFile, "a"), "javaDir"));
        }
        finally
        {
            File.SetAttributes(ProfilesFile, FileAttributes.Normal);
        }
    }

    [Theory]
    [InlineData("1.16.4")]
    [InlineData("1.8.9")]
    public void RegisterProfileInLauncher_ForAGameBeforeTheTable_KeepsTheLaunchersDefaults(string mcVersion)
    {
        Assert.Equal(LauncherDefaultJavaArgs, NewProfileJavaArgs(mcVersion, new SystemProfile(32 * Gib, 16)));
    }

    [Fact]
    public void RegisterProfileInLauncher_LeavesTheArgumentsOfAnExistingProfileAlone()
    {
        File.WriteAllText(ProfilesFile, """
            { "profiles": {
                "fabric-loader-1.21.1": { "name": "Mine", "lastVersionId": "fabric-loader-0.15.0-1.21.1", "javaArgs": "-Xmx16G -XX:+CustomFlag", "javaDir": "C:\\my\\javaw.exe" },
                "fabric-loader-26.1": { "name": "No args", "lastVersionId": "fabric-loader-0.15.0-26.1" }
            } }
            """);

        FabricService.RegisterProfileInLauncher(_mc, "1.21.1", "fabric-loader-0.16.10-1.21.1", new SystemProfile(32 * Gib, 16));
        FabricService.RegisterProfileInLauncher(_mc, "26.1", "fabric-loader-0.19.5-26.1", new SystemProfile(32 * Gib, 16));

        var mine = ReadProfile(ProfilesFile, "fabric-loader-1.21.1");
        Assert.Equal("fabric-loader-0.16.10-1.21.1", Str(mine, "lastVersionId"));
        Assert.Equal("-Xmx16G -XX:+CustomFlag", Str(mine, "javaArgs"));
        Assert.Equal(@"C:\my\javaw.exe", Str(mine, "javaDir"));
        var noArgs = ReadProfile(ProfilesFile, "fabric-loader-26.1");
        Assert.Equal("fabric-loader-0.19.5-26.1", Str(noArgs, "lastVersionId"));
        Assert.False(noArgs.TryGetProperty("javaArgs", out _));
    }

    [Fact]
    public void FindJavaExecutable_PrefersTheNewestLauncherRuntime()
    {
        string runtimes = Path.Combine(_mc, "runtime");
        string delta = Path.Combine(runtimes, "java-runtime-delta", "windows-x64", "java-runtime-delta", "bin", "java.exe");
        string epsilon = Path.Combine(runtimes, "java-runtime-epsilon", "windows-x64", "java-runtime-epsilon", "bin", "java.exe");
        foreach (string java in new[] { delta, epsilon })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(java)!);
            File.WriteAllText(java, "not really java");
        }

        Assert.Equal(epsilon, FabricService.FindJavaExecutable(null, null, new[] { runtimes }));
    }
}

public class AppSettingsGraalVmTests : IDisposable
{
    private readonly string _dir = TempFolders.Create("graalsettings");

    public void Dispose() => TempFolders.Delete(_dir);

    private string SettingsFile => Path.Combine(_dir, "settings.json");

    [Fact]
    public void ANewSettingsObject_UsesGraalVmUntilTheUserHasConsented()
    {
        var settings = new AppSettings();

        Assert.True(settings.UseGraalVm);
        Assert.False(settings.GraalVmConsentGiven);
    }

    [Fact]
    public void Load_WithoutAFile_GivesTheDefaults()
    {
        var settings = AppSettings.Load(SettingsFile);

        Assert.True(settings.UseGraalVm);
        Assert.False(settings.GraalVmConsentGiven);
    }

    [Fact]
    public void Load_FromAFileOfAnEarlierBuild_KeepsTheDefaultsAndTheModChoices()
    {
        File.WriteAllText(SettingsFile, """{ "optionalMods": { "distanthorizons": true, "xaeros-minimap": false } }""");

        var settings = AppSettings.Load(SettingsFile);

        Assert.True(settings.UseGraalVm);
        Assert.False(settings.GraalVmConsentGiven);
        Assert.True(settings.OptionalModChoices["distanthorizons"]);
        Assert.False(settings.OptionalModChoices["xaeros-minimap"]);
    }

    [Fact]
    public void SaveAndLoad_PersistBothSwitches()
    {
        var settings = new AppSettings { UseGraalVm = false, GraalVmConsentGiven = true };
        settings.SetOptionalModEnabled("distanthorizons", true);
        settings.Save(SettingsFile);

        using (var doc = JsonDocument.Parse(File.ReadAllText(SettingsFile)))
        {
            Assert.False(doc.RootElement.GetProperty("useGraalVm").GetBoolean());
            Assert.True(doc.RootElement.GetProperty("graalVmConsent").GetBoolean());
            Assert.True(doc.RootElement.GetProperty("optionalMods").GetProperty("distanthorizons").GetBoolean());
        }

        var loaded = AppSettings.Load(SettingsFile);
        Assert.False(loaded.UseGraalVm);
        Assert.True(loaded.GraalVmConsentGiven);
        Assert.True(loaded.OptionalModChoices["distanthorizons"]);

        loaded.UseGraalVm = true;
        loaded.GraalVmConsentGiven = false;
        loaded.Save(SettingsFile);
        var reloaded = AppSettings.Load(SettingsFile);
        Assert.True(reloaded.UseGraalVm);
        Assert.False(reloaded.GraalVmConsentGiven);
    }

    [Fact]
    public void Save_WritesTheDefaultsToo()
    {
        new AppSettings().Save(SettingsFile);

        using var doc = JsonDocument.Parse(File.ReadAllText(SettingsFile));
        Assert.True(doc.RootElement.GetProperty("useGraalVm").GetBoolean());
        Assert.False(doc.RootElement.GetProperty("graalVmConsent").GetBoolean());
    }

    [Theory]
    [InlineData("""{ "useGraalVm": "no", "graalVmConsent": 1 }""")]
    [InlineData("""{ "useGraalVm": null, "graalVmConsent": "true" }""")]
    [InlineData("""{ "useGraalVm": [], "graalVmConsent": {} }""")]
    public void Load_IgnoresValuesThatAreNotBooleans(string json)
    {
        File.WriteAllText(SettingsFile, json);

        var settings = AppSettings.Load(SettingsFile);

        Assert.True(settings.UseGraalVm);
        Assert.False(settings.GraalVmConsentGiven);
    }

    [Fact]
    public void Clone_CopiesBothSwitchesAndStaysIndependent()
    {
        var settings = new AppSettings { UseGraalVm = false, GraalVmConsentGiven = true };

        var copy = settings.Clone();
        settings.UseGraalVm = true;
        settings.GraalVmConsentGiven = false;

        Assert.False(copy.UseGraalVm);
        Assert.True(copy.GraalVmConsentGiven);
    }
}

/// <summary>
/// Runs every argument set on real GraalVM installs. Local only: it does nothing unless MCPERF_GRAALVM_HOMES
/// names the installs, as "17=C:\path\to\jdk17;21=C:\path\to\jdk21;25=C:\path\to\jdk25". Needs several GB of
/// free memory, as the biggest sets reserve and touch an 8 GB heap.
/// </summary>
public class GraalVmRealJvmTests
{
    private const long Gib = 1024L * 1024 * 1024;

    private readonly ITestOutputHelper _output;

    public GraalVmRealJvmTests(ITestOutputHelper output) => _output = output;

    private static Dictionary<int, string> ParseHomes(string homes)
    {
        var result = new Dictionary<int, string>();
        foreach (string entry in homes.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] parts = entry.Split('=', 2, StringSplitOptions.TrimEntries);
            if (parts.Length != 2 || !int.TryParse(parts[0], out int major))
                throw new InvalidOperationException($"MCPERF_GRAALVM_HOMES entry '{entry}' is not <major>=<path>.");

            result[major] = parts[1];
        }

        return result;
    }

    private static GraalVmService CreateService() =>
        new(new HttpClient(new FakeDownloadServer()), TempFolders.Create("realjvm"));

    private static InstalledRuntime RuntimeAt(int major, string home)
    {
        var runtime = new InstalledRuntime(major, home, Path.Combine(home, "bin", "java.exe"), Path.Combine(home, "bin", "javaw.exe"), "local", "");
        Assert.True(File.Exists(runtime.JavaExe), $"No java.exe at {runtime.JavaExe}");
        return runtime;
    }

    [Fact]
    public async Task TheSelfTest_RejectsUnknownGraalAndVmOptions()
    {
        string? homes = Environment.GetEnvironmentVariable("MCPERF_GRAALVM_HOMES");
        if (string.IsNullOrWhiteSpace(homes))
            return;

        var service = CreateService();
        foreach (var (major, home) in ParseHomes(homes).OrderBy(h => h.Key))
        {
            var runtime = RuntimeAt(major, home);
            string graalPrefix = major == 25 ? "jdk.graal" : "graal";

            ArgumentTestResult fine = await service.TestArgumentsAsync(runtime, $"-Xmx1G -D{graalPrefix}.TuneInlinerExploration=1", CancellationToken.None);
            Assert.True(fine.Ok, $"Java {major}: {fine.Output}");
            Assert.Contains("GraalVM", fine.Output);

            // Without eager initialization Java 17 and 21 would not even look at a Graal option.
            ArgumentTestResult badGraal = await service.TestArgumentsAsync(runtime, $"-Xmx1G -D{graalPrefix}.NoSuchOption=1", CancellationToken.None);
            Assert.False(badGraal.Ok, $"Java {major} accepted an unknown Graal option");
            Assert.Equal(1, badGraal.ExitCode);
            Assert.Contains("Error parsing Graal options", badGraal.Output);

            ArgumentTestResult badVm = await service.TestArgumentsAsync(runtime, "-Xmx1G -XX:+NoSuchVmOption", CancellationToken.None);
            Assert.False(badVm.Ok, $"Java {major} accepted an unknown VM option");
            Assert.Equal(1, badVm.ExitCode);
            Assert.Contains("Unrecognized VM option", badVm.Output);
        }
    }

    [Fact]
    public async Task EveryArgumentSet_StartsTheRealGraalVm()
    {
        string? homes = Environment.GetEnvironmentVariable("MCPERF_GRAALVM_HOMES");
        if (string.IsNullOrWhiteSpace(homes))
            return;

        var failures = new StringBuilder();
        int runs = 0;
        var service = CreateService();

        foreach (var (major, home) in ParseHomes(homes).OrderBy(h => h.Key))
        {
            var runtime = RuntimeAt(major, home);
            int before = runs;

            async Task Check(string label, string args)
            {
                runs++;
                ArgumentTestResult result = await service.TestArgumentsAsync(runtime, args, CancellationToken.None);
                if (!result.Ok)
                    failures.AppendLine($"Java {major}, {label}: exit {result.ExitCode}: {result.Output}\n    {args}");
                else if (result.Output.Contains("warning", StringComparison.OrdinalIgnoreCase))
                    failures.AppendLine($"Java {major}, {label}: exit 0 but with a warning: {result.Output}\n    {args}");
            }

            foreach (int ramGib in new[] { 4, 8, 16, 32 })
            foreach (int cpus in new[] { 4, 8, 16 })
            foreach (bool distantHorizons in new[] { false, true })
            {
                var system = new SystemProfile(ramGib * Gib, cpus);
                string label = $"{ramGib} GiB, {cpus} CPUs, DH {distantHorizons}";
                await Check(label, JvmArguments.Build(major, graalVm: true, system, distantHorizons));

                // The same set for the launcher's HotSpot, and the fallback, once per memory class.
                if (cpus == 16)
                {
                    await Check(label + ", HotSpot", JvmArguments.Build(major, graalVm: false, system, distantHorizons));
                    await Check(label + ", minimal", JvmArguments.BuildMinimal(system, distantHorizons));
                }
            }

            _output.WriteLine($"Java {major}: {runs - before} argument sets run against {home}");
        }

        Assert.True(failures.Length == 0, failures.ToString());
    }
}
