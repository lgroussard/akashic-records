using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace AkashicRecords.App;

// In-app notification card for the calendar (§57): today's agenda at startup (the "toast du
// matin") and lead-time reminders before dated events. Dumb widget like DesktopReminderWidget —
// MainWindow computes the items and decides when to show it. Clicking a row raises ItemClicked
// so the app can jump to the Calendrier section.
public partial class CalendarToast : Window
{
    // Set only via ForceClose() on real app exit; otherwise closing hides (DragMove/Alt+F4 safety).
    private bool _isExiting;

    private DispatcherTimer? _autoCloseTimer;

    // Raised when the user clicks a row (parameter: the item's notification key) — MainWindow
    // opens the calendar section.
    public event Action<string>? ItemClicked;

    // Raised when the user dismisses with ✕.
    public event Action? Dismissed;

    public CalendarToast()
    {
        InitializeComponent();
    }

    // Replaces the displayed rows. keys[i] identifies row i for ItemClicked. When autoCloseSeconds
    // > 0 the card fades itself away (lead-time reminders); the daily toast passes 0 and stays
    // until dismissed.
    public void SetItems(string header, IReadOnlyList<ToastLine> lines, int autoCloseSeconds = 0)
    {
        StopAutoClose();
        HeaderText.Text = header.ToUpperInvariant();
        ItemsPanel.Children.Clear();

        foreach (var line in lines)
            ItemsPanel.Children.Add(BuildLine(line));

        if (autoCloseSeconds > 0)
        {
            _autoCloseTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(autoCloseSeconds) };
            _autoCloseTimer.Tick += (_, _) =>
            {
                StopAutoClose();
                Hide();
            };
            _autoCloseTimer.Start();
        }
    }

    public sealed record ToastLine(Color DotColor, string TimeText, string Title, string Subtitle, string Key);

    private void StopAutoClose()
    {
        _autoCloseTimer?.Stop();
        _autoCloseTimer = null;
    }

    private FrameworkElement BuildLine(ToastLine line)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 8), Cursor = Cursors.Hand };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var dot = new Ellipse
        {
            Width = 8,
            Height = 8,
            Margin = new Thickness(0, 0, 7, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Fill = new SolidColorBrush(line.DotColor),
        };
        Grid.SetColumn(dot, 0);
        grid.Children.Add(dot);

        if (line.TimeText.Length > 0)
        {
            var time = new TextBlock
            {
                Text = line.TimeText,
                Foreground = new SolidColorBrush(Color.FromRgb(0x7F, 0xB0, 0xFF)),
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 7, 0),
                VerticalAlignment = VerticalAlignment.Top,
            };
            Grid.SetColumn(time, 1);
            grid.Children.Add(time);
        }

        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = line.Title,
            Foreground = Brushes.White,
            FontWeight = FontWeights.SemiBold,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
        });
        if (line.Subtitle.Length > 0)
        {
            stack.Children.Add(new TextBlock
            {
                Text = line.Subtitle,
                Foreground = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xC8)),
                FontSize = 10.5,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 1, 0, 0),
            });
        }
        Grid.SetColumn(stack, 2);
        grid.Children.Add(stack);

        var key = line.Key;
        grid.MouseLeftButtonUp += (_, _) =>
        {
            Hide();
            ItemClicked?.Invoke(key);
        };

        return grid;
    }

    private void CloseButton_OnClick(object sender, RoutedEventArgs e)
    {
        StopAutoClose();
        Hide();
        Dismissed?.Invoke();
    }

    // Only DragMove when the click didn't originate on an interactive element — same visual-tree
    // walk as DesktopReminderWidget (OriginalSource is often a template part, not the control).
    private void Root_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (IsInteractiveOrigin(e.OriginalSource as DependencyObject)) return;
        try
        {
            DragMove();
        }
        catch { /* DragMove throws if button already released */ }
    }

    private static bool IsInteractiveOrigin(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is ButtonBase or TextBoxBase) return true;
            source = VisualTreeHelper.GetParent(source);
        }
        return false;
    }

    public void ForceClose()
    {
        _isExiting = true;
        StopAutoClose();
        Close();
    }

    private void CalendarToast_OnClosing(object? sender, CancelEventArgs e)
    {
        if (_isExiting) return;
        e.Cancel = true;
        StopAutoClose();
        Hide();
    }
}
