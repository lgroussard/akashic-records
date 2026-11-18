using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AkashicRecords.Domain;

namespace AkashicRecords.App;

// Ambient desktop projection of the week's recipe picks, so what to cook is already decided. Dumb widget,
// like FilmOfTheDayWidget: MainWindow supplies the picks, and the widget only reports clicks. Hides on
// close and only truly closes via ForceClose() on app exit.
public partial class RecipeOfTheWeekWidget : Window
{
    private bool _isExiting;

    // ✕ clicked: MainWindow persists ShowRecipeOfTheWeek=false and unchecks the tray item.
    public event Action? HideRequested;

    // ↻ clicked: MainWindow draws other recipes and calls ShowPicks again.
    public event Action? RerollRequested;

    // A recipe row clicked: MainWindow opens that recipe in the journal.
    public event Action<int>? RecipeClicked;

    public event Action<double, double>? PositionChanged;
    public event Action<double, double>? PositionCommitted;

    public RecipeOfTheWeekWidget()
    {
        InitializeComponent();
        LocationChanged += (_, _) => PositionChanged?.Invoke(Left, Top);
    }

    // Covers are full paths already resolved by MainWindow (null when the recipe has no photo).
    public void ShowPicks(IReadOnlyList<(Recipe Recipe, string? Cover)> picks)
    {
        RecipesPanel.Children.Clear();
        foreach (var (recipe, cover) in picks)
        {
            RecipesPanel.Children.Add(BuildRow(recipe, cover));
        }
    }

    private Button BuildRow(Recipe recipe, string? cover)
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        if (cover is not null && File.Exists(cover))
        {
            var thumb = new Border
            {
                Width = 44,
                Height = 44,
                CornerRadius = new CornerRadius(4),
                Margin = new Thickness(0, 0, 8, 0),
                Background = new ImageBrush(LoadImage(cover)) { Stretch = Stretch.UniformToFill },
            };
            row.Children.Add(thumb);
        }

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new Emoji.Wpf.TextBlock
        {
            Text = recipe.Title,
            Foreground = Brushes.White,
            FontWeight = FontWeights.SemiBold,
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
        });
        if (!string.IsNullOrWhiteSpace(recipe.Category))
        {
            text.Children.Add(new TextBlock
            {
                Text = recipe.Category,
                Foreground = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)),
                FontSize = 11,
                Margin = new Thickness(0, 2, 0, 0),
            });
        }
        Grid.SetColumn(text, 1);
        row.Children.Add(text);

        var button = new Button
        {
            Style = (Style)FindResource("RecipeRowButton"),
            Content = row,
            ToolTip = "Ouvrir la recette",
        };
        var id = recipe.Id;
        button.Click += (_, _) => RecipeClicked?.Invoke(id);
        return button;
    }

    private void RerollButton_OnClick(object sender, RoutedEventArgs e) => RerollRequested?.Invoke();

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

    // Loaded fully into memory so the photo file stays free to be replaced or deleted.
    private static BitmapImage LoadImage(string fullPath)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.DecodePixelWidth = 100;
        bitmap.UriSource = new Uri(fullPath);
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    public void ForceClose()
    {
        _isExiting = true;
        Close();
    }

    private void RecipeOfTheWeekWidget_OnClosing(object? sender, CancelEventArgs e)
    {
        if (_isExiting) return;
        e.Cancel = true;
        Hide();
        HideRequested?.Invoke();
    }
}
