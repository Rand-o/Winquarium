Phase 5 — Settings persistence + config dialog

Restated facts: registry behind ISettingsStore; settings list in Anchor A4.

Build:

    App RegistrySettingsStore : ISettingsStore (HKCU\Software\AquariumSaver), try/catch → defaults.
    App ConfigForm: controls bound to all settings (numeric/sliders, checkboxes, two color pickers, 30/60 selector). OK → Save; Cancel → discard. AutoScaleMode=Dpi on this form only. Optional small live-preview panel reusing the lightweight scene.
    Wire per-monitor seeding to the IndependentScenesPerMonitor setting.

Fedora tests: settings serialize/deserialize through the in-memory store; out-of-range values clamp to Anchor A4 ranges; defaults when keys absent.

Laptop check: /c dialog binds + persists across relaunch (registry); changing FishCount/colors/speed visibly changes the running scene; the two monitors differ when independent seeding is on.
