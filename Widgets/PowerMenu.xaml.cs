using System.Windows;
using System.Windows.Controls;
using MacShell.Native;

namespace MacShell.Widgets;

public partial class PowerMenu : PopupWindow
{
    private readonly WidgetContext _context;
    private Button? _pendingItem;

    // Tag in the XAML -> question, confirm button label, action.
    private static readonly Dictionary<string, (string Question, string Verb, Action Run)> Actions = new()
    {
        ["Lock"] = ("Lock now?", "Lock", Power.Lock),
        ["Sleep"] = ("Sleep now?", "Sleep", Power.Sleep),
        ["Restart"] = ("Restart now?", "Restart", Power.Restart),
        ["ShutDown"] = ("Shut down now?", "Shut down", Power.ShutDown),
    };

    public PowerMenu(WidgetContext context) : base(context.Theme)
    {
        InitializeComponent();
        _context = context;
        StartupCheck.Visibility = context.Commands.StartWithWindows ? Visibility.Visible : Visibility.Hidden;
    }

    // ---------------- MacShell's own items ----------------

    private void OnReload(object sender, RoutedEventArgs e)
    {
        SafeClose();
        _context.Commands.Reload();
    }

    private void OnToggleStartup(object sender, RoutedEventArgs e)
    {
        _context.Commands.StartWithWindows = !_context.Commands.StartWithWindows;
        StartupCheck.Visibility = _context.Commands.StartWithWindows ? Visibility.Visible : Visibility.Hidden;
    }

    // Quitting restores the Windows taskbar (and the screen space).
    private void OnQuit(object sender, RoutedEventArgs e)
    {
        SafeClose();
        Dispatcher.InvokeAsync(_context.Commands.Quit);
    }

    // First click: swap the item's row for "Restart now? [Cancel] [Restart]".
    private void OnItemClick(object sender, RoutedEventArgs e)
    {
        CancelPending();
        var item = (Button)sender;
        var (question, verb, _) = Actions[(string)item.Tag];

        ConfirmText.Text = question;
        ConfirmButton.Content = verb;
        Items.Children.Remove(Confirm);
        Items.Children.Insert(Items.Children.IndexOf(item) + 1, Confirm);
        item.Visibility = Visibility.Collapsed;
        Confirm.Visibility = Visibility.Visible;
        _pendingItem = item;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => CancelPending();

    private void CancelPending()
    {
        if (_pendingItem is null) return;
        _pendingItem.Visibility = Visibility.Visible;
        Confirm.Visibility = Visibility.Collapsed;
        _pendingItem = null;
    }

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        if (_pendingItem is null) return;
        var (_, verb, run) = Actions[(string)_pendingItem.Tag];
        SafeClose();

        // Run after the menu is gone, so it isn't left on screen behind the lock screen or during shutdown.
        Dispatcher.InvokeAsync(() =>
        {
            try
            {
                run();
            }
            catch (Exception ex)
            {
                _context.Notify($"{verb} failed", ex.Message);
            }
        });
    }
}
