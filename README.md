# AquariumSaver

A Windows screensaver (`.scr`) rendering an animated underwater aquarium with sprite-based fish, rising bubbles, and corner reefs. Renders independently on every monitor at mixed resolutions and DPI, with smooth 60–120 Hz animation.

## Features

- **7 fish species** — yellow butterflyfish, stingray, blue triggerfish, blue tang, moorish idol, orange butterflyfish, clown triggerfish — each with hand-crafted cel-style animation frames
- **Depth-sorted rendering** — fish at different depths with parallax-like speed and opacity variation
- **4 bubble streams** — rising from left and right reef corners with per-bubble sway and opacity
- **Multi-monitor support** — each monitor shows a viewport into one shared virtual-desktop aquarium; simulation time is derived from a single process-wide stopwatch so all monitors display the same moment
- **120 Hz display support** — detects display refresh rate via `EnumDisplaySettings`, respects the user's `TargetFps` setting (30 / 60 / 120)
- **Smooth fixed-timestep simulation** — `SharedAquarium.Advance()` steps simulation forward; `Draw()` renders at the interpolated position. Timer Tick and `OnPaint` run on the same UI thread so the back buffer is never read while being written
- **Settings dialog** — fish count, bubble density, speed multiplier, background colors, target FPS, battery-saver pause
- **Exit on input** — mouse movement > 8 px or any key press quits the screensaver
- **Self-contained publish** — single-file `.scr` with no .NET runtime dependency

## Architecture

```
AquariumSaver/
├── AquariumSaver.csproj    # .NET 8 WinForms, net8.0-windows
├── Program.cs              # Entry point: /s run, /p preview, /c configure, --windowed debug
├── Scene.cs                # SpriteAtlas (PNG loader), SharedAquarium (simulation + rendering), Scene (viewport)
├── Screensaver.cs          # ScreensaverForm (full-screen), PreviewForm (control panel), ConfigForm (settings), ExitWatcher
├── Settings.cs             # SettingsData (POCO), Settings (registry read/write)
├── Native.cs               # Win32 P/Invoke: SetParent, IsWindow, GetClientRect, Get/SetWindowLongPtr
├── build.ps1               # Windows build script
├── build.sh                # Linux cross-compile script
└── Sprites/                # 82 PNG assets (7 fish species, 5 bubble sizes, 2 reefs)
    ├── manifest.json       # Species metadata, frame counts, speeds, scales
    ├── Fish/               # 7 species × 8–12 frames each + preview.png
    ├── Bubbles/            # 5 bubble sprite sizes (12–52 px)
    └── Reef/               # reef-left.png, reef-right.png
```

### Rendering pipeline (per frame)

1. **Timer Tick** (UI thread) — computes elapsed time, calls `Scene.Update(delta)`
2. **`SharedAquarium.Advance()`** — reads absolute time from the process-wide `Stopwatch`, stores `_prevSimTime` / `_currSimTime`
3. **`Scene.Draw()`** — renders to an off-screen `Bitmap` back buffer:
   - Water gradient background
   - Rear fish layer (depth < 0.42, 70% opacity)
   - Left and right corner reefs
   - Front fish layer (depth ≥ 0.42)
   - Bubble streams (in front of everything)
4. **`Invalidate()`** — posts `WM_PAINT`
5. **`OnPaint`** (UI thread) — blits the completed back buffer to screen

Because steps 1–4 and 5 all run on the UI thread, the back buffer is never accessed concurrently — no tearing, no black flashes.

### Multi-monitor design

All full-screen `ScreensaverForm` instances share one `SharedAquarium`. Each form holds a `Scene` that maps virtual-desktop coordinates to its own monitor's client area. The clip rectangle is set in device coordinates *before* the translate transform, so off-screen content is correctly culled on secondary monitors (including those left of or above the primary display).

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

| Setting                       | Default    | Range        |
|-------------------------------|------------|--------------|
| `FishCount`                   | 12         | 1–60         |
| `BubbleDensity`               | 50         | 0–200        |
| `SpeedMultiplier`             | 1.0        | 0.25–3.0     |
| `ShowBackgroundChest`         | false      | bool         |
| `IndependentScenesPerMonitor` | true       | bool         |
| `PauseOnBattery`              | false      | bool         |
| `BackgroundTopColor`          | #FF001845  | #AARRGGBB    |
| `BackgroundBottomColor`       | #FF000208  | #AARRGGBB    |
| `TargetFps`                   | 60         | 30, 60, 120  |

## Error logging

Unhandled exceptions are logged to `%LOCALAPPDATA%\AquariumSaver\error.log` (screensavers must fail silently).

## License

Apache 2.0. See [LICENSE](LICENSE).
