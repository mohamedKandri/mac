using System.Text.Json;
using MacShell.Core;

namespace MacShell.Widgets;

// The list of widget types config.json may use. Adding a widget = one entry here (plus its UserControl).
public static class WidgetRegistry
{
    // Create:   builds the widget from its config entry.
    // Settings: the extra keys the entry may have and their JSON kind; anything else is reported as a typo.
    // Validate: optional deeper check (e.g. a date format), so a bad value is caught at load time
    //           and the last valid config is kept.
    private sealed record WidgetType(
        Func<WidgetContext, WidgetEntry, IWidget> Create,
        Dictionary<string, JsonValueKind>? Settings = null,
        Func<WidgetEntry, string?>? Validate = null);

    private const JsonValueKind Bool = JsonValueKind.True;   // stands for "true or false"
    private const JsonValueKind Text = JsonValueKind.String;

    private static readonly Dictionary<string, WidgetType> Types = new()
    {
        ["logo"] = new((ctx, _) => new LogoWidget(ctx)),
        ["activeApp"] = new((_, _) => new ActiveAppWidget()),
        ["clock"] = new((ctx, e) => new ClockWidget(ctx, e), new() { ["format"] = Text }, ClockWidget.Validate),
        ["battery"] = new((_, e) => new BatteryWidget(e), new() { ["showPercentage"] = Bool }),
        ["wifi"] = new((_, e) => new WifiWidget(e), new() { ["showName"] = Bool }),
        ["volume"] = new((_, e) => new VolumeWidget(e), new() { ["showPercentage"] = Bool }),
    };

    // Passed to ConfigLoader: returns an error message, or null when the entry is fine.
    public static string? Check(WidgetEntry entry)
    {
        if (!Types.TryGetValue(entry.Type, out var type))
            return $"unknown widget type '{entry.Type}'. Known: {string.Join(", ", Types.Keys)}.";

        foreach (var (key, value) in entry.Settings)
        {
            if (type.Settings is null || !type.Settings.TryGetValue(key, out var kind))
            {
                string allowed = type.Settings is null ? "none" : string.Join(", ", type.Settings.Keys);
                return $"unknown setting '{key}' (allowed: {allowed}).";
            }
            bool ok = kind == Bool ? value.ValueKind is JsonValueKind.True or JsonValueKind.False : value.ValueKind == kind;
            if (!ok)
                return $"'{key}' must be {(kind == Bool ? "true or false" : "a string")}.";
        }

        return type.Validate?.Invoke(entry);
    }

    public static IWidget? Create(WidgetContext context, WidgetEntry entry) =>
        Types.TryGetValue(entry.Type, out var type) ? type.Create(context, entry) : null;
}
