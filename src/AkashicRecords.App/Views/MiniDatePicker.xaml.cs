using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace AkashicRecords.App.Views;

// Dark date picker replacing DatePicker in the calendar forms. Same role, no native Calendar:
// the WPF Calendar/CalendarItem popup can only be re-themed by cloning its whole template, and
// half-themed popups (light chrome, invisible ink) are what made the old pickers ugly.
public partial class MiniDatePicker : UserControl
{
    public static readonly DependencyProperty SelectedDateProperty =
        DependencyProperty.Register(nameof(SelectedDate), typeof(DateTime?), typeof(MiniDatePicker),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnSelectedDateChanged));

    // Fires only for user picks (day cell / Aujourd'hui), not programmatic assignments — the view
    // uses it to pull the selection along (e.g. the end date follows the start date).
    public event EventHandler? SelectedDateChangedByUser;

    public DateTime? SelectedDate
    {
        get => (DateTime?)GetValue(SelectedDateProperty);
        set => SetValue(SelectedDateProperty, value);
    }

    private DateTime _displayMonth = new(DateTime.Today.Year, DateTime.Today.Month, 1);

    public MiniDatePicker()
    {
        InitializeComponent();
        UpdateLabel();
        BuildDays();
    }

    private static void OnSelectedDateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var picker = (MiniDatePicker)d;
        picker.UpdateLabel();
        if (e.NewValue is DateTime date) picker._displayMonth = new DateTime(date.Year, date.Month, 1);
        picker.BuildDays();
    }

    private void UpdateLabel()
    {
        var text = SelectedDate is { } date
            ? date.ToString("d MMMM yyyy", CultureInfo.CurrentCulture)
            : string.Empty;
        Label.Text = text.Length > 0 ? char.ToUpperInvariant(text[0]) + text[1..] : string.Empty;
        Watermark.Visibility = text.Length > 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Toggle_OnChecked(object sender, RoutedEventArgs e)
    {
        // Open on the month being picked (or today). Rebuilt every open so stale cells never linger.
        var anchor = SelectedDate ?? DateTime.Today;
        _displayMonth = new DateTime(anchor.Year, anchor.Month, 1);
        BuildDays();
    }

    private void Toggle_OnUnchecked(object sender, RoutedEventArgs e)
    {
    }

    private void Prev_OnClick(object sender, RoutedEventArgs e)
    {
        _displayMonth = _displayMonth.AddMonths(-1);
        BuildDays();
    }

    private void Next_OnClick(object sender, RoutedEventArgs e)
    {
        _displayMonth = _displayMonth.AddMonths(1);
        BuildDays();
    }

    private void Today_OnClick(object sender, RoutedEventArgs e)
    {
        SetUserSelection(DateTime.Today);
        PART_Popup.IsOpen = false;
    }

    private void BuildDays()
    {
        var month = CultureInfo.CurrentCulture.DateTimeFormat.GetMonthName(_displayMonth.Month);
        MonthText.Text = char.ToUpperInvariant(month[0]) + month[1..] + " " + _displayMonth.Year;

        DayGrid.Children.Clear();
        var first = new DateTime(_displayMonth.Year, _displayMonth.Month, 1);
        var leading = ((int)first.DayOfWeek + 6) % 7; // Monday-first, like the section grid
        for (var slot = 0; slot < 42; slot++)
        {
            var date = first.AddDays(slot - leading);
            DayGrid.Children.Add(BuildDayCell(date));
        }
    }

    private Button BuildDayCell(DateTime date)
    {
        var inMonth = date.Month == _displayMonth.Month;
        var isToday = date.Date == DateTime.Today;
        var isSelected = SelectedDate?.Date == date.Date;

        var border = new Border
        {
            Background = isSelected ? (Brush)Application.Current.Resources["AccentBrush"] : Brushes.Transparent,
            CornerRadius = new CornerRadius(6),
        };
        border.Child = new TextBlock
        {
            Text = date.Day.ToString(),
            FontSize = 11.5,
            TextAlignment = TextAlignment.Center,
            FontWeight = isSelected || isToday ? FontWeights.Bold : FontWeights.Normal,
            Foreground = inMonth
                ? (Brush)Application.Current.Resources[isSelected ? "TextBrush" : isToday ? "AccentBrush" : "TextMutedBrush"]
                : (Brush)Application.Current.Resources["TextFaintBrush"],
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var cell = new Button
        {
            Content = border,
            Width = 30,
            Height = 27,
            Margin = new Thickness(1),
            Cursor = System.Windows.Input.Cursors.Hand,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
        };
        // Strip the Button chrome entirely — cells must read like the big month grid, not buttons.
        var template = new ControlTemplate(typeof(Button));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
        presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Stretch);
        template.VisualTree = presenter;
        cell.Template = template;

        var picked = date;
        cell.Click += (_, _) =>
        {
            SetUserSelection(picked);
            PART_Popup.IsOpen = false;
        };
        return cell;
    }

    private void SetUserSelection(DateTime date)
    {
        SelectedDate = date.Date;
        SelectedDateChangedByUser?.Invoke(this, EventArgs.Empty);
    }
}
