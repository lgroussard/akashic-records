using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace AkashicRecords.App.Views;

// Lets the calendar's day cells tint themselves for dates that have a journal entry - the "chip"
// indicator the user wants. Static state (not a DependencyProperty) since this is a single-instance
// desktop app; the view refreshes DatesWithEntries whenever entries change.
public sealed class EntryDateBackgroundConverter : IValueConverter
{
    public static HashSet<DateTime> DatesWithEntries { get; set; } = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is DateTime date && DatesWithEntries.Contains(date.Date))
        {
            var brush = new SolidColorBrush(Color.FromArgb(0x29, 0x5B, 0x8C, 0xFF));
            brush.Freeze();
            return brush;
        }
        return Brushes.Transparent;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
