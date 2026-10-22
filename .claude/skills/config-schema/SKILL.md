---
name: config-schema
description: The MacShell config.json structure (theme, font, bar, widgets, colors), every key and default, how loading, validation, hot-reload and "keep the last valid config" work, and how to add a new config key safely. Use this whenever the user wants to change how MacShell looks or behaves through config.json, asks what a setting does or why a config edit "doesn't work" / shows an error toast, or when changing Core/AppConfig.cs, Core/ConfigLoader.cs, Core/ConfigService.cs or adding settings for a widget or the dock, even if they just say "make the bar taller" or "change the colors".
---

# MacShell config.json

`config.json` sits next to `MacShell.exe` (`dist\config.json`). The first run writes a full default file.
Saving it applies within about a second; a broken save never crashes the app.

| File | Role |
|---|---|
| `Core/AppConfig.cs` | The shape: classes with a default for every property (single source of truth) |
| `Core/ConfigLoader.cs` | Text → `AppConfig` or a one-line error. `Parse(json, checkWidget)` never throws |
| `Core/ConfigService.cs` | Load, create defaults, watch the file, debounce, keep `Current`, raise `Changed` / `Failed` |
| `Widgets/WidgetRegistry.cs` | `Check(entry)`: widget types and per-widget settings schema, passed into `Parse` |
| `App.xaml.cs` | Wiring: `Failed` → warning toast, `Changed` → `ThemeManager.Apply` + `BarManager.Apply` |

## Current defaults (generated from AppConfig, so always in sync)

```json
{
  "theme": "auto",                       // auto | dark | light   (auto follows Windows' app mode, live)
  "font": { "family": "Inter, Segoe UI Variable Text, Segoe UI", "size": 13 },   // size 9..24
  "bar": {
    "height": 24,                        // 20..64, WPF units (x1.25 at 125% scale); 24 = macOS
    "monitors": "primary",               // primary | all
    "left":  [ { "type": "logo", "enabled": true }, { "type": "activeApp", "enabled": true } ],
    "right": [
      { "type": "volume",  "enabled": true, "showPercentage": false },
      { "type": "wifi",    "enabled": true, "showName": true },
      { "type": "battery", "enabled": true, "showPercentage": true },
      { "type": "clock",   "enabled": true, "format": "ddd d MMM  HH:mm" }
    ]
  },
  "dock": {
    "enabled": true, "tileSize": 40, "magnifiedSize": 54,             // tileSize 32..96, magnifiedSize tileSize..128
    "hideTaskbar": true,                                              // hide the Windows taskbar while MacShell runs
    "autoHide": false,                                                // true = slide away; mouse at the bottom edge brings it back
    "numberShortcuts": true,                                          // Win+1…9 open/switch to the Nth dock app
    "pinned": ["File Explorer", "Microsoft Edge", "Settings"]         // Start > All apps names, app IDs, or .exe/.lnk paths
  },
  "island": { "enabled": true },                                       // black pill in the menu bar: time, opens on hover/click
  "notch": {
    "enabled": true,
    "rings": ["cpu", "memory", "disk", "battery"],                     // top to bottom; each known ring at most once
    "refreshSeconds": 3                                               // 1..60; each refresh that changes a number redraws the notch
  },
  "colors": {
    "dark":  { "background": "#8C14161E", "panel": "#B81C1E28", "foreground": "#F4F5F7", "muted": "#A9AEBB",
               "hairline": "#24FFFFFF", "pressed": "#24FFFFFF", "accent": "#5B9DFF", "selection": "#2F6FD6", "warning": "#FF453A", "dock": "#FF000000" },
    "light": { "background": "#8CFFFFFF", "panel": "#C7FFFFFF", "foreground": "#14161B", "muted": "#555B69",
               "hairline": "#1F000000", "pressed": "#14000000", "accent": "#5B9DFF", "selection": "#2F6FD6", "warning": "#D70015", "dock": "#FF000000" }
  }
}
```
(The real file has no comments, but `//` comments and trailing commas are allowed.) To regenerate the exact
current defaults, delete `dist\config.json` and start MacShell, or call `ConfigLoader.DefaultJson()`.

Things that surprise people:
- **Partial files are fine.** Every missing key falls back to its default; `{}` is a valid config.
- **Colors are WPF hex with alpha first**: `#RRGGBB` or `#AARRGGBB`. CSS `rgba(20,22,30,0.55)` → `#8C14161E` (0.55×255 = 140 = 0x8C). `background` / `panel` are the tint over the blur; with Windows "Transparency effects" off they're drawn opaque.
- **`colors.dark` / `colors.light` merge** into the default palette (`JsonObjectCreationHandling.Populate`), so `"dark": { "accent": "#FF8800" }` changes only the accent. Lists (`left`, `right`) **replace** the default list entirely.
- **Widget order** = list order; hide one with `"enabled": false` or by deleting the entry. Extra keys in an entry are that widget's settings.
- `font.family`: the name `Inter` maps to the copy embedded in the exe; other names must be installed fonts. WPF tries them left to right.
- `dock.pinned` entries are matched against Start > All apps by name (case-insensitive), then by app ID; anything containing `\` is a file path. Two apps can share a name (classic and new "Outlook"): the first wins, so pin the other by its app ID (`Microsoft.OutlookForWindows_8wekyb3d8bbwe!Microsoft.OutlookforWindows`). List the IDs with PowerShell: `(New-Object -ComObject Shell.Application).NameSpace('shell:AppsFolder').Items() | select Name, Path`. Unknown pins don't invalidate the config: they're skipped with a toast.
- **MacShell writes `dock.pinned` itself** when the owner uses Pin to dock / Unpin from dock (`Core/ConfigEditor`): the file is rewritten indented and `//` comments are lost. Nothing else is ever written back.
- `colors.*.dock` is the dock's **solid** shape color (black by default, like the notch), not a glass tint: the dock has no blur behind it, so keep alpha at FF unless you want it see-through.
- `notch.rings`: unknown names or duplicates are config errors. The battery ring hides itself on a PC without a battery. `refreshSeconds` is the notch's whole idle CPU cost (a redraw of the layered window is ~40 ms of CPU in software rendering), so don't lower the default casually.
- Clock `format` is a .NET date format, using the Windows locale (`ddd` = Wed, `dddd` = Wednesday, `d` = 21, `MMM` = Oct, `HH:mm` = 24 h, `h:mm tt` = 12 h, `ss` = seconds and makes the clock tick every second).

## Load, validate, hot-reload

```
start:   file missing? write DefaultJson()  ->  Reload(force)  ->  start FileSystemWatcher
save:    Changed / Created / Renamed (editors save differently)  ->  UI thread  ->  300 ms debounce
         -> read (FileShare.ReadWrite|Delete, retry 5 x 50 ms while the editor holds it)
         -> identical text? skip (one save fires several events)
         -> Parse:  JSON  ->  structural checks  ->  WidgetRegistry.Check per entry
         -> valid:   Current = config; Changed(config)   (theme + bars re-apply; widgets rebuild only if bar.left/right changed)
            invalid: Current untouched; Failed(error) -> tray warning "config.json has an error … Still using the last working config."
manual:  tray "Reload config" = Reload(force: true): also reports a missing/locked file, toasts a summary on success
```
A deleted or locked file during hot-reload is ignored silently (the editor may be mid-save).

Error messages are one line, so they fit a toast:
- JSON syntax / type: `Line 4 (bar.height): The JSON value could not be converted to System.Double.`
- Typo'd key: `Line 1 (bar.heigth): The JSON property 'heigth' could not be mapped …` (unknown keys are errors via `[JsonUnmappedMemberHandling(Disallow)]`, except inside widget entries)
- Ranges / colors: `bar.height must be 20 to 64 (got 200).`, `colors.light.accent: 'blue-ish' is not a color. Use #RRGGBB or #AARRGGBB.`
- Widgets: `bar.right[0] (clock): unknown setting 'fromat' (allowed: format).`, `… unknown widget type 'weather'. Known: …`
- `null` for a section is rejected ("Remove the key instead to use its default").

## Adding a config key

1. Add the property with its default to the right class in `Core/AppConfig.cs` (`{ get; init; } = default`). Nested classes need `[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]`. Enums are written camelCase automatically.
2. Add range/shape checks to `ConfigLoader.Validate` if JSON types alone can't express them. Return a one-line message naming the key path.
3. A new color: property on `ThemeColors` + both `DefaultDark()` / `DefaultLight()` + the `All()` list (that's what validates it) + `ThemeKeys` + `ThemeManager.Publish`.
4. A widget setting: **don't** touch AppConfig. Add it to the widget's schema in `WidgetRegistry` and read it with `entry.GetBool/GetString` (see the widget-pattern skill). Put it in the default entry with `.With("key", value)` if the owner should discover it.
5. Consume it in the `Changed` path (`ThemeManager.Apply`, `BarManager.Apply` → `BarWindow.Apply`). Keep reloads cheap: compare against the previous config and only redo what changed (see `BarWindow.Apply` / `BuildWidgets`).
6. Existing config files don't get new keys automatically; that's fine because defaults apply. Mention the new key to the owner.
7. Test the parser alone: a throwaway console project (`net8.0-windows`, `UseWPF`) that compiles `Core/AppConfig.cs` + `Core/ConfigLoader.cs` and calls `Parse` with a stub `checkWidget`. Cover valid, partial, typo, wrong type, out of range, `null`. Earlier this caught `"dark": null` throwing `InvalidOperationException` instead of `JsonException`, which is why `Parse` catches every exception.
8. Log the decision in CLAUDE.md.
