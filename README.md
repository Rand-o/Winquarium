# AquariumSaver

A Windows screensaver (`.scr`) rendering an animated underwater aquarium with sprite-based fish, rising bubbles, and corner reefs. Renders independently on every monitor at mixed resolutions and DPI, with smooth 30–120 Hz animation.

## Features

- **7 fish species** — yellow butterflyfish, stingray, blue triggerfish, blue tang, moorish idol, orange butterflyfish, clown triggerfish — each with 30–40 hand-crafted cel-style animation frames at 30 FPS for smooth swimming
- **Double-buffered rendering** — front/back buffer swap ensures a failed draw is never presented; last successful frame remains visible
- **Static background caching** — water gradient and reef corners are rendered once per client size and reused every frame
- **Depth-sorted rendering** — fish at different depths with parallax-like speed and opacity variation
- **Variable swim angles** — each fish follows a unique diagonal path with angles up to ±15° for natural upward and downward movement
- **4 bubble streams** — burst-based rising from left and right reef corners with per-bubble sway, growth, and opacity fade
- **Multi-monitor support** — each monitor shows a viewport into one shared virtual-desktop aquarium; simulation time is derived from a single process-wide stopwatch so all monitors display the same moment
- **Auto refresh-rate detection** — detects display refresh rate per-monitor via `EnumDisplaySettings`, with user-overridable `TargetFps` setting (Auto / 30 / 50 / 60 / 100 / 120)
- **Fixed-timestep simulation** — `SharedAquarium.Advance()` steps simulation forward using absolute wall-clock time; `Draw()` renders at the interpolated position
- **Graceful error handling** — render failures are logged and the last good frame persists; after 300 consecutive failures rendering is suspended. All unhandled exceptions are caught and logged to `%LOCALAPPDATA%\AquariumSaver\AquariumSaver.log`
- **Exit on input** — mouse movement > 80 px or any key press quits the screensaver (3-second startup grace period to ignore cursor settling)
- **Settings dialog** — fish count, bubble density, speed multiplier, background colors, target FPS, battery-saver pause, with live preview panel
- **Self-contained publish** — single-file `.scr` with no .NET runtime dependency

## Architecture

```
AquariumSaver/
├── AquariumSaver.csproj          # .NET 8 WinForms, net8.0-windows
├── Program.cs                    # Entry point: /s run, /p preview, /c configure, --windowed debug
├── Scene.cs                      # SpriteAtlas (PNG loader), SharedAquarium (simulation + rendering), Scene (viewport)
├── Screensaver.cs                # ScreensaverForm (full-screen), PreviewForm (control panel), ConfigForm (settings), ExitWatcher, AppLog
├── Settings.cs                   # SettingsData (POCO), Settings (registry read/write)
├── Native.cs                     # Win32 P/Invoke: SetParent, IsWindow, GetClientRect, Get/SetWindowLongPtr
├── build.ps1                     # Windows build script
├── build.sh                      # Linux cross-compile script
├── AquariumSpriteGenerator/      # Sprite generation tool (separate project, excluded from main build)
│   ├── AquariumSpriteGenerator.csproj
│   ├── SpriteGenerator.cs
│   └── sprites.png               # source sprite sheet
└── Sprites/                      # 200+ PNG assets (7 fish species, 5 bubble sizes, 2 reefs)
    ├── manifest.json             # Species metadata, frame counts, speeds, scales
    ├── README.md                 # Sprite generation notes
    ├── Fish/                     # 7 species × 30–40 frames each + preview.png
    ├── Bubbles/                  # 5 bubble sprite sizes (12–52 px)
    └── Reef/                     # reef-left.png, reef-right.png
```

### Rendering pipeline (per frame)

1. **Timer Tick** (UI thread) — computes elapsed time, calls `Scene.Update(delta)`
2. **`SharedAquarium.Advance()`** — reads absolute time from the process-wide `Stopwatch`, stores `_prevSimTime` / `_currSimTime`
3. **`Scene.Draw()`** — renders into the hidden back buffer bitmap:
   - Opaque black safety clear via `SourceCopy` compositing
   - Cached static background (water gradient + corner reefs)
   - Foreground layer: rear fish, front fish, bubble streams
4. **Buffer swap** — `_frontIsA` is toggled only after the complete frame succeeds
5. **`Invalidate()`** — posts `WM_PAINT`
6. **`OnPaint`** (UI thread) — blits the completed front buffer to screen via `SourceCopy`

Because all steps run on the UI thread, the back buffer is never accessed concurrently — no tearing, no black flashes. A failed draw never reaches the screen.

### Double-buffered publication

`ScreensaverForm` maintains two `Bitmap`/`Graphics` pairs (A and B). One is always the visible front buffer, the other is the hidden render target. After `Scene.Draw()` completes successfully, `_frontIsA` is toggled to publish the new frame. If rendering throws, the swap is skipped and the previous frame remains visible. After 300 consecutive failures, the render timer is stopped and the failure is logged.

### Multi-monitor design

All full-screen `ScreensaverForm` instances share one `SharedAquarium`. Each form holds a `Scene` that maps virtual-desktop coordinates to its own monitor's client area. The clip rectangle is set in device coordinates *before* the translate transform, so off-screen content is correctly culled on secondary monitors (including those left of or above the primary display).

### Sprite generation

The `AquariumSpriteGenerator` sub-project processes `sprites.png` into individual frame PNGs using a retro cel-deformation technique:

- Complete rear-body deformation instead of detached tail rotation
- Continuous (unquantized) poses for smooth swimming animation
- Perspective-compressed side-fin strokes
- Traveling dorsal/anal edge waves on triggerfish
- Whole-silhouette stingray wing stroke and delayed tip curl
- Transparent padding keeps all animated poses aligned

Source resolution: 1339×800, playback at 30 FPS.

## Building

### Windows (PowerShell)

```powershell
.\build.ps1
```

Produces `publish/AquariumSaver.scr` — a self-contained single-file executable.

### Linux (cross-compile)

```bash
./build.sh
```

Requires the .NET 8 SDK with Windows targeting packs installed.

### Manual

```bash
dotnet publish AquariumSaver.csproj -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:PublishReadyToRun=true -o publish
```

Rename `publish/AquariumSaver.exe` → `publish/AquariumSaver.scr`.

## Usage

```
AquariumSaver.scr /s           # Full-screen on all monitors
AquariumSaver.scr /p:<hwnd>    # Preview inside control panel
AquariumSaver.scr /c           # Settings dialog
AquariumSaver.scr --windowed   # Debug window (resizable, no exit watcher)
```

Register as a system screensaver by copying `AquariumSaver.scr` to `%WINDIR%\System32\` or selecting it in Windows Settings → Personalization → Lock screen → Screen saver.

## Settings

Stored in `HKCU\Software\AquariumSaver`:

| Setting                       | Default    | Range              |
|-------------------------------|------------|--------------------|
| `FishCount`                   | 12         | 1–60               |
| `BubbleDensity`               | 50         | 0–200              |
| `SpeedMultiplier`             | 1.0        | 0.25–3.0           |
| `ShowBackgroundChest`         | false      | bool               |
| `IndependentScenesPerMonitor` | true       | bool               |
| `PauseOnBattery`              | false      | bool               |
| `BackgroundTopColor`          | #FF001845  | #RRGGBB / #AARRGGBB|
| `BackgroundBottomColor`       | #FF000208  | #RRGGBB / #AARRGGBB|
| `TargetFps`                   | 0 (Auto)   | 0, 30, 50, 60, 100, 120 |

## Error logging

All exceptions and render failures are logged to `%LOCALAPPDATA%\AquariumSaver\AquariumSaver.log`. Screensavers must fail silently — no UI dialogs are shown on error.

## License

Apache 2.0. See [LICENSE](LICENSE).
