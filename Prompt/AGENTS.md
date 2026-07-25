[analyze-mode]
ANALYSIS MODE. Gather context before diving deep:
CONTEXT GATHERING (parallel):
- 1-2 explore agents (codebase patterns, implementations)
- 1-2 librarian agents (if external library involved)
- Direct tools: Grep, AST-grep, LSP for targeted searches

IF COMPLEX - DO NOT STRUGGLE ALONE. Consult specialists:
- **Oracle**: Conventional problems (architecture, debugging, complex logic)
- **Artistry**: Non-conventional problems (different approach needed)

SYNTHESIZE findings before proceeding.
---
MANDATORY delegate_task params: ALWAYS include load_skills=[] and run_in_background when calling delegate_task.
Example: delegate_task(subagent_type="explore", prompt="...", run_in_background=true, load_skills=[])

---

# AquariumSaver Specification

## A1. What we're building

A Windows 11 screensaver (`.scr`) recreating the classic Win95 underwater aquarium (blue gradient sea, light shafts, swaying seaweed, rising bubbles, fish with depth). It must render **independently on every monitor** (mixed resolution + mixed DPI), and exit on any input.

## A2. Hard constraints (never violate)

*   **Two-assembly split** (this is what lets us test on Fedora):
    *   `AquariumSaver.Core` → targets `net8.0` (plain). **No WinForms, no** `System.Drawing`. All simulation + parsing + settings logic lives here. Runs and unit-tests natively on Fedora.
    *   `AquariumSaver.App` → targets `net8.0-windows`, `WinExe`, WinForms + GDI+. Windowing, rendering, dialogs. Builds on Fedora, but **only runs on Windows**.
    *   `AquariumSaver.Tests` → targets `net8.0`, xUnit, references **Core only**. Runs on Fedora.

*   Core uses **platform-neutral types only**: `Vec2(float X,Y)`, `RgbaColor(byte R,G,B,A)`, `RectF`. The renderer/forms translate these to `System.Drawing` types. Never leak `Bitmap`/`Color`/`Rectangle` into Core.

*   Registry access is hidden behind `ISettingsStore` (Core defines the interface + an in-memory impl for tests; App provides the registry impl).

*   Rendering goes through `IRenderer` (defined in Core using neutral types). Core never calls a draw API directly.

*   Self-contained single-file publish so the `.scr` needs no .NET install on the laptop.

*   Do **not** use Microsoft's original aquarium artwork. Sprites are **procedurally generated** in code (optional PNG override later).

## A3. Screensaver command-line contract

Parse `args[0]` case-insensitively; strip leading `/` or `-`, take first char, lowercase.

| Args | Mode | Action |
| :--- | :--- | :--- |
| `/s` or none | Run | Full-screen scene on every monitor |
| `/p <hwnd>` or `/p:<hwnd>` | Preview | Child window inside `<hwnd>`, lightweight scene |
| `/c` or `/c:<hwnd>` | Configure | Modal settings dialog |

HWND may be in `args[0]` after a colon **or** in `args[1]` — try both. Unknown/empty → Configure.

## A4. Settings (registry `HKCU\Software\AquariumSaver`, defaults if absent)

*   `FishCount`=12 (1–60)
*   `BubbleDensity`=50 (0–200)
*   `SpeedMultiplier`=1.0 (0.25–3.0)
*   `ShowSeaweed`=true
*   `ShowLightShafts`=true
*   `ShowBackgroundChest`=true
*   `IndependentScenesPerMonitor`=true
*   `BackgroundTopColor`=#FF1E6F9F
*   `BackgroundBottomColor`=#FF06243A
*   `TargetFps`=60 (30/60)
*   `PauseOnBattery`=false

## A5. Update/draw order (back→front)

1.  Background gradient
2.  Light shafts
3.  Far decor (rocks/chest)
4.  Seaweed
5.  Fish (depth-sorted)
6.  Bubbles
7.  Optional foreground tint

## A6. Conventions

*   .NET 8, C# 12, nullable enabled, implicit usings.
*   Win32 P/Invoke isolated in `App/Native.cs` via `[LibraryImport]`.
*   `Stopwatch` for timing, never `DateTime.Now`. Clamp frame delta to ≤ 0.05s.
*   Object-pool bubbles (no per-frame allocations in the sim loop).
*   Top-level try/catch logs to `%LOCALAPPDATA%\AquariumSaver\error.log` (screensavers fail silently).
*   A `--windowed` debug flag runs the scene in a normal resizable window with the exit-watcher disabled (for easy testing).

## A7. Build / publish commands (run on Fedora)

```bash
# Fast logic tests (Fedora-native)
dotnet test

# Produce the Windows screensaver
dotnet publish AquariumSaver.App -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:PublishReadyToRun=true

# Then rename the produced AquariumSaver.App.exe -> AquariumSaver.scr

Provide build.ps1 (Windows) and build.sh (Fedora) that do publish + rename.

## A8. Definition of done per phase

dotnet test green on Fedora · code committed · the phase card's "Laptop check" performed when it has one.