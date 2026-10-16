![MCperfBooster – Minecraft. Faster. Simpler.](src/MCperfBooster.UI/Assets/readme-banner.svg)

# MCperfBooster

> **Zero-Setup Performance Booster for the Vanilla Launcher**

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![Platform: Windows](https://img.shields.io/badge/Platform-Windows-0078D6.svg)](https://microsoft.com/windows)
[![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4.svg)](https://dotnet.microsoft.com/)
[![Built for Fabric](https://img.shields.io/badge/Fabric-ModLoader-D97706.svg)](https://fabricmc.net/)
[![Runs on GraalVM](https://img.shields.io/badge/Java-Oracle_GraalVM-F29111.svg)](https://www.graalvm.org/)

**MCperfBooster** (formerly MCmodsLoader) is a lightweight, zero-configuration utility designed for friends and casual players who want buttery-smooth Minecraft FPS without needing to learn modding, install third-party launchers, or fiddle with `.minecraft` directories.

With a single click, it sets up the **Fabric Loader**, injects an essential suite of **top-tier performance and quality-of-life mods**, and runs the game on **Oracle GraalVM** with **Java arguments tuned to your PC** – all inside the official vanilla Minecraft launcher. Nothing it changes touches your graphics settings, so the game looks exactly the same, only faster.

---

## 🎯 Why MCperfBooster?

* **No Extra Launchers:** Your friends keep using the standard official Minecraft Launcher they already know and trust.
* **Zero Configuration:** Automatically locates `.minecraft`, determines compatible mod versions, and registers launcher profiles.
* **Automatic Fabric Setup:** If Fabric is missing, the tool automatically downloads and configures the latest Fabric Loader for the selected Minecraft version.
* **GraalVM per Minecraft Version:** Picks the Java version your Minecraft version needs and runs it on Oracle GraalVM, whose optimizing compiler makes the game's code faster.
* **Java Arguments for Your PC:** Memory and garbage collection are set from your PC's RAM and processor, for weak laptops and strong gaming PCs alike, and tested once before they are saved.
* **Direct Modrinth Integration:** Downloads the newest stable release of each mod directly from the official [Modrinth API](https://modrinth.com/); an alpha or beta is only used when a Minecraft version has nothing else, and builds a mod depends on exactly (such as the Sodium version Iris was made for) are respected.
* **Conflict Prevention:** Automatically purges outdated mod jars when updating to avoid duplicate file crashes.
* **Built-in Self-Updater:** Checks GitHub releases on startup; warns you when an update is ready and updates itself in one click.

---

## 🚀 Quick Start Guide

1. **Download** the latest `MCperfBooster.exe` from [GitHub Releases](https://github.com/SecretLUL/MCperfBooster/releases).
2. **Launch** the program (no installation required).
3. **Select your Minecraft version** (e.g. `26.3`, `1.21.11`, `1.20.1`).
4. Click **`Inject Performance Mods`**. The first time, it asks before downloading GraalVM.
5. Open your official **Minecraft Launcher**, select the **Fabric** profile it names, and enjoy high FPS!

---

## ☕ GraalVM and Java Arguments

### Which Java runs which Minecraft

| Minecraft | Java Mojang requires | MCperfBooster uses |
| :--- | :---: | :--- |
| 1.16.5 – 1.17.1 | 8 or 16 | Oracle GraalVM for JDK 17 (17.0.12) |
| 1.18 – 1.20.4 | 17 | Oracle GraalVM for JDK 17 (17.0.12) |
| 1.20.5 – 1.21.11 | 21 | Oracle GraalVM for JDK 21 (21.0.12) |
| 26.1 and newer | 25 | Oracle GraalVM 25 (newest 25.0.x, updated automatically) |

Versions older than 1.16.5 keep the launcher's own Java. For a future Minecraft version that needs a newer Java than any GraalVM release, the launcher's Java is kept as well, until a GraalVM for it exists.

* **Download once, small on disk:** GraalVM comes straight from `download.oracle.com` (about 320–360 MB per Java version) and is checked against its SHA-256 checksum. Only the parts needed to play are unpacked, about 300 MB per version, to `%LOCALAPPDATA%\MCperfBooster\runtimes`. Only the versions you actually play are downloaded.
* **Updates:** GraalVM 25 is updated whenever Oracle publishes a new 25.0.x build. 17.0.12 and 21.0.12 are the last builds of those versions Oracle offers for free; newer ones require a paid Oracle license, so they stay as they are.
* **Switch it off:** Untick **Use GraalVM** under **Advanced**. The next injection puts the launcher's own Java back (with the same tuned arguments) and removes the downloaded GraalVM.

### Java arguments

| Your PC's memory | Memory for Minecraft | With Distant Horizons |
| :--- | :--- | :--- |
| under 6 GB | 2 GB, starts at 1 GB | 2 GB |
| 6 – 12 GB | 3 GB, starts at half | 4 GB |
| 12 – 24 GB | 4 GB, reserved up front | 6 GB |
| 24 GB and more | 6 GB, reserved up front | 8 GB |

On top of that come G1 garbage collector settings tuned for short pauses (fewer stutters), a larger code cache on PCs with 12 GB or more, earlier background collection on processors with 8 or more threads, and deeper GraalVM inlining on processors with 12 or more threads. Before anything is saved, MCperfBooster starts the chosen Java once with exactly these arguments; if it does not start cleanly, a minimal safe set is used instead, so a bad argument can never keep Minecraft from launching. The arguments are written to the Fabric profiles of the selected version that use your `.minecraft` folder; profiles with their own game directory are left alone.

### License

Oracle GraalVM is free to use under the [GraalVM Free Terms and Conditions (GFTC)](https://www.oracle.com/downloads/licenses/graal-free-license.html). MCperfBooster downloads it directly from Oracle for you and does not redistribute it. MCperfBooster is not affiliated with Oracle.

---

## 📦 Curated Modpack

MCperfBooster installs 21 mandatory mods and offers 3 optional ones.

### Mandatory mods

Always installed. A mod that has no build for the selected Minecraft version yet is skipped without counting as an error, and is installed as soon as Modrinth has one. ModernFix, for example, is not yet available for the newest versions.

| Mod | Category | Purpose |
| :--- | :--- | :--- |
| **Sodium** | Performance | State-of-the-art rendering engine delivering colossal FPS boosts and stutter reduction. |
| **Lithium** | Performance | General-purpose optimization for physics, AI, chunk loading, and block ticking. |
| **FerriteCore** | Performance | Memory usage optimizations that drastically cut down RAM consumption. |
| **ModernFix** | Performance | Faster game launch and world loading, lower memory use, and many small fixes. |
| **Krypton** | Performance | Optimizes the networking stack, cutting CPU and memory overhead for smoother multiplayer. |
| **Entity Culling** | Performance | Skips rendering of hidden entities and tile entities for higher framerates. |
| **ImmediatelyFast** | Performance | Accelerates immediate mode rendering, optimizing HUD, fonts, and particle rendering. |
| **More Culling** | Performance | Aggressively skips rendering invisible block faces, item frame backs, and sign texts. |
| **Cull Leaves** | Performance | Culls interior faces of leaf blocks for substantial FPS boosts in forest biomes. |
| **BadOptimizations** | Performance | Micro-optimizations for lightmap caching, sky color sampling, and client tick loops. |
| **Iris Shaders** | Graphics | Shader pack support built on Sodium; costs next to nothing until a shader pack is turned on. |
| **Fast IP Ping** | Quality of Life | Pings multiplayer servers concurrently in the background for instant server list loading. |
| **LambDynamicLights** | Quality of Life | Dynamic hand-held and entity lighting effects with high performance. |
| **Zoomify** | Quality of Life | Smooth, highly configurable camera zoom with mouse wheel support. |
| **AppleSkin** | Quality of Life | Displays food saturation, exhaustion levels, and potential health restoration in the HUD. |
| **Mod Menu** | Quality of Life | In-game mod list and settings interface accessible right from the pause menu. |
| **Fabric API** | Library | Essential core hooks required by virtually all modern Fabric mods. |
| **Fabric Language Kotlin** | Library | Kotlin language adapter and runtime required by modern Kotlin-based mods. |
| **Cloth Config API** | Library | Configuration screen API required by More Culling and companion mods. |
| **YetAnotherConfigLib (YACL)** | Library | Configuration and GUI framework required by Zoomify and companion mods. |
| **Text Placeholder API** | Library | Text formatting and placeholder parsing utility for UI and HUD mods. |

### Optional mods

Switched on or off under **Advanced** in the app; the choice is remembered. A mod you switch off is removed on the next injection. An optional mod you installed yourself is picked up as switched on and kept up to date from then on.

| Mod | On by default | Purpose |
| :--- | :---: | :--- |
| **Xaero's Minimap** | Yes | Lightweight, smooth in-game minimap with waypoint markers and entity radar. |
| **Xaero's World Map** | Yes | Fullscreen map showing all explored terrain and waypoints. |
| **Distant Horizons** | No | Level-of-detail rendering for very far view distances. Demanding on CPU, GPU and memory, so it is opt-in; with it on, Minecraft gets more memory (see above). |

---

## 🛠️ Architecture & Tech Stack

* **Frontend:** A dark-mode UI drawn directly on Win32 (GDI), compiled ahead of time with **NativeAOT**. WPF was dropped because it cannot be trimmed, which forced a ~68 MB self-contained download; the AOT binary is ~6 MB and needs no .NET runtime installed.
* **Core Engine:** `MCperfBooster.Core`
  * `FabricService`: Probes local `launcher_profiles.json` and versions directory; runs the official Fabric CLI installer if Java is present, or falls back to direct Meta profile JSON registration; writes the Java executable and arguments into the Fabric profiles.
  * `JavaRuntimeCatalog`: Maps each Minecraft version to the Java version it needs and to the matching Oracle GraalVM build, and reads the version JSON for Minecraft versions newer than the table.
  * `GraalVmService`: Downloads, verifies (SHA-256), slims down and installs GraalVM, keeps GraalVM 25 up to date, and test-starts Java with the arguments before they are used.
  * `JvmArguments`: Builds the Java arguments from the Java version and the PC's memory and processor count.
  * `MinecraftService`: Automatically resolves `%APPDATA%\.minecraft`, detects installed versions, and retrieves game version manifests.
  * `ModrinthService`: Asynchronous HTTP client targeting the Modrinth v2 REST API to locate primary Fabric-compatible jar releases.
  * `ModManagerService`: Inspects existing mods (reads internal `fabric.mod.json`), removes obsolete jars, and downloads updates.
  * `UpdateService`: Checks GitHub REST API for new releases, verifies the downloaded executable (PE header and the release asset's SHA-256 digest), and swaps it in by renaming the running executable to `<name>.old.exe`, which the new build deletes on startup.
* **Testing:** xUnit test suite (`MCperfBooster.Tests`) covering version parsing, zip metadata inspection, mod presets, directory validation, the GraalVM installer and the Java arguments.

---

## 💻 Building from Source

### Prerequisites
* [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
* Windows 10/11 (64-bit)
* For publishing only: the **Desktop development with C++** workload (MSVC linker and
  Windows SDK). NativeAOT links a native binary, so `dotnet build` and `dotnet test`
  work without it, but `dotnet publish` does not.

### Clone & Build
```powershell
# Clone repository
git clone https://github.com/SecretLUL/MCperfBooster.git
cd MCperfBooster

# Run automated tests
dotnet test

# Build debug binaries
dotnet build
```
The version is taken from the newest Git tag (`v2.0.0`, or `v2.1.0-beta.1` for a
pre-release). A source tree without tags, such as a ZIP download or a shallow clone,
still builds and is stamped with the SDK default `1.0.0`.

To check the Java arguments against real GraalVM builds, point the test suite at
unpacked GraalVM homes before `dotnet test`; without the variable that test does nothing:
```powershell
$env:MCPERF_GRAALVM_HOMES = "17=C:\jdks\graalvm-17;21=C:\jdks\graalvm-21;25=C:\jdks\graalvm-25"
```

### Publish the Native Executable
To produce the standalone portable `.exe` for distribution:
```powershell
# The AOT link step shells out to vswhere.exe to locate the MSVC linker,
# and vswhere is not on PATH by default.
$env:PATH = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer;$env:PATH"

dotnet publish src/MCperfBooster.UI/MCperfBooster.UI.csproj `
  -c Release `
  -r win-x64 `
  -o ./publish
```
`PublishAot` is set in the project file for any build with a runtime identifier, so no
extra flags are needed. The output `./publish/MCperfBooster.exe` is around 6 MB, runs on
a machine with no .NET installed, and is the only file the release ships.

### Releasing
Releases are made on GitHub: **Actions → Release Build → Run workflow**, pick which
number of the newest release goes up (`patch`, `minor` or `major`) and start it. The
workflow counts the new version from the newest release tag, builds and tests the
executable under it, and pushes the tag only once that build has passed. Tick
**dry run** for a rehearsal that builds everything and attaches the executable to the
run, but pushes no tag and publishes no release. Notes placed at
`docs/release-notes/<tag>.md` appear above the generated list of changes. Pushing a
`v*` tag by hand still releases exactly that tag.

---

## 🤝 Contributing & Support

Issues and pull requests are welcome! If you find a bug or have a suggestion for the default modpack, feel free to open an issue on GitHub.

## 📄 License

This project is licensed under the [MIT License](LICENSE). Minecraft is a trademark of Mojang AB / Microsoft. Oracle, Java and GraalVM are trademarks of Oracle and/or its affiliates. MCperfBooster is an independent open-source project and is not affiliated with Mojang, Microsoft or Oracle.
