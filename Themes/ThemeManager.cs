using System.Windows;
using System.Windows.Media;
using MacShell.Core;
using MacShell.Native;

namespace MacShell.Themes;

// Resource keys XAML uses, e.g. Foreground="{DynamicResource Theme.Foreground}".
// DynamicResource = "look it up now and again whenever it changes", like reading a React context or a
// Compose CompositionLocal, so a theme switch repaints every widget without touching their code.
public static class ThemeKeys
{
    public const string Foreground = "Theme.Foreground";
    public const string Muted = "Theme.Muted";
    public const string Hairline = "Theme.Hairline";
    public const string Pressed = "Theme.Pressed";
    public const string Accent = "Theme.Accent";
    public const string Selection = "Theme.Selection";
    public const string Warning = "Theme.Warning";
    public const string BarFill = "Theme.BarFill";      // window fill for the bar (transparent when the blur supplies it)
    public const string PanelFill = "Theme.PanelFill";  // same for popups and menus
    public const string DockFill = "Theme.DockFill";    // same for the dock panel
    public const string LabelFill = "Theme.LabelFill";  // app name above a dock tile: panel color, no blur behind it
    public const string FontFamily = "Theme.FontFamily";
    public const string FontSize = "Theme.FontSize";
}

// Decides dark vs light (config "theme", or Windows' mode for "auto") and publishes palette + font as app resources.
public sealed class ThemeManager : IDisposable
{
    // Base for "./Themes/Fonts/#Inter": the fonts compiled into the exe (see <Resource> in MacShell.csproj).
    private static readonly Uri EmbeddedFonts = new("pack://application:,,,/");

    private readonly SystemTheme _system = new();
    private AppConfig _config;

    public bool IsDark { get; private set; }
    public bool Blur { get; private set; }               // false when Windows' "Transparency effects" is off
    public ThemeColors Palette { get; private set; } = ThemeColors.DefaultDark();

    // Fired after resources are updated; windows use it to re-apply their native blur and frame colors.
    public event Action? Changed;

    public ThemeManager(AppConfig config)
    {
        _config = config;
        _system.Changed += Publish;   // Windows mode or transparency switched
        Publish();
    }

    public void Apply(AppConfig config)
    {
        _config = config;
        Publish();
    }

    private void Publish()
    {
        IsDark = _config.Theme switch
        {
            ThemeMode.Dark => true,
            ThemeMode.Light => false,
            _ => !SystemTheme.AppsUseLightTheme,
        };
        Blur = SystemTheme.TransparencyEnabled;
        Palette = IsDark ? _config.Colors.Dark : _config.Colors.Light;

        var resources = Application.Current.Resources;
        resources[ThemeKeys.Foreground] = Brush(Palette.Foreground);
        resources[ThemeKeys.Muted] = Brush(Palette.Muted);
        resources[ThemeKeys.Hairline] = Brush(Palette.Hairline);
        resources[ThemeKeys.Pressed] = Brush(Palette.Pressed);
        resources[ThemeKeys.Accent] = Brush(Palette.Accent);
        resources[ThemeKeys.Selection] = Brush(Palette.Selection);
        resources[ThemeKeys.Warning] = Brush(Palette.Warning);

        // With blur on, the tint is drawn by DWM behind the window, so WPF paints nothing.
        // With blur off there's nothing behind a semi-transparent color but black, so use it fully opaque.
        resources[ThemeKeys.BarFill] = Blur ? Brushes.Transparent : Brush(Palette.Background, opaque: true);
        resources[ThemeKeys.PanelFill] = Blur ? Brushes.Transparent : Brush(Palette.Panel, opaque: true);
        // The dock draws its own solid shape (no glass behind it), so its color is used as given.
        resources[ThemeKeys.DockFill] = Brush(Palette.Dock);
        resources[ThemeKeys.LabelFill] = Brush(Palette.Panel);

        resources[ThemeKeys.FontFamily] = BuildFont(_config.Font.Family);
        resources[ThemeKeys.FontSize] = _config.Font.Size;

        Changed?.Invoke();
    }

    public static Color ParseColor(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    private static SolidColorBrush Brush(string hex, bool opaque = false)
    {
        var color = ParseColor(hex);
        if (opaque) color.A = 255;
        var brush = new SolidColorBrush(color);
        brush.Freeze();   // immutable brushes are cheaper: WPF doesn't have to watch them for changes
        return brush;
    }

    // "Inter, Segoe UI Variable Text" -> the embedded Inter first, then installed fonts as fallbacks.
    private static FontFamily BuildFont(string families)
    {
        var parts = families.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(name => name.Equals("Inter", StringComparison.OrdinalIgnoreCase) ? "./Themes/Fonts/#Inter" : name);
        return new FontFamily(EmbeddedFonts, string.Join(", ", parts));
    }

    public void Dispose() => _system.Dispose();
}
