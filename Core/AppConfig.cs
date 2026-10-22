using System.Text.Json;
using System.Text.Json.Serialization;

namespace MacShell.Core;

// The shape of config.json. Every property has a default, so a config file only needs the keys you want to change.
// These classes are like Kotlin data classes / TS interfaces with default values;
// `init` means "settable only while the object is created" (by the JSON reader), then read-only.
//
// [JsonUnmappedMemberHandling(Disallow)] turns a typo like "heigth" into an error instead of silently ignoring it.

public enum ThemeMode { Auto, Dark, Light }

public enum MonitorMode { Primary, All }

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class AppConfig
{
    public ThemeMode Theme { get; init; } = ThemeMode.Auto;
    public FontConfig Font { get; init; } = new();
    public BarConfig Bar { get; init; } = new();
    public DockConfig Dock { get; init; } = new();
    public NotchConfig Notch { get; init; } = new();
    public ColorsConfig Colors { get; init; } = new();
}

// The black notch on the right edge of the primary monitor: system rings with a details popup on hover.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class NotchConfig
{
    public static readonly string[] KnownRings = ["cpu", "memory", "disk", "battery"];

    public bool Enabled { get; init; } = true;

    // Top to bottom. Any of KnownRings, each at most once.
    public List<string> Rings { get; init; } = ["cpu", "memory", "disk", "battery"];

    // How often the rings (and an open popup) refresh, 1 to 60. Each redraw of the layered notch window costs a
    // few ms of CPU, so this is the notch's idle cost.
    public int RefreshSeconds { get; init; } = 3;
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class DockConfig
{
    public bool Enabled { get; init; } = true;

    // Hide the Windows taskbar while MacShell runs (the dock replaces it); restored on Quit.
    // While hidden, other apps' tray icons are out of reach; MacShell's own menu is in the logo menu.
    public bool HideTaskbar { get; init; } = true;

    // Slide away when not in use and come back when the mouse touches the bottom edge (macOS "Automatically hide
    // and show the Dock"). Maximized windows then get the full height. false = always visible, space reserved.
    public bool AutoHide { get; init; } = false;

    public double TileSize { get; init; } = 40;         // icon tiles, WPF units (macOS-like; the canvas used 52)
    public double MagnifiedSize { get; init; } = 54;    // the tile under the mouse while hovering

    // In order. Each is the app's name as shown in Start > All apps ("Microsoft Edge"), its app ID, or a full
    // path to an .exe / .lnk (environment variables like %LOCALAPPDATA% allowed).
    public List<string> Pinned { get; init; } = ["File Explorer", "Microsoft Edge", "Settings"];
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class FontConfig
{
    // WPF tries each family in order until one is installed.
    public string Family { get; init; } = "Inter, Segoe UI Variable Text, Segoe UI";
    public double Size { get; init; } = 13;
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class BarConfig
{
    public double Height { get; init; } = 24;   // macOS menu bar height (the first canvas design used 32)
    public MonitorMode Monitors { get; init; } = MonitorMode.Primary;

    // Widgets in display order. Remove an entry or set "enabled": false to hide it.
    public List<WidgetEntry> Left { get; init; } = [new("logo"), new("activeApp")];
    // Same order as the design canvas.
    public List<WidgetEntry> Right { get; init; } =
    [
        new WidgetEntry("volume").With("showPercentage", false),
        new WidgetEntry("wifi").With("showName", true),
        new WidgetEntry("battery").With("showPercentage", true),
        new WidgetEntry("clock").With("format", "ddd d MMM  HH:mm"),
    ];
}

public sealed class WidgetEntry
{
    public WidgetEntry() { }
    public WidgetEntry(string type) => Type = type;

    public string Type { get; init; } = "";
    public bool Enabled { get; init; } = true;

    // Every other key in the entry ("format", "showName"...) lands here; each widget reads its own settings.
    [JsonExtensionData]
    public Dictionary<string, JsonElement> Settings { get; init; } = new();

    // Typed reads with a fallback. WidgetRegistry has already checked the types at load time.
    public string GetString(string key, string fallback) =>
        Settings.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : fallback;

    public bool GetBool(string key, bool fallback) =>
        Settings.TryGetValue(key, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : fallback;

    // For building the defaults above.
    internal WidgetEntry With(string key, object value)
    {
        Settings[key] = JsonSerializer.SerializeToElement(value);
        return this;
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class ColorsConfig
{
    // Populate = keys you give are merged INTO these defaults, instead of replacing the whole palette.
    // So "dark": { "accent": "#FF8800" } changes only the accent.
    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public ThemeColors Dark { get; } = ThemeColors.DefaultDark();

    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public ThemeColors Light { get; } = ThemeColors.DefaultLight();
}

// Colors use WPF's hex format: #RRGGBB, or #AARRGGBB with alpha FIRST (CSS puts it last).
// Example: CSS rgba(20,22,30,0.55) = #8C14161E (0.55 * 255 = 140 = 0x8C).
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class ThemeColors
{
    public string Background { get; set; } = "";  // the bar itself (frosted, so semi-transparent)
    public string Panel { get; set; } = "";       // popups and menus
    public string Foreground { get; set; } = "";
    public string Muted { get; set; } = "";       // secondary text
    public string Hairline { get; set; } = "";    // borders and separators
    public string Pressed { get; set; } = "";     // hovered / pressed item background
    public string Accent { get; set; } = "";
    public string Selection { get; set; } = "";   // highlighted menu item, today in the calendar
    public string Warning { get; set; } = "";     // low battery
    public string Dock { get; set; } = "";        // the dock's solid shape (black by default, like the notch)

    public static ThemeColors DefaultDark() => new()
    {
        Background = "#8C14161E", Panel = "#B81C1E28", Foreground = "#F4F5F7", Muted = "#A9AEBB",
        Hairline = "#24FFFFFF", Pressed = "#24FFFFFF", Accent = "#5B9DFF", Selection = "#2F6FD6",
        Warning = "#FF453A", Dock = "#FF000000",
    };

    public static ThemeColors DefaultLight() => new()
    {
        Background = "#8CFFFFFF", Panel = "#C7FFFFFF", Foreground = "#14161B", Muted = "#555B69",
        Hairline = "#1F000000", Pressed = "#14000000", Accent = "#5B9DFF", Selection = "#2F6FD6",
        Warning = "#D70015", Dock = "#FF000000",
    };

    public IEnumerable<(string Name, string Value)> All() =>
    [
        ("background", Background), ("panel", Panel), ("foreground", Foreground), ("muted", Muted),
        ("hairline", Hairline), ("pressed", Pressed), ("accent", Accent), ("selection", Selection),
        ("warning", Warning), ("dock", Dock),
    ];
}
