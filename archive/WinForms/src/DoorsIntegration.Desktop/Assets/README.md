# DOORS Integration app icon

Modern navy rounded-square icon with an interlocking cobalt/cyan doorway mark, matching the desktop application's dark blue theme.

- [doors-integration.png](doors-integration.png): generated 1254 × 1254 RGBA master; transparent outside the tile.
- [doors-integration.ico](doors-integration.ico): Windows icon with 16, 20, 24, 32, 40, 48, 64, 128 and 256 pixel frames, 32-bit color with alpha and AND masks.

The ICO is compiled into the desktop EXE using ApplicationIcon and embedded as `DoorsIntegration.Desktop.AppIcon.ico` for the main window/taskbar. Runtime deployment requires no external icon file. The window owns and disposes its cloned icon. Settings and DOORS behavior are unchanged.

To rebuild the ICO after deliberately replacing the PNG, run `scripts/Convert-AppIcon.ps1` from the solution root in a PowerShell environment where local scripts are permitted. It uses the installed .NET System.Drawing implementation, preserves alpha while resizing and requires no network or Python dependency. A normal solution build consumes the committed ICO directly and does not regenerate artwork.

## Generation record

Created using the built-in **image_gen** tool, through the imagegen skill; no API-key/CLI fallback was used. The selected output was copied into this project. The original generation remains in the generation cache; the application does not reference that cache.

Exact generation prompt:

```text
Use case: logo-brand. Asset type: one finished Windows desktop application icon, square 1024x1024 master PNG for a DOORS requirements integration tool. Design a refined, modern, extremely legible blue/cyan app icon: a midnight-navy rounded-square tile with a bold luminous cobalt-to-cyan abstract open doorway / portal mark, made from two clean interlocking geometric strokes suggesting connected requirements. Large simple silhouette, excellent contrast and unmistakable at 16 and 32 pixels. Front-facing, optically centered, icon tile occupies about 90% of the square canvas, generous internal breathing room. The doorway has a narrow intentional negative-space opening; subtle color depth, crisp polished edges. Transparent only outside the rounded-square tile. Flat premium desktop software identity, restrained gradient, no photorealism, no extra symbols, no tiny dots, no details that disappear at small size. No text, no letters, no IBM logo, no captions, no mockup, no surrounding objects, no drop shadow outside tile. Deliver only the single icon artwork.
```

The generation request specified 1024 pixels; the returned master is 1254 pixels. The committed file preserves the returned image without cropping. ICO frames are downsampled from that master.
