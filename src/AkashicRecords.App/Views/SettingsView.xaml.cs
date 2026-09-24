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

    private async void TestTmdbButton_OnClick(object sender, RoutedEventArgs e)
    {
        var key = TmdbKeyInput.Text.Trim();
        if (key.Length == 0)
        {
            TmdbStatusText.Text = "TMDB : entrez d'abord une clé.";
            TmdbStatusText.Foreground = System.Windows.Media.Brushes.OrangeRed;
            return;
        }

        TestTmdbButton.IsEnabled = false;
        TmdbStatusText.Text = "TMDB : test en cours…";
        TmdbStatusText.Foreground = System.Windows.Media.Brushes.LightGoldenrodYellow;
        try
        {
            // Tests the TMDB path in isolation (no Wikipedia fallback) with a real download,
            // reading the field, so it verifies what is typed and not only what was saved.
            var status = await new ImageSearchService(key).TestImageLookupAsync("Inception", ImageSearchKind.Film);
            ApplyTestStatus(TmdbStatusText, status, "TMDB");
        }
        finally
        {
            TestTmdbButton.IsEnabled = true;
        }
    }

    private async void TestRawgButton_OnClick(object sender, RoutedEventArgs e)
    {
        var key = RawgKeyInput.Text.Trim();
        if (key.Length == 0)
        {
            RawgStatusText.Text = "RAWG : entrez d'abord une clé.";
            RawgStatusText.Foreground = System.Windows.Media.Brushes.OrangeRed;
            return;
        }

        TestRawgButton.IsEnabled = false;
        RawgStatusText.Text = "RAWG : test en cours…";
        RawgStatusText.Foreground = System.Windows.Media.Brushes.LightGoldenrodYellow;
        try
        {
            var status = await new ImageSearchService(null, key).TestImageLookupAsync("Celeste", ImageSearchKind.VideoGame);
            ApplyTestStatus(RawgStatusText, status, "RAWG");
        }
        finally
        {
            TestRawgButton.IsEnabled = true;
        }
    }

    // Shared rendering for the two API tests. The service returns "ok" | "no-result" | "error: ...";
    // "ok" already proves a real image was downloaded, so it is the only green state.
    private static void ApplyTestStatus(TextBlock target, string status, string api)
    {
        switch (status)
        {
            case "ok":
                target.Text = $"{api} : OK — une couverture réelle a été téléchargée, la clé fonctionne.";
                target.Foreground = System.Windows.Media.Brushes.ForestGreen;
                break;
            case "no-result":
                target.Text = $"{api} : clé acceptée mais AUCUNE couverture trouvée pour le titre d'essai.";
                target.Foreground = System.Windows.Media.Brushes.OrangeRed;
                break;
            default:
                target.Text = $"{api} : {status}";
                target.Foreground = System.Windows.Media.Brushes.OrangeRed;
                break;
        }
    }

    // The window has WindowStyle="None" (no OS chrome), so its only close affordance is the pinned
    // top-right x. Matches the hide-on-close behaviour of SettingsView_OnClosing.
    private void CloseButton_OnClick(object sender, RoutedEventArgs e) => Hide();

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