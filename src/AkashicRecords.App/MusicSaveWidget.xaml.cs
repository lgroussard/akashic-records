using System.ComponentModel;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace AkashicRecords.App;

// Floating "add to library" pill for the suggestion currently playing, independent of the
// music player window. Dumb widget: MainWindow feeds it and forwards the click to the player.
public partial class MusicSaveWidget : Window
{
    private bool _isExiting;

    public event Action? SaveRequested;
    public event Action<double, double>? PositionCommitted;

    public MusicSaveWidget()
    {
        InitializeComponent();
    }

    public void Render(TrackSaveState state)
    {
        SaveButton.IsEnabled = state == TrackSaveState.None;
        (SaveButton.Content, SaveButton.ToolTip) = state == TrackSaveState.Queued
            ? ("✓", "Dans la file de téléchargement")
            : ("⤓", "Ajouter à la file de téléchargement");
    }

    private void SaveButton_OnClick(object sender, RoutedEventArgs e) => SaveRequested?.Invoke();

    private void Root_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        for (var source = e.OriginalSource as DependencyObject; source is not null; source = VisualTreeHelper.GetParent(source))
            if (source is ButtonBase) return;
        try
        {
            DragMove();
            PositionCommitted?.Invoke(Left, Top);
        }
        catch { /* DragMove throws if button already released */ }
    }

    public void ForceClose()
    {
        _isExiting = true;
        Close();
    }

    private void MusicSaveWidget_OnClosing(object? sender, CancelEventArgs e)
    {
        if (_isExiting) return;
        e.Cancel = true;
        Hide();
    }
}
