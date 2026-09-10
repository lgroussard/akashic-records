using System.Threading.Tasks;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using AkashicRecords.Infrastructure.Configuration;
using AkashicRecords.Infrastructure.Web;

namespace AkashicRecords.App.Views;

public partial class SettingsView : Window
{
    private readonly AppConfig _config;
    private readonly ConfigService _configService;

    // Set only via ForceClose() when the app is genuinely exiting; otherwise closing hides the window.
    private bool _isExiting;

    public SettingsView(AppConfig config, ConfigService configService)
    {
        InitializeComponent();
        _config = config;
        _configService = configService;

        // Pre-fill with whatever is already saved, so reopening settings doesn't wipe the fields.
        TmdbKeyInput.Text = _config.TmdbApiKey ?? string.Empty;
        RawgKeyInput.Text = _config.RawgApiKey ?? string.Empty;
    }

    private void SaveButton_OnClick(object sender, RoutedEventArgs e)
    {
        _config.TmdbApiKey = TmdbKeyInput.Text.Trim();
        _config.RawgApiKey = RawgKeyInput.Text.Trim();
        _configService.Save(_config);
        StatusText.Text = "Enregistré. Films/animation/anime : TMDB (si une clé est mise). Jeux vidéo : RAWG (si une clé est mise). Livres : Wikipédia.";
        StatusText.Foreground = System.Windows.Media.Brushes.ForestGreen;
    }

    private async void TestButton_OnClick(object sender, RoutedEventArgs e)
    {
        StatusText.Text = "Test de TMDB en cours…";
        StatusText.Foreground = System.Windows.Media.Brushes.LightGoldenrodYellow;

        if (string.IsNullOrWhiteSpace(_config.TmdbApiKey))
        {
            StatusText.Text = "Entrez d'abord une clé TMDB, puis testez.";
            StatusText.Foreground = System.Windows.Media.Brushes.OrangeRed;
            return;
        }

        var service = new ImageSearchService(_config.TmdbApiKey);
        var status = await service.TestTmdbAsync("Your Name", ImageSearchKind.Anime);

        if (status == "ok")
        {
            StatusText.Text = "TMDB fonctionne : une affiche a été trouvée. Les couvertures de films s'ajouteront automatiquement.";
            StatusText.Foreground = System.Windows.Media.Brushes.ForestGreen;
        }
        else if (status == "no-result")
        {
            StatusText.Text = "TMDB est actif mais n'a rien trouvé pour ce titre. Essayez un autre titre.";
            StatusText.Foreground = System.Windows.Media.Brushes.OrangeRed;
        }
        else
        {
            // status starts with "error:" — TMDB rejected the request (clé invalide, etc.).
            StatusText.Text = $"TMDB ne répond pas : {status}";
            StatusText.Foreground = System.Windows.Media.Brushes.OrangeRed;
        }
    }

    // Lets MainWindow truly close the window on real app exit.
    public void ForceClose()
    {
        _isExiting = true;
        Close();
    }

    // Closing (e.g. clicking the X) hides instead of tearing down, so it can be reopened.
    private void SettingsView_OnClosing(object? sender, CancelEventArgs e)
    {
        if (_isExiting) return;
        e.Cancel = true;
        Hide();
    }
}