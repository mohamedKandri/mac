using System.Windows;
using System.Windows.Controls;

namespace MacShell.Widgets;

public partial class LogoWidget : UserControl, IWidget
{
    private readonly PopupToggle _menu;

    public FrameworkElement View => this;

    public LogoWidget(WidgetContext context)
    {
        InitializeComponent();
        _menu = new PopupToggle(Button, context, () => new PowerMenu(context));
    }

    private void OnClick(object sender, RoutedEventArgs e) => _menu.Toggle();

    // Also used by the `--open power-menu` developer switch (see App.xaml.cs).
    public void OpenMenu() => _menu.Open();

    public void Dispose() => _menu.Close();
}
