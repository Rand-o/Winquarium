Phase 6 — Preview pane, hot-plug, battery, packaging

Restated facts: /p gives a parent HWND; preview must not orphan; self-contained single-file publish.

Build:

    App PreviewForm: SetParent(Handle, parentHwnd), style WS_CHILD|WS_VISIBLE, size to parent GetClientRect; lightweight scene (2–3 fish, few bubbles, no shafts) at 30 FPS; poll IsWindow(parentHwnd) and exit when false. No exit-watcher in preview. (P/Invoke in Native.cs.)
    Hot-plug: handle WM_DISPLAYCHANGE → tear down forms and re-run DisplayManager.RunAllScreens.
    Battery: if PauseOnBattery and on-battery+discharging (SystemInformation.PowerStatus), throttle to ~10 FPS.
    Packaging: build.sh (Fedora publish + rename to .scr) and build.ps1; short README.md (build, install via right-click→Install or copy to System32, settings reference).

Fedora tests: battery-throttle decision function; preview scene builds with reduced entity counts.

Laptop check (final acceptance): preview animates inside the real Screen Saver settings page and the .scr process exits when that page closes (no orphan in Task Manager); physically plug/unplug the external monitor → windows rebuild; unplug power → throttle engages; let it trigger via the idle timer, then wake → clean exit, cursor restored; install flow works end-to-end.
