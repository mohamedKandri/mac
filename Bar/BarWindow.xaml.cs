using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using MacShell.Core;
using MacShell.Native;
using MacShell.Themes;
using MacShell.Widgets;

namespace MacShell.Bar;

// The menu bar: a full-width strip at the top, square and flush like macOS. Glass, docking and fullscreen
// hiding come from ShellWindow; this class only hosts the widgets.
public partial class BarWindow : ShellWindow
{
    private const double RightItemGap = 18;   // design: 18 px between right-side items

    private readonly WidgetContext _widgetContext;
    private readonly List<IWidget> _widgets = [];
    private AppConfig _config;
    private string _widgetLayout = "";

    public BarWindow(AppConfig config, ThemeManager theme, Action<string, string> notify, AppCommands commands, string deviceName)
        : base(theme, deviceName)
    {
        InitializeComponent();
        _config = config;
        _widgetContext = new WidgetContext(this, theme, notify, commands);
        BuildWidgets();
    }

    protected override AppBarEdge Edge => AppBarEdge.Top;
    protected override double ReservedThickness => _config.Bar.Height;
    protected override bool RoundedCorners => false;
    protected override string TintColor => Theme.Palette.Background;

    // Called on every config reload. Colors and font update by themselves through DynamicResource.
    public void Apply(AppConfig config)
    {
        bool heightChanged = config.Bar.Height != _config.Bar.Height;
        _config = config;
        if (heightChanged) Redock();
        BuildWidgets();
    }

    // For developer switches: the first live widget of a given type, if any.
    public T? FindWidget<T>() where T : class, IWidget => _widgets.OfType<T>().FirstOrDefault();

    // (Re)creates the widgets, but only when their part of the config changed: a color tweak shouldn't
    // restart every widget's OS hooks.
    private void BuildWidgets()
    {
        string layout = JsonSerializer.Serialize(new { _config.Bar.Left, _config.Bar.Right });
        if (layout == _widgetLayout) return;
        _widgetLayout = layout;

        DisposeWidgets();
        Fill(LeftItems, _config.Bar.Left, gap: 0);
        Fill(RightItems, _config.Bar.Right, gap: RightItemGap);
    }

    private void Fill(Panel panel, List<WidgetEntry> entries, double gap)
    {
        foreach (var entry in entries.Where(e => e.Enabled))
        {
            IWidget? widget;
            try
            {
                widget = WidgetRegistry.Create(_widgetContext, entry);
            }
            catch (Exception ex)
            {
                // One broken widget must not take the bar down: skip it and say why.
                _widgetContext.Notify($"Widget '{entry.Type}' failed to start", ex.Message);
                continue;
            }
            if (widget is null) continue;   // type not built yet

            widget.View.Margin = new Thickness(panel.Children.Count == 0 ? 0 : gap, 0, 0, 0);
            widget.View.VerticalAlignment = VerticalAlignment.Center;
            panel.Children.Add(widget.View);
            _widgets.Add(widget);
        }
    }

    private void DisposeWidgets()
    {
        foreach (var widget in _widgets) widget.Dispose();
        _widgets.Clear();
        LeftItems.Children.Clear();
        RightItems.Children.Clear();
    }

    protected override void OnClosed(EventArgs e)
    {
        DisposeWidgets();
        base.OnClosed(e);
    }
}
