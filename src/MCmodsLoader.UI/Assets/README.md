# Branding assets

- `logo.png`: original transparent logo generated with the built-in Imagegen tool.
- `app.ico`: the same logo converted to Windows ICO, with 16, 20, 24, 32, 40, 48,
  64, 96, 128 and 256 pixel frames. Embedded both as the executable icon and as a
  managed resource for the window, taskbar and DPI-aware program header.
- `readme-banner.png`: matching GitHub README banner, generated with the built-in
  Imagegen tool using `logo.png` as its reference.

No loose image files are required next to the published executable.

## Generation prompts

### Logo

Use case: logo-brand. Create one beautiful minimal app logo for MCmodsLoader, a small Minecraft performance mod installer. Asset: square Windows app icon and in-app header logo. A single bold geometric isometric cube with a simple lightning bolt cut into its front face, communicating blocks and speed. Flat vector-like design, crisp clean edges, very few large shapes, readable at 16px. Palette matches existing UI: emerald #10B981, mint #34D399, dark emerald #059669, optional near-white lightning bolt. Centered symbol filling 85% of square with even padding. Truly transparent background. No lettering, no text, no mockup, no shadow, no glow, no fine detail, no gradients. Output one 1024x1024 transparent PNG.

### Banner

Use case: logo-brand. Create a polished minimal wide GitHub README banner for MCmodsLoader. Reference image is the exact existing app logo: preserve its emerald cube and white lightning bolt identity. Landscape 3:1 aspect ratio, dark navy #0F172A background. Place the reference cube mark on the left, large but with generous padding. On the right clean highly legible white modern sans-serif title exactly 'MCmodsLoader', below it smaller muted light slate text exactly 'Minecraft. Faster. Simpler.' Understated emerald accents, lots of clean negative space, flat restrained developer-tool branding. No extra icons, no scenery, no Minecraft official logo, no extra text. Export one banner PNG.

## Rebuild the ICO

With Python and Pillow installed, run from the repository root:

```powershell
python -c "from PIL import Image; Image.open('src/MCmodsLoader.UI/Assets/logo.png').save('src/MCmodsLoader.UI/Assets/app.ico', sizes=[(s,s) for s in [16,20,24,32,40,48,64,96,128,256]])"
```
