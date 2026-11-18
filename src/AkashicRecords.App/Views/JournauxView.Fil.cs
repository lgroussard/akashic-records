using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AkashicRecords.Domain;
using AkashicRecords.Infrastructure.Persistence;

namespace AkashicRecords.App.Views;

// Archive artistique → Fil. The second face of an artistic project: where the canvas is a free-form
// board, the Fil lists every observation the project references (Reference rows), grouped by the kind
// of item the observation sits on, each card opening that item. A project is the app's "theme".
public partial class JournauxView
{
    // Section headers, in display order, per Observation.OwnerKind.
    private static readonly (string Kind, string Label)[] FilGroups =
    {
        (Observation.ArtworkOwner, "ŒUVRES"),
        (Observation.JournalEntryOwner, "JOURNAL"),
        (Observation.PoemOwner, "POÈMES"),
        (Observation.RecipeOwner, "RECETTES"),
        (Observation.PhotoOwner, "PHOTOS"),
        (Observation.RefImageOwner, "RÉFÉRENCES"),
        (Observation.MusicTrackOwner, "MUSIQUE"),
    };

    private readonly GlobalSearchService _filSearchService = new(new SqliteConnectionFactory());
    private bool _filMode;

    // --- Observations on a journal day, a recipe, a poem (the shared panel, in its dialog) ---

    // Each page's button carries the observation count of the item it shows.
    private void RefreshObservationButtons()
    {
        EntryObservationsButton.Content = ObservationsDialog.ButtonLabel(Observation.JournalEntryOwner, _selectedEntry?.Id ?? 0);
        RecipeObservationsButton.Content = ObservationsDialog.ButtonLabel(Observation.RecipeOwner, _selectedRecipe?.Id ?? 0);
        PoemObservationsButton.Content = ObservationsDialog.ButtonLabel(Observation.PoemOwner, _selectedPoem?.Id ?? 0);
        RecipeObservationsButton.IsEnabled = _selectedRecipe is not null;
        PoemObservationsButton.IsEnabled = _selectedPoem is not null;
    }

    private void EntryObservationsButton_OnClick(object sender, RoutedEventArgs e)
    {
        // A day with nothing written has no row yet; observing it is a reason to create one.
        var entry = EnsureSelectedEntryExists();
        var title = entry.Title.Length > 0 ? entry.Title : _currentPageDate.ToString("dddd d MMMM yyyy", CultureInfo.CurrentCulture);
        ObservationsDialog.Open(this, Observation.JournalEntryOwner, entry.Id, title);
        RefreshObservationButtons();
    }

    private void RecipeObservationsButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_selectedRecipe is null) return;
        ObservationsDialog.Open(this, Observation.RecipeOwner, _selectedRecipe.Id, _selectedRecipe.Title);
        RefreshObservationButtons();
    }

    private void PoemObservationsButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_selectedPoem is null) return;
        ObservationsDialog.Open(this, Observation.PoemOwner, _selectedPoem.Id, _selectedPoem.Title);
        RefreshObservationButtons();
    }

    private void ProjectModeToggle_OnClick(object sender, RoutedEventArgs e)
    {
        SetFilMode(ReferenceEquals(sender, FilModeToggle));
        SaveActiveItem();
    }

    private void SetFilMode(bool fil)
    {
        _filMode = fil;
        CanvasModeToggle.IsChecked = !fil;
        FilModeToggle.IsChecked = fil;
        CanvasBoard.Visibility = fil ? Visibility.Collapsed : Visibility.Visible;
        FilBoard.Visibility = fil ? Visibility.Visible : Visibility.Collapsed;
        CanvasToolsPanel.Visibility = fil ? Visibility.Collapsed : Visibility.Visible;
        FilAddButton.Visibility = fil ? Visibility.Visible : Visibility.Collapsed;
        if (fil) RefreshFil();
    }

    private void RefreshFil()
    {
        FilHost.Children.Clear();
        if (_currentProject is null)
        {
            FilHost.Children.Add(FilNote("Aucun projet ouvert. Crée-en un avec « Nouveau projet »."));
            return;
        }

        var observations = _observationRepository.GetByProject(_currentProject.Id);
        FilHost.Children.Add(new TextBlock
        {
            Text = _currentProject.Title, FontSize = 20, FontWeight = FontWeights.Bold,
            Foreground = (Brush)FindResource("TextBrush"), Margin = new Thickness(0, 0, 0, 2),
        });
        FilHost.Children.Add(FilNote(observations.Count == 0
            ? "Ce projet ne référence encore rien. Ajoute une observation ici, ou lie-en une depuis la page " +
              "d'une œuvre, d'un poème, d'un jour du journal, d'une image… (bouton « ＋ projet »)."
            : $"{observations.Count} observation(s) référencée(s)."));

        foreach (var (kind, label) in FilGroups)
        {
            var group = observations.Where(o => o.OwnerKind == kind).ToList();
            if (group.Count == 0) continue;

            FilHost.Children.Add(new TextBlock
            {
                Text = $"{label} · {group.Count}", Style = (Style)FindResource("PanelSectionHeaderStyle"),
                Margin = new Thickness(0, 18, 0, 8),
            });
            foreach (var observation in group) FilHost.Children.Add(BuildFilCard(observation));
        }
    }

    private TextBlock FilNote(string text) => new()
    {
        Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12.5,
        Foreground = (Brush)FindResource("TextMutedBrush"), Margin = new Thickness(0, 0, 0, 4),
    };

    private Border BuildFilCard(ObservationRepository.ObservationSummary observation)
    {
        var projectId = _currentProject!.Id;
        var hit = observation.ToSearchResult(string.Empty);

        var text = new StackPanel();
        text.Children.Add(new Emoji.Wpf.TextBlock
        {
            Text = observation.OwnerTitle, FontWeight = FontWeights.SemiBold, FontSize = 14,
            Foreground = (Brush)FindResource("AccentBrush"), TextWrapping = TextWrapping.Wrap,
        });
        if (observation.Subject.Length > 0)
        {
            text.Children.Add(new Emoji.Wpf.TextBlock
            {
                Text = observation.Subject, FontWeight = FontWeights.SemiBold, FontStyle = FontStyles.Italic,
                Foreground = (Brush)FindResource("TextBrush"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0),
            });
        }
        text.Children.Add(new Emoji.Wpf.TextBlock
        {
            Text = observation.Content, Foreground = (Brush)FindResource("TextBrush"),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0),
        });
        text.Children.Add(new TextBlock
        {
            Text = observation.CreatedAt.ToString("d MMMM yyyy", CultureInfo.CurrentCulture), FontSize = 11,
            Foreground = (Brush)FindResource("TextFaintBrush"), Margin = new Thickness(0, 6, 0, 0),
        });

        var unlink = new Button
        {
            Content = "✕", ToolTip = "Retirer du projet (l'observation reste sur son élément)",
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), Cursor = Cursors.Hand,
            Foreground = (Brush)FindResource("TextFaintBrush"), VerticalAlignment = VerticalAlignment.Top,
            Padding = new Thickness(6, 0, 0, 0),
        };
        unlink.Click += (_, e) =>
        {
            e.Handled = true;
            _referenceRepository.Unlink(observation.ObservationId, projectId);
            RefreshFil();
        };
        DockPanel.SetDock(unlink, Dock.Right);

        var dock = new DockPanel();
        dock.Children.Add(unlink);

        // The owner's own picture when it has one (cover, photo, reference image, poem image).
        var preview = _filSearchService.GetPreview(hit.Kind, hit.PrimaryId, hit.SecondaryId);
        if (preview?.ImagePath is { } imagePath)
        {
            try
            {
                var thumbnail = new Border
                {
                    Width = 64, Height = 64, CornerRadius = new CornerRadius(6), Margin = new Thickness(0, 0, 12, 0),
                    VerticalAlignment = VerticalAlignment.Top,
                    Background = new ImageBrush(LoadImage(MediaStorage.ResolveFullPath(imagePath))) { Stretch = Stretch.UniformToFill },
                };
                DockPanel.SetDock(thumbnail, Dock.Left);
                dock.Children.Add(thumbnail);
            }
            catch
            {
                // An unreadable image only costs the thumbnail.
            }
        }
        dock.Children.Add(text);

        var card = new Border
        {
            Background = (Brush)FindResource("Surface2Brush"), BorderBrush = (Brush)FindResource("AppBorderBrush"),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 0, 0, 8), Cursor = Cursors.Hand, Child = dock,
            ToolTip = "Ouvrir l'élément",
        };
        card.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            SaveActiveItem(); // so coming back to Journaux lands on this project's Fil
            (Window.GetWindow(this) as MainWindow)?.NavigateToSearchResult(hit);
        };
        return card;
    }
}
