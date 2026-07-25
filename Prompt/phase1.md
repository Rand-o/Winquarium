Phase 1 — Multi-monitor windows + exit watcher + frame clock

Restated facts: App-only phase. One window per monitor, shared exit, exit on any input on any monitor.

Build (all in App):

    DisplayManager.RunAllScreens(settings): for each Screen.AllScreens, create a borderless TopMost ScreensaverForm with Bounds = screen.Bounds, ShowInTaskbar=false, cursor hidden. Run all forms under one ApplicationContext.
    Seed each form baseSeed + index when IndependentScenesPerMonitor, else same seed (seed lives in Core; pass an int).
    ExitWatcher (shared, single instance): records start cursor pos; fires Quit on mouse move > 8px, any MouseDown/click, or any KeyDown. Every form forwards input to it; also poll Cursor.Position each frame. Quit closes all forms → Application.Exit().
    FrameClock (Core, neutral): Stopwatch-based, caps at TargetFps, yields clamped deltaSeconds (≤0.05). Form ticks: clock → (later) scene.Update → render → Invalidate.
    --windowed mode: single normal resizable form, exit-watcher disabled.

Fedora tests: FrameClock delta clamping (huge elapsed → ≤0.05); FPS-cap interval math; 8px threshold logic (a pure function ShouldExit(start, current, moved, key) in Core so it's testable).

