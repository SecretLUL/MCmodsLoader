using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using MCmodsLoader.Core.Models;
using MCmodsLoader.Core.Utils;

namespace MCmodsLoader.Core.Services;

public interface IFabricService
{
    Task<FabricStatus> CheckFabricStatusAsync(string minecraftPath, string mcVersion);
    Task<string?> GetLatestLoaderVersionAsync(string mcVersion);
    Task<bool> InstallFabricAsync(string minecraftPath, string mcVersion, IProgress<string>? progress = null);
}

public class FabricService : IFabricService
{
    private readonly HttpClient _httpClient;
    private const string FabricIconBase64 = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAIAAAACABAMAAAAxEHz4AAAAGFBMVEUAAAA4NCrb0LTGvKW8spyAem2uppSakn5SsnMLAAAAAXRSTlMAQObYZgAAAJ5JREFUaIHt1MENgCAMRmFWYAVXcAVXcAVXcH3bhCYNkYjcKO8dSf7v1JASUWdZAlgb0PEmDSMAYYBdGkYApgf8ER3SbwRgesAf0BACMD1gB6S9IbkEEBfwY49oNj4lgLhA64C0o9R9RABTAvp4SX5kB2TA5y8EEAK4pRrxB9QcA4QBWkj3GCAMUCO/xwBhAI/kEsCagCHDY4AwAC3VA6t4zTAMj0OJAAAAAElFTkSuQmCC";

    public FabricService(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        if (!_httpClient.DefaultRequestHeaders.Contains("User-Agent"))
        {
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "SecretLUL/MCmodsLoader (github.com/SecretLUL/MCmodsLoader)");
        }
    }

    public async Task<FabricStatus> CheckFabricStatusAsync(string minecraftPath, string mcVersion)
    {
        string? installedLoaderVersion = null;
        string? profileName = null;

        // 1. Check versions directory
        string versionsDir = Path.Combine(minecraftPath, "versions");
        if (Directory.Exists(versionsDir))
        {
            string suffix = $"-{mcVersion}";
            foreach (var dir in Directory.GetDirectories(versionsDir))
            {
                string dirName = Path.GetFileName(dir);
                // Look for fabric-loader-<loaderVersion>-<mcVersion>
                if (dirName.StartsWith("fabric-loader-", StringComparison.OrdinalIgnoreCase) &&
                    dirName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) &&
                    dirName.Length >= "fabric-loader-".Length + suffix.Length + 1)
                {
                    string extracted = dirName.Substring("fabric-loader-".Length, dirName.Length - "fabric-loader-".Length - suffix.Length);
                    if (!string.IsNullOrWhiteSpace(extracted))
                    {
                        if (installedLoaderVersion == null || MinecraftVersionComparer.Instance.Compare(extracted, installedLoaderVersion) > 0)
                        {
                            installedLoaderVersion = extracted;
                        }
                    }
                }
            }
        }

        // 2. Check launcher_profiles.json
        string profilesFile = Path.Combine(minecraftPath, "launcher_profiles.json");
        if (File.Exists(profilesFile))
        {
            try
            {
                var docOptions = new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
                using var doc = JsonDocument.Parse(File.ReadAllText(profilesFile), docOptions);
                if (doc.RootElement.TryGetProperty("profiles", out var profiles))
                {
                    string suffix = $"-{mcVersion}";
                    foreach (var p in profiles.EnumerateObject())
                    {
                        if (p.Value.TryGetProperty("lastVersionId", out var lastVer))
                        {
                            string ver = lastVer.GetString() ?? "";
                            if (ver.StartsWith("fabric-loader-", StringComparison.OrdinalIgnoreCase) &&
                                ver.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) &&
                                ver.Length >= "fabric-loader-".Length + suffix.Length + 1)
                            {
                                if (p.Value.TryGetProperty("name", out var nameProp) && !string.IsNullOrWhiteSpace(nameProp.GetString()))
                                {
                                    profileName = nameProp.GetString();
                                }
                                else
                                {
                                    profileName = p.Name;
                                }

                                if (installedLoaderVersion == null)
                                {
                                    string extracted = ver.Substring("fabric-loader-".Length, ver.Length - "fabric-loader-".Length - suffix.Length);
                                    if (!string.IsNullOrWhiteSpace(extracted))
                                        installedLoaderVersion = extracted;
                                }
                                break;
                            }
                        }
                    }
                }
            }
            catch
            {
                // Ignore json read errors
            }
        }

        string? latestLoader = await GetLatestLoaderVersionAsync(mcVersion);

        bool isInstalled = !string.IsNullOrEmpty(installedLoaderVersion);
        string description = isInstalled
            ? $"Fabric Loader {installedLoaderVersion} is installed"
            : (latestLoader != null ? $"Not installed (Latest available: {latestLoader})" : "Not installed");

        return new FabricStatus
        {
            IsInstalled = isInstalled,
            InstalledLoaderVersion = installedLoaderVersion,
            ProfileName = profileName ?? (isInstalled ? $"fabric-loader-{mcVersion}" : null),
            LatestAvailableLoaderVersion = latestLoader,
            StatusDescription = description
        };
    }

    public async Task<string?> GetLatestLoaderVersionAsync(string mcVersion)
    {
        try
        {
            string url = $"https://meta.fabricmc.net/v2/versions/loader/{Uri.EscapeDataString(mcVersion)}";
            var response = await _httpClient.GetAsync(url);
            if (!response.IsSuccessStatusCode)
                return null;

            using var stream = await response.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);

            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.TryGetProperty("loader", out var loader))
                {
                    string? version = loader.GetProperty("version").GetString();
                    bool stable = loader.TryGetProperty("stable", out var st) && st.GetBoolean();
                    if (!string.IsNullOrEmpty(version))
                    {
                        // Return the first one (most recent) or prefer stable
                        return version;
                    }
                }
            }
        }
        catch
        {
            // Network or parsing error
        }

        return null;
    }

    public async Task<bool> InstallFabricAsync(string minecraftPath, string mcVersion, IProgress<string>? progress = null)
    {
        progress?.Report($"Checking latest Fabric version for Minecraft {mcVersion}...");
        string? loaderVersion = await GetLatestLoaderVersionAsync(mcVersion);
        if (string.IsNullOrEmpty(loaderVersion))
        {
            progress?.Report($"Could not find a compatible Fabric version for Minecraft {mcVersion}.");
            return false;
        }

        progress?.Report($"Fabric Loader {loaderVersion} found.");

        // Strategy 1: Check for Java and run official Fabric Installer CLI
        string? javaPath = FindJavaExecutable(minecraftPath);
        if (!string.IsNullOrEmpty(javaPath))
        {
            progress?.Report($"Java found ({javaPath}). Using official Fabric Installer...");
            bool cliSuccess = await InstallViaCliAsync(javaPath, minecraftPath, mcVersion, loaderVersion, progress);
            if (cliSuccess)
            {
                progress?.Report("Fabric installed successfully via official installer!");
                return true;
            }
            progress?.Report("CLI installer was unsuccessful. Switching to direct profile installation...");
        }
        else
        {
            progress?.Report("No Java installation found. Using direct profile installation...");
        }

        // Strategy 2: Direct installation via Fabric Meta profile JSON
        bool directSuccess = await InstallDirectAsync(minecraftPath, mcVersion, loaderVersion, progress);
        if (directSuccess)
        {
            progress?.Report("Fabric installed successfully!");
            return true;
        }

        progress?.Report("Error installing Fabric.");
        return false;
    }

    private string? FindJavaExecutable(string minecraftPath)
    {
        // 1. Check system PATH
        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = "java",
                Arguments = "-version",
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (proc != null)
            {
                proc.WaitForExit(3000);
                return "java";
            }
        }
        catch
        {
            // Not in PATH
        }

        // 2. Check JAVA_HOME
        string? javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrEmpty(javaHome))
        {
            string javaExe = Path.Combine(javaHome, "bin", "java.exe");
            if (File.Exists(javaExe))
                return javaExe;
        }

        // 3. Check Minecraft bundled runtime
        string runtimeBase = Path.Combine(minecraftPath, "runtime");
        if (Directory.Exists(runtimeBase))
        {
            string[] searchPaths =
            {
                Path.Combine(runtimeBase, "java-runtime-gamma", "windows", "java-runtime-gamma", "bin", "java.exe"),
                Path.Combine(runtimeBase, "java-runtime-alpha", "windows", "java-runtime-alpha", "bin", "java.exe"),
                Path.Combine(runtimeBase, "java-runtime-beta", "windows", "java-runtime-beta", "bin", "java.exe"),
                Path.Combine(runtimeBase, "java-runtime-delta", "windows", "java-runtime-delta", "bin", "java.exe")
            };

            foreach (var sp in searchPaths)
            {
                if (File.Exists(sp))
                    return sp;
            }

            try
            {
                var files = Directory.GetFiles(runtimeBase, "java.exe", SearchOption.AllDirectories);
                if (files.Length > 0)
                    return files[0];
            }
            catch
            {
                // Ignore search errors
            }
        }

        return null;
    }

    private async Task<bool> InstallViaCliAsync(string javaPath, string minecraftPath, string mcVersion, string loaderVersion, IProgress<string>? progress)
    {
        string tempJar = Path.Combine(Path.GetTempPath(), $"fabric-installer-{Guid.NewGuid():N}.jar");

        try
        {
            progress?.Report("Downloading Fabric Installer...");
            string installerUrl = await GetInstallerJarUrlAsync();
            var bytes = await _httpClient.GetByteArrayAsync(installerUrl);
            await File.WriteAllBytesAsync(tempJar, bytes);

            progress?.Report("Running Fabric Installer...");
            var psi = new ProcessStartInfo
            {
                FileName = javaPath,
                Arguments = $"-jar \"{tempJar}\" client -dir \"{minecraftPath}\" -mcversion {mcVersion} -loader {loaderVersion}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            if (proc == null)
                return false;

            string stdout = await proc.StandardOutput.ReadToEndAsync();
            string stderr = await proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();

            if (proc.ExitCode == 0)
            {
                return true;
            }
        }
        catch (Exception ex)
        {
            progress?.Report($"Installer error: {ex.Message}");
        }
        finally
        {
            try
            {
                if (File.Exists(tempJar))
                    File.Delete(tempJar);
            }
            catch
            {
                // Ignore temp delete failure
            }
        }

        return false;
    }

    private async Task<string> GetInstallerJarUrlAsync()
    {
        try
        {
            var response = await _httpClient.GetAsync("https://meta.fabricmc.net/v2/versions/installer");
            if (response.IsSuccessStatusCode)
            {
                using var stream = await response.Content.ReadAsStreamAsync();
                using var doc = await JsonDocument.ParseAsync(stream);
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    if (el.TryGetProperty("stable", out var st) && st.GetBoolean())
                    {
                        return el.GetProperty("url").GetString()!;
                    }
                }
            }
        }
        catch
        {
            // Fallback
        }

        return "https://maven.fabricmc.net/net/fabricmc/fabric-installer/1.1.2/fabric-installer-1.1.2.jar";
    }

    public async Task<bool> InstallDirectAsync(string minecraftPath, string mcVersion, string loaderVersion, IProgress<string>? progress)
    {
        try
        {
            progress?.Report("Downloading Fabric profile JSON...");
            string profileUrl = $"https://meta.fabricmc.net/v2/versions/loader/{Uri.EscapeDataString(mcVersion)}/{Uri.EscapeDataString(loaderVersion)}/profile/json";
            var response = await _httpClient.GetAsync(profileUrl);
            if (!response.IsSuccessStatusCode)
            {
                progress?.Report($"Failed to download profile JSON (HTTP {response.StatusCode})");
                return false;
            }

            string profileJsonContent = await response.Content.ReadAsStringAsync();

            string versionId = $"fabric-loader-{loaderVersion}-{mcVersion}";
            string versionDir = Path.Combine(minecraftPath, "versions", versionId);
            Directory.CreateDirectory(versionDir);

            string targetJsonPath = Path.Combine(versionDir, $"{versionId}.json");
            await File.WriteAllTextAsync(targetJsonPath, profileJsonContent);

            progress?.Report("Updating Minecraft Launcher profiles...");
            RegisterProfileInLauncher(minecraftPath, mcVersion, versionId);

            return true;
        }
        catch (Exception ex)
        {
            progress?.Report($"Direct installation error: {ex.Message}");
            return false;
        }
    }

    public static void RegisterProfileInLauncher(string minecraftPath, string mcVersion, string versionId)
    {
        string defaultProfilesFile = Path.Combine(minecraftPath, "launcher_profiles.json");
        string msStoreProfilesFile = Path.Combine(minecraftPath, "launcher_profiles_microsoft_store.json");

        // If neither file exists, create a default launcher_profiles.json
        if (!File.Exists(defaultProfilesFile) && !File.Exists(msStoreProfilesFile))
        {
            try
            {
                string dir = Path.GetDirectoryName(defaultProfilesFile) ?? "";
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                var initialObj = new JsonObject
                {
                    ["profiles"] = new JsonObject(),
                    ["version"] = 3
                };
                File.WriteAllText(defaultProfilesFile, initialObj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            }
            catch
            {
                // Ignore initialization error
            }
        }

        string[] profileFiles = { defaultProfilesFile, msStoreProfilesFile };

        foreach (var file in profileFiles)
        {
            if (!File.Exists(file))
                continue;

            try
            {
                string jsonText = File.ReadAllText(file);
                var docOptions = new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
                var node = JsonNode.Parse(jsonText, documentOptions: docOptions);
                if (node is JsonObject rootObj)
                {
                    if (rootObj["profiles"] is not JsonObject profilesObj)
                    {
                        profilesObj = new JsonObject();
                        rootObj["profiles"] = profilesObj;
                    }

                    // Find if any existing profile is for this Minecraft version
                    string suffix = $"-{mcVersion}";
                    string newKey = $"fabric-loader-{mcVersion}";
                    string? targetKey = null;
                    JsonObject? existingProfile = null;

                    foreach (var kvp in profilesObj)
                    {
                        if (kvp.Value is JsonObject pObj)
                        {
                            string lastVer = string.Empty;
                            if (pObj["lastVersionId"] is JsonValue val && val.TryGetValue<string>(out string? s) && s != null)
                            {
                                lastVer = s;
                            }

                            if (kvp.Key.Equals(newKey, StringComparison.OrdinalIgnoreCase) ||
                                lastVer.Equals(versionId, StringComparison.OrdinalIgnoreCase) ||
                                (lastVer.StartsWith("fabric-loader-", StringComparison.OrdinalIgnoreCase) && lastVer.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)))
                            {
                                targetKey = kvp.Key;
                                existingProfile = pObj;
                                break;
                            }
                        }
                    }

                    string nowIso = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

                    if (existingProfile != null && targetKey != null)
                    {
                        // Preserve existing profile's javaArgs, resolution, name, etc.
                        existingProfile["lastVersionId"] = versionId;
                        existingProfile["lastUsed"] = nowIso;
                        if (!existingProfile.ContainsKey("icon") || existingProfile["icon"] == null)
                        {
                            existingProfile["icon"] = FabricIconBase64;
                        }
                    }
                    else
                    {
                        // Create brand-new profile with high-FPS recommended memory settings
                        var newProfile = new JsonObject
                        {
                            ["created"] = nowIso,
                            ["icon"] = FabricIconBase64,
                            ["lastUsed"] = nowIso,
                            ["lastVersionId"] = versionId,
                            ["name"] = $"Fabric {mcVersion}",
                            ["type"] = "custom",
                            ["javaArgs"] = "-Xmx4G -XX:+UnlockExperimentalVMOptions -XX:+UseG1GC -XX:G1NewSizePercent=20 -XX:G1ReservePercent=20 -XX:MaxGCPauseMillis=50 -XX:G1HeapRegionSize=32M"
                        };

                        profilesObj[newKey] = newProfile;
                    }

                    var options = new JsonSerializerOptions
                    {
                        WriteIndented = true,
                        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                    };
                    File.WriteAllText(file, rootObj.ToJsonString(options));
                }
            }
            catch
            {
                // Continue with other files if any error
            }
        }
    }
}
