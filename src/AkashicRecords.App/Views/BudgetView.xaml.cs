using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using AkashicRecords.Domain;
using AkashicRecords.Infrastructure.Configuration;
using AkashicRecords.Infrastructure.Persistence;
using AkashicRecords.Infrastructure.Budgeting;
using Microsoft.Win32;

namespace AkashicRecords.App.Views;

// Fifth section: budget. Transactions are imported from the user's own bank-statement exports
// (never fetched with credentials) or typed by hand. Categories carry an optional monthly envelope;
// keyword rules auto-tag rows at import. Plans are saved scenarios of envelopes. Amounts are signed
// (negative = money out). Every money parse/format is culture-invariant on purpose (the machine is
// fr-BE) — see the repo memory note on StatementImporter.
public partial class BudgetView : UserControl, ISearchNavigable
{
    private readonly BudgetTransactionRepository _txnRepo = new(new SqliteConnectionFactory());
    private readonly BudgetCategoryRepository _catRepo = new(new SqliteConnectionFactory());
    private readonly BudgetRuleRepository _ruleRepo = new(new SqliteConnectionFactory());
    private readonly BudgetPlanRepository _planRepo = new(new SqliteConnectionFactory());

    private readonly AppConfig _config;
    private readonly ConfigService _configService = new();

    private SearchResult? _pendingSearchResult;
    private int? _selectedPlanId;
    private string _txFilter = string.Empty;

    public BudgetView(AppConfig config)
    {
        InitializeComponent();
        _config = config;

        Loaded += (_, _) =>
        {
            RestorePersistedTab();
            RefreshAll();
            ApplyPendingSearchResult();
        };
    }

    // Reopen where the user left off (mirrors the other sections' persisted sub-tab). Yields to an
    // explicit navigation: a headless capture or a search deep-link drives its own tab, so the
    // restore must not override them.
    private void RestorePersistedTab()
    {
        if (_screenshotTabRequested || _pendingSearchResult is not null) return;
        if (_config.BudgetActiveTab is not { } tab) return;
        if (tab is not ("Overview" or "Transactions" or "Plans")) return;
        OverviewTab.IsChecked = tab == "Overview";
        TransactionsTab.IsChecked = tab == "Transactions";
        PlansTab.IsChecked = tab == "Plans";
        ShowTab(tab);
    }

    // =================================================================== tabs

    private void SubTab_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton btn) return;
        var tag = btn.Tag as string;
        _config.BudgetActiveTab = tag;
        _configService.Save(_config);
        OverviewTab.IsChecked = tag == "Overview";
        TransactionsTab.IsChecked = tag == "Transactions";
        PlansTab.IsChecked = tag == "Plans";
        ShowTab(tag);
        if (tag == "Overview") RefreshOverview();
        else if (tag == "Transactions") RefreshTransactions();
        else if (tag == "Plans") RefreshPlans();
    }

    private void ShowTab(string? tag)
    {
        OverviewRoot.Visibility = tag == "Overview" ? Visibility.Visible : Visibility.Collapsed;
        TransactionsRoot.Visibility = tag == "Transactions" ? Visibility.Visible : Visibility.Collapsed;
        PlansRoot.Visibility = tag == "Plans" ? Visibility.Visible : Visibility.Collapsed;
    }

    // Headless-capture entry (see MainWindow.OpenSectionForScreenshot).
    public void ShowTabForScreenshot(string tab)
    {
        _screenshotTabRequested = true; // suppresses the reopen-where-left restore so the capture is faithful
        var tag = tab switch { "Transactions" => "Transactions", "Plans" => "Plans", _ => "Overview" };
        OverviewTab.IsChecked = tag == "Overview";
        TransactionsTab.IsChecked = tag == "Transactions";
        PlansTab.IsChecked = tag == "Plans";
        ShowTab(tag);
        if (tag == "Overview") RefreshOverview();
        else if (tag == "Transactions") RefreshTransactions();
        else if (tag == "Plans") RefreshPlans();
    }

    private void RefreshAll()
    {
        if (OverviewTab.IsChecked == true) RefreshOverview();
        if (TransactionsTab.IsChecked == true) RefreshTransactions();
        if (PlansTab.IsChecked == true) RefreshPlans();
    }

    // =================================================================== overview

    private void RefreshOverview()
    {
        var months = _txnRepo.GetDistinctMonths().ToList();
        var prev = MonthPicker.SelectedItem as string;
        _suppressMonthEvent = true;
        MonthPicker.ItemsSource = months;
        // Keep the current selection; on the very first build, restore the persisted period instead.
        var want = prev ?? (_firstOverview ? _config.BudgetActivePeriod : null);
        var idx = want is not null ? months.IndexOf(want) : -1;
        MonthPicker.SelectedIndex = idx >= 0 ? idx : (months.Count > 0 ? 0 : -1);
        _suppressMonthEvent = false;
        _firstOverview = false;
        BuildPlanPicker();
        BuildOverview();
    }

    private bool _suppressMonthEvent;
    private bool _screenshotTabRequested;   // set by ShowTabForScreenshot; suppresses the reopen-where-left restore
    private bool _firstOverview = true;      // lets the very first build honor the persisted period

    private bool _suppressPlanEvent;                    // guards PlanPicker while its items are rebuilt
    private int? _overviewPlanId;                       // plan picked as the overview's planned-vs-actual reference
    private List<BudgetPlan> _planPickerPlans = new();  // parallel to PlanPicker rows (row 0 is the "(aucun)" sentinel)

    private void MonthPicker_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressMonthEvent) return;
        if (MonthPicker.SelectedItem is string m) _config.BudgetActivePeriod = m;
        _configService.Save(_config);
        BuildOverview();
    }

    private void RefreshOverviewButton_OnClick(object sender, RoutedEventArgs e) => BuildOverview();

    private void BuildOverview()
    {
        var month = MonthPicker.SelectedItem as string;
        var txs = _txnRepo.GetAll();
        var scoped = month is null
            ? txs
            : txs.Where(t => t.Date.ToString("yyyy-MM", CultureInfo.InvariantCulture) == month).ToList();

        decimal income = scoped.Where(t => t.Amount > 0).Sum(t => t.Amount);
        decimal expense = scoped.Where(t => t.Amount < 0).Sum(t => -t.Amount);
        decimal net = income - expense;

        FillSummaryCell(SummaryCell0, "REVENUS", Money(income), Color.FromRgb(0x4A, 0xDE, 0x80), 0);
        FillSummaryCell(SummaryCell1, "DÉPENSES", Money(expense), Color.FromRgb(0xFF, 0x7A, 0x8A), 16);
        FillSummaryCell(SummaryCell2, "SOLDE", Money(net), Color.FromRgb(0x5B, 0x8C, 0xFF), 16);

        CategoryBarsPanel.Children.Clear();
        var cats = _catRepo.GetAll();

        // When a plan is chosen for the overview, its envelopes become the reference caps so each bar reads
        // planned-vs-actual (and tips red on an over-spend), overriding the category's own standing cap.
        var planCaps = _overviewPlanId is { } pid
            ? _planRepo.GetLines(pid).Where(l => l.MonthlyAmount > 0).ToDictionary(l => l.CategoryId, l => l.MonthlyAmount)
            : null;

        // Spending per category (absolute of negative rows); "Non classé" gathers the rest.
        var totals = new List<(string name, string color, decimal spent, decimal? cap)>();
        foreach (var c in cats)
        {
            var spent = scoped.Where(t => t.CategoryId == c.Id && t.Amount < 0).Sum(t => -t.Amount);
            var cap = planCaps is not null && planCaps.TryGetValue(c.Id, out var pc) ? pc : c.MonthlyCap;
            totals.Add((c.Name, c.Color, spent, cap));
        }
        var uncat = scoped.Where(t => t.CategoryId is null && t.Amount < 0).Sum(t => -t.Amount);
        if (uncat > 0) totals.Add(("Non classé", "#6F6F8C", uncat, null));

        // Without a plan only categories that saw money show. With a plan, planned-but-not-yet-spent envelopes
        // still appear (at 0) so the gap is visible instead of hidden.
        var shown = totals.Where(t => t.spent > 0 || (planCaps is not null && t.cap > 0))
                          .OrderByDescending(t => t.spent).ToList();
        OverviewEmptyText.Visibility = shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var max = shown.Count > 0 ? shown.Max(t => t.spent) : 0m;
        foreach (var row in shown)
            CategoryBarsPanel.Children.Add(BuildCategoryBar(row.name, row.color, row.spent, row.cap, max));

        // Income breakdown (money-in), the mirror of the expense bars: a budget cannot be steered without
        // seeing where the money coming in comes from. Same plan reference drives the planned-vs-received caps.
        IncomeBarsPanel.Children.Clear();
        var incomePlanCaps = _overviewPlanId is { } ipid
            ? _planRepo.GetLines(ipid).Where(l => l.MonthlyAmount > 0).ToDictionary(l => l.CategoryId, l => l.MonthlyAmount)
            : null;
        var incomeTotals = new List<(string name, string color, decimal earned, decimal? cap)>();
        foreach (var c in cats)
        {
            var earned = scoped.Where(t => t.CategoryId == c.Id && t.Amount > 0).Sum(t => t.Amount);
            var icap = incomePlanCaps is not null && incomePlanCaps.TryGetValue(c.Id, out var ic) ? ic : (decimal?)null;
            incomeTotals.Add((c.Name, c.Color, earned, icap));
        }
        var uncatIn = scoped.Where(t => t.CategoryId is null && t.Amount > 0).Sum(t => t.Amount);
        if (uncatIn > 0) incomeTotals.Add(("Non classé", "#6F6F8C", uncatIn, null));
        var shownIn = incomeTotals.Where(t => t.earned > 0 || (incomePlanCaps is not null && t.cap > 0))
                                  .OrderByDescending(t => t.earned).ToList();
        IncomeEmptyText.Visibility = shownIn.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var maxIn = shownIn.Count > 0 ? shownIn.Max(t => t.earned) : 0m;
        foreach (var row in shownIn)
            IncomeBarsPanel.Children.Add(BuildCategoryBar(row.name, row.color, row.earned, row.cap, maxIn, overIsBad: false));

        BuildTrend();
    }

    private void FillSummaryCell(StackPanel cell, string label, string value, Color color, double leftPad)
    {
        cell.Children.Clear();
        cell.Margin = new Thickness(leftPad, 0, 0, 0);
        cell.Children.Add(new TextBlock
        {
            Text = label, Foreground = (Brush)FindResource("TextMutedBrush"), FontSize = 11, FontWeight = FontWeights.SemiBold
        });
        cell.Children.Add(new TextBlock
        {
            Text = value, Foreground = new SolidColorBrush(color), FontSize = 22, FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 2, 0, 0)
        });
    }

    // Measurement-free proportional bar: the filled cell takes `ratio` star, the track the remainder.
    // overIsBad: an over-spend over an expense envelope tips red (bad); an income beating its target tips
    // green (good). Same bar geometry, opposite semantics on the overflow colour.
    private FrameworkElement BuildCategoryBar(string name, string color, decimal spent, decimal? cap, decimal max, bool overIsBad = true)
    {
        var outer = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        head.Children.Add(new TextBlock
        {
            Text = name, Foreground = (Brush)FindResource("TextBrush"), FontSize = 12, FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis
        });
        Grid.SetColumn(head.Children[0], 0);

        var capSuffix = cap is { } c ? "  /  " + Money(c) : string.Empty;
        var amountText = new TextBlock
        {
            Text = Money(spent) + capSuffix, FontSize = 11,
            Foreground = cap is not null && spent > cap
                ? new SolidColorBrush(overIsBad ? Color.FromRgb(0xFF, 0x7A, 0x8A) : Color.FromRgb(0x4A, 0xDE, 0x80))
                : (Brush)FindResource("TextMutedBrush")
        };
        Grid.SetColumn(amountText, 1);
        head.Children.Add(amountText);
        outer.Children.Add(head);

        var ratio = max > 0 ? (double)Math.Min(1m, Math.Max(0m, spent / max)) : 0d;
        var track = new Grid { Height = 7, Margin = new Thickness(0, 4, 0, 0) };
        track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ratio <= 0 ? 0.0001 : ratio, GridUnitType.Star) });
        track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ratio >= 1 ? 0.0001 : 1 - ratio, GridUnitType.Star) });
        track.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)), CornerRadius = new CornerRadius(4)
        });
        Grid.SetColumn(track.Children[0], 0);
        Grid.SetColumnSpan(track.Children[0], 2);
        var fill = new Border { Background = BrushFromHex(color), CornerRadius = new CornerRadius(4) };
        Grid.SetColumn(fill, 0);
        track.Children.Add(fill);
        outer.Children.Add(track);
        return outer;
    }

    // =================================================================== auto-classification (built-in merchant map)

    // Fills the category of every still-uncategorised transaction whose label matches one of the built-in
    // merchant seeds, creating the implied category on first use. CategorizeMatching only touches NULL rows,
    // so a manual choice or a user-defined rule always wins. Returns how many rows newly got a category.
    private int ApplyDefaultClassification()
    {
        var cats = _catRepo.GetAll().ToList();
        var nextSort = cats.Count == 0 ? 0 : cats.Max(c => c.SortOrder) + 1;
        var system = new List<(string, int, bool?)>();
        foreach (var seed in BudgetDefaults.Classification)
        {
            var cat = cats.FirstOrDefault(c => c.Name.Equals(seed.Category, StringComparison.OrdinalIgnoreCase));
            if (cat is null)
            {
                var id = _catRepo.Add(new BudgetCategory { Name = seed.Category, Color = seed.Color, MonthlyCap = seed.Cap, SortOrder = nextSort++ });
                cat = new BudgetCategory { Id = id, Name = seed.Category, Color = seed.Color, MonthlyCap = seed.Cap };
                cats.Add(cat);
            }
            // Expense seeds may only tag money-out rows, income seeds only money-in — a merchant can
            // never sweep a salary line into its bucket, and vice versa.
            system.Add((seed.Keyword, cat.Id, seed.Income ? true : false));
        }
        // User rules are the higher tier (gate = null: they match either sign, the user picked a
        // sign-appropriate category themselves). The whole set goes in one CategorizeAll pass so
        // "most specific keyword wins" is decided across every rule, not by call order.
        var user = new List<(string, int, bool?)>();
        foreach (var rule in _ruleRepo.GetAll())
            user.Add((rule.Keyword, rule.CategoryId, null));
        return _txnRepo.CategorizeAll(user, system);
    }

    private void AutoClassifyButton_OnClick(object sender, RoutedEventArgs e)
    {
        var n = ApplyDefaultClassification();
        ShowImportStatus(
            n > 0 ? $"{n} opération(s) classée(s) d'aprés le nom du marchand."
                  : "Rien de nouveau à classer — les marchands connus sont déja tous rangés.",
            isError: false);
        RefreshAll();
    }

    // =================================================================== plan-vs-real overlay (overview)

    private void BuildPlanPicker()
    {
        _planPickerPlans = _planRepo.GetAll().ToList();
        _suppressPlanEvent = true;
        if (_planPickerPlans.Count == 0)
        {
            PlanPicker.ItemsSource = new List<string> { "(aucun)" };
            PlanPicker.SelectedIndex = 0;
            _overviewPlanId = null;
        }
        else
        {
            var names = new List<string> { "(aucun)" };
            names.AddRange(_planPickerPlans.Select(p => p.Name));
            PlanPicker.ItemsSource = names;
            var idx = -1;
            if (_overviewPlanId is { } id)
                for (var i = 0; i < _planPickerPlans.Count; i++)
                    if (_planPickerPlans[i].Id == id) { idx = i; break; }
            PlanPicker.SelectedIndex = idx >= 0 ? idx + 1 : 0;
            if (idx < 0) _overviewPlanId = null;
        }
        _suppressPlanEvent = false;
    }

    private void PlanPicker_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressPlanEvent) return;
        var idx = PlanPicker.SelectedIndex;
        _overviewPlanId = idx >= 1 && idx - 1 < _planPickerPlans.Count ? _planPickerPlans[idx - 1].Id : null;
        BuildOverview();
    }

    // =================================================================== monthly expense trend (overview)

    // A tiny column chart of the last months' total spend (oldest at the left, newest at the right), to spot
    // a category — or the whole budget — ballooning month over month. Hidden until two months exist.
    private void BuildTrend()
    {
        TrendPanel.Children.Clear();
        var months = _txnRepo.GetDistinctMonths().ToList(); // newest first
        if (months.Count < 2)
        {
            TrendEmptyText.Visibility = Visibility.Visible;
            return;
        }
        var recent = months.Take(6).ToList();
        recent.Reverse(); // oldest first so the timeline reads left → right
        var txs = _txnRepo.GetAll();
        var series = recent.Select(m => (
            month: m,
            expense: txs.Where(t => t.Date.ToString("yyyy-MM", CultureInfo.InvariantCulture) == m && t.Amount < 0).Sum(t => -t.Amount),
            income: txs.Where(t => t.Date.ToString("yyyy-MM", CultureInfo.InvariantCulture) == m && t.Amount > 0).Sum(t => t.Amount))).ToList();
        var max = series.Count > 0 ? Math.Max(series.Max(s => s.expense), series.Max(s => s.income)) : 0m;
        if (max <= 0)
        {
            TrendEmptyText.Visibility = Visibility.Visible;
            return;
        }
        TrendEmptyText.Visibility = Visibility.Collapsed;

        // Legend: a red swatch = spend, a green swatch = income, so the paired columns read without a key.
        var legend = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 4) };
        legend.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 16, 0), Children = { new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(2), VerticalAlignment = VerticalAlignment.Center, Background = new SolidColorBrush(Color.FromRgb(0xFF, 0x7A, 0x8A)) }, new TextBlock { Text = "Dépenses", FontSize = 10, Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = (Brush)FindResource("TextMutedBrush") } } });
        legend.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(2), VerticalAlignment = VerticalAlignment.Center, Background = new SolidColorBrush(Color.FromRgb(0x4A, 0xDE, 0x80)) }, new TextBlock { Text = "Revenus", FontSize = 10, Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = (Brush)FindResource("TextMutedBrush") } } });

        var bars = new Grid { Height = 74, Margin = new Thickness(0, 4, 0, 0) };
        var labels = new Grid { Margin = new Thickness(0, 2, 0, 0) };
        for (var i = 0; i < series.Count; i++)
        {
            // Two narrow columns per month: expense bar then income bar, sharing one month label underneath.
            bars.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bars.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            labels.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });

            var lastMonth = i == series.Count - 1;
            var eh = Math.Max(3.0, (double)(series[i].expense / max) * 62.0);
            var ebar = new Border
            {
                Height = eh, Width = 15, CornerRadius = new CornerRadius(3),
                VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Center,
                Background = lastMonth ? new SolidColorBrush(Color.FromRgb(0xFF, 0x7A, 0x8A)) : new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0x7A, 0x8A))
            };
            Grid.SetColumn(ebar, i * 2);
            bars.Children.Add(ebar);

            var ih = Math.Max(3.0, (double)(series[i].income / max) * 62.0);
            var ibar = new Border
            {
                Height = ih, Width = 15, CornerRadius = new CornerRadius(3),
                VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Center,
                Background = lastMonth ? new SolidColorBrush(Color.FromRgb(0x4A, 0xDE, 0x80)) : new SolidColorBrush(Color.FromArgb(0x80, 0x4A, 0xDE, 0x80))
            };
            Grid.SetColumn(ibar, i * 2 + 1);
            bars.Children.Add(ibar);

            var label = new TextBlock
            {
                Text = series[i].month[2..4] + "/" + series[i].month[5..], FontSize = 10,
                HorizontalAlignment = HorizontalAlignment.Center, Foreground = (Brush)FindResource("TextMutedBrush")
            };
            Grid.SetColumn(label, i * 2);
            Grid.SetColumnSpan(label, 2);
            labels.Children.Add(label);
        }
        TrendPanel.Children.Add(legend);
        TrendPanel.Children.Add(bars);
        TrendPanel.Children.Add(labels);
    }

    // =================================================================== import

    // The bank's own "Se connecter" entry point (Hello bank! France / BNP Paribas). This stable link is
    // used deliberately instead of the connexion.hellobank.fr oauth2 deep link: that one carries a single-use
    // state + nonce minted per browsing session, so hardcoding it would fail on the next launch, whereas the
    // server mints fresh ones on every redirect. Opening the browser is the one manual step of the loop - the
    // statement has to be exported from the bank's own web app (no public data API), then handed back below.
    private const string BankSiteUrl = "https://www.hellobank.fr/fr/client?requiredDAC=3";

    private void OpenBankSiteButton_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(BankSiteUrl) { UseShellExecute = true });
        }
        catch
        {
            ShowImportStatus("Impossible d'ouvrir le site de la banque — le lien est " + BankSiteUrl, isError: true);
        }
    }

    private void ImportButton_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Exports bancaires|*.csv;*.txt;*.ofx;*.pdf|Tous les fichiers|*.*" };
        if (dialog.ShowDialog() != true) return;

        var parse = new StatementImporter().ParseFile(dialog.FileName);
        if (!parse.Success)
        {
            ShowImportStatus("Import échoué : " + parse.Error, isError: true);
            return;
        }

        var source = Path.GetFileName(dialog.FileName);
        int added = 0, dupes = 0;
        foreach (var row in parse.Rows)
        {
            var ext = StatementImporter.ExternalIdOf(row);
            if (_txnRepo.ExistsByExternalId(ext)) { dupes++; continue; }
            _txnRepo.Add(new BudgetTransaction
            {
                Date = row.Date, Label = row.Label, Amount = row.Amount,
                Source = source, ExternalId = ext, ImportedAt = DateTime.Now
            });
            added++;
        }

        // Classify the fresh rows in one managed pass: the user's own rules first (higher tier), then the
        // built-in merchant/income seeds fill the rest. Both go through CategorizeAll so accent folding,
        // whole-word boundaries and "most specific keyword wins" apply uniformly; only still-NULL rows move.
        ApplyDefaultClassification();

        var cols = parse.DelimiterName == "pdf"
            ? "lecture géométrique du PDF"
            : string.Join(" · ", new[]
            {
                "colonnes : " + (parse.DateColumn ?? "—") + " / " + (parse.LabelColumn ?? "—") + " / "
                    + (parse.AmountColumn ?? (parse.DebitColumn is not null || parse.CreditColumn is not null ? "débit·crédit" : "—")),
                "séparateur : " + parse.DelimiterName,
                parse.AmountIsInCents ? "montants en centimes (÷100)" : null
            }.Where(s => s is not null));

        ShowImportStatus($"{added} opération(s) importée(s), {dupes} doublon(s) ignoré(s) — {cols}", isError: false);
        RefreshAll();
    }

    private void ShowImportStatus(string text, bool isError)
    {
        ImportStatus.Text = text;
        ImportStatus.Foreground = isError
            ? new SolidColorBrush(Color.FromRgb(0xFF, 0x7A, 0x8A))
            : new SolidColorBrush(Color.FromRgb(0x4A, 0xDE, 0x80));
        ImportStatus.Visibility = Visibility.Visible;
    }

    // =================================================================== transactions tab

    private void TxSearch_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        _txFilter = TxSearch.Text.Trim();
        RefreshTransactions();
    }

    private void RefreshTransactions()
    {
        var all = _txnRepo.GetAll();
        var filtered = string.IsNullOrEmpty(_txFilter)
            ? all
            : all.Where(t => t.Label.Contains(_txFilter, StringComparison.OrdinalIgnoreCase)
                            || t.Amount.ToString(CultureInfo.InvariantCulture).Contains(_txFilter)).ToList();

        var cats = _catRepo.GetAll().ToDictionary(c => c.Id);
        TxListPanel.Children.Clear();
        TxEmptyText.Visibility = filtered.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var t in filtered.Take(500))
            TxListPanel.Children.Add(BuildTransactionRow(t, cats));
    }

    // One row: date | label | category picker | amount | delete. The category picker is a plain
    // string ComboBox (a "—" sentinel at index 0) with a parallel id list — string items render in
    // the closed box without needing DisplayMemberPath (see repo memory).
    private FrameworkElement BuildTransactionRow(BudgetTransaction t, Dictionary<int, BudgetCategory> cats)
    {
        var row = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x0C, 0xFF, 0xFF, 0xFF)),
            CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 6, 8, 6), Margin = new Thickness(0, 0, 0, 4)
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(66, GridUnitType.Pixel) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150, GridUnitType.Pixel) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(92, GridUnitType.Pixel) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        grid.Children.Add(new TextBlock
        {
            Text = t.Date.ToString("dd/MM/yy", CultureInfo.InvariantCulture),
            Foreground = (Brush)FindResource("TextMutedBrush"), FontSize = 11, VerticalAlignment = VerticalAlignment.Center
        });
        Grid.SetColumn(grid.Children[0], 0);

        grid.Children.Add(new TextBlock
        {
            Text = t.Label, Foreground = (Brush)FindResource("TextBrush"), FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(8, 0, 8, 0), ToolTip = t.Source
        });
        Grid.SetColumn(grid.Children[1], 1);

        // category picker
        var names = new List<string> { "— Non classé" };
        var ids = new List<int?> { null };
        int selected = 0;
        foreach (var c in _catRepo.GetAll())
        {
            names.Add(c.Name); ids.Add(c.Id);
            if (t.CategoryId == c.Id) selected = names.Count - 1;
        }
        var picker = new ComboBox { ItemsSource = names, SelectedIndex = selected, Style = (Style)FindResource("DarkComboBoxStyle"), FontSize = 11 };
        var txId = t.Id;
        picker.SelectionChanged += (_, _) => { if (picker.SelectedIndex >= 0) _txnRepo.SetCategory(txId, ids[picker.SelectedIndex]); };
        Grid.SetColumn(picker, 2);
        grid.Children.Add(picker);

        grid.Children.Add(new TextBlock
        {
            Text = Money(t.Amount), FontSize = 12, FontWeight = FontWeights.Bold, TextAlignment = TextAlignment.Right,
            Foreground = t.Amount < 0 ? new SolidColorBrush(Color.FromRgb(0xFF, 0x7A, 0x8A)) : new SolidColorBrush(Color.FromRgb(0x4A, 0xDE, 0x80)),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0)
        });
        Grid.SetColumn(grid.Children[3], 3);

        var del = new Button { Content = "✕", Width = 24, Height = 24, Padding = new Thickness(0), Style = (Style)FindResource("SecondaryButtonStyle"), ToolTip = "Supprimer" };
        var delId = t.Id;
        del.Click += (_, _) => { _txnRepo.Delete(delId); RefreshAll(); };
        Grid.SetColumn(del, 4);
        grid.Children.Add(del);

        row.Child = grid;
        return row;
    }

    private void ManualTxButton_OnClick(object sender, RoutedEventArgs e)
    {
        var window = new Window
        {
            Title = "Nouvelle opération", Width = 360, Height = 300, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterScreen, Owner = Window.GetWindow(this)
        };
        var dateBox = new TextBox { Text = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), Margin = new Thickness(0, 0, 0, 8) };
        var labelBox = new TextBox { Margin = new Thickness(0, 0, 0, 8) };
        var amountBox = new TextBox { Margin = new Thickness(0, 0, 0, 12) };
        var ok = new Button { Content = "Ajouter", Width = 90, IsDefault = true };
        ok.Click += (_, _) => window.DialogResult = true;
        var cancel = new Button { Content = "Annuler", Width = 90, Margin = new Thickness(8, 0, 0, 0), IsCancel = true };

        window.Content = new StackPanel
        {
            Margin = new Thickness(16),
            Children =
            {
                new TextBlock { Text = "Date (AAAA-MM-JJ)", Foreground = (Brush)FindResource("TextMutedBrush"), FontSize = 11, Margin = new Thickness(0, 0, 0, 2) },
                dateBox,
                new TextBlock { Text = "Libellé", Foreground = (Brush)FindResource("TextMutedBrush"), FontSize = 11, Margin = new Thickness(0, 0, 0, 2) },
                labelBox,
                new TextBlock { Text = "Montant (négatif = dépense, ex. -45,90)", Foreground = (Brush)FindResource("TextMutedBrush"), FontSize = 11, Margin = new Thickness(0, 0, 0, 2) },
                amountBox,
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok, cancel } }
            }
        };
        labelBox.Focus();
        if (window.ShowDialog() != true) return;

        var label = labelBox.Text.Trim();
        if (label.Length == 0 || !TryDateIso(dateBox.Text, out var date) || !TryMoney(amountBox.Text, out var amount))
            return;

        _txnRepo.Add(new BudgetTransaction
        {
            Date = date, Label = label, Amount = amount, Source = "manuel", ExternalId = null, ImportedAt = DateTime.Now
        });
        foreach (var rule in _ruleRepo.GetAll())
            _txnRepo.CategorizeMatching(rule.Keyword, rule.CategoryId);
        ShowTab("Transactions");
        TransactionsTab.IsChecked = true; OverviewTab.IsChecked = false; PlansTab.IsChecked = false;
        RefreshAll();
    }

    // =================================================================== rules

    private void AddRuleButton_OnClick(object sender, RoutedEventArgs e)
    {
        var keyword = RuleKeywordInput.Text.Trim();
        if (keyword.Length == 0) return;
        var catId = ChooseCategory("Catégorie pour « " + keyword + " »");
        if (catId is not { } id) return;
        _ruleRepo.Add(new BudgetRule { Keyword = keyword, CategoryId = id });
        _txnRepo.CategorizeMatching(keyword, id); // apply to the whole back-catalog now
        RuleKeywordInput.Text = string.Empty;
        RefreshTransactions();
        if (OverviewTab.IsChecked == true) RefreshOverview();
    }

    private void RefreshRules()
    {
        var rules = _ruleRepo.GetAll();
        var cats = _catRepo.GetAll().ToDictionary(c => c.Id);
        RuleListPanel.Children.Clear();
        RuleEmptyText.Visibility = rules.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var r in rules)
        {
            var catName = cats.TryGetValue(r.CategoryId, out var c) ? c.Name : "?";
            var row = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0x0C, 0xFF, 0xFF, 0xFF)),
                CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 5, 8, 5), Margin = new Thickness(0, 0, 0, 4)
            };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(new TextBlock
            {
                Text = r.Keyword + "  →  " + catName, Foreground = (Brush)FindResource("TextBrush"), FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis
            });
            Grid.SetColumn(grid.Children[0], 0);
            var rid = r.Id;
            var del = new Button { Content = "✕", Width = 24, Height = 24, Padding = new Thickness(0), Style = (Style)FindResource("SecondaryButtonStyle") };
            del.Click += (_, _) => { _ruleRepo.Delete(rid); RefreshRules(); };
            Grid.SetColumn(del, 1);
            grid.Children.Add(del);
            row.Child = grid;
            RuleListPanel.Children.Add(row);
        }
    }

    // =================================================================== plans

    private void NewPlanButton_OnClick(object sender, RoutedEventArgs e)
    {
        var name = PromptForText("Nouveau plan", "Nom du plan", string.Empty);
        if (string.IsNullOrWhiteSpace(name)) return;
        _planRepo.Add(new BudgetPlan { Name = name.Trim(), CreatedAt = DateTime.Now });
        RefreshPlans();
    }

    private void RefreshPlans()
    {
        RefreshRules(); // rules live on the transactions tab but share the category cache
        var plans = _planRepo.GetAll();
        PlanListPanel.Children.Clear();
        PlanEmptyText.Visibility = plans.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_selectedPlanId is { } sid && !plans.Any(p => p.Id == sid)) _selectedPlanId = null;

        foreach (var p in plans)
        {
            var pid = p.Id;
            var selected = _selectedPlanId == pid;
            var row = new Button
            {
                Content = p.Name, Style = (Style)FindResource(selected ? "ChipButtonStyle" : "SecondaryButtonStyle"),
                Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 0, 6),
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            row.Click += (_, _) => { _selectedPlanId = pid; RefreshPlans(); };
            PlanListPanel.Children.Add(row);
        }
        BuildPlanDetail();
    }

    private void BuildPlanDetail()
    {
        PlanLinesPanel.Children.Clear();
        var plan = _selectedPlanId is { } pid ? _planRepo.GetAll().FirstOrDefault(x => x.Id == pid) : null;
        if (plan is null)
        {
            PlanDetailTitle.Text = "Choisissez un plan";
            RenamePlanButton.Visibility = DeletePlanButton.Visibility = Visibility.Collapsed;
            PlanLinesEmptyText.Visibility = Visibility.Collapsed;
            return;
        }
        PlanDetailTitle.Text = plan.Name;
        RenamePlanButton.Visibility = DeletePlanButton.Visibility = Visibility.Visible;

        var lines = _planRepo.GetLines(plan.Id).ToDictionary(l => l.CategoryId, l => l.MonthlyAmount);
        var cats = _catRepo.GetAll();
        PlanLinesEmptyText.Visibility = cats.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var c in cats)
        {
            var amount = lines.TryGetValue(c.Id, out var a) ? a : 0m;
            var row = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120, GridUnitType.Pixel) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            row.Children.Add(new TextBlock
            {
                Text = c.Name, Foreground = (Brush)FindResource("TextBrush"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center
            });
            Grid.SetColumn(row.Children[0], 0);

            var input = new TextBox
            {
                Text = amount == 0 ? string.Empty : amount.ToString("0.00", CultureInfo.InvariantCulture),
                Width = 110, Margin = new Thickness(0, 0, 8, 0), VerticalContentAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(input, 1);
            row.Children.Add(input);

            var catId = c.Id; var planId = plan.Id;
            var apply = new Button { Content = "✓", Width = 26, Height = 26, Padding = new Thickness(0), Style = (Style)FindResource("SecondaryButtonStyle") };
            apply.Click += (_, _) =>
            {
                var ok = TryMoney(input.Text, out var val);
                _planRepo.SetLine(planId, catId, ok ? val : 0m);
                BuildPlanDetail();
            };
            Grid.SetColumn(apply, 2);
            row.Children.Add(apply);

            PlanLinesPanel.Children.Add(row);
        }
    }

    private void RenamePlanButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_selectedPlanId is not { } pid) return;
        var plan = _planRepo.GetAll().FirstOrDefault(x => x.Id == pid);
        if (plan is null) return;
        var name = PromptForText("Renomer le plan", "Nom du plan", plan.Name);
        if (string.IsNullOrWhiteSpace(name)) return;
        _planRepo.Update(pid, name.Trim(), plan.Notes);
        RefreshPlans();
    }

    private void DeletePlanButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_selectedPlanId is not { } pid) return;
        _planRepo.Delete(pid);
        _selectedPlanId = null;
        RefreshPlans();
    }

    // =================================================================== search deep-link

    public void NavigateToSearchResult(SearchResult result)
    {
        if (IsLoaded) ApplySearchResult(result);
        else _pendingSearchResult = result;
    }

    private void ApplyPendingSearchResult()
    {
        if (_pendingSearchResult is { } result)
        {
            _pendingSearchResult = null;
            ApplySearchResult(result);
        }
    }

    private void ApplySearchResult(SearchResult result)
    {
        TransactionsTab.IsChecked = true; OverviewTab.IsChecked = false; PlansTab.IsChecked = false;
        ShowTab("Transactions");
        TxSearch.Text = string.Empty; _txFilter = string.Empty;
        RefreshTransactions();
    }

    // =================================================================== shared helpers

    private static SolidColorBrush BrushFromHex(string hex)
    {
        var s = (hex ?? string.Empty).Trim();
        if (s.StartsWith("#")) s = s[1..];
        if (s.Length == 6 && int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v))
            return new SolidColorBrush(Color.FromArgb(0xFF, (byte)((v >> 16) & 0xFF), (byte)((v >> 8) & 0xFF), (byte)(v & 0xFF)));
        return new SolidColorBrush(Color.FromRgb(0x5B, 0x8C, 0xFF));
    }

    // Invariant culture on purpose (machine is fr-BE). Thousands grouped with a narrow nbsp.
    private static string Money(decimal v)
    {
        var sign = v < 0 ? "−" : string.Empty;
        var abs = Math.Abs(v).ToString("0.00", CultureInfo.InvariantCulture);
        var dot = abs.LastIndexOf('.');
        var intPart = dot >= 0 ? abs[..dot] : abs;
        var frac = dot >= 0 ? abs[dot..] : string.Empty;
        var grouped = new StringBuilder();
        var count = 0;
        for (var i = intPart.Length - 1; i >= 0; i--)
        {
            grouped.Insert(0, intPart[i]);
            if (++count % 3 == 0 && i > 0) grouped.Insert(0, '\u202F');
        }
        return $"{sign}{grouped}{frac} €";
    }

    // Accepts FR/BE style money ("1.234,56", "-45,9", "3 500,00") or plain ("1234.56").
    private static bool TryMoney(string raw, out decimal value)
    {
        value = 0m;
        var s = (raw ?? string.Empty).Trim()
            .Replace("\u00A0", string.Empty).Replace("\u202F", string.Empty).Replace(" ", string.Empty);
        if (s.Length == 0) return false;
        var neg = s.StartsWith("-") || (s.StartsWith("(") && s.EndsWith(")"));
        s = s.Trim('(', ')', '-', '+');
        var lastComma = s.LastIndexOf(',');
        var lastDot = s.LastIndexOf('.');
        if (lastComma >= 0 && lastDot >= 0)
            s = lastComma > lastDot ? s.Replace(".", string.Empty).Replace(',', '.') : s.Replace(",", string.Empty);
        else if (lastComma >= 0)
            s = (s.Length - lastComma - 1) <= 2 ? s.Replace(',', '.') : s.Replace(",", string.Empty);
        else if (lastDot >= 0 && (s.Length - lastDot - 1) > 2)
            s = s.Replace(".", string.Empty);
        if (!decimal.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) return false;
        if (neg) value = -Math.Abs(value);
        return true;
    }

    private static bool TryDateIso(string raw, out DateTime date) =>
        DateTime.TryParse(raw.Trim(), CultureInfo.InvariantCulture, out date);

    private string? PromptForText(string title, string label, string initial)
    {
        var window = new Window
        {
            Title = title, Width = 320, Height = 150, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterScreen, Owner = Window.GetWindow(this)
        };
        var textBox = new TextBox { Text = initial, Margin = new Thickness(0, 0, 0, 8) };
        var ok = new Button { Content = "OK", Width = 80, IsDefault = true };
        ok.Click += (_, _) => window.DialogResult = true;
        var cancel = new Button { Content = "Annuler", Width = 80, Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
        window.Content = new StackPanel
        {
            Margin = new Thickness(16),
            Children =
            {
                new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 6) },
                textBox,
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok, cancel } }
            }
        };
        textBox.Focus();
        return window.ShowDialog() == true ? textBox.Text : null;
    }

    private int? ChooseCategory(string title)
    {
        var cats = _catRepo.GetAll();
        if (cats.Count == 0) return null;
        var window = new Window
        {
            Title = title, Width = 320, Height = 170, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterScreen, Owner = Window.GetWindow(this)
        };
        var names = cats.Select(c => c.Name).ToList();
        var combo = new ComboBox { ItemsSource = names, SelectedIndex = 0, Style = (Style)FindResource("DarkComboBoxStyle"), Margin = new Thickness(0, 0, 0, 12) };
        var ok = new Button { Content = "OK", Width = 80, IsDefault = true };
        ok.Click += (_, _) => window.DialogResult = true;
        var cancel = new Button { Content = "Annuler", Width = 80, Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
        window.Content = new StackPanel
        {
            Margin = new Thickness(16),
            Children =
            {
                new TextBlock { Text = "Catégorie", Foreground = (Brush)FindResource("TextMutedBrush"), FontSize = 11, Margin = new Thickness(0, 0, 0, 4) },
                combo,
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok, cancel } }
            }
        };
        if (window.ShowDialog() == true && combo.SelectedIndex >= 0) return cats[combo.SelectedIndex].Id;
        return null;
    }
}
