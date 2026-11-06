# DellFans icon

Original artwork for DellFans (open-source Windows fan-control utility). It is not affiliated with
Dell Inc. and borrows nothing from Dell's logo, wordmark or colors.

## Design

A pinwheel fan rotor: swept, paddle-shaped blades with rounded tips around a round hub, on a
rounded-square badge. Blades are generated procedurally (`IconBuilder.cs`): each is a curved
centerline with a width that grows toward the tip, a slightly asymmetric camber, and a half-circle
tip. Paddle blades with moderate sweep (not thin hooks) keep the shape reading as a fan at 16 px.

| File | Use | Notes |
| --- | --- | --- |
| `dellfans.svg` | master, 256 viewBox | 7 blades, gradient badge, housing ring, hub with accent dot. Sizes 40-256 in the ICO. |
| `dellfans-small.svg` | 16/20/24/32 px, 32 viewBox | Hand-tuned: 5 broader blades, bigger hub, flat color, no ring, no gradient. Not a downscale of the master. |
| `tray-light.svg` | light taskbar | No badge, glyph `#1F1F1F`. 5 fat blades, hub with 1-px hole at 16 px. |
| `tray-dark.svg` | dark taskbar | Same glyph in `#FFFFFF`. |

## Colors

- Badge gradient (large sizes): `#3F8CFF` (top-left) to `#5546D9` (bottom-right)
- Badge flat (small sizes): `#3A6BE6`
- Glyph: `#FFFFFF`; housing ring: white at 30% opacity; hub dot `#5546D9`
- Tray glyph: `#1F1F1F` on light, `#FFFFFF` on dark

One saturated blue-violet badge with a white glyph is visible against white, black and purple-black
(`#1A1625`) surfaces, so there is a single app icon and no light/dark pair. It sits between the blue
(`#3B82F6`) and purple accents used by the UI themes.

## Outputs

- `dellfans.ico`: 16, 20, 24, 32, 40, 48 (32-bit BMP with alpha), 64, 128, 256 (PNG)
- `tray-light.ico`, `tray-dark.ico`: 16, 20, 24, 32, 40, 48 (32-bit BMP with alpha)
- `preview.png`: contact sheet on white, black, purple-black and light/dark taskbar strips

## Rebuild

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File assets\icon\build-icons.ps1
```

Compiles `IconBuilder.cs` with the Windows `csc.exe`, then writes the SVGs, ICOs and preview from one
geometry source (System.Drawing, anti-aliased). No network or third-party tools. The preview sheet
uses Segoe UI for its labels only; the icons contain no text.

## License

The icon is part of the DellFans project and is licensed MIT OR Apache-2.0, like the rest of it.
