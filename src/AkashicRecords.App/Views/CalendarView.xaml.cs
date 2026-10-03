using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using AkashicRecords.Domain;
using AkashicRecords.Infrastructure.Configuration;
using AkashicRecords.Infrastructure.Persistence;

namespace AkashicRecords.App.Views;

// Fourth-plus section (§57): the calendar. Aggregates project deadlines (read-only — editing
// stays in Organisation), birthdays of loved ones (new) and outings/plans (new), over a month
// grid with per-source colored dots. Lead-time notifications are configured here but fired by
// MainWindow (the view owns presentation, not scheduling).
public partial class CalendarView : UserControl
{
    private readonly BirthdayRepository _birthdayRepo = new(new SqliteConnectionFactory());
    private readonly CalendarEventRepository _eventRepo = new(new SqliteConnectionFactory());
    private readonly CalendarService _calendarService = new(new SqliteConnectionFactory());
    private readonly CalendarEventLinkRepository _linkRepo = new(new SqliteConnectionFactory());
    private readonly GlobalSearchService _searchService = new(new SqliteConnectionFactory());

    // Working copy of the links for the calendar entry currently open in the form (saved on
    // Enregistrer). Owner type comes from _mode; both forms share one link row in the DB.
    private List<CalendarEventLink> _workingLinks = new();

    private readonly AppConfig _config;
    private readonly ConfigService _configService = new();
    private readonly Action? _notifySettingsChanged;

    private DateTime _selectedDate = DateTime.Today;

    private enum FormMode { None, NewBirthday, EditBirthday, NewEvent, EditEvent }
    private FormMode _mode = FormMode.None;
    private int _editingId;

    // Mirrors the XAML palette (UserControl.Resources) so code-built rows and the day dots share
    // one source of truth without resource lookups per row.
    private static readonly Color BirthdayColor = Color.FromRgb(0xF0, 0x6C, 0xA8);
    private static readonly Color SortieColor = Color.FromRgb(0xE0, 0x82, 0x3C);
    private static readonly Color PlanColor = Color.FromRgb(0x4A, 0xDE, 0x80);
    private static readonly Color VoyageColor = Color.FromRgb(0x38, 0xBD, 0xF8);
    private static readonly Color RendezvousColor = Color.FromRgb(0xC0, 0x84, 0xFC);
    private static readonly Color AutreColor = Color.FromRgb(0x94, 0xA3, 0xB8);
    private static readonly Color DeadlineColor = Color.FromRgb(0xF2, 0xC1, 0x4E);

    private sealed record KindOption(CalendarEventKind Kind, string Display);

    public CalendarView(AppConfig config, Action? notifySettingsChanged = null)
    {
        InitializeComponent();
        _config = config;
        _notifySettingsChanged = notifySettingsChanged;

        EventKindPicker.ItemsSource = new List<KindOption>
        {
            new(CalendarEventKind.Sortie, "Sortie"),
            new(CalendarEventKind.Plan, "Plan"),
            new(CalendarEventKind.Voyage, "Voyage"),
            new(CalendarEventKind.Rendezvous, "Rendez-vous"),
            new(CalendarEventKind.Autre, "Autre"),
        };
        EventKindPicker.SelectedIndex = 0;

        // Keep the multi-day span coherent: picking a "fin" before the start (or an "après"
        // before an already-later end) snaps it back instead of storing an inverted range.
        EventDateInput.SelectedDateChangedByUser += (_, _)
            => SnapEndAfterStart();
        EventEndDateInput.SelectedDateChangedByUser += (_, _)
            => SnapEndAfterStart();

        Loaded += (_, _) =>
        {
            _selectedDate = DateTime.Today;
            RefreshAll();
            if (_pendingScreenshotPane is { } pane)
            {
                _pendingScreenshotPane = null;
                if (pane == "Settings") OpenSettingsPane();
                else if (pane == "Edit") ShowEditFormForScreenshot();
                else if (pane == "Picker") ShowPickerForScreenshot();
                else if (pane == "Hover") ShowHoverForScreenshot();
                else PopulateEventFormForScreenshot();
            }
        };
    }

    // =================================================================== calendar

    private DateTime _displayMonth = new(DateTime.Today.Year, DateTime.Today.Month, 1);
    private IReadOnlyList<AgendaItem> _monthItems = new List<AgendaItem>();

    // The old code fought the native Calendar control (PART_* surgery, non-stretchable grid,
    // no room for content). The month is now built by hand: 42 plain elements — cheap enough
    // to rebuild wholesale on every month change or edit, no virtualization needed.

    private void PreviousMonthButton_OnClick(object sender, RoutedEventArgs e) =>
        ShiftMonth(-1);

    private void NextMonthButton_OnClick(object sender, RoutedEventArgs e) =>
        ShiftMonth(1);

    private void TodayButton_OnClick(object sender, RoutedEventArgs e) => NavigateToDay(DateTime.Today);

    private void ShiftMonth(int delta)
    {
        _displayMonth = _displayMonth.AddMonths(delta);
        RefreshAll();
    }

    private void NavigateToDay(DateTime date)
    {
        _selectedDate = date.Date;
        _displayMonth = new DateTime(date.Year, date.Month, 1);
        RefreshAll();
    }

    private void UpdateMonthHeader()
    {
        var month = _displayMonth.ToString("MMMM yyyy", CultureInfo.CurrentCulture);
        MonthHeader.Text = char.ToUpperInvariant(month[0]) + month[1..];
    }

    // 6 weeks x 7 days of cells that stretch to fill the card, Monday-first like every European
    // calendar. Each cell shows the day number and up to three inline chips ("20h30 Sortie ciné"),
    // with a "+N" overflow line — the content density the native control could never offer.
    private void BuildMonthGrid()
    {
        var firstOfMonth = new DateTime(_displayMonth.Year, _displayMonth.Month, 1);
        var lastOfMonth = firstOfMonth.AddMonths(1).AddDays(-1);
        _monthItems = GetVisibleRange(firstOfMonth, lastOfMonth);

        MonthGrid.Children.Clear();
        var leading = ((int)firstOfMonth.DayOfWeek + 6) % 7; // Monday=0 … Sunday=6
        for (var slot = 0; slot < 42; slot++)
        {
            var date = firstOfMonth.AddDays(slot - leading);
            MonthGrid.Children.Add(BuildDayCell(date));
        }
    }

    private Border BuildDayCell(DateTime date)
    {
        var inMonth = date.Month == _displayMonth.Month;
        var isToday = date.Date == DateTime.Today;
        var isSelected = date.Date == _selectedDate.Date;
        var dayItems = _monthItems.Where(i => i.Date.Date == date.Date).ToList();

        // The grid's cells have a fixed height (a 6-row UniformGrid in a star row), so a wrapped title can
        // outgrow the room one day is allotted. Scrolling the cell keeps every event reachable instead of
        // silently clipping the ones that fall past its bottom edge.
        var cellScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false,
        };
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = date.Day.ToString(),
            FontSize = 14,
            FontWeight = FontWeights.Bold,
            Foreground = inMonth
                ? (Brush)FindResource(isToday ? "AccentBrush" : "TextBrush")
                : (Brush)FindResource("TextFaintBrush"),
        });

        foreach (var item in dayItems.Take(3))
        {
            var color = ColorFor(item);
            // Stretch so the chip spans the cell's full width, and wrap rather than ellipsize: the reader
            // reads each event's whole name right there ("Bruxelles 2026" over two lines), never an
            // ellipsized fragment ("Bruxel…") that hides what the event is until the day is clicked.
            var chip = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0x33, color.R, color.G, color.B)),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(5, 1, 5, 1),
                Margin = new Thickness(0, 2, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            var chipColor = color;
            // A birthday chip carries just the name — the dot already marks it as a birthday, and the
            // narrow cell would only ever show "Anniversaire de Lou…" where "Louison" fits whole.
            var chipText = item.Source == AgendaSource.Birthday
                ? CalendarService.BirthdayChipName(item.Title)
                : item.Title;
            chip.Child = new TextBlock
            {
                Text = chipText,
                FontSize = 10.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(chipColor),
                TextWrapping = TextWrapping.Wrap,
            };
            stack.Children.Add(chip);
        }
        if (dayItems.Count > 3)
        {
            stack.Children.Add(new TextBlock
            {
                Text = $"+{dayItems.Count - 3} autre(s)",
                FontSize = 10,
                Foreground = (Brush)FindResource("TextMutedBrush"),
                Margin = new Thickness(2, 1, 0, 0),
            });
        }

        cellScroll.Content = stack;

        var cell = new Border
        {
            Background = isSelected ? (Brush)FindResource("AccentSoftBrush") : Brushes.Transparent,
            BorderBrush = isSelected ? (Brush)FindResource("AccentBrush") : (Brush)FindResource("AppBorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(7, 5, 7, 5),
            Margin = new Thickness(2),
            Child = cellScroll,
            Cursor = System.Windows.Input.Cursors.Hand,
            Opacity = inMonth ? 1.0 : 0.55,
            Tag = date,
        };
        var clickedDate = date;
        cell.MouseLeftButtonUp += (_, _) => NavigateToDay(clickedDate);
        return cell;
    }

    // =================================================================== data refresh

    private bool ShowsBirthdays => FilterBirthdays.IsChecked == true;
    private bool ShowsEvents => FilterEvents.IsChecked == true;
    private bool ShowsDeadlines => FilterDeadlines.IsChecked == true;

    private IReadOnlyList<AgendaItem> GetVisibleRange(DateTime from, DateTime to) =>
        _calendarService.GetRange(from, to).Where(MatchesFilter).ToList();

    private bool MatchesFilter(AgendaItem item) => item.Source switch
    {
        AgendaSource.Birthday => ShowsBirthdays,
        AgendaSource.Event => ShowsEvents,
        AgendaSource.Deadline => ShowsDeadlines,
        _ => true,
    };

    private void RefreshAll()
    {
        UpdateMonthHeader();
        BuildMonthGrid();
        RenderSelectedDay();
        RenderUpcoming();
    }

    // Public: MainWindow's notification toast maps rows to the same palette without duplicating it.
    public static Color ColorFor(AgendaItem item) => item.Source switch
    {
        AgendaSource.Birthday => BirthdayColor,
        AgendaSource.Deadline => DeadlineColor,
        AgendaSource.Event => item.EventKind switch
        {
            CalendarEventKind.Sortie => SortieColor,
            CalendarEventKind.Plan => PlanColor,
            CalendarEventKind.Voyage => VoyageColor,
            CalendarEventKind.Rendezvous => RendezvousColor,
            _ => AutreColor,
        },
        _ => AutreColor,
    };

    private static string SourceLabel(AgendaItem item) => item.Source switch
    {
        AgendaSource.Birthday => "Anniversaire",
        AgendaSource.Deadline => "Échéance",
        AgendaSource.Event => item.EventKind switch
        {
            CalendarEventKind.Sortie => "Sortie",
            CalendarEventKind.Plan => "Plan",
            CalendarEventKind.Voyage => "Voyage",
            CalendarEventKind.Rendezvous => "Rendez-vous",
            _ => "Autre",
        },
        _ => string.Empty,
    };

    private void RenderSelectedDay()
    {
        DayItemsPanel.Children.Clear();
        var items = GetVisibleRange(_selectedDate, _selectedDate);
        foreach (var item in items) DayItemsPanel.Children.Add(BuildAgendaRow(item));

        DayEmptyText.Visibility = items.Count > 0 ? Visibility.Collapsed : Visibility.Visible;

        var label = _selectedDate.ToString("dddd d MMMM yyyy", CultureInfo.CurrentCulture);
        label = char.ToUpperInvariant(label[0]) + label[1..];
        SelectedDayHeader.Text = _selectedDate == DateTime.Today ? $"Aujourd'hui — {label}" : label;
    }

    private void RenderUpcoming()
    {
        UpcomingPanel.Children.Clear();
        var today = DateTime.Today;
        var items = GetVisibleRange(today, today.AddDays(30));
        var shown = 0;
        DateTime? lastDay = null;
        foreach (var item in items)
        {
            if (shown >= 25) break;

            // One header per calendar day, so the horizon reads as a dated agenda not a flat list.
            if (lastDay is not { } || item.Date.Date != lastDay.Value)
            {
                lastDay = item.Date.Date;
                UpcomingPanel.Children.Add(BuildUpcomingHeader(item.Date.Date, today));
            }

            UpcomingPanel.Children.Add(BuildAgendaRow(item));
            shown++;
        }
        UpcomingEmptyText.Visibility = shown > 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private static FrameworkElement BuildUpcomingHeader(DateTime day, DateTime today)
    {
        var label = day == today
            ? "Aujourd'hui"
            : day == today.AddDays(1)
                ? "Demain"
                : Capitalize(day.ToString("dddd d MMMM", CultureInfo.CurrentCulture));

        return new TextBlock
        {
            Text = label,
            FontSize = 10.5,
            FontWeight = FontWeights.Bold,
            Foreground = (Brush)FindStaticAccent(),
            Margin = new Thickness(2, 10, 0, 4),
        };
    }

    // FindResource is an instance method; the static header builder needs the brush without an
    // instance, so resolve it off the app resources here.
    private static Brush FindStaticAccent() =>
        (Brush)Application.Current.Resources["AccentBrush"];

    // Compact link chips for an agenda row: thumbnail + section badge + title, clickable straight
    // to the target, hover opens a preview card of the linked item (see AttachPreviewHover).
    // Capped at four with a "+N" tail so a heavily linked event stays a line, not a wall.
    private FrameworkElement? BuildLinksStrip(AgendaItem item)
    {
        if (item.Source == AgendaSource.Deadline) return null;
        var ownerType = item.Source == AgendaSource.Birthday
            ? CalendarLinkOwnerType.Birthday : CalendarLinkOwnerType.Event;
        var links = _linkRepo.GetByOwner(ownerType, item.RefId);
        if (links.Count == 0) return null;

        var strip = new WrapPanel { Margin = new Thickness(0, 5, 0, 0) };
        foreach (var link in links.Take(4))
        {
            var preview = Enum.TryParse<SearchResultKind>(link.Kind, out var kind)
                ? _searchService.GetPreview(kind, link.PrimaryId, link.SecondaryId)
                : null;
            var thumb = preview?.ImagePath is { } path ? LoadThumb(MediaStorage.ResolveFullPath(path)) : null;

            var chip = new Border
            {
                Background = (Brush)FindResource("Surface3Brush"),
                BorderBrush = (Brush)FindResource("AppBorderBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(6, 2, 6, 2),
                Margin = new Thickness(0, 0, 5, 0),
            };
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            if (thumb is not null)
                row.Children.Add(new Image
                {
                    Source = thumb,
                    Width = 14,
                    Height = 14,
                    Margin = new Thickness(0, 0, 5, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Stretch = Stretch.Uniform,
                });
            row.Children.Add(new TextBlock
            {
                Text = SectionBadge(link.Section),
                FontSize = 8.5,
                FontWeight = FontWeights.Bold,
                Foreground = (Brush)FindResource("AccentBrush"),
                Margin = new Thickness(0, 0, 5, 0),
                VerticalAlignment = VerticalAlignment.Center,
            });
            row.Children.Add(new TextBlock
            {
                Text = preview is null ? link.Title + " (introuvable)" : link.Title,
                FontSize = 10.5,
                MaxWidth = 200,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = preview is null ? (Brush)FindResource("TextFaintBrush") : (Brush)FindResource("TextMutedBrush"),
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = preview is null ? System.Windows.Input.Cursors.Arrow : System.Windows.Input.Cursors.Hand,
                ToolTip = preview is null ? "L'élément lié a été supprimé." : null,
            });
            var opened = link;
            chip.MouseLeftButtonUp += (_, _) => { ClosePreviewPopups(); NavigateToLink(opened); };
            AttachPreviewHover(chip, link, preview);
            chip.Child = row;
            strip.Children.Add(chip);
        }
        if (links.Count > 4)
            strip.Children.Add(new TextBlock
            {
                Text = $"+{links.Count - 4}",
                FontSize = 10,
                Foreground = (Brush)FindResource("TextFaintBrush"),
                VerticalAlignment = VerticalAlignment.Center,
            });
        return strip;
    }

    private static string Capitalize(string text) =>
        text.Length > 0 ? char.ToUpperInvariant(text[0]) + text[1..] : text;


    // One agenda line: colored dot, time, title, subtitle, and an "Modifier" button
    // for editable rows (birthdays/events — deadlines stay editable in Organisation).
    private Border BuildAgendaRow(AgendaItem item)
    {
        var grid = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var dot = new Ellipse
        {
            Width = 8,
            Height = 8,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Fill = new SolidColorBrush(ColorFor(item)),
        };
        Grid.SetColumn(dot, 0);
        grid.Children.Add(dot);

        var when = new TextBlock
        {
            Text = item.TimeText.Length > 0 ? item.TimeText : "—",
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("AccentBrush"),
            MinWidth = 38,
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(when, 1);
        grid.Children.Add(when);

        var titleStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        titleStack.Children.Add(new TextBlock
        {
            Text = item.Title,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("TextBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        var subtitle = item.Subtitle;
        // "toute la journée" is a fact about a timed-less event's FIRST day only. Continuation
        // days of a span carry no hour either, but they're a continuation, not an all-day event —
        // claiming otherwise made day 2-3 of a trip read as standalone all-day entries.
        if (item.Source == AgendaSource.Event && item.Hour is null && !item.IsSpanContinuation)
            subtitle = subtitle.Length > 0 ? subtitle + " · toute la journée" : "toute la journée";
        if (subtitle.Length > 0)
        {
            titleStack.Children.Add(new TextBlock
            {
                Text = subtitle,
                FontSize = 11,
                Foreground = (Brush)FindResource("TextMutedBrush"),
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
        }
        else if (item.Source == AgendaSource.Event)
        {
            titleStack.Children.Add(new TextBlock
            {
                Text = SourceLabel(item),
                FontSize = 11,
                Foreground = (Brush)FindResource("TextMutedBrush"),
            });
        }
        Grid.SetColumn(titleStack, 2);
        grid.Children.Add(titleStack);

        // Linked items show right on the agenda line — no detour through the edit form to see
        // (or open) what an event or birthday points at.
        if (BuildLinksStrip(item) is { } linksStrip) titleStack.Children.Add(linksStrip);

        if (item.Source != AgendaSource.Deadline)
        {
            var edit = new Button
            {
                Content = "Modifier",
                Style = (Style)FindResource("MiniButtonStyle"),
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            var captured = item;
            edit.Click += (_, _) => OpenEditForm(captured);
            Grid.SetColumn(edit, 3);
            grid.Children.Add(edit);
        }

        return new Border
        {
            Child = grid,
            Background = (Brush)FindResource("Surface2Brush"),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 8, 12, 8),
        };
    }

    private void FilterChip_OnClick(object sender, RoutedEventArgs e) => RefreshAll();

    // Headless-capture entry (see MainWindow.OpenSectionForScreenshot): opens the event form or
    // the notification settings pane, which are otherwise behind buttons. Applied on Loaded so
    // every control exists before values are pushed into it.
    public void ShowPaneForScreenshot(string pane) => _pendingScreenshotPane = pane;

    // Opens the edit form of the first calendar entry that carries links (an event, else a
    // birthday; falls back to any event, then to the new-event form), so a headless capture shows
    // the chip row with real data. No test-specific knowledge: it reads the same repos the UI does.
    public void ShowEditFormForScreenshot()
    {
        var all = _eventRepo.GetAll();
        var target = all.FirstOrDefault(ev => _linkRepo.GetByOwner(CalendarLinkOwnerType.Event, ev.Id).Count > 0);
        if (target is not null)
        {
            OpenEditForm(new AgendaItem
            {
                Date = target.Date, Title = target.Title, Source = AgendaSource.Event, RefId = target.Id,
            });
            return;
        }
        var birthday = _birthdayRepo.GetAll()
            .FirstOrDefault(b => _linkRepo.GetByOwner(CalendarLinkOwnerType.Birthday, b.Id).Count > 0);
        if (birthday is not null)
        {
            OpenEditForm(new AgendaItem
            {
                Date = _calendarService.NextOccurrenceOnMonthDay(birthday.Month, birthday.Day) ?? DateTime.Today,
                Title = birthday.Name, Source = AgendaSource.Birthday, RefId = birthday.Id,
            });
            return;
        }
        if (all.Count > 0)
        {
            OpenEditForm(new AgendaItem
            {
                Date = all[0].Date, Title = all[0].Title, Source = AgendaSource.Event, RefId = all[0].Id,
            });
            return;
        }
        PopulateEventFormForScreenshot();
    }

    private string? _pendingScreenshotPane;

    // Opens the link picker over the event form, queried for the probe rows (ZZPV marker), so a
    // headless capture sees the thumbnail rows as they look while typing. StaysOpen is forced on:
    // the headless window never takes activation, and a StaysOpen=False popup closes on deactivation.
    private void ShowPickerForScreenshot()
    {
        PopulateEventFormForScreenshot();
        LinkPickerPopup.StaysOpen = true;
        LinkPickerPopup.IsOpen = true;
        LinkSearchInput.Text = "ZZPV";
    }

    // The popup lives in a detached visual tree — App.RunScreenshot renders this element instead
    // of the window content. Measured/arranged by hand: the popup's own layout can lag the capture.
    public FrameworkElement? PickerElementForScreenshot()
    {
        if (!LinkPickerPopup.IsOpen) return null;
        var child = LinkPickerPopup.Child as FrameworkElement;
        if (child is null) return null;
        child.Measure(new Size(440, 900));
        child.Arrange(new Rect(0, 0, 440, Math.Max(child.DesiredSize.Height, 200)));
        return child;
    }

    // Hover capture: opens the linked form, then builds the preview card of the first still-live
    // link in it. The card is returned as a loose element (a live Popup would render in a detached
    // PopupRoot the window-level RenderTargetBitmap can't see — see the memory note).
    private Border? _screenshotPreviewCard;

    private void ShowHoverForScreenshot()
    {
        ShowEditFormForScreenshot();
        foreach (var link in _workingLinks)
        {
            if (!Enum.TryParse<SearchResultKind>(link.Kind, out var kind)) continue;
            if (_searchService.GetPreview(kind, link.PrimaryId, link.SecondaryId) is not { } preview) continue;
            _screenshotPreviewCard = BuildPreviewCard(link, preview);
            return;
        }
    }

    public FrameworkElement? PreviewCardForScreenshot()
    {
        if (_screenshotPreviewCard is not { } card) return null;
        card.Measure(new Size(380, 900));
        card.Arrange(new Rect(0, 0, Math.Max(card.DesiredSize.Width, 200), Math.Max(card.DesiredSize.Height, 100)));
        return card;
    }

    private void PopulateEventFormForScreenshot()
    {
        _mode = FormMode.NewEvent;
        _editingId = 0;
        EventTitleInput.Text = "Sortie ciné";
        EventKindPicker.SelectedIndex = 0;
        EventDateInput.SelectedDate = DateTime.Today;
        EventEndDateInput.SelectedDate = null;
        EventSameDurationToggle.IsChecked = false;
        _workingLinks = new List<CalendarEventLink>();
        RenderLinks();
        EventAllDayToggle.IsChecked = false;
        EventTimeInput.IsEnabled = true;
        EventTimeInput.Text = "20:30";
        EventLocationInput.Text = string.Empty;
        EventNotesInput.Text = string.Empty;
        EventRecurringToggle.IsChecked = false;
        ShowForm("Nouvel événement");
    }

    // =================================================================== forms

    private void NewBirthdayButton_OnClick(object sender, RoutedEventArgs e)
    {
        _mode = FormMode.NewBirthday;
        _editingId = 0;
        BirthdayNameInput.Text = string.Empty;
        BirthdayDateInput.SelectedDate = _selectedDate;
        BirthdayYearInput.Text = string.Empty;
        BirthdayNotesInput.Text = string.Empty;
        _workingLinks = new List<CalendarEventLink>();
        RenderLinks();
        ShowForm("Nouvel anniversaire");
    }

    private void NewEventButton_OnClick(object sender, RoutedEventArgs e)
    {
        _mode = FormMode.NewEvent;
        _editingId = 0;
        EventTitleInput.Text = string.Empty;
        EventKindPicker.SelectedIndex = 0;
        EventDateInput.SelectedDate = _selectedDate;
        EventEndDateInput.SelectedDate = null;
        EventSameDurationToggle.IsChecked = false;
        _workingLinks = new List<CalendarEventLink>();
        RenderLinks();
        EventAllDayToggle.IsChecked = true;
        EventTimeInput.Text = "20:00";
        EventLocationInput.Text = string.Empty;
        EventNotesInput.Text = string.Empty;
        EventRecurringToggle.IsChecked = false;
        ShowForm("Nouvel événement");
    }

    private void OpenEditForm(AgendaItem item)
    {
        if (item.Source == AgendaSource.Birthday)
        {
            var birthday = _birthdayRepo.GetAll().FirstOrDefault(b => b.Id == item.RefId);
            if (birthday is null) return;
            _mode = FormMode.EditBirthday;
            _editingId = birthday.Id;
            BirthdayNameInput.Text = birthday.Name;
            var occurrence = _calendarService.NextOccurrenceOnMonthDay(birthday.Month, birthday.Day);
            BirthdayDateInput.SelectedDate = occurrence;
            BirthdayYearInput.Text = birthday.BirthYear?.ToString() ?? string.Empty;
            BirthdayNotesInput.Text = birthday.Notes;
            _workingLinks = _linkRepo.GetByOwner(CalendarLinkOwnerType.Birthday, birthday.Id).ToList();
            RenderLinks();
            ShowForm("Modifier l'anniversaire");
            return;
        }

        if (item.Source == AgendaSource.Event)
        {
            var calendarEvent = _eventRepo.GetById(item.RefId);
            if (calendarEvent is null) return;
            _mode = FormMode.EditEvent;
            _editingId = calendarEvent.Id;
            EventTitleInput.Text = calendarEvent.Title;
            SelectKind(calendarEvent.Kind);
            EventDateInput.SelectedDate = calendarEvent.Date.Date;
            EventEndDateInput.SelectedDate = calendarEvent.EndDate;
            _workingLinks = _linkRepo.GetByOwner(CalendarLinkOwnerType.Event, calendarEvent.Id).ToList();
            RenderLinks();
            EventAllDayToggle.IsChecked = calendarEvent.StartHour is null;
            EventTimeInput.Text = calendarEvent.StartHour is int h
                ? $"{h:D2}:{(calendarEvent.StartMinute ?? 0):D2}" : "20:00";
            EventLocationInput.Text = calendarEvent.Location;
            EventNotesInput.Text = calendarEvent.Notes;
            EventRecurringToggle.IsChecked = calendarEvent.RecurringYearly;
            ShowForm("Modifier l'événement");
        }
    }

    private void ShowForm(string title)
    {
        FormTitle.Text = title;
        BirthdayFormPanel.Visibility = _mode is FormMode.NewBirthday or FormMode.EditBirthday
            ? Visibility.Visible : Visibility.Collapsed;
        EventFormPanel.Visibility = _mode is FormMode.NewEvent or FormMode.EditEvent
            ? Visibility.Visible : Visibility.Collapsed;
        DeleteFormButton.Visibility = _mode is FormMode.EditBirthday or FormMode.EditEvent
            ? Visibility.Visible : Visibility.Collapsed;
        SettingsRoot.Visibility = Visibility.Collapsed;
        AgendaRoot.Visibility = Visibility.Collapsed;
        FormRoot.Visibility = Visibility.Visible;
    }

    private void CloseForm()
    {
        _mode = FormMode.None;
        FormRoot.Visibility = Visibility.Collapsed;
        AgendaRoot.Visibility = Visibility.Visible;
    }

    private void CancelFormButton_OnClick(object sender, RoutedEventArgs e) => CloseForm();

    private void EventAllDayToggle_OnChecked(object sender, RoutedEventArgs e) =>
        EventTimeInput.IsEnabled = false;

    private void EventAllDayToggle_OnUnchecked(object sender, RoutedEventArgs e) =>
        EventTimeInput.IsEnabled = true;

    private void SelectKind(CalendarEventKind kind)
    {
        if (EventKindPicker.ItemsSource is not List<KindOption> options) return;
        for (var i = 0; i < options.Count; i++)
        {
            if (options[i].Kind == kind) { EventKindPicker.SelectedIndex = i; return; }
        }
    }

    private void SaveFormButton_OnClick(object sender, RoutedEventArgs e)
    {
        switch (_mode)
        {
            case FormMode.NewBirthday or FormMode.EditBirthday:
                SaveBirthdayForm();
                break;
            case FormMode.NewEvent or FormMode.EditEvent:
                SaveEventForm();
                break;
        }
        CloseForm();
        RefreshAll();
    }

    private void SaveBirthdayForm()
    {
        var name = BirthdayNameInput.Text.Trim();
        if (name.Length == 0 || BirthdayDateInput.SelectedDate is not { } date) return;

        int? year = int.TryParse(BirthdayYearInput.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var y)
                     && y >= 1 && y <= DateTime.Today.Year ? y : null;

        if (_mode == FormMode.NewBirthday)
        {
            var id = _birthdayRepo.Add(new Birthday { Name = name, Month = date.Month, Day = date.Day, BirthYear = year, Notes = BirthdayNotesInput.Text.Trim() });
            ReplaceLinks(CalendarLinkOwnerType.Birthday, id);
        }
        else
        {
            _birthdayRepo.Update(_editingId, name, date.Month, date.Day, year, BirthdayNotesInput.Text.Trim());
            ReplaceLinks(CalendarLinkOwnerType.Birthday, _editingId);
        }

        // A new/edited row may deserve its lead-time notification — let MainWindow reschedule.
        _notifySettingsChanged?.Invoke();
    }

    private void SaveEventForm()
    {
        var title = EventTitleInput.Text.Trim();
        if (title.Length == 0 || EventDateInput.SelectedDate is not { } date) return;

        int? hour = null, minute = null;
        if (EventAllDayToggle.IsChecked != true && TryParseTime(EventTimeInput.Text, out var h, out var m))
        {
            hour = h;
            minute = m;
        }

        var kind = EventKindPicker.SelectedItem is KindOption option ? option.Kind : CalendarEventKind.Autre;

        // Span: an end before the start (or equal to it) is simply "no end" — one-day event.
        DateTime? end = EventEndDateInput.SelectedDate is { } endDate && endDate.Date > date.Date
            ? endDate.Date
            : null;

        if (_mode == FormMode.NewEvent)
        {
            var id = _eventRepo.Add(new CalendarEvent
            {
                Title = title,
                Kind = kind,
                Date = date.Date,
                EndDate = end,
                StartHour = hour,
                StartMinute = minute,
                RecurringYearly = EventRecurringToggle.IsChecked == true,
                Location = EventLocationInput.Text.Trim(),
                Notes = EventNotesInput.Text.Trim(),
                CreatedAt = DateTime.Now,
            });
            ReplaceLinks(CalendarLinkOwnerType.Event, id);
        }
        else
        {
            _eventRepo.Update(_editingId, title, kind, date.Date, end, hour, minute,
                EventRecurringToggle.IsChecked == true, EventLocationInput.Text.Trim(), EventNotesInput.Text.Trim());
            ReplaceLinks(CalendarLinkOwnerType.Event, _editingId);
        }

        _notifySettingsChanged?.Invoke();
    }

    // Replace-on-save: wipe the owner's old link rows, re-insert the form's working copy.
    private void ReplaceLinks(CalendarLinkOwnerType ownerType, int ownerId)
    {
        _linkRepo.DeleteForOwner(ownerType, ownerId);
        foreach (var link in _workingLinks)
        {
            link.OwnerType = ownerType;
            link.OwnerId = ownerId;
            _linkRepo.Add(link);
        }
    }

    private void SnapEndAfterStart()
    {
        if (EventDateInput.SelectedDate is { } start && EventEndDateInput.SelectedDate is { } end
            && end.Date < start.Date)
        {
            EventEndDateInput.SelectedDate = start.Date;
        }
    }

    // "Même durée que le dernier créé": take the day-span of the most recently created event and
    // apply it to the form's current start date. A 3-day trip stays a 3-day trip next time.
    private void SameDuration_OnClick(object sender, RoutedEventArgs e)
    {
        if (EventSameDurationToggle.IsChecked != true) return;
        var last = _eventRepo.GetAll().OrderByDescending(ev => ev.CreatedAt).FirstOrDefault();
        if (last is null || EventDateInput.SelectedDate is not { } start) return;
        var days = (int)Math.Round(((last.EndDate ?? last.Date).Date - last.Date.Date).TotalDays);
        EventEndDateInput.SelectedDate = days > 0 ? start.Date.AddDays(days) : null;
    }

    // =================================================================== event links

    // One chip per linked item: kind badge + live title + a × to unlink. Title is re-resolved
    // through the global search so a renamed target shows its new title; a deleted target goes
    // grey with "(introuvable)" instead of vanishing silently.
    private void RenderLinks()
    {
        var panel = _mode is FormMode.NewBirthday or FormMode.EditBirthday
            ? BirthdayLinksPanel : EventLinksPanel;
        panel.Children.Clear();
        foreach (var link in _workingLinks)
        {
            var live = ResolveLiveTitle(link);
            var preview = Enum.TryParse<SearchResultKind>(link.Kind, out var kind)
                ? _searchService.GetPreview(kind, link.PrimaryId, link.SecondaryId)
                : null;
            var chip = new Border
            {
                Background = (Brush)FindResource("Surface2Brush"),
                BorderBrush = (Brush)FindResource("AppBorderBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8, 3, 6, 3),
                Margin = new Thickness(0, 2, 6, 2),
            };
            var row = new StackPanel { Orientation = Orientation.Horizontal };

            // Thumbnail first when the target owns an image; clicking it opens the element, same
            // gesture as the title.
            var thumb = live is not null && preview?.ImagePath is { } path
                ? LoadThumb(MediaStorage.ResolveFullPath(path)) : null;
            if (thumb is not null)
            {
                var image = new Border
                {
                    Width = 26,
                    Height = 26,
                    CornerRadius = new CornerRadius(4),
                    Margin = new Thickness(0, 1, 7, 1),
                    ClipToBounds = true,
                    Background = (Brush)FindResource("Surface3Brush"),
                    Child = new Image { Source = thumb, Stretch = Stretch.Uniform },
                };
                image.MouseLeftButtonUp += (_, _) => { ClosePreviewPopups(); NavigateToLink(link); };
                row.Children.Add(image);
            }

            row.Children.Add(new TextBlock
            {
                Text = SectionBadge(link.Section),
                FontSize = 9.5,
                FontWeight = FontWeights.Bold,
                Foreground = (Brush)FindResource("AccentBrush"),
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center,
            });
            var title = new TextBlock
            {
                Text = live ?? link.Title + " (introuvable)",
                FontSize = 11.5,
                MaxWidth = 260,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = live is null ? (Brush)FindResource("TextFaintBrush") : (Brush)FindResource("TextBrush"),
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = live is null ? System.Windows.Input.Cursors.Arrow : System.Windows.Input.Cursors.Hand,
                ToolTip = live is null ? "L'élément lié a été supprimé." : null,
            };
            title.MouseLeftButtonUp += (_, _) => { ClosePreviewPopups(); NavigateToLink(link); };
            row.Children.Add(title);

            var remove = new Button
            {
                Content = "✕",
                FontSize = 10,
                Width = 18,
                Height = 18,
                Margin = new Thickness(6, 0, 0, 0),
                Padding = new Thickness(0),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = (Brush)FindResource("TextFaintBrush"),
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = "Retirer le lien",
            };
            // Button.Click must be wired with += (events can't go in an object initializer — CS0079).
            var target = link;
            remove.Click += (_, _) =>
            {
                _workingLinks.Remove(target);
                RenderLinks();
            };
            remove.Template = ChromelessButtonTemplate();
            row.Children.Add(remove);

            chip.Child = row;
            AttachPreviewHover(chip, link, live is null ? null : preview);
            panel.Children.Add(chip);
        }
    }

    // The Button default template draws its own background — strip it so the ✕ reads as a glyph.
    private static ControlTemplate ChromelessButtonTemplate()
    {
        var template = new ControlTemplate(typeof(Button));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        template.VisualTree = presenter;
        return template;
    }

    private static string SectionBadge(string section) => section switch
    {
        "Journaux" => "JOURNAL",
        "Collections" => "COLLECT.",
        "Organisation" => "PROJET",
        "Archives" => "ARCHIVE",
        "Musique" => "MUSIQUE",
        "Budget" => "BUDGET",
        _ => section.ToUpperInvariant(),
    };

    private string? ResolveLiveTitle(CalendarEventLink link)
    {
        if (!Enum.TryParse<SearchResultKind>(link.Kind, out var kind)) return null;
        var hits = _searchService.Search(link.Title, 60);
        var hit = hits.FirstOrDefault(h => h.Kind == kind && h.PrimaryId == link.PrimaryId &&
                                           h.SecondaryId == link.SecondaryId);
        // The search ranks by term matches — a title it can't match (accents, stopwords) must not
        // read as "deleted". The direct row lookup is the ground truth for existence.
        return hit?.Title ?? (_searchService.GetPreview(kind, link.PrimaryId, link.SecondaryId) is null ? null : link.Title);
    }

    // Opens a linked item through the same navigation the global search uses. A target deleted
    // since the chip rendered silently no-ops (its chip is greyed out anyway).
    private void NavigateToLink(CalendarEventLink link)
    {
        if (!Enum.TryParse<SearchResultKind>(link.Kind, out var kind)) return;
        if (_searchService.GetPreview(kind, link.PrimaryId, link.SecondaryId) is null) return;
        var window = Window.GetWindow(this) as MainWindow;
        window?.NavigateToSearchResult(new SearchResult(kind, link.Section, link.Title,
            string.Empty, link.PrimaryId, link.SecondaryId, link.Category));
    }

    // =================================================================== link hover preview

    // One shared popup: hovering a link chip long enough opens a card showing the linked item
    // itself — a poem rendered in its own reading style, an image element its picture, text
    // elements their excerpt. It lingers while the pointer is over the card, closes on leave.
    private Popup? _hoverPopup;
    private readonly DispatcherTimer _hoverOpenTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private readonly DispatcherTimer _hoverCloseTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private (FrameworkElement Host, CalendarEventLink Link, GlobalSearchService.SearchPreview Preview)? _pendingHover;
    private bool _hoverTimersWired;

    private void AttachPreviewHover(FrameworkElement host, CalendarEventLink link, GlobalSearchService.SearchPreview? preview)
    {
        if (preview is null) return; // deleted target: the greyed chip carries a plain tooltip
        if (!_hoverTimersWired)
        {
            _hoverTimersWired = true;
            _hoverOpenTimer.Tick += (_, _) =>
            {
                _hoverOpenTimer.Stop();
                if (_pendingHover is { } pending) OpenHoverPopup(pending.Host, pending.Link, pending.Preview);
            };
            _hoverCloseTimer.Tick += (_, _) =>
            {
                _hoverCloseTimer.Stop();
                ClosePreviewPopups();
            };
        }
        host.MouseEnter += (_, _) =>
        {
            _hoverCloseTimer.Stop();
            _pendingHover = (host, link, preview);
            _hoverOpenTimer.Start();
        };
        host.MouseLeave += (_, _) =>
        {
            _hoverOpenTimer.Stop();
            _hoverCloseTimer.Start();
        };
    }

    private void OpenHoverPopup(FrameworkElement host, CalendarEventLink link, GlobalSearchService.SearchPreview preview)
    {
        ClosePreviewPopups();
        var card = BuildPreviewCard(link, preview);
        // The card keeps the popup alive while the pointer moves from chip to card.
        card.MouseEnter += (_, _) => _hoverCloseTimer.Stop();
        card.MouseLeave += (_, _) => _hoverCloseTimer.Start();
        _hoverPopup = new Popup
        {
            PlacementTarget = host,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
            StaysOpen = true,
            AllowsTransparency = true,
            PopupAnimation = PopupAnimation.Fade,
            Child = card,
        };
        _hoverPopup.IsOpen = true;
    }

    private void ClosePreviewPopups()
    {
        _hoverOpenTimer.Stop();
        _hoverCloseTimer.Stop();
        if (_hoverPopup is null) return;
        _hoverPopup.IsOpen = false;
        _hoverPopup.Child = null;
        _hoverPopup = null;
    }

    // The preview card: dark rounded panel mirroring the link picker's look. A poem gets its
    // verses in its own reader style (font, size, italic, alignment) — the same typography the
    // poem page uses — so hovering a link reads like a mini page, not a tooltip.
    private Border BuildPreviewCard(CalendarEventLink link, GlobalSearchService.SearchPreview preview)
    {
        var body = new StackPanel();
        body.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children =
            {
                new TextBlock
                {
                    Text = SectionBadge(link.Section),
                    FontSize = 9,
                    FontWeight = FontWeights.Bold,
                    Foreground = (Brush)FindResource("AccentBrush"),
                    Margin = new Thickness(0, 0, 7, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                },
                new TextBlock
                {
                    Text = link.Title,
                    FontSize = 12.5,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = (Brush)FindResource("TextBrush"),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxWidth = 300,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            },
        });

        if (preview.Poem is { } poem)
        {
            if (preview.ImagePath is { } poemImage)
                body.Children.Add(new Border
                {
                    Margin = new Thickness(0, 8, 0, 0),
                    MaxHeight = 140,
                    ClipToBounds = true,
                    CornerRadius = new CornerRadius(6),
                    Child = new Image
                    {
                        Source = LoadThumb(MediaStorage.ResolveFullPath(poemImage), 360),
                        Stretch = Stretch.Uniform,
                    },
                });
            if (preview.Body is { Length: > 0 } verses)
                body.Children.Add(new TextBlock
                {
                    Text = verses.Length > 600 ? verses[..600] + "…" : verses,
                    FontFamily = new FontFamily(poem.FontFamily),
                    FontSize = Math.Min(poem.FontSize, 17),
                    FontStyle = poem.Italic ? FontStyles.Italic : FontStyles.Normal,
                    FontWeight = poem.Bold ? FontWeights.Bold : FontWeights.Normal,
                    TextAlignment = poem.Alignment switch
                    {
                        "Center" => TextAlignment.Center,
                        "Right" => TextAlignment.Right,
                        "Justify" => TextAlignment.Justify,
                        _ => TextAlignment.Left,
                    },
                    Foreground = (Brush)FindResource("TextBrush"),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 8, 0, 0),
                });
        }
        else if (preview.ImagePath is { } image)
        {
            body.Children.Add(new Border
            {
                Margin = new Thickness(0, 8, 0, 0),
                MaxHeight = 220,
                ClipToBounds = true,
                CornerRadius = new CornerRadius(6),
                Child = new Image
                {
                    Source = LoadThumb(MediaStorage.ResolveFullPath(image), 360),
                    Stretch = Stretch.Uniform,
                },
            });
            if (preview.Body is { Length: > 0 } caption)
                body.Children.Add(new TextBlock
                {
                    Text = ClipTip(caption),
                    FontSize = 11,
                    Foreground = (Brush)FindResource("TextMutedBrush"),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 6, 0, 0),
                });
        }
        else if (preview.Body is { Length: > 0 } text)
        {
            body.Children.Add(new TextBlock
            {
                Text = text.Length > 400 ? text[..400] + "…" : text,
                FontSize = 11.5,
                Foreground = (Brush)FindResource("TextMutedBrush"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 8, 0, 0),
            });
        }

        body.Children.Add(new TextBlock
        {
            Text = "Cliquer pour ouvrir",
            FontSize = 9.5,
            Foreground = (Brush)FindResource("TextFaintBrush"),
            Margin = new Thickness(0, 9, 0, 0),
        });

        return new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xF4, 0x1A, 0x1A, 0x2E)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, 0x3B, 0x82, 0xF6)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 11, 14, 11),
            MaxWidth = 380,
            Child = body,
        };
    }

    private void AddLinkButton_OnClick(object sender, RoutedEventArgs e)
    {
        LinkSearchInput.Text = string.Empty;
        LinkResultsList.ItemsSource = Array.Empty<PickerRow>();
        LinkNoResultsText.Visibility = Visibility.Collapsed;
        LinkPickerPopup.IsOpen = true;
        LinkSearchInput.Focus();
    }

    // Picker row view-model: the raw hit plus its resolved preview (thumbnail + tooltip text),
    // resolved once per search so the template never hits the database per binding. The pass-through
    // properties feed the DataTemplate's {Binding Section/Title/Snippet} paths.
    private sealed record PickerRow(SearchResult Hit, ImageSource? Thumb, Visibility ThumbVisibility, string Tip)
    {
        public string Section => Hit.Section;
        public string Title => Hit.Title;
        public string Snippet => Hit.Snippet;
    }

    // Set while the ↗ button is pressed: the ListBox's PreviewMouseLeftButtonUp would otherwise
    // also run and silently link the row the user only wanted to open.
    private bool _openJustClicked;

    private void LinkSearchInput_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        var results = LinkSearchInput.Text.Trim().Length == 0
            ? Array.Empty<SearchResult>()
            : _searchService.Search(LinkSearchInput.Text, 12);
        LinkResultsList.ItemsSource = results.Select(hit =>
        {
            var preview = _searchService.GetPreview(hit.Kind, hit.PrimaryId, hit.SecondaryId);
            var thumb = preview?.ImagePath is { } path ? LoadThumb(MediaStorage.ResolveFullPath(path)) : null;
            var tip = preview?.Body is { Length: > 0 } body ? ClipTip(body) : null;
            return new PickerRow(hit, thumb,
                thumb is null ? Visibility.Collapsed : Visibility.Visible,
                tip is null ? "Cliquer pour lier — ↗ pour ouvrir" : tip + "\nCliquer pour lier — ↗ pour ouvrir");
        }).ToList();
        LinkNoResultsText.Visibility = results.Count == 0 && LinkSearchInput.Text.Trim().Length > 0
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OpenResult_PreviewDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        => _openJustClicked = true;

    private void OpenResultButton_OnClick(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not PickerRow row) return;
        (Window.GetWindow(this) as MainWindow)?.NavigateToSearchResult(row.Hit);
        LinkPickerPopup.IsOpen = false;
    }

    private static string ClipTip(string body)
    {
        var oneLine = body.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return oneLine.Length <= 140 ? oneLine : oneLine[..140] + "…";
    }

    // Thumbnails must not lock their file (OnLoad + Freeze) — the user may delete/replace the
    // image later while the app still shows the chip.
    private static ImageSource LoadThumb(string fullPath, int decodeWidth = 92)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.DecodePixelWidth = decodeWidth;
        bitmap.UriSource = new Uri(fullPath);
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private void LinkResult_OnClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_openJustClicked) { _openJustClicked = false; return; }
        if ((LinkResultsList.SelectedItem as PickerRow)?.Hit is not SearchResult hit) return;
        var alreadyLinked = _workingLinks.Any(l => l.Kind == hit.Kind.ToString() &&
                                                   l.PrimaryId == hit.PrimaryId && l.SecondaryId == hit.SecondaryId);
        if (!alreadyLinked)
        {
            _workingLinks.Add(new CalendarEventLink
            {
                OwnerType = _mode is FormMode.NewBirthday or FormMode.EditBirthday
                    ? CalendarLinkOwnerType.Birthday : CalendarLinkOwnerType.Event,
                OwnerId = _editingId > 0 ? _editingId : 0, // real id assigned on save via ReplaceLinks
                Kind = hit.Kind.ToString(),
                Section = hit.Section,
                Title = hit.Title,
                PrimaryId = hit.PrimaryId,
                SecondaryId = hit.SecondaryId,
                Category = hit.Category,
            });
            RenderLinks();
        }
        LinkPickerPopup.IsOpen = false;
    }

    // Accepts "20:30", "20h30", "8h", "08.30" — the shapes people actually type.
    private static bool TryParseTime(string text, out int hour, out int minute)
    {
        hour = 0;
        minute = 0;
        var trimmed = text.Trim().ToLowerInvariant();
        if (trimmed.Length == 0) return false;

        var parts = trimmed.Split(':', 'h', '.');
        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out hour) || hour < 0 || hour > 23)
            return false;
        if (parts.Length > 1 && parts[1].Length > 0 &&
            (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out minute) || minute < 0 || minute > 59))
            return false;
        return true;
    }

    private void DeleteFormButton_OnClick(object sender, RoutedEventArgs e)
    {
        switch (_mode)
        {
            case FormMode.EditBirthday:
                _birthdayRepo.Delete(_editingId);
                _linkRepo.DeleteForOwner(CalendarLinkOwnerType.Birthday, _editingId);
                break;
            case FormMode.EditEvent:
                _eventRepo.Delete(_editingId);
                _linkRepo.DeleteForOwner(CalendarLinkOwnerType.Event, _editingId);
                break;
        }
        CloseForm();
        _notifySettingsChanged?.Invoke();
        RefreshAll();
    }

    // =================================================================== notification settings

    private void SettingsButton_OnClick(object sender, RoutedEventArgs e) => OpenSettingsPane();

    private void OpenSettingsPane()
    {
        SettingsNotificationsToggle.IsChecked = _config.CalendarNotificationsEnabled;
        SettingsDailyToastToggle.IsChecked = _config.CalendarDailyToastEnabled;
        LeadBirthdayInput.Text = _config.CalendarBirthdayLeadMinutes.ToString(CultureInfo.InvariantCulture);
        LeadDeadlineInput.Text = _config.CalendarDeadlineLeadMinutes.ToString(CultureInfo.InvariantCulture);
        LeadSortieInput.Text = _config.CalendarLeadMinutesSortie.ToString(CultureInfo.InvariantCulture);
        LeadPlanInput.Text = _config.CalendarLeadMinutesPlan.ToString(CultureInfo.InvariantCulture);
        LeadVoyageInput.Text = _config.CalendarLeadMinutesVoyage.ToString(CultureInfo.InvariantCulture);
        LeadRendezvousInput.Text = _config.CalendarLeadMinutesRendezvous.ToString(CultureInfo.InvariantCulture);
        LeadAutreInput.Text = _config.CalendarLeadMinutesAutre.ToString(CultureInfo.InvariantCulture);
        FormRoot.Visibility = Visibility.Collapsed;
        AgendaRoot.Visibility = Visibility.Collapsed;
        SettingsRoot.Visibility = Visibility.Visible;
    }

    private void CancelSettingsButton_OnClick(object sender, RoutedEventArgs e) =>
        CloseSettings();

    private void SaveSettingsButton_OnClick(object sender, RoutedEventArgs e)
    {
        _config.CalendarNotificationsEnabled = SettingsNotificationsToggle.IsChecked == true;
        _config.CalendarDailyToastEnabled = SettingsDailyToastToggle.IsChecked == true;
        _config.CalendarBirthdayLeadMinutes = ParseLead(LeadBirthdayInput.Text, _config.CalendarBirthdayLeadMinutes);
        _config.CalendarDeadlineLeadMinutes = ParseLead(LeadDeadlineInput.Text, _config.CalendarDeadlineLeadMinutes);
        _config.CalendarLeadMinutesSortie = ParseLead(LeadSortieInput.Text, _config.CalendarLeadMinutesSortie);
        _config.CalendarLeadMinutesPlan = ParseLead(LeadPlanInput.Text, _config.CalendarLeadMinutesPlan);
        _config.CalendarLeadMinutesVoyage = ParseLead(LeadVoyageInput.Text, _config.CalendarLeadMinutesVoyage);
        _config.CalendarLeadMinutesRendezvous = ParseLead(LeadRendezvousInput.Text, _config.CalendarLeadMinutesRendezvous);
        _config.CalendarLeadMinutesAutre = ParseLead(LeadAutreInput.Text, _config.CalendarLeadMinutesAutre);
        _configService.Save(_config);
        CloseSettings();

        // New lead times change when the next check must fire — MainWindow re-arms its timer.
        _notifySettingsChanged?.Invoke();
    }

    private void CloseSettings()
    {
        SettingsRoot.Visibility = Visibility.Collapsed;
        AgendaRoot.Visibility = Visibility.Visible;
    }

    // -1 (disabled) is valid; unparseable input keeps the previous value instead of zeroing it.
    private static int ParseLead(string text, int fallback) =>
        int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value >= -1
            ? value : fallback;
}
