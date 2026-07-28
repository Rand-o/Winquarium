# Bubble System Redesign

**Date:** 2026-07-27
**Status:** Implemented

## Goal

Replace the fixed 4-stream burst-based bubble system with a dynamic, user-configurable bubble emitter system. Users can add/remove bubble sources (1–6), and configure each source's X/Y position, float speed, and bubble size range.

## Current System (what we're replacing)

- 4 hardcoded `BubbleStream` instances at fixed corner positions
- Burst-based emission: bubbles appear in groups, float up, then pause
- Per-stream config: X (0–1 normalized), Y (0–1 normalized), Speed (0.1–3.0), Enabled
- Time-based rendering — no per-bubble state between frames
- Settings stored as fixed array of 4 under registry key `BubbleStreams`

## Data Model

### BubbleEmitterConfig (settings layer, in `Settings.cs`)

Replaces `BubbleStreamConfig`. Per-emitter persisted configuration:

| Property   | Type  | Range        | Default | Description                        |
|------------|-------|--------------|---------|------------------------------------|
| X          | float | 0–100        | 50      | Horizontal position (% from left)  |
| Y          | float | 0–100        | 10      | Vertical position (% from top)     |
| Speed      | float | 0.1–3.0      | 1.0     | Float speed multiplier             |
| SizeMin    | float | 5–80         | 15      | Minimum bubble diameter (px at 1080p) |
| SizeMax    | float | 5–80         | 30      | Maximum bubble diameter (px at 1080p) |
| Enabled    | bool  | —            | true    | Whether this emitter is active     |

Constraints: `SizeMin <= SizeMax` (enforced in Clamp).

### SettingsData changes

- Replace `BubbleStreamConfig[] BubbleStreams` with `BubbleEmitterConfig[] BubbleEmitters`
- Replace `BubbleCount` global setting (remove — no longer needed, emission is per-emitter)
- Default: 1 emitter at X=50, Y=10, Speed=1.0, SizeMin=15, SizeMax=30, Enabled=true
- Min 1, max 6 emitters enforced at settings level and UI level

### Registry storage

- Bump `SettingsData.CurrentVersion` from 4 to 5 (old settings discarded on upgrade)
- Store under `BubbleEmitters` subkey with a `Count` value, then indexed subkeys `0..N-1`
- Each subkey stores: X, Y, Speed, SizeMin, SizeMax, Enabled

## Runtime Model

### BubbleEmitter (in `Scene.cs`)

Replaces `BubbleStream`. Holds runtime state for one emitter:

- Position (X%, Y%), speed, size range, enabled state
- `nextEmissionTime` — when the next bubble should be spawned (simulated seconds)
- `activeBubbles` — list of `Bubble` particles currently alive (max 8 per emitter)

### Bubble (particle, in `Scene.cs`)

Individual bubble particle with:

- `Diameter` — random size chosen from [SizeMin, SizeMax] at spawn time
- `FloatProgress` — 0.0 (just spawned) to 1.0 (exited top of screen)
- `SwayPhase` — random phase offset for horizontal sway
- `SizeAt1080p` — the base diameter before viewport scaling

### Emission logic

- On construction, `nextEmissionTime` is set to current time + random(0, 1.0) so emitters don't sync
- During `Advance(dt)`, each emitter checks: if `currentTime >= nextEmissionTime`, spawn a bubble and set `nextEmissionTime = currentTime + random(0.3, 1.5)`
- Spawned bubble gets: random size in [SizeMin, SizeMax], random sway phase, floatProgress = 0
- Disabled emitters skip emission and don't render

### Bubble lifecycle

- **Float duration:** Base ~8 seconds at speed 1.0. Scaled inversely: `duration = 8.0 / speed`. So speed 2.0 = 4 seconds, speed 0.5 = 16 seconds.
- **Vertical movement:** Linear rise from emitter Y position to top of screen. `floatProgress` advances by `dt / duration` each frame.
- **Horizontal sway:** `sin(time * 1.75 + swayPhase) * referenceHeight * 0.008` (same amplitude as current system)
- **Growth:** Bubble diameter scales from 0.85× at spawn to 1.2× at exit: `diameter * (0.85 + floatProgress * 0.35)`
- **Opacity:** Fade in first 10% (`progress / 0.1 * 0.6`), full 0.6 middle 70%, fade out last 20% (`(1.0 - progress) / 0.2 * 0.6`)
- **Removal:** Bubble is removed when `floatProgress > 1.0`
- **Cap:** Max 8 active bubbles per emitter — if cap reached, skip spawning new bubble

### Rendering

- Same sprite selection: `SpriteAtlas.GetBubbleForDiameter()` picks closest pre-rendered PNG
- Same drawing: translate to position, scale to diameter, draw
- Viewport scaling: `diameter * viewportScale` where viewportScale is clamped 0.72–1.75× based on 1080p reference
- X/Y conversion: emitter X% maps to `world.Left + (X / 100) * world.Width`; emitter Y% maps to `world.Top + (Y / 100) * world.Height` (Y from top)
- X clamped to keep bubble fully on screen (account for radius + sway)

## Settings UI (ConfigForm)

### Layout changes

- Replace the 4 hardcoded `BubbleStreamRow` controls with a dynamic list
- "Add Stream" button — enabled when count < 6, appends a new emitter row with defaults
- Each row has a "Remove" button — disabled when count == 1
- Row label updates to "Stream 1", "Stream 2", etc. (re-indexed on remove)

### Per-row controls

Each emitter row shows:
- Checkbox (enabled/disabled) + "Stream N" label
- X: NumericUpDown (0–100, increment 1)
- Y: NumericUpDown (0–100, increment 1)
- Speed: NumericUpDown (0.1–3.0, increment 0.1)
- Size Min: NumericUpDown (5–80, increment 1)
- Size Max: NumericUpDown (5–80, increment 1)

### Behavior

- **Add:** Append new row with defaults (X=50, Y=10, Speed=1.0, SizeMin=15, SizeMax=30, Enabled=true). Re-layout form.
- **Remove:** Remove the clicked row. Re-index all remaining row labels. Re-layout form.
- **Size validation:** If SizeMin > SizeMax after user edit, swap them so SizeMin holds the smaller value. This preserves the user's intent (they picked two sizes) better than clamping, which silently discards one value.
- **Preview:** Updates on any change (same `OnChanged` pattern as today).
- **Save/Load:** On OK, build `BubbleEmitterConfig[]` from rows and save. On load, populate rows from settings.

### Migration

- Version bump to 5 means old v4 settings are discarded — user gets fresh defaults (1 emitter)
- No migration logic needed

## Files Changed

| File        | Changes |
|-------------|---------|
| `Settings.cs` | New `BubbleEmitterConfig`, remove `BubbleStreamConfig`. Update `SettingsData` to use `BubbleEmitterConfig[]`. Bump version to 5. Update registry Load/Save. |
| `Scene.cs` | Replace `BubbleStream` with `BubbleEmitter` + `Bubble`. Rewrite `DrawBubbles()` to use particle-based rendering. Update `SharedAquarium` constructor. |
| `Screensaver.cs` | Update `ConfigForm` to use dynamic emitter rows with Add/Remove. Update `BubbleStreamRow` → `BubbleEmitterRow`. Update `PreviewForm` settings passthrough. |

## Error Handling

- Invalid size ranges (SizeMin > SizeMax) clamped in `Clamp()`
- Empty emitter array: if somehow 0 emitters reach runtime, use 1 default emitter
- Registry corruption: fall back to defaults (existing pattern)

## Testing

- Verify 1–6 emitters can be added/removed in UI
- Verify X/Y 0–100 maps correctly to screen positions
- Verify speed 0.1 (very slow) through 3.0 (very fast) works
- Verify SizeMin/SizeMax produces visible size variation
- Verify disabled emitters produce no bubbles
- Verify multi-monitor shared aquarium still works (emitters advance in `SharedAquarium.Advance()`)
- Verify preview updates correctly when emitters are added/removed
