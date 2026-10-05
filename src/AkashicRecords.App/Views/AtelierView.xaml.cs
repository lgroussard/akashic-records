using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AkashicRecords.Domain;
using AkashicRecords.Infrastructure.Atelier;
using AkashicRecords.Infrastructure.Configuration;
using AkashicRecords.Infrastructure.Web;

namespace AkashicRecords.App.Views;

// The drawing atelier: a linear bank of drills (Structure → Valeur → Gesture → Couleur →
// Composition) with a Commons-enriched reference strip, plus a 30-second gesture chrono.
// No persisted history — the pointer resets to 1 on each open, per the agreed scope.
public partial class AtelierView : UserControl
{
    private readonly AppConfig _config;
    private readonly CommonsSearchService _commons = new();

    private int _index = 1;             // 1-based drill number in Cartes mode
    private readonly int _total = ExerciseBank.Count;

    // Chrono state: a small ring of 6 subjects, the timer drives the current one.
    private int _chronoIndex;           // 0-based within ChronoSeries
    private int _chronoRemaining;
    private bool _chronoPaused;
    private DispatcherTimer? _timer;

    private string? _pendingScreenshotTab;

    public AtelierView(AppConfig config)
    {
        InitializeComponent();
        _config = config;
        Loaded += (_, _) =>
        {
            if (_pendingScreenshotTab is { } tab)
            {
                _pendingScreenshotTab = null;
                ApplyMode(tab);
            }
            else
            {
                ApplyMode("Cartes");
            }
            RenderCartes();
            RenderChrono();
        };
        SizeChanged += (_, _) => UpdateProgressWidth();
    }

    // Headless routing (see MainWindow.OpenSectionForScreenshot): the view isn't Loaded yet,
    // so the requested mode is stored and applied on Loaded.
    public void ShowTabForScreenshot(string tab)
    {
        _pendingScreenshotTab = tab;
        if (IsLoaded)
        {
            _pendingScreenshotTab = null;
            ApplyMode(tab);
            RenderCartes();
            RenderChrono();
        }
    }

    private void ApplyMode(string tab)
    {
        var chrono = tab.Equals("Chrono", System.StringComparison.OrdinalIgnoreCase);
        ModeCartesTab.IsChecked = !chrono;
        ModeChronoTab.IsChecked = chrono;
        CartesRoot.Visibility = chrono ? Visibility.Collapsed : Visibility.Visible;
        ChronoRoot.Visibility = chrono ? Visibility.Visible : Visibility.Collapsed;
        if (chrono) StartChronoIfNeeded();
    }

    private void ModeTab_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { Tag: string tag }) ApplyMode(tag);
    }

    // ── Cartes ────────────────────────────────────────────────────────────────
    private void RenderCartes()
    {
        if (ExerciseBank.ByNumber(_index) is not { } ex) return;

        ExKicker.Text = $"EXERCICE {ex.N} · {ex.Theme.ToUpperInvariant()} · {ex.Seconds / 60.0:0.#} MIN";
        ExTitle.Text = ex.Title;

        StepsHost.Children.Clear();
        for (var i = 0; i < ex.Steps.Count; i++)
        {
            var row = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 3) };
            row.Children.Add(new Border
            {
                Width = 20,
                Height = 20,
                CornerRadius = new CornerRadius(10),
                Background = (Brush)FindResource("AccentBrush"),
                Child = new TextBlock
                {
                    Text = (i + 1).ToString(),
                    Foreground = Brushes.White,
                    FontSize = 11,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            });
            row.Children.Add(new TextBlock
            {
                Text = ex.Steps[i],
                Foreground = (Brush)FindResource("TextBrush"),
                FontSize = 14,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0),
            });
            StepsHost.Children.Add(row);
        }

        ChipsHost.Children.Clear();
        foreach (var chip in ControlChips(ex))
            ChipsHost.Children.Add(Pill(chip, first: ChipsHost.Children.Count == 0));

        RefsHost.Children.Clear();
        foreach (var r in ex.Refs)
            RefsHost.Children.Add(RefCard(r));

        StatePillText.Text = $"{_index} / {_total} · {ex.Theme}";
        UpdateProgressWidth();

        // Best-effort thumbnails; the text cards are already painted so an offline run is fine.
        _ = EnrichRefsAsync(ex);
    }

    private static string[] ControlChips(DrawingExercise ex) => ex.Theme switch
    {
        "Structure" => new[] { "3 plans lisibles ?", "Valeur 3 = ombre nette ?", "Aplat unique ?" },
        "Valeur" => new[] { "3 valeurs distinctes ?", "Pli le plus noir ?", "Reflet le plus clair ?" },
        "Gesture" => new[] { "Une seule ligne tenue ?", "Silhouette pleine ?", "Bloc, pas détail ?" },
        "Couleur" => new[] { "3 couleurs max ?", "Mélange avant pose ?", "Dominante nette ?" },
        "Composition" => new[] { "Sujet sur un tiers ?", "Nombre impair ?", "Vide présent ?" },
        _ => new[] { "3 plans ?", "3 valeurs ?", "3 couleurs ?" },
    };

    private static Border Pill(string text, bool first) => new()
    {
        Background = (Brush)new SolidColorBrush(Color.FromArgb(0x29, 0x5B, 0x8C, 0xFF)),
        CornerRadius = new CornerRadius(999),
        Padding = new Thickness(12, 4, 12, 4),
        Margin = new Thickness(0, 0, 8, 0),
        Child = new TextBlock
        {
            Text = text,
            FontSize = 12,
            Foreground = (Brush)new SolidColorBrush(Color.FromRgb(0xEC, 0xEC, 0xF4)),
        },
    };

    private Border RefCard(DrawingReference r)
    {
        var body = new StackPanel();
        body.Children.Add(new TextBlock
        {
            Text = "RÉF",
            FontSize = 10,
            FontWeight = FontWeights.Bold,
            Foreground = (Brush)FindResource("TextFaintBrush"),
            Margin = new Thickness(0, 0, 0, 3),
        });
        body.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Foreground = (Brush)FindResource("TextBrush"),
            Inlines =
            {
                new Bold(new Run(r.Artist + " ")),
                new Italic(new Run(r.Work + " ")),
                new Run("— " + r.Takeaway),
            },
        });

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(body, 0);
        grid.Children.Add(body);

        var thumb = new Image
        {
            MaxHeight = 84,
            MaxWidth = 110,
            Stretch = Stretch.Uniform,
            Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Top,
        };
        Grid.SetColumn(thumb, 1);
        grid.Children.Add(thumb);

        if (r.Query is { } q) thumb.Tag = q; // stashed for the async pass

        return new Border
        {
            Background = (Brush)FindResource("Surface2Brush"),
            BorderBrush = (Brush)FindResource("AppBorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10),
            Margin = new Thickness(0, 0, 12, 0),
            Child = grid,
        };
    }

    private async System.Threading.Tasks.Task EnrichRefsAsync(DrawingExercise ex)
    {
        var thumbs = new List<(Image Image, string Query)>();
        CollectThumbImages(RefsHost, thumbs);
        for (var i = 0; i < ex.Refs.Count && i < thumbs.Count; i++)
        {
            var q = ex.Refs[i].Query;
            if (string.IsNullOrWhiteSpace(q)) continue;
            var hits = await _commons.SearchAsync(q, 1);
            if (hits.Count > 0 && hits[0].ImageUrl is { } url)
                thumbs[i].Image.Source = RemoteImage(url);
        }
    }

    private static void CollectThumbImages(Panel host, List<(Image, string)> into)
    {
        foreach (var child in host.Children)
            if (child is Border { Child: Grid g })
                foreach (var c in g.Children)
                    if (c is Image { Tag: string q } img)
                        into.Add((img, q));
    }

    private static BitmapImage? RemoteImage(string url)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(url);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch (System.Exception)
        {
            return null;
        }
    }

    private void UpdateProgressWidth()
    {
        var ratio = _total == 0 ? 0 : (double)_index / _total;
        var trackWidth = CartesRoot.ActualWidth > 0 ? CartesRoot.ActualWidth : SystemParameters.PrimaryScreenWidth;
        // The card has 20px padding each side inside a ~16px section margin.
        var inner = System.Math.Max(60, trackWidth - 72);
        ProgressFill.Width = inner * ratio;
    }

    private void PrevButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_index > 1) _index--;
        RenderCartes();
    }

    private void NextButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_index < _total) _index++;
        RenderCartes();
    }

    // ── Chrono ────────────────────────────────────────────────────────────────
    private void RenderChrono()
    {
        ChronoChips.Children.Clear();
        var series = ExerciseBank.ChronoSeries;
        for (var i = 0; i < series.Count; i++)
        {
            var done = i < _chronoIndex;
            var current = i == _chronoIndex;
            ChronoChips.Children.Add(new Border
            {
                Background = done || current
                    ? new SolidColorBrush(Color.FromArgb(0x29, 0x5B, 0x8C, 0xFF))
                    : (Brush)FindResource("Surface3Brush"),
                CornerRadius = new CornerRadius(999),
                Padding = new Thickness(10, 3, 10, 3),
                Margin = new Thickness(0, 0, 6, 0),
                Child = new TextBlock
                {
                    Text = done ? $"Croquis {series[i].N} ✓" : $"Croquis {series[i].N}",
                    FontSize = 12,
                    Foreground = (Brush)FindResource("TextBrush"),
                },
            });
        }
        ChronoSubjectText.Text = series[_chronoIndex % series.Count].Subject;
        ChronoTime.Text = $"{_chronoRemaining} s";
    }

    private void StartChronoIfNeeded()
    {
        if (_timer is not null) return;
        _chronoRemaining = ExerciseBank.ChronoSeries[_chronoIndex % ExerciseBank.ChronoSeries.Count].Seconds;
        _timer = new DispatcherTimer { Interval = System.TimeSpan.FromSeconds(1) };
        _timer.Tick += ChronoTick;
        _timer.Start();
        RenderChrono();
    }

    private void ChronoTick(object? sender, System.EventArgs e)
    {
        if (_chronoPaused) return;
        _chronoRemaining--;
        if (_chronoRemaining <= 0)
        {
            var series = ExerciseBank.ChronoSeries;
            _chronoIndex = (_chronoIndex + 1) % series.Count;
            _chronoRemaining = series[_chronoIndex].Seconds;
        }
        RenderChrono();
    }

    private void ChronoPause_OnClick(object sender, RoutedEventArgs e)
    {
        _chronoPaused = !_chronoPaused;
        ChronoPauseButton.Content = _chronoPaused ? "▶" : "⏸";
    }

    private void ChronoRestart_OnClick(object sender, RoutedEventArgs e)
    {
        _chronoIndex = 0;
        _chronoRemaining = ExerciseBank.ChronoSeries[0].Seconds;
        _chronoPaused = false;
        ChronoPauseButton.Content = "⏸";
        RenderChrono();
    }
}
