using System.Windows.Controls;

namespace MacShell.Widgets;

// Open/close logic shared by every bar item that has a dropdown (logo, clock...).
//  - A click while the popup is open closes it. (The bar never takes focus, so clicking it doesn't
//    deactivate the popup first; the click reaches us with the popup still open.)
//  - The bar item stays highlighted while its popup is open, like macOS (Tag="open", see Shell.BarButton).
internal sealed class PopupToggle(Button anchor, WidgetContext context, Func<PopupWindow> create, bool alignRight = false)
{
    private PopupWindow? _popup;

    public void Toggle()
    {
        if (_popup is not null)
            _popup.SafeClose();
        else
            Open();
    }

    public void Open()
    {
        if (_popup is not null) return;
        _popup = create();
        anchor.Tag = "open";
        _popup.Closed += (_, _) =>
        {
            _popup = null;
            anchor.Tag = null;
        };
        _popup.ShowBelow(anchor, context.Host, alignRight);
    }

    public void Close() => _popup?.SafeClose();
}
