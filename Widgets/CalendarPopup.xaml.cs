using System.Globalization;
using System.Windows;
using System.Windows.Input;

namespace MacShell.Widgets;

// One cell of the month grid. Label is empty for the padding cells before the 1st.
public sealed record CalendarDay(string Label, bool IsToday);

public partial class CalendarPopup : PopupWindow
{
    private DateTime _month;   // first day of the month on display

    public CalendarPopup(WidgetContext context) : base(context.Theme)
    {
        InitializeComponent();

        // Weekday letters in the user's locale, starting on its first day of the week (Monday in most of Europe).
        var culture = CultureInfo.CurrentCulture.DateTimeFormat;
        Weekdays.ItemsSource = Enumerable.Range(0, 7)
            .Select(i => culture.AbbreviatedDayNames[((int)culture.FirstDayOfWeek + i) % 7][..1].ToUpper())
            .ToList();

        PreviewKeyDown += OnKey;
        ShowMonth(DateTime.Today);
    }

    private void ShowMonth(DateTime anyDay)
    {
        _month = new DateTime(anyDay.Year, anyDay.Month, 1);
        var culture = CultureInfo.CurrentCulture;

        // "October 2026" (some locales write month names lower case: capitalize the first letter).
        string title = _month.ToString("MMMM yyyy", culture);
        MonthTitle.Text = char.ToUpper(title[0], culture) + title[1..];

        // Blank cells until the 1st falls on its weekday column, then one cell per day.
        int blanks = ((int)_month.DayOfWeek - (int)culture.DateTimeFormat.FirstDayOfWeek + 7) % 7;
        var today = DateTime.Today;
        Days.ItemsSource = Enumerable.Repeat(new CalendarDay("", false), blanks)
            .Concat(Enumerable.Range(1, DateTime.DaysInMonth(_month.Year, _month.Month))
                .Select(d => new CalendarDay(d.ToString(culture), _month.AddDays(d - 1) == today)))
            .ToList();
    }

    private void OnPrevious(object sender, RoutedEventArgs e) => ShowMonth(_month.AddMonths(-1));
    private void OnNext(object sender, RoutedEventArgs e) => ShowMonth(_month.AddMonths(1));
    private void OnTitleClick(object sender, MouseButtonEventArgs e) => ShowMonth(DateTime.Today);

    private void OnKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Left: ShowMonth(_month.AddMonths(-1)); break;
            case Key.Right: ShowMonth(_month.AddMonths(1)); break;
            case Key.Home: ShowMonth(DateTime.Today); break;
            default: return;
        }
        e.Handled = true;
    }
}
