using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AkashicRecords.Infrastructure.Persistence;

namespace AkashicRecords.App.Views;

// Opens the shared ObservationsPanel in a small modal window, for pages whose own layout has no room
// for it (journal entry, recipe, poem, photo, reference image, music track). The Collections fiche
// embeds the panel directly instead.
public static class ObservationsDialog
{
    private static readonly ObservationRepository Observations = new(new SqliteConnectionFactory());

    // Text for the button that opens the dialog: the count shows at a glance that an item has observations.
    public static string ButtonLabel(string ownerKind, int ownerId)
    {
        var count = ownerId > 0 ? Observations.GetByOwner(ownerKind, ownerId).Count : 0;
        return count > 0 ? $"✎ Observations · {count}" : "✎ Observations";
    }

    public static void Open(DependencyObject host, string ownerKind, int ownerId, string ownerTitle)
    {
        if (ownerId <= 0) return;

        var panel = new ObservationsPanel();
        panel.SetOwner(ownerKind, ownerId);

        var title = new Emoji.Wpf.TextBlock
        {
            Text = ownerTitle, FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.FindResource("TextBrush"), Margin = new Thickness(0, 0, 0, 12),
        };

        var window = new Window
        {
            Title = "Observations",
            Width = 480,
            SizeToContent = SizeToContent.Height,
            MaxHeight = 680,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = host as Window ?? Window.GetWindow(host),
            ShowInTaskbar = false,
            Background = (Brush)Application.Current.FindResource("AppBgBrush"),
            Content = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = new StackPanel { Margin = new Thickness(20), Children = { title, panel } },
            },
        };
        window.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { e.Handled = true; window.Close(); }
        };
        window.ShowDialog();
    }
}
