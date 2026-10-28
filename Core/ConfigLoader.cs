using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media;

namespace MacShell.Core;

// Turns config.json text into an AppConfig, or a short human-readable error. Never throws.
public static class ConfigLoader
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,     // C# "FontSize" <-> JSON "fontSize"
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,        // allow // comments in the file
        AllowTrailingCommas = true,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // write "+" and accents as-is, not +
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static string DefaultJson() => JsonSerializer.Serialize(new AppConfig(), Options);

    // `checkWidget` validates one widget entry (type exists, its own settings are fine) and returns an error or null.
    // It's passed in (WidgetRegistry.Check in the app) so this file doesn't depend on the widgets.
    public static (AppConfig? Config, string? Error) Parse(string json, Func<WidgetEntry, string?> checkWidget)
    {
        try
        {
            var config = JsonSerializer.Deserialize<AppConfig>(json, Options);
            if (config is null)
                return (null, "The file is empty or just 'null'.");

            string? error = Validate(config, checkWidget);
            return error is null ? (config, null) : (null, error);
        }
        catch (JsonException ex)
        {
            // ex.Message ends with "Path: ... | LineNumber: ..."; keep only the readable first part,
            // and show the key as "bar.height" instead of "$.bar.height".
            string message = ex.Message.Split(" Path:")[0];
            string where = ex.LineNumber is long line ? $"Line {line + 1}" : "Config";
            if (ex.Path is { Length: > 2 } path) where += $" ({path[2..]})";
            return (null, $"{where}: {message}");
        }
        catch (Exception ex)
        {
            // Other shapes the JSON reader rejects, e.g. "dark": null throws InvalidOperationException.
            // The app must never crash on a bad config, so anything else lands here too.
            return (null, ex.Message);
        }
    }

    // Checks values JSON itself can't express ("height must be 20 to 64"). Returns null when everything is fine.
    private static string? Validate(AppConfig c, Func<WidgetEntry, string?> checkWidget)
    {
        // `null` in the file ("font": null) bypasses the defaults, so reject it explicitly.
        if (c.Font is null || c.Bar is null || c.Dock is null || c.Colors is null || c.Bar.Left is null || c.Bar.Right is null || c.Dock.Pinned is null
            || c.Notch is null || c.Notch.Rings is null || c.Island is null)
            return "A section is set to null. Remove the key instead to use its default.";

        if (string.IsNullOrWhiteSpace(c.Font.Family)) return "font.family can't be empty.";
        if (c.Font.Size is < 9 or > 24) return $"font.size must be 9 to 24 (got {c.Font.Size}).";
        if (c.Bar.Height is < 20 or > 64) return $"bar.height must be 20 to 64 (got {c.Bar.Height}).";
        if (c.Dock.TileSize is < 32 or > 96) return $"dock.tileSize must be 32 to 96 (got {c.Dock.TileSize}).";
        if (c.Dock.MagnifiedSize < c.Dock.TileSize || c.Dock.MagnifiedSize > 128)
            return $"dock.magnifiedSize must be between tileSize ({c.Dock.TileSize}) and 128 (got {c.Dock.MagnifiedSize}).";
        if (c.Dock.Pinned.Any(string.IsNullOrWhiteSpace)) return "dock.pinned contains an empty entry.";
        if (!DockConfig.KnownStyles.Contains(c.Dock.Style))
            return $"dock.style must be one of: {string.Join(", ", DockConfig.KnownStyles)} (got '{c.Dock.Style}').";
        foreach (var ring in c.Notch.Rings)
        {
            if (!NotchConfig.KnownRings.Contains(ring))
                return $"notch.rings: unknown ring '{ring}'. Known: {string.Join(", ", NotchConfig.KnownRings)}.";
        }
        if (c.Notch.Rings.Distinct().Count() != c.Notch.Rings.Count) return "notch.rings lists a ring twice.";
        if (c.Notch.RefreshSeconds is < 1 or > 60) return $"notch.refreshSeconds must be 1 to 60 (got {c.Notch.RefreshSeconds}).";
        // Whether each pin exists is checked when the dock builds (it needs the Start menu): unknown ones get a toast.

        foreach (var (side, list) in new[] { ("left", c.Bar.Left), ("right", c.Bar.Right) })
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i] is null) return $"bar.{side} contains a null entry.";
                if (checkWidget(list[i]) is string widgetError)
                    return $"bar.{side}[{i}] ({list[i].Type}): {widgetError}";
            }
        }

        foreach (var (theme, colors) in new[] { ("dark", c.Colors.Dark), ("light", c.Colors.Light) })
        {
            foreach (var (name, value) in colors.All())
            {
                if (!IsColor(value))
                    return $"colors.{theme}.{name}: '{value}' is not a color. Use #RRGGBB or #AARRGGBB.";
            }
        }

        return null;
    }

    private static bool IsColor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        try
        {
            return ColorConverter.ConvertFromString(value) is Color;
        }
        catch (Exception)   // FormatException or NotSupportedException depending on the input
        {
            return false;
        }
    }
}
