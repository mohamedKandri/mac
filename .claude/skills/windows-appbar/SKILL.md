---
name: windows-appbar
description: How MacShell reserves screen space with the Windows AppBar API (SHAppBarMessage), handles DPI, multi-monitor, fullscreen hiding, sleep/wake and Explorer restarts, plus the gotchas already hit. Use this whenever touching Native/AppBar.cs, Native/Monitors.cs, Bar/BarManager.cs or Bar/BarWindow.xaml.cs, when building the dock (milestone 2) or any other window that docks to a screen edge, when the bar is in the wrong place / covered by maximized windows / leaves a gap after quitting, or when testing anything that involves Explorer, the work area or monitors, even if "AppBar" isn't mentioned.
---

# Windows AppBar in MacShell

The bar is an *AppBar*: the same mechanism the taskbar uses. Windows shrinks the monitor's *work area*,
so maximized windows stop below the bar. Everything lives in three files:

| File | Role |
|---|---|
| `Native/AppBar.cs` | Registers one window as an AppBar on one monitor and edge (`AppBarEdge.Top/Bottom`); the only code calling `SHAppBarMessage` |
| `Native/Monitors.cs` | Monitor list (by device name), DPI, `IsFullscreenAppOn` |
| `Bar/ShellWindow.cs` | Base class of every edge-docked window (bar, dock): chrome, glass, AppBar, fullscreen hide, release |
| `Bar/BarManager.cs` | One `BarWindow` per monitor + the `DockWindow` on the primary; hot-plug sync, fullscreen hiding |

All Win32 declarations are in `Native/Win32.cs`. Nothing outside `Native/` calls Windows APIs.

## The registration flow (physical pixels throughout)

```
ABM_NEW        once per window, with our callback message id ("MacShell.AppBarNotify")
ABM_QUERYPOS   ask for a full-width strip on the edge; the shell may move it inward (e.g. away from a visible taskbar)
               -> force our thickness again: top edge rc.Bottom = rc.Top + h, bottom edge rc.Top = rc.Bottom - h
ABM_SETPOS     claim it; the work area shrinks
SetWindowPos   HWND_TOPMOST, SWP_NOACTIVATE, to the granted strip, or to StripPlacement(strip, monitor, scale)
ABM_REMOVE     on exit / crash / monitor gone
```
The reservation is always full width, but the window can be smaller: the dock passes a `StripPlacement` that returns
a centered panel 14 DIP above the strip's bottom. The strip is only gap + panel; magnified tiles draw above it in a
click-through overlay (owner's choice: less lost space beats never overlapping windows). `AppBar.Reposition()`
re-places the window without changing the strip (e.g. the dock gained a tile).

`height = round(configHeightDip * monitorDpi / 96)` (32 → 40 px at 125%, 48 px at 150%).
The app is Per-Monitor-V2 DPI aware via `app.manifest`; without it the math above is wrong.

## When to re-dock (`Position()`)

| Event | Why |
|---|---|
| `ABN_POSCHANGED` (our callback) | another AppBar or the taskbar moved |
| `WM_DISPLAYCHANGE`, `WM_DPICHANGED` | resolution / layout / scale changed; **deferred** with `Dispatcher.InvokeAsync(..., Background)` so WPF handles WM_DPICHANGED first |
| `PBT_APMRESUMEAUTOMATIC` | displays may be re-detected during sleep |
| `TaskbarCreated` (registered message) | Explorer restarted and forgot all AppBars: set `_registered = false` and `Dock()` again. Anything else registered with the shell must re-register too (the tray icon does) |
| config `bar.height` changed | `BarWindow.Apply` calls `Dock(newHeight)` |

Also forward `WM_ACTIVATE` → `ABM_ACTIVATE` and `WM_WINDOWPOSCHANGED` → `ABM_WINDOWPOSCHANGED` (part of the AppBar contract).

## Monitors

- The bar follows `bar.monitors` (primary | all). The dock is always on the primary only, and BarManager recreates it
  when the primary changes.

- Identify monitors by **device name** (`"\\.\DISPLAY1"`, from `MONITORINFOEX.szDevice`). HMONITOR handles change on every layout change.
- `AppBar.Position()` looks the monitor up each time with `Monitors.Find(name)`; if it's gone it does nothing. `BarManager.Sync()` closes that bar on the same (debounced) display change.
- `BarManager` debounces `WM_DISPLAYCHANGE` by 500 ms (plugging in sends several), then: removed monitors → `ReleaseAppBar()` + `Close()`; new monitors → new `BarWindow`. `monitors: primary` keys the single bar by the primary's device name, so a primary-display change swaps bars.
- Popups are positioned in physical pixels (`WindowStyles.MoveTo`) using the anchor monitor's DPI, never with WPF `Left/Top`: those use the DPI of whichever monitor the window was created on.

## Window styles (no focus steal, no Alt+Tab)

`WindowStyles.MakeToolWindow(hwnd, noActivate: true)` = `WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE`, set in
`SourceInitialized` (before the window is visible), plus `ShowActivated=False`, `ShowInTaskbar=False`, `Topmost=True`.
Verified exstyle `0x8000088`. Popups use `noActivate: false`: they must activate so `Deactivated` can close them.
A side effect worth knowing: clicking the bar never deactivates an open popup, which is why `PopupToggle` can toggle.

## Fullscreen hiding

Two signals, combined in `BarManager`:
- Explorer's `ABN_FULLSCREENAPP` (lParam ≠ 0 = opened). **Opened** → per bar, `Monitors.IsFullscreenAppOn(device)`; **closed** → show all bars directly (the window may still be shrinking, so re-checking would keep the bar hidden).
- Every foreground change (`ForegroundWatcher`) → re-check all bars.

`IsFullscreenAppOn`: the foreground window's rect covers its whole monitor, it's on *this* monitor, it isn't a shell surface
(`Progman`, `WorkerW`, `MultitaskingViewFrame`, `XamlExplorerHostIslandWindow`, `ForegroundStaging`) and `AppInfo.GetAppName` doesn't ignore it.
Maximized windows never match, because the AppBar keeps them below the bar. Hidden bars keep their reserved space.

## The Windows taskbar (Native/Taskbar)

With `dock.hideTaskbar`, MacShell hides the taskbar: original state saved to `dist\taskbar-state.txt` (written only
if absent, so it survives a hard kill), `ABM_SETSTATE(ABS_AUTOHIDE)` so it reserves nothing, `ShowWindow(SW_HIDE)` on
`Shell_TrayWnd` + `Shell_SecondaryTrayWnd`, a show-event hook on Explorer's process to re-hide, and re-hide on
`TaskbarCreated`. `Restore()` runs on Quit, in the crash handler, and whenever the state file is found while hiding
is off. Anything that can stop MacShell must leave a path to `Restore()`: use the `Local\MacShell.Quit` named event
(what `publish.ps1` does) instead of killing the process.

## Failure handling

- `ABM_NEW` can fail (MacShell starting before Explorer at sign-in). The bar is still placed on top, `IsRegistered` is false, and `TaskbarCreated` retries. App warns only if a bar is still undocked after **15 s**.
- Crash: `App.OnCrash` → `BarManager.ReleaseAll()` (ABM_REMOVE is thread-safe: it's just a message to Explorer), then tray icon removal, then `crash.log`. A hard kill (Task Manager) can't run code.
- `ABM_REMOVE` is idempotent (`_registered` guard), so Dispose + crash + Close can all call it.

## Gotchas already hit (don't rediscover them)

-1. **Shell change notifications broke the owner's desktop.** The Trash's `SHChangeNotifyRegister` on the Recycle
   Bin (all events, recursive, interrupt level) made the desktop go black (Explorer stopped drawing) twice; removing
   it fixed it (confirmed with MacShell closed, then with a build without it). Poll-free alternatives: check on user
   interaction (the dock re-checks the bin on mouse enter). Don't add `SHChangeNotifyRegister` back without a long
   soak test on the owner's desktop.
0. **Testing from the sandbox can break the owner's desktop.** A sandbox-launched MacShell can't reserve space, so
   windows slide under the bar. Kill it before ending the turn and ask the owner to start `dist\MacShell.exe`
   themselves. Never start `explorer.exe` (or anything shell-like) from the sandbox: it broke the owner's desktop
   once (black desktop and taskbar; restarting Explorer didn't help, signing out and back in did).
   **What Claude can't see:** the sandbox *and* the app's Terminal panel both miss Explorer, the taskbar and an
   owner-launched MacShell (process list and FindWindow), and sandbox screenshots never show the taskbar (black
   strip). Don't diagnose desktop state from those: ask the owner, or have them run `tools\diagnose.ps1` in their own
   Windows Terminal.

1. **Claude's shell is sandboxed**: `explorer.exe` and `Shell_TrayWnd` are invisible from it, so `SHAppBarMessage` returns 0 for everything (a minimal WinForms AppBar fails the same way). Work-area reservation, tray icon and toasts can only be verified by the owner on the real desktop with `tools\check-appbar.ps1`. Window rects, styles, screenshots (`CopyFromScreen`), RAM and CPU *can* be checked from the sandbox.
2. PowerShell turns `$null` into `""` for `FindWindowW(string, string)`: pass `[NullString]::Value`. Add-Type P/Invokes default to ANSI; use `CharSet = CharSet.Unicode` with `W` functions.
3. `taskkill` without `/f` posts WM_CLOSE to *every* top-level window, including WPF's internal hidden ones, and leaves the process half shut down. Don't treat WM_CLOSE as "quit"; quit from the tray, and use a hard kill in scripts.
4. Hidden helper windows must be normal hidden windows (`HwndSourceParameters { WindowStyle = 0 }`), not message-only (`HWND_MESSAGE`): message-only windows miss broadcasts such as `TaskbarCreated`, `WM_SETTINGCHANGE` and `WM_DISPLAYCHANGE`. Reuse `Native/MessageWindow`.
5. WPF ignores posted `WM_LBUTTON*` (it reads the real mouse state). To check a popup from the sandbox, use `MacShell.exe --open power-menu` / `--open calendar`.
6. The sandbox also hides some folders completely (e.g. `AppData\Local\Mozilla Firefox`), even from the MacShell it launches. A missing icon or "file not found" seen only in the sandbox may be fine on the owner's desktop; check with `Test-Path` before changing code.
7. Right after MacShell exits, the exe stays locked for a few seconds (likely antivirus). `publish.ps1` publishes to `obj\publish-stage` and retries the copy.

## Checklist for a new edge-docked window

1. Derive from `Bar/ShellWindow` (XAML root `<b:ShellWindow x:Class=...>`) and implement `Edge`, `ReservedThickness` (DIP),
   `RoundedCorners`, `TintColor` (a palette color), and `PlaceInStrip` if the window is smaller than the strip.
   ShellWindow does `MakeToolWindow`, `Backdrop.Prepare/Apply` and the `AppBar` in `SourceInitialized`.
2. Call `Redock()` when the reserved thickness changes, `Reposition()` when only the window size changes.
3. Glass limits: the blur covers the whole window rect and Windows only rounds corners at ~8 px. Anything that must
   draw outside the glass shape needs its own transparent window, like `Bar/DockMagnifier`: `AllowsTransparency=True`
   (layered) + `WindowStyles.MakeClickThroughOverlay`, placed with `WindowStyles.PlaceOnTop`. Close it when it's not
   in use: a hidden layered window keeps its pixel buffer (~12 MB for the dock overlay). Don't hand-roll a "faster"
   overlay (normal window + DWM alpha + manual `WS_EX_LAYERED|WS_EX_TRANSPARENT`): tried 2026-10-22, it wasn't
   click-through on the real desktop and made the dock hover loop.
4. Own it from `BarManager` (see how the dock is opened/closed in `Sync`) so hot-plug, crash release and fullscreen
   hiding come for free.
5. Verify: owner runs `tools\check-appbar.ps1` (work area), quit restores the area, the fullscreen test, and
   screenshots for visuals (`CopyFromScreen` with an X/Y offset).
