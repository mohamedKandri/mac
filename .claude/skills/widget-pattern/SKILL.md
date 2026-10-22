---
name: widget-pattern
description: The exact pattern for adding or changing a MacShell bar (or dock) widget - IWidget interface, WidgetRegistry entry with settings schema, config.json entry, UserControl XAML using Theme.* resources and Shell.* styles, line icons, Native wrapper with a Changed event, dropdowns via PopupWindow + PopupToggle, and how to verify it. Use this whenever the user asks for a new bar item or dock item (CPU, weather, media, bluetooth, keyboard layout, notifications...), wants a widget setting, a click action, a popup/dropdown, or a new icon, or when editing anything under Widgets/, even if they just say "add X to the bar".
---

# Adding a widget to MacShell

A widget = one UserControl (XAML + class) that implements `IWidget`, plus one registry line. The bar
builds widgets from `bar.left` / `bar.right` in config.json and rebuilds them only when that part of the config changes.

## Files you touch

| Step | File |
|---|---|
| 1. OS access (if any) | `Native/<Thing>.cs` wrapper with a `Changed` event (+ declarations in `Native/Win32.cs`) |
| 2. View | `Widgets/<Name>Widget.xaml` |
| 3. Logic | `Widgets/<Name>Widget.xaml.cs` |
| 4. Registration + settings schema | `Widgets/WidgetRegistry.cs` |
| 5. Default entry (optional) | `Core/AppConfig.cs` → `BarConfig.Left/Right` |
| 6. Icons (if new) | `Themes/Icons.xaml` |
| 7. Log the decision | `CLAUDE.md` → "Decisions made" |

## The rules, and why

- **No Windows API in widgets.** All P/Invoke and COM lives in `Native/`, commented, exposing plain C# (a `Read()` returning a record struct, a `Changed` event raised **on the UI thread**). Widgets never see raw messages or HRESULTs. This keeps widgets readable for the owner (new to C#/WPF) and lets the dock reuse the same wrappers.
- **Animations only while visible change happens.** Use `CompositionTarget.Rendering` (per-frame, like requestAnimationFrame) subscribed only while animating, and unsubscribe once settled (see the dock magnifier's `PauseLoop`/`StopLoop`), or a WPF `DoubleAnimation` with a fixed duration.
- **No polling.** Idle CPU must stay ≈0. Use OS notifications: hidden `MessageWindow` for broadcasts / power messages, `SetWinEventHook`, COM callbacks, `NetworkChange`. If you truly need a timer, schedule the next exact boundary (see `ClockWidget.Update`), never a fixed fast interval.
- **Colors and fonts only via theme resources**, so dark/light/auto and config colors apply live with no widget code: `{DynamicResource Theme.Foreground}` in XAML, `SetResourceReference(prop, ThemeKeys.X)` in code. Never hard-code a color (exception: `White` on `Theme.Selection` backgrounds).
- **Dispose everything.** Config reloads that touch the widget layout dispose and rebuild widgets; leaked hooks or timers pile up all day.
- **Catch nothing that hides bugs, but never crash the bar.** `BarWindow.Fill` already catches constructor exceptions and toasts "Widget 'x' failed to start". Inside event handlers, handle expected failures (no device, access denied) as *states*.

## Template

`Native/Cpu.cs` (only if you need OS data):
```csharp
namespace MacShell.Native;

// What it reads and from which API, in one or two lines. How changes are detected (no polling).
internal sealed class Cpu : IDisposable
{
    public readonly record struct State(bool Available, int Percent);
    public event Action? Changed;          // raised on the UI thread
    public static State Read() { /* Win32.Xxx(...) with a comment per call */ }
    public void Dispose() { /* unhook / unregister */ }
}
```
Background-thread callbacks (COM, NetworkChange, threadpool) must hop to the UI thread:
`dispatcher.InvokeAsync(() => Changed?.Invoke())`, debounced with a `DispatcherTimer` if they arrive in bursts (see `Native/Wifi.cs`).

`Widgets/CpuWidget.xaml`:
```xml
<!-- One line on what it shows and what clicking does. -->
<UserControl x:Class="MacShell.Widgets.CpuWidget"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:t="clr-namespace:MacShell.Themes">
    <Button x:Name="Button" Style="{StaticResource Shell.BarButton}" Click="OnClick">
        <StackPanel Orientation="Horizontal">
            <t:LineIcon x:Name="Icon" Data="{StaticResource Icon.Cpu}" />
            <TextBlock x:Name="Value" VerticalAlignment="Center" Margin="6,0,0,0" />
        </StackPanel>
    </Button>
</UserControl>
```
Use a plain `TextBlock` root instead of a `Button` if the item isn't clickable (like `ActiveAppWidget`).

`Widgets/CpuWidget.xaml.cs`:
```csharp
// Settings: "showPercentage" (default true).
public partial class CpuWidget : UserControl, IWidget
{
    private readonly Cpu _cpu = new();
    private readonly bool _showPercentage;

    public FrameworkElement View => this;

    public CpuWidget(WidgetEntry entry)
    {
        InitializeComponent();
        _showPercentage = entry.GetBool("showPercentage", true);
        _cpu.Changed += Show;
        Show();
    }

    private void Show()
    {
        var state = Cpu.Read();
        Visibility = state.Available ? Visibility.Visible : Visibility.Collapsed;   // hide when not applicable
        Value.Text = $"{state.Percent}%";
    }

    private void OnClick(object sender, RoutedEventArgs e) => Shell.Open("ms-settings:...");

    public void Dispose() => _cpu.Dispose();
}
```

`Widgets/WidgetRegistry.cs`, one line in `Types`:
```csharp
["cpu"] = new((ctx, e) => new CpuWidget(e), new() { ["showPercentage"] = Bool }, Validate: null),
```
- The dictionary key is the config `type` (camelCase).
- The settings schema lists every allowed extra key and its JSON kind (`Bool` = true/false, `Text` = string). Unknown keys and wrong kinds are reported at load time ("unknown setting 'fromat' (allowed: format)"), and the last valid config is kept.
- `Validate` (optional) does deeper checks, returning an error string or null. See `ClockWidget.Validate`.
- Take `ctx` (`WidgetContext`: `Host` window, `Theme`, `Notify(title, text)`, `Commands` = MacShell's Reload / Quit / Start with Windows) only if the widget needs popups, toasts or app commands.

config.json entry the owner adds (or put a default in `BarConfig` with `.With("key", value)`):
```json
{ "type": "cpu", "enabled": true, "showPercentage": false }
```

## Dropdowns (popups)

1. `Widgets/<Name>Popup.xaml`: root `<w:PopupWindow x:Class=... xmlns:w="clr-namespace:MacShell.Widgets">`, content inside `<Border Background="{DynamicResource Theme.PanelFill}" Padding="6..16" Width="...">`.
2. Code-behind: `public XPopup(WidgetContext context) : base(context.Theme) { InitializeComponent(); }`.
3. In the widget: `_popup = new PopupToggle(Button, context, () => new XPopup(context), alignRight: <true on the right side>);`, then `OnClick => _popup.Toggle()` and `Dispose => _popup.Close()`.

You get for free: frosted glass, Windows 11 rounded corners + shadow, 150 ms fade/slide-in of the content, close on click-away or Esc, toggle on a second click, the bar item highlighted while open (`Tag="open"`), and physical-pixel placement on the right monitor.
Popup-specific keys: handle `PreviewKeyDown` (see `CalendarPopup`). Actions that leave the desktop (lock, sleep) run after `SafeClose()` via `Dispatcher.InvokeAsync`.
For visual checks without a mouse, add a case to the `--open` switch in `App.xaml.cs` and a public `OpenX()` on the widget.

## Dock items

Dock tiles aren't `IWidget`s: they live in `Bar/DockWindow` + `Bar/DockAppTile`. Pinned tiles come from `dock.pinned`
via `Native/AppCatalog` (Start menu apps: name, parsing name, exe, icon, launch). Running state comes from
`Native/WindowTracker` (`Windows` front to back + `Changed`), matched with `DockAppTile.Owns` (app ID or exe).

Adding a new kind of dock tile (e.g. a folder stack or a "recent apps" section):
1. Add its element to `Bar/DockWindow.xaml` in the panel's `StackPanel` (before the Trash divider), at `dock.tileSize`
   with a right margin of `DockWindow.TileGap` (6 DIP between tiles, macOS proportions).
2. Give it an entry in `DockWindow.LoadMagnifier` (`Add(element, name, icon, ...)`) so it magnifies and gets a name
   label. Its real element is hidden while the magnified copy shows, so the copy must look the same
   (`DockMagnifier.Load` builds it).
3. After anything that changes the panel width, call `Reposition()` (same strip, new window size).
4. Icons are extracted at `magnifiedSize × DPI` (`_iconPixels`) and re-extracted on `OnDpiChanged`.
5. Clicks: `Shell.DockTile` button style; play feedback on the magnified copy through `_bounceIndex`.
Reuse these two Native classes for anything app- or window-related (e.g. a bar widget that lists windows) instead
of enumerating windows again. The Trash tile uses `Native/RecycleBin` (state via `SHQueryRecycleBin`, re-checked on dock mouse-enter and after a
drop, plus recycle). No shell change notifications: subscribing to the Recycle Bin made Explorer stop drawing the
desktop (see the windows-appbar skill).
Never modify the owner's Recycle Bin (or other personal data) while testing from the sandbox: verify read-only and
leave the rest to the owner.

Dock look (since 2026-10-22): no glass. `DockWindow` overrides `ShellWindow.Glass => false`, so the window is
see-through and draws a black `Shape` (a `Path` inside a `Canvas`, so it never drives the window size) with the notch's
16 px edge flares, rebuilt in `UpdateShape` on every size change. The window and `DockMagnifier` override
`Theme.Foreground`, `Hairline`, `Pressed` / `LabelFill` in their own `Resources` to stay white on black in both themes.
Tile x positions for the magnifier are in `Root` coordinates (the panel sits 16 px in, after the left flare).

## Notch rings

The black right-edge notch (`Bar/NotchWindow`, popup `Bar/NotchPopup`) isn't an `IWidget` either. A new ring = a
`NotchRing` subclass in `Bar/NotchRings.cs` (Label, IconKey, Color, `Update(tick)`, `Details()`), one line in
`NotchRing.Create`, and its key in `NotchConfig.KnownRings`. Numbers come from `Native/SystemStats` (no polling events
exist for CPU/memory, so the notch timer samples every `notch.refreshSeconds`). Rules:
- The notch and its popup are **layered windows**: every visual change redraws the whole window (~40 ms CPU in
  software rendering). Only touch a ring when its shown number changed; the popup skips identical details.
- Per-process data comes from one `NtQuerySystemInformation` snapshot (`ProcessSnapshot`), never
  `Process.GetProcesses()` + `TotalProcessorTime` (a handle per process), and only while a popup is open.
- Effects (shadows) go on a separate element with `CacheMode = BitmapCache`, so text refreshes don't re-blur.
- `--open notch` opens the first ring's popup for screenshots.
- The notch can be dragged up/down; its position (0..1 of the work area) lives in `notch-position.txt` next to the exe, not in config.json.

## Styles and icons

- Buttons: `Shell.BarButton` (bar items), `Shell.MenuItem` (menu rows), `Shell.IconButton` (28 px square), `Shell.PushButton` / `Shell.PrimaryButton`, `Shell.DockTile` (in `Themes/Controls.xaml`).
- Theme keys (`ThemeKeys` in `Themes/ThemeManager.cs`): Foreground, Muted, Hairline, Pressed, Accent, Selection, Warning, BarFill, PanelFill, DockFill, FontFamily, FontSize. A new color = property on `ThemeColors` (both defaults + `All()`), a key in `ThemeKeys`, and one line in `ThemeManager.Publish`.
- Icons: thin line icons on a **24×24 grid**, stroke 2, round caps, drawn by `t:LineIcon` (16×16 by default, stroke color = current text color). Add `<StreamGeometry x:Key="Icon.X">` to `Themes/Icons.xaml` using **absolute** WPF path syntax (convert SVG relative `a`/`v`/`h` commands), and an optional `FillData` geometry for solid parts. Swap icons in code with `Icon.Data = (Geometry)FindResource("Icon.X")`. A shape that doesn't fit the 24 grid (the battery) gets its own `Viewbox` + `Canvas` in the widget's XAML.
- Design source of truth: the MacShell Design canvas (link in CLAUDE.md), compacted to macOS proportions: 24 px bar, 20 px high bar buttons, 13 px Inter, 18 px between right-side items (the bar adds the gap), 6 px between icon and label. Keep new bar items within the 20 px button height.

## Verify (in this order)

1. `.\publish.ps1`: zero errors and warnings.
2. Icon paths parse: run `[Windows.Media.Geometry]::Parse(...)` on each new geometry (a bad path only fails at runtime when the XAML loads).
3. Launch `dist\MacShell.exe`, check `crash.log` is absent, screenshot the bar strip (`CopyFromScreen` works from the sandbox) and read it.
4. Config: break one of the widget's settings and confirm a readable error; remove the entry and confirm the widget disappears.
5. RAM: private bytes stay under 80 MB (`Process.PrivateMemorySize64`, several launches; readings vary ±13 MB). Idle CPU over 20 s ≈ 0 ms.
6. Clicks, scrolling and keyboard can't be driven from the sandbox: list them for the owner to test.
