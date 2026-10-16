# Branding assets

- `logo.png`: original transparent logo generated with the built-in Imagegen tool, back
  when the app was still called MCmodsLoader.
- `app.ico`: the same logo converted to Windows ICO, with 16, 20, 24, 32, 40, 48,
  64, 96, 128 and 256 pixel frames. Embedded both as the executable icon and as a
  managed resource for the window, taskbar and DPI-aware program header.
- `readme-banner.svg`: the GitHub README banner, written by hand as SVG. The cube is
  `logo.png` traced into four polygons (three faces and the bolt) with its exact colours,
  so it stays sharp at any size. The text uses the viewer's system font (Segoe UI on
  Windows), with "perf" in the UI's mint accent.

No loose image files are required next to the published executable.

## Logo generation prompt

Use case: logo-brand. Create one beautiful minimal app logo for MCmodsLoader, a small Minecraft performance mod installer. Asset: square Windows app icon and in-app header logo. A single bold geometric isometric cube with a simple lightning bolt cut into its front face, communicating blocks and speed. Flat vector-like design, crisp clean edges, very few large shapes, readable at 16px. Palette matches existing UI: emerald #10B981, mint #34D399, dark emerald #059669, optional near-white lightning bolt. Centered symbol filling 85% of square with even padding. Truly transparent background. No lettering, no text, no mockup, no shadow, no glow, no fine detail, no gradients. Output one 1024x1024 transparent PNG.

## Rebuild the ICO

With Python and Pillow installed, run from the repository root:

```powershell
python -c "from PIL import Image; Image.open('src/MCperfBooster.UI/Assets/logo.png').save('src/MCperfBooster.UI/Assets/app.ico', sizes=[(s,s) for s in [16,20,24,32,40,48,64,96,128,256]])"
```
