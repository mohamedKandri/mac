using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using MacShell.Native;
using MacShell.Themes;

namespace MacShell.Widgets;

// Base for every dropdown below the bar (power menu, calendar...): frosted glass, Windows 11 rounded corners
// and shadow, 150 ms fade + slide in, closes on click-away or Esc.
//
// A separate Window rather than WPF's Popup control: a Popup is a layered window, which can't have DWM's
// rounded corners/shadow and doesn't play well with the blur.
public class PopupWindow : Window
{
    private readonly ThemeManager _theme;

    public PopupWindow(ThemeManager theme)
    {
        _theme = theme;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        UseLayoutRounding = true;
        Background = Brushes.Transparent;

        // Code version of {DynamicResource ...}: follow the theme live.
        SetResourceReference(ForegroundProperty, ThemeKeys.Foreground);
        SetResourceReference(FontFamilyProperty, ThemeKeys.FontFamily);
        SetResourceReference(FontSizeProperty, ThemeKeys.FontSize);

        SourceInitialized += (_, _) =>
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            // Out of Alt+Tab, but NOT no-activate: the menu must become active so that clicking elsewhere
            // deactivates it, which is how it knows to close.
            WindowStyles.MakeToolWindow(hwnd, noActivate: false);
            Backdrop.Prepare(this, roundedCorners: true);
            ApplyBackdrop();
        };
        _theme.Changed += ApplyBackdrop;

        Deactivated += (_, _) => SafeClose();
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) SafeClose();
        };
    }

    public bool IsClosing { get; private set; }

    // Closing an active window deactivates it, which would call Close() a second time; WPF throws on that.
    public void SafeClose()
    {
        if (!IsClosing) Close();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        IsClosing = true;
        base.OnClosing(e);
    }

    // Opens below the bar, left edge aligned with `anchor` (or right edge, for items on the right side).
    public void ShowBelow(FrameworkElement anchor, Window bar, bool alignRight = false)
    {
        // Everything in physical pixels on the bar's monitor. WPF's Left/Top are in WPF units of whatever monitor
        // the window was created on, which is wrong when monitors have different scaling.
        Point anchorLeftPx = anchor.PointToScreen(new Point(0, 0));
        Point anchorRightPx = anchor.PointToScreen(new Point(anchor.ActualWidth, 0));
        Point barBottomPx = bar.PointToScreen(new Point(0, bar.ActualHeight));
        double scale = Monitors.DpiAt((int)anchorLeftPx.X, (int)anchorLeftPx.Y) / 96.0;

        int top = (int)Math.Round(barBottomPx.Y + 6 * scale);   // 6 px gap under the bar
        int PlaceX(double widthDip) => (int)Math.Round(alignRight
            ? anchorRightPx.X - widthDip * scale + 2 * scale
            : anchorLeftPx.X - 2 * scale);

        // Measure the content before showing, so the popup appears in its final place (no jump).
        double width = 0;
        if (Content is FrameworkElement content)
        {
            content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            width = content.DesiredSize.Width;
        }

        // Move the native window before it becomes visible, then once more after layout in case the size differed
        // (e.g. a font still loading, or a DPI change after moving to another monitor).
        SourceInitialized += (_, _) => WindowStyles.MoveTo(new WindowInteropHelper(this).Handle, PlaceX(width), top);
        Loaded += (_, _) => WindowStyles.MoveTo(new WindowInteropHelper(this).Handle, PlaceX(ActualWidth), top);

        Show();
        Activate();
        AnimateIn();
    }

    private void AnimateIn()
    {
        if (Content is not FrameworkElement content) return;

        // Only the content animates: DWM draws the glass and shadow, and a non-layered window has no opacity.
        var slide = new TranslateTransform(0, -4);
        content.RenderTransform = slide;
        var duration = TimeSpan.FromMilliseconds(150);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        content.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = ease });
        slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-4, 0, duration) { EasingFunction = ease });
    }

    private void ApplyBackdrop() =>
        Backdrop.Apply(this, ThemeManager.ParseColor(_theme.Palette.Panel), _theme.Blur, _theme.IsDark);

    protected override void OnClosed(EventArgs e)
    {
        _theme.Changed -= ApplyBackdrop;
        base.OnClosed(e);
    }
}
