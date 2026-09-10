using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace AkashicRecords.App;

// Ambient sticky-note style projection of the transitions the user flagged for the desktop. Dumb
// widget: MainWindow supplies the reminder data. Hides on close (like MusicPlayerWindow) and only
// truly closes via ForceClose() on app exit.
public partial class DesktopReminderWidget : Window
{
    // Set only via ForceClose() when the app is genuinely exiting; otherwise closing hides the window.
    private bool _isExiting;

    // Raised when the user clicks ✕ so MainWindow can persist ShowDesktopReminders=false and
    // uncheck the tray item.
    public event Action? HideRequested;

    // Raised when the user clicks ↻ so MainWindow can reload the reminders from the repository.
    public event Action? RefreshRequested;

    // Raised continuously while the widget moves so MainWindow can keep its in-memory position current.
    public event Action<double, double>? PositionChanged;

    // Raised once when a drag finishes so MainWindow can persist the final position to disk
    // (one write per drag rather than one per LocationChanged).
    public event Action<double, double>? PositionCommitted;

    public DesktopReminderWidget()
    {
        InitializeComponent();
        LocationChanged += (_, _) => PositionChanged?.Invoke(Left, Top);
    }

    // Replaces the displayed reminders. An empty list shows a graceful empty state rather than
    // an empty card.
    public void SetReminders(IReadOnlyList<(string Title, string Text)> items)
    {
        RemindersPanel.Children.Clear();

        if (items.Count == 0)
        {
            EmptyText.Visibility = Visibility.Visible;
            return;
        }

        EmptyText.Visibility = Visibility.Collapsed;
        foreach (var (title, text) in items)
        {
            RemindersPanel.Children.Add(BuildReminder(title, text));
        }
    }

    private static FrameworkElement BuildReminder(string title, string text)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        panel.Children.Add(new TextBlock
        {
            Text = title,
            Foreground = Brushes.White,
            FontWeight = FontWeights.Bold,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap
        });

        if (!string.IsNullOrWhiteSpace(text))
        {
            panel.Children.Add(new TextBlock
            {
                Text = text,
                Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xDD)),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 0)
            });
        }

        return panel;
    }

    private void RefreshButton_OnClick(object sender, RoutedEventArgs e) => RefreshRequested?.Invoke();

    private void HideButton_OnClick(object sender, RoutedEventArgs e)
    {
        Hide();
        HideRequested?.Invoke();
    }

    // Only DragMove when the click didn't originate on an interactive control, otherwise
    // buttons would never receive the mouse (capture steals it first).
    private void Root_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (IsInteractiveOrigin(e.OriginalSource as DependencyObject)) return;
        try
        {
            // DragMove blocks until the mouse button is released; on return a drag ran, so
            // persist the final position once (skipped if DragMove throws).
            DragMove();
            PositionCommitted?.Invoke(Left, Top);
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

    // Lets MainWindow truly close the widget on real app exit.
    public void ForceClose()
    {
        _isExiting = true;
        Close();
    }

    // Closing (e.g. Alt+F4) hides instead of tearing down, so it can be reopened; only a real
    // app exit (ForceClose) lets it actually close.
    private void DesktopReminderWidget_OnClosing(object? sender, CancelEventArgs e)
    {
        if (_isExiting) return;
        e.Cancel = true;
        Hide();
        HideRequested?.Invoke();
    }
}
