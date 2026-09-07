# MCmodsLoader ⚡

> **Zero-Setup Performance Injector for the Vanilla Launcher**

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![Platform: Windows](https://img.shields.io/badge/Platform-Windows-0078D6.svg)](https://microsoft.com/windows)
[![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4.svg)](https://dotnet.microsoft.com/)
[![Built for Fabric](https://img.shields.io/badge/Fabric-ModLoader-D97706.svg)](https://fabricmc.net/)

**MCmodsLoader** is a lightweight, zero-configuration utility designed for friends and casual players who want buttery-smooth Minecraft FPS without needing to learn modding, install third-party launchers, or fiddle with `.minecraft` directories.

With a single click, it sets up the **Fabric Loader** and injects an essential suite of **top-tier performance and quality-of-life mods** directly into the official vanilla Minecraft launcher.

---

## 🎯 Why MCmodsLoader?

* **No Extra Launchers:** Your friends keep using the standard official Minecraft Launcher they already know and trust.
* **Zero Configuration:** Automatically locates `.minecraft`, determines compatible mod versions, and registers launcher profiles.
* **Automatic Fabric Setup:** If Fabric is missing, the tool automatically downloads and configures the latest Fabric Loader for the selected Minecraft version.
* **Direct Modrinth Integration:** Downloads the latest, primary release jars directly from the official [Modrinth API](https://modrinth.com/).
* **Conflict Prevention:** Automatically purges outdated mod jars when updating to avoid duplicate file crashes.
* **Built-in Self-Updater:** Checks GitHub releases on startup; warns you when an update is ready and updates itself in one click.

---

## 🚀 Quick Start Guide

1. **Download** the latest `MCmodsLoader.exe` from [GitHub Releases](https://github.com/SecretLUL/MCmodsLoader/releases).
2. **Launch** the program (no installation required).
3. **Select your Minecraft version** (e.g. `1.21.4`, `1.21.1`, etc.).
4. Click **`Inject Performance Mods`**.
5. Open your official **Minecraft Launcher**, select the new **`fabric-loader`** profile, and enjoy high FPS!

---

## 📦 Curated Modpack

MCmodsLoader comes pre-configured with 15 performance and quality-of-life mods:

| Mod | Category | Purpose |
| :--- | :--- | :--- |
| **Sodium** | Performance | State-of-the-art rendering engine delivering colossal FPS boosts and stutter reduction. |
| **Lithium** | Performance | General-purpose optimization for physics, AI, chunk loading, and block ticking. |
| **FerriteCore** | Performance | Memory usage optimizations that drastically cut down RAM consumption. |
| **Entity Culling** | Performance | Skips rendering of hidden entities and tile entities for higher framerates. |
| **ImmediatelyFast** | Performance | Accelerates immediate mode rendering, optimizing HUD, fonts, and particle rendering. |
| **LambDynamicLights** | Quality of Life | Dynamic hand-held and entity lighting effects with high performance. |
| **Zoomify** | Quality of Life | Smooth, highly configurable camera zoom with mouse wheel support. |
| **AppleSkin** | Quality of Life | Displays food saturation, exhaustion levels, and potential health restoration in the HUD. |
| **Xaero's Minimap** | Quality of Life | Lightweight, smooth in-game minimap with waypoint markers and entity radar. |
| **Xaero's World Map** | Quality of Life | Fullscreen map showing all explored terrain and waypoints. |
| **Mod Menu** | Quality of Life | In-game mod list and settings interface accessible right from the pause menu. |
| **Fabric API** | Library | Essential core hooks required by virtually all modern Fabric mods. |
| **Fabric Language Kotlin** | Library | Kotlin language adapter and runtime required by modern Kotlin-based mods. |
| **YetAnotherConfigLib (YACL)** | Library | Configuration and GUI framework required by Zoomify and companion mods. |
| **Text Placeholder API** | Library | Text formatting and placeholder parsing utility for UI and HUD mods. |

---

## 🛠️ Architecture & Tech Stack

* **Frontend:** A dark-mode UI drawn directly on Win32 (GDI), compiled ahead of time with **NativeAOT**. WPF was dropped because it cannot be trimmed, which forced a ~68 MB self-contained download; the AOT binary is ~6 MB and needs no .NET runtime installed.
* **Core Engine:** `MCmodsLoader.Core`
  * `FabricService`: Probes local `launcher_profiles.json` and versions directory; runs the official Fabric CLI installer if Java is present, or falls back to direct Meta profile JSON registration.
  * `MinecraftService`: Automatically resolves `%APPDATA%\.minecraft`, detects installed versions, and retrieves game version manifests.
  * `ModrinthService`: Asynchronous HTTP client targeting the Modrinth v2 REST API to locate primary Fabric-compatible jar releases.
  * `ModManagerService`: Inspects existing mods (reads internal `fabric.mod.json`), removes obsolete jars, and downloads updates.
  * `UpdateService`: Checks GitHub REST API for new releases and handles atomic batch script hot-swaps.
* **Testing:** xUnit test suite (`MCmodsLoader.Tests`) covering version parsing, zip metadata inspection, mod presets, and directory validation.

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
git clone https://github.com/SecretLUL/MCmodsLoader.git
cd MCmodsLoader

# Run automated tests
dotnet test

# Build debug binaries
dotnet build
```

### Publish the Native Executable
To produce the standalone portable `.exe` for distribution:
```powershell
# The AOT link step shells out to vswhere.exe to locate the MSVC linker,
# and vswhere is not on PATH by default.
$env:PATH = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer;$env:PATH"

dotnet publish src/MCmodsLoader.UI/MCmodsLoader.UI.csproj `
  -c Release `
  -r win-x64 `
  -o ./publish
```
`PublishAot` is set in the project file for any build with a runtime identifier, so no
extra flags are needed. The output `./publish/MCmodsLoader.exe` is around 6 MB, runs on
a machine with no .NET installed, and is the only file the release ships.

---

## 🤝 Contributing & Support

Issues and pull requests are welcome! If you find a bug or have a suggestion for the default modpack, feel free to open an issue on GitHub.

## 📄 License

This project is licensed under the [MIT License](LICENSE). Minecraft is a trademark of Mojang AB / Microsoft. MCmodsLoader is an independent open-source project and is not affiliated with Mojang or Microsoft.
