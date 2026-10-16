![MCperfBooster – Minecraft. Faster. Simpler.](src/MCperfBooster.UI/Assets/readme-banner.png)

# MCperfBooster

> **Zero-Setup Performance Injector for the Vanilla Launcher**

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![Platform: Windows](https://img.shields.io/badge/Platform-Windows-0078D6.svg)](https://microsoft.com/windows)
[![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4.svg)](https://dotnet.microsoft.com/)
[![Built for Fabric](https://img.shields.io/badge/Fabric-ModLoader-D97706.svg)](https://fabricmc.net/)

**MCperfBooster** is a lightweight, zero-configuration utility designed for friends and casual players who want buttery-smooth Minecraft FPS without needing to learn modding, install third-party launchers, or fiddle with `.minecraft` directories.

With a single click, it sets up the **Fabric Loader** and injects an essential suite of **top-tier performance and quality-of-life mods** directly into the official vanilla Minecraft launcher.

---

## 🎯 Why MCperfBooster?

* **No Extra Launchers:** Your friends keep using the standard official Minecraft Launcher they already know and trust.
* **Zero Configuration:** Automatically locates `.minecraft`, determines compatible mod versions, and registers launcher profiles.
* **Automatic Fabric Setup:** If Fabric is missing, the tool automatically downloads and configures the latest Fabric Loader for the selected Minecraft version.
* **Direct Modrinth Integration:** Downloads the newest stable release of each mod directly from the official [Modrinth API](https://modrinth.com/); an alpha or beta is only used when a Minecraft version has nothing else, and builds a mod depends on exactly (such as the Sodium version Iris was made for) are respected.
* **Conflict Prevention:** Automatically purges outdated mod jars when updating to avoid duplicate file crashes.
* **Built-in Self-Updater:** Checks GitHub releases on startup; warns you when an update is ready and updates itself in one click.

---

## 🚀 Quick Start Guide

1. **Download** the latest `MCperfBooster.exe` from [GitHub Releases](https://github.com/SecretLUL/MCperfBooster/releases).
2. **Launch** the program (no installation required).
3. **Select your Minecraft version** (e.g. `1.21.4`, `1.21.1`, etc.).
4. Click **`Inject Performance Mods`**.
5. Open your official **Minecraft Launcher**, select the new **`fabric-loader`** profile, and enjoy high FPS!

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
| **Distant Horizons** | No | Level-of-detail rendering for very far view distances. Demanding on CPU, GPU and memory, so it is opt-in. |

---

## 🛠️ Architecture & Tech Stack

* **Frontend:** A dark-mode UI drawn directly on Win32 (GDI), compiled ahead of time with **NativeAOT**. WPF was dropped because it cannot be trimmed, which forced a ~68 MB self-contained download; the AOT binary is ~6 MB and needs no .NET runtime installed.
* **Core Engine:** `MCperfBooster.Core`
  * `FabricService`: Probes local `launcher_profiles.json` and versions directory; runs the official Fabric CLI installer if Java is present, or falls back to direct Meta profile JSON registration.
  * `MinecraftService`: Automatically resolves `%APPDATA%\.minecraft`, detects installed versions, and retrieves game version manifests.
  * `ModrinthService`: Asynchronous HTTP client targeting the Modrinth v2 REST API to locate primary Fabric-compatible jar releases.
  * `ModManagerService`: Inspects existing mods (reads internal `fabric.mod.json`), removes obsolete jars, and downloads updates.
  * `UpdateService`: Checks GitHub REST API for new releases, verifies the downloaded executable (PE header and the release asset's SHA-256 digest), and swaps it in by renaming the running executable to `MCperfBooster.old.exe`, which the new build deletes on startup.
* **Testing:** xUnit test suite (`MCperfBooster.Tests`) covering version parsing, zip metadata inspection, mod presets, and directory validation.

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
The version is taken from the newest Git tag (`v1.2.0`, or `v1.3.0-beta.1` for a
pre-release). A source tree without tags, such as a ZIP download or a shallow clone,
still builds and is stamped with the SDK default `1.0.0`.

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

This project is licensed under the [MIT License](LICENSE). Minecraft is a trademark of Mojang AB / Microsoft. MCperfBooster is an independent open-source project and is not affiliated with Mojang or Microsoft.
