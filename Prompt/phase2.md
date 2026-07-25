Phase 2 — Renderer + background + decor

Restated facts: Draw order (Anchor A5). Core stays neutral; only GdiRenderer touches System.Drawing.

Build:

    App GdiRenderer : IRenderer: off-screen Bitmap back buffer sized to the form's device-pixel client size (ClientSize * DeviceDpi/96), SmoothingMode.AntiAlias, HighQualityBilinear; one blit in End; OnPaintBackground no-op to kill flicker. Implement FillGradientRect, DrawSprite(dest, opacity, flipX), DrawSpriteRotated.
    Core Background: vertical gradient top→bottom color; ShowLightShafts → 3–5 low-alpha (8–20) white angled quads, each oscillating x-offset + alpha on independent sine phases.
    Core Decor: sand band along bottom ~12% with a sine-summed undulating top edge; a few static rocks from the seed; optional chest sprite (procedural).
    Core Scene: owns layers, Update(delta) + Draw(IRenderer).

Fedora tests: gradient color interpolation at y=0/mid/bottom; light-shaft oscillation stays within bounds and is deterministic per seed; sand edge function continuous.

Laptop check: flicker-free seascape on every monitor; verify it fills edge-to-edge with no clipping or blur on the mixed-DPI pair (internal panel + external monitor).
