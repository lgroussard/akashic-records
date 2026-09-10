using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using AkashicRecords.Domain;
using AkashicRecords.Infrastructure.Configuration;
using AkashicRecords.Infrastructure.Persistence;
using Microsoft.Win32;

namespace AkashicRecords.App.Views;

public partial class JournauxView : UserControl, ISearchNavigable
{
    private interface ICanvasCommand
    {
        void Do();
        void Undo();
    }

    private sealed class CanvasCommand : ICanvasCommand
    {
        private readonly Action _doAction;
        private readonly Action _undoAction;

        public CanvasCommand(Action doAction, Action undoAction)
        {
            _doAction = doAction;
            _undoAction = undoAction;
        }

        public void Do() => _doAction();
        public void Undo() => _undoAction();
    }

    private sealed record ConnectorVisual(Line Line, Polygon ArrowHead, int FromElementId, int ToElementId);

    private const double DefaultTextWidth = 200, DefaultTextHeight = 110;
    private const double DefaultImageWidth = 220, DefaultImageHeight = 160;
    private const double DefaultReferenceWidth = 180, DefaultReferenceHeight = 90;
    private const double MinElementWidth = 60, MinElementHeight = 40;

    private readonly ArtisticProjectRepository _projectRepository = new(new SqliteConnectionFactory());
    private readonly CanvasElementRepository _canvasRepository = new(new SqliteConnectionFactory());
    private readonly CanvasConnectorRepository _connectorRepository = new(new SqliteConnectionFactory());
    private readonly ObservationRepository _observationRepository = new(new SqliteConnectionFactory());
    private readonly ArtworkRepository _artworkRepository = new(new SqliteConnectionFactory());
    private readonly ReferenceRepository _referenceRepository = new(new SqliteConnectionFactory());
    private readonly JournalEntryRepository _journalEntryRepository = new(new SqliteConnectionFactory());
    private readonly JournalPhotoRepository _journalPhotoRepository = new(new SqliteConnectionFactory());
    private readonly RecipeRepository _recipeRepository = new(new SqliteConnectionFactory());
    private readonly RecueilRepository _recueilRepository = new(new SqliteConnectionFactory());
    private readonly PoemRepository _poemRepository = new(new SqliteConnectionFactory());
    private readonly MediaStorage _mediaStorage = new();
    private readonly AppConfig _config;

    private readonly Stack<ICanvasCommand> _undoStack = new();
    private readonly Stack<ICanvasCommand> _redoStack = new();

    private readonly Dictionary<int, FrameworkElement> _elementVisuals = new();
    private readonly Dictionary<int, ConnectorVisual> _connectorVisuals = new();

    private ArtisticProject? _currentProject;
    private JournalEntry? _selectedEntry;
    private Recipe? _selectedRecipe;
    private string? _selectedRecipeCategoryFilter;
    private Poem? _selectedPoem;
    private bool _isConnectMode;
    private int? _pendingConnectFromId;

    private SearchResult? _pendingSearchResult;

    // The journaux sub-tab (Personal/Recipes/Poetry/Artistic) the user last had open, so
    // reopening Journaux returns them there instead of always defaulting to the personal diary.
    private string? _activeTab;

    private static readonly SolidColorBrush NormalBorderBrush = new(Color.FromArgb(0x55, 0x3B, 0x82, 0xF6));
    private static readonly SolidColorBrush PendingConnectBrush = Brushes.Gold;

    public JournauxView(AppConfig config)
    {
        InitializeComponent();
        _config = config;
        _activeTab = config.JournauxActiveTab;
        if (!string.IsNullOrEmpty(_activeTab))
        {
            var tab = _activeTab switch
            {
                "Personal" => PersonalJournalTab,
                "Recipes" => RecipesJournalTab,
                "Poetry" => PoetryJournalTab,
                "Artistic" => ArtisticJournalTab,
                _ => null
            };
            if (tab is not null) SwitchJournalTab(tab, _activeTab);
        }

        Loaded += (_, _) =>
        {
            // Populate the content for whichever tab was restored, so the view isn't blank
            // until a re-layout triggers a refresh.
            if (!string.IsNullOrEmpty(_activeTab))
            {
                if (_activeTab == "Recipes") RefreshRecipesList();
                else if (_activeTab == "Poetry") RefreshPoetry();
                else if (_activeTab == "Artistic") RefreshProjectsList();
                else RefreshCalendarChips();
            }

            RestoreActiveItem();

            RefreshProjectsList();
            RefreshCalendarChips();
            NavigateToDate(DateTime.Today);
        };
        Loaded += JournauxView_OnLoaded;
        Unloaded += JournauxView_OnUnloaded;
        Loaded += (_, _) => ApplyPendingSearchResult();
        SetupCanvasPanning();
    }

    // --- Global search deep-link (ISearchNavigable) ---

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
        switch (result.Kind)
        {
            case SearchResultKind.JournalEntry:
                SwitchJournalTab(PersonalJournalTab, "Personal");
                var entry = _journalEntryRepository.GetById(result.PrimaryId);
                if (entry is not null) NavigateToDate(entry.EntryDate);
                break;

            case SearchResultKind.Recipe:
                SwitchJournalTab(RecipesJournalTab, "Recipes");
                _selectedRecipeCategoryFilter = null;
                RecipeSearchInput.Text = string.Empty;
                _selectedRecipe = _recipeRepository.GetAll().FirstOrDefault(r => r.Id == result.PrimaryId);
                RefreshRecipesList();
                break;

            case SearchResultKind.Poem:
                SwitchJournalTab(PoetryJournalTab, "Poetry");
                PoemSearchInput.Text = string.Empty;
                _selectedPoem = _poemRepository.GetAll().FirstOrDefault(p => p.Id == result.PrimaryId);
                RefreshPoetry();
                break;

            case SearchResultKind.ArtisticProject:
                SwitchJournalTab(ArtisticJournalTab, "Artistic");
                var project = _projectRepository.GetById(result.PrimaryId);
                if (project is not null) OpenProject(project);
                break;
        }
    }

    // Switches to a Journaux sub-tab through the real path, for headless capture (see App --screenshot).
    public void ShowTabForScreenshot(string tab)
    {
        var target = tab switch
        {
            "Recipes" => RecipesJournalTab,
            "Poetry" => PoetryJournalTab,
            "Artistic" => ArtisticJournalTab,
            _ => PersonalJournalTab
        };
        SwitchJournalTab(target, tab);

        if (tab == "Recipes") RefreshRecipesList();
        else if (tab == "Poetry") RefreshPoetry();
        else if (tab == "Artistic") RefreshProjectsList();
        else RefreshCalendarChips();
    }

    // Mirrors JournalTypeTab_OnClick without needing the original event args, for deep-linking.
    private void SwitchJournalTab(ToggleButton tab, string type)
    {
        foreach (var t in new[] { PersonalJournalTab, RecipesJournalTab, PoetryJournalTab, ArtisticJournalTab })
        {
            t.IsChecked = t == tab;
        }

        PersonalJournalRoot.Visibility = type == "Personal" ? Visibility.Visible : Visibility.Collapsed;
        RecipesRoot.Visibility = type == "Recipes" ? Visibility.Visible : Visibility.Collapsed;
        PoetryRoot.Visibility = type == "Poetry" ? Visibility.Visible : Visibility.Collapsed;
        ArtisticJournalRoot.Visibility = type == "Artistic" ? Visibility.Visible : Visibility.Collapsed;

        _activeTab = type;
        _config.JournauxActiveTab = type;
    }

    // Records the exact item the user is viewing so reopening Journaux returns them there,
    // not just to the right sub-tab. Serialized as "<tab>:<kind>:<id>" (id optional for Personal).
    private void SaveActiveItem()
    {
        if (_activeTab is null) return;

        var payload = _activeTab switch
        {
            "Personal" => $"Personal:Personal:",
            "Recipes" => _selectedRecipe is null ? null : $"Recipes:Recipe:{_selectedRecipe.Id}",
            "Poetry" => _selectedPoem is null ? null : $"Poetry:Poem:{_selectedPoem.Id}",
            "Artistic" => _currentProject is null ? null : $"Artistic:Project:{_currentProject.Id}",
            _ => null
        };
        _config.JournauxActiveItem = payload;
    }

    // Reopens the exact item the user last viewed (after the tab), mirroring ApplySearchResult.
    private void RestoreActiveItem()
    {
        if (string.IsNullOrEmpty(_config.JournauxActiveItem)) return;

        var parts = _config.JournauxActiveItem.Split(':', 3);
        if (parts.Length < 3) return;

        var kind = parts[1];
        var id = parts[2].Length > 0 ? int.TryParse(parts[2], out var parsed) ? parsed : -1 : -1;

        switch (kind)
        {
            case "Personal":
                NavigateToDate(_currentPageDate);
                break;
            case "Recipe":
                if (id >= 0)
                {
                    _selectedRecipe = _recipeRepository.GetById(id);
                    RefreshRecipesList();
                }
                break;
            case "Poem":
                if (id >= 0)
                {
                    _selectedPoem = _poemRepository.GetById(id);
                    RefreshPoetry();
                }
                break;
            case "Project":
                if (id >= 0)
                {
                    var project = _projectRepository.GetById(id);
                    if (project is not null) OpenProject(project);
                }
                break;
        }
    }

    private void JournalTypeTab_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton clicked || clicked.Tag is not string type) return;

        foreach (var tab in new[] { PersonalJournalTab, RecipesJournalTab, PoetryJournalTab, ArtisticJournalTab })
        {
            tab.IsChecked = tab == clicked;
        }

        SwitchJournalTab(clicked, type);

        if (type == "Recipes") RefreshRecipesList();
        if (type == "Poetry") RefreshPoetry();
    }

    // Two-finger horizontal touchpad swipes arrive as the native WM_MOUSEHWHEEL message, which WPF's
    // MouseWheel event does not surface at all - needs a raw window message hook to catch it.
    private const int WM_MOUSEHWHEEL = 0x020E;

    private void JournauxView_OnLoaded(object sender, RoutedEventArgs e)
    {
        if (PresentationSource.FromVisual(this) is HwndSource hwndSource)
        {
            hwndSource.AddHook(HorizontalWheelHook);
        }
    }

    private void GoToTodayButton_OnClick(object sender, RoutedEventArgs e)
    {
        EntryCalendar.DisplayDate = DateTime.Today;
        EntryCalendar.SelectedDate = DateTime.Today;
    }

    private void JournauxView_OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (PresentationSource.FromVisual(this) is HwndSource hwndSource)
        {
            hwndSource.RemoveHook(HorizontalWheelHook);
        }
    }

    private IntPtr HorizontalWheelHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_MOUSEHWHEEL || CanvasRoot.Visibility != Visibility.Visible) return IntPtr.Zero;

        var delta = (short)((wParam.ToInt64() >> 16) & 0xFFFF);
        CanvasScrollViewer.ScrollToHorizontalOffset(CanvasScrollViewer.HorizontalOffset + delta);
        handled = true;
        return IntPtr.Zero;
    }

    // Click-and-drag on empty canvas background scrolls the view - a touchpad has no scrollbar to grab,
    // so this is the main way to pan. Shift+wheel also scrolls horizontally (common touchpad convention).
    private void SetupCanvasPanning()
    {
        Point? panStart = null;
        double panStartHOffset = 0, panStartVOffset = 0;

        ElementCanvas.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (e.OriginalSource != ElementCanvas) return; // a child element handles its own drag instead

            panStart = e.GetPosition(CanvasScrollViewer);
            panStartHOffset = CanvasScrollViewer.HorizontalOffset;
            panStartVOffset = CanvasScrollViewer.VerticalOffset;
            ElementCanvas.CaptureMouse();
            ElementCanvas.Cursor = Cursors.ScrollAll;
        };
        ElementCanvas.PreviewMouseMove += (_, e) =>
        {
            if (panStart is null || e.LeftButton != MouseButtonState.Pressed) return;
            var pos = e.GetPosition(CanvasScrollViewer);
            CanvasScrollViewer.ScrollToHorizontalOffset(panStartHOffset - (pos.X - panStart.Value.X));
            CanvasScrollViewer.ScrollToVerticalOffset(panStartVOffset - (pos.Y - panStart.Value.Y));
        };
        ElementCanvas.PreviewMouseLeftButtonUp += (_, _) =>
        {
            if (panStart is null) return;
            ElementCanvas.ReleaseMouseCapture();
            ElementCanvas.Cursor = Cursors.Arrow;
            panStart = null;
        };
    }

    private void CanvasScrollViewer_OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) return;

        CanvasScrollViewer.ScrollToHorizontalOffset(CanvasScrollViewer.HorizontalOffset - e.Delta);
        e.Handled = true;
    }

    // Loads fully into memory and releases the file handle immediately, so the image file
    // can be deleted/replaced afterwards without "file in use" errors.
    private static BitmapImage LoadImage(string fullPath)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.UriSource = new Uri(fullPath);
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    // A click that started on a Button, a TextBox, or the resize handle should never start a
    // canvas drag - only a plain click on the border/background should move the whole element.
    private static bool ShouldSkipDrag(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is ButtonBase or TextBoxBase) return true;
            if (source is FrameworkElement { Tag: "ResizeHandle" }) return true;
            source = VisualTreeHelper.GetParent(source);
        }
        return false;
    }

    private void RefreshProjectsList()
    {
        ProjectsList.ItemsSource = _projectRepository.GetAll();
    }

    private void NewProjectButton_OnClick(object sender, RoutedEventArgs e)
    {
        var title = NewProjectTitleInput.Text.Trim();
        if (title.Length == 0) return;

        _projectRepository.Add(new ArtisticProject { Title = title, CreatedAt = DateTime.Now });
        NewProjectTitleInput.Clear();
        RefreshProjectsList();
    }

    private void ProjectsList_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProjectsList.SelectedItem is ArtisticProject project) OpenProject(project);
    }

    private void OpenProject(ArtisticProject project)
    {
        _currentProject = project;
        SaveActiveItem();
        _undoStack.Clear();
        _redoStack.Clear();
        _elementVisuals.Clear();
        _connectorVisuals.Clear();
        _isConnectMode = false;
        _pendingConnectFromId = null;
        DrawArrowButton.Content = "Tracer une flèche";

        CanvasProjectTitleText.Text = project.Title;
        ElementCanvas.Children.Clear();

        foreach (var element in _canvasRepository.GetByProject(project.Id))
        {
            var visual = CreateVisual(element);
            _elementVisuals[element.Id] = visual;
            ElementCanvas.Children.Add(visual);
        }

        foreach (var connector in _connectorRepository.GetByProject(project.Id))
        {
            AddConnectorVisual(connector);
        }

        ProjectListRoot.Visibility = Visibility.Collapsed;
        CanvasRoot.Visibility = Visibility.Visible;
        CanvasRoot.Focus();
    }

    private void BackToProjectsButton_OnClick(object sender, RoutedEventArgs e)
    {
        _currentProject = null;
        CanvasRoot.Visibility = Visibility.Collapsed;
        ProjectListRoot.Visibility = Visibility.Visible;
        RefreshProjectsList();
    }

    // Escape closes the canvas (back to project list) first; Ctrl+Z/Ctrl+Y drive undo/redo
    // unless focus is in a text note, where the native textbox undo should apply instead.
    private void CanvasRoot_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            BackToProjectsButton_OnClick(sender, e);
            e.Handled = true;
            return;
        }

        if (Keyboard.FocusedElement is TextBox) return;

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key == Key.Z)
        {
            Undo();
            e.Handled = true;
        }
        else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key == Key.Y)
        {
            Redo();
            e.Handled = true;
        }
    }

    private void UndoButton_OnClick(object sender, RoutedEventArgs e) => Undo();
    private void RedoButton_OnClick(object sender, RoutedEventArgs e) => Redo();

    private void Undo()
    {
        if (_undoStack.Count == 0) return;
        var command = _undoStack.Pop();
        command.Undo();
        _redoStack.Push(command);
    }

    private void Redo()
    {
        if (_redoStack.Count == 0) return;
        var command = _redoStack.Pop();
        command.Do();
        _undoStack.Push(command);
    }

    private void Execute(ICanvasCommand command)
    {
        command.Do();
        _undoStack.Push(command);
        _redoStack.Clear();
    }

    private void AddTextNoteButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_currentProject is null) return;
        AddElement(new CanvasElement
        {
            ProjectId = _currentProject.Id,
            Type = CanvasElementType.TextNote,
            X = 100,
            Y = 100,
            TextContent = "Nouvelle note",
            Width = DefaultTextWidth,
            Height = DefaultTextHeight
        });
    }

    private void AddImageButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_currentProject is null) return;

        var dialog = new OpenFileDialog { Filter = "Image files|*.png;*.jpg;*.jpeg;*.bmp;*.gif" };
        if (dialog.ShowDialog() != true) return;

        var relativePath = _mediaStorage.ImportArtwork(dialog.FileName);
        AddElement(new CanvasElement
        {
            ProjectId = _currentProject.Id,
            Type = CanvasElementType.Image,
            X = 100,
            Y = 100,
            ImagePath = relativePath,
            Width = DefaultImageWidth,
            Height = DefaultImageHeight
        });
    }

    private void AddReferenceButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_currentProject is null) return;

        ObservationPickerList.ItemsSource = _observationRepository.GetAllWithArtwork();
        ReferencePickerOverlay.Visibility = Visibility.Visible;
        ReferencePickerOverlay.Focus();
    }

    private void ReferencePickerOverlay_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            ReferencePickerOverlay.Visibility = Visibility.Collapsed;
            e.Handled = true;
        }
    }

    private void ObservationPickerList_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ObservationPickerList.SelectedItem is not ObservationRepository.ObservationSummary summary || _currentProject is null) return;

        ReferencePickerOverlay.Visibility = Visibility.Collapsed;
        ObservationPickerList.SelectedItem = null;

        AddElement(new CanvasElement
        {
            ProjectId = _currentProject.Id,
            Type = CanvasElementType.ObservationReference,
            X = 100,
            Y = 100,
            ObservationId = summary.ObservationId,
            Width = DefaultReferenceWidth,
            Height = DefaultReferenceHeight
        });
    }

    // Toggles "connect mode": the next two element clicks draw a schematic arrow between them.
    private void DrawArrowButton_OnClick(object sender, RoutedEventArgs e)
    {
        _isConnectMode = !_isConnectMode;
        if (_pendingConnectFromId is int previousId && _elementVisuals.TryGetValue(previousId, out var previousVisual) && previousVisual is Border previousBorder)
        {
            previousBorder.BorderBrush = NormalBorderBrush;
        }
        _pendingConnectFromId = null;
        DrawArrowButton.Content = _isConnectMode ? "Annuler la flèche" : "Tracer une flèche";
    }

    private void HandleConnectClick(int elementId, Border border)
    {
        if (_pendingConnectFromId is null)
        {
            _pendingConnectFromId = elementId;
            border.BorderBrush = PendingConnectBrush;
        }
        else if (_pendingConnectFromId == elementId)
        {
            border.BorderBrush = NormalBorderBrush;
            _pendingConnectFromId = null;
        }
        else
        {
            var fromId = _pendingConnectFromId.Value;
            if (_elementVisuals.TryGetValue(fromId, out var fromVisual) && fromVisual is Border fromBorder)
            {
                fromBorder.BorderBrush = NormalBorderBrush;
            }
            AddConnector(fromId, elementId);
            _pendingConnectFromId = null;
            _isConnectMode = false;
            DrawArrowButton.Content = "Tracer une flèche";
        }
    }

    private void AddElement(CanvasElement element)
    {
        FrameworkElement? visual = null;
        int? referenceId = null;

        Execute(new CanvasCommand(
            doAction: () =>
            {
                element.Id = _canvasRepository.Add(element);
                if (element.Type == CanvasElementType.ObservationReference && element.ObservationId is int obsId)
                {
                    referenceId = _referenceRepository.Add(new Reference { ObservationId = obsId, ProjectId = element.ProjectId });
                }
                visual = CreateVisual(element);
                _elementVisuals[element.Id] = visual;
                ElementCanvas.Children.Add(visual);
            },
            undoAction: () =>
            {
                ElementCanvas.Children.Remove(visual);
                _elementVisuals.Remove(element.Id);
                _canvasRepository.Delete(element.Id);
                if (referenceId is int rid) _referenceRepository.Delete(rid);
            }));
    }

    private void DeleteElement(CanvasElement element, FrameworkElement visual)
    {
        int? referenceId = element.Type == CanvasElementType.ObservationReference
            ? _referenceRepository.GetByProject(element.ProjectId).FirstOrDefault(r => r.ObservationId == element.ObservationId)?.Id
            : null;

        // Arrows attached to this element can't dangle - they're removed together with it and restored together on undo.
        var affectedConnectors = _connectorRepository.GetByProject(element.ProjectId)
            .Where(c => c.FromElementId == element.Id || c.ToElementId == element.Id)
            .ToList();

        Execute(new CanvasCommand(
            doAction: () =>
            {
                foreach (var connector in affectedConnectors) RemoveConnectorVisual(connector.Id);
                ElementCanvas.Children.Remove(visual);
                _elementVisuals.Remove(element.Id);
                _canvasRepository.Delete(element.Id);
                if (referenceId is int rid) _referenceRepository.Delete(rid);
            },
            undoAction: () =>
            {
                _canvasRepository.Restore(element);
                _elementVisuals[element.Id] = visual;
                if (element.Type == CanvasElementType.ObservationReference && element.ObservationId is int obsId)
                {
                    referenceId = _referenceRepository.Add(new Reference { ObservationId = obsId, ProjectId = element.ProjectId });
                }
                ElementCanvas.Children.Add(visual);
                foreach (var connector in affectedConnectors)
                {
                    _connectorRepository.Restore(connector);
                    AddConnectorVisual(connector);
                }
            }));
    }

    private void MoveElement(CanvasElement element, FrameworkElement visual, double oldX, double oldY, double newX, double newY)
    {
        Execute(new CanvasCommand(
            doAction: () =>
            {
                Canvas.SetLeft(visual, newX);
                Canvas.SetTop(visual, newY);
                _canvasRepository.UpdatePosition(element.Id, newX, newY);
                element.X = newX;
                element.Y = newY;
                UpdateConnectorsFor(element.Id);
            },
            undoAction: () =>
            {
                Canvas.SetLeft(visual, oldX);
                Canvas.SetTop(visual, oldY);
                _canvasRepository.UpdatePosition(element.Id, oldX, oldY);
                element.X = oldX;
                element.Y = oldY;
                UpdateConnectorsFor(element.Id);
            }));
    }

    private void ResizeElement(CanvasElement element, FrameworkElement visual, double oldWidth, double oldHeight, double newWidth, double newHeight)
    {
        Execute(new CanvasCommand(
            doAction: () =>
            {
                visual.Width = newWidth;
                visual.Height = newHeight;
                _canvasRepository.UpdateSize(element.Id, newWidth, newHeight);
                element.Width = newWidth;
                element.Height = newHeight;
                UpdateConnectorsFor(element.Id);
            },
            undoAction: () =>
            {
                visual.Width = oldWidth;
                visual.Height = oldHeight;
                _canvasRepository.UpdateSize(element.Id, oldWidth, oldHeight);
                element.Width = oldWidth;
                element.Height = oldHeight;
                UpdateConnectorsFor(element.Id);
            }));
    }

    private void EditText(CanvasElement element, TextBox textBox, string oldText, string newText)
    {
        Execute(new CanvasCommand(
            doAction: () =>
            {
                textBox.Text = newText;
                _canvasRepository.UpdateText(element.Id, newText);
                element.TextContent = newText;
            },
            undoAction: () =>
            {
                textBox.Text = oldText;
                _canvasRepository.UpdateText(element.Id, oldText);
                element.TextContent = oldText;
            }));
    }

    private void AddConnector(int fromElementId, int toElementId)
    {
        if (_currentProject is null) return;

        var connector = new CanvasConnector { ProjectId = _currentProject.Id, FromElementId = fromElementId, ToElementId = toElementId };

        Execute(new CanvasCommand(
            doAction: () =>
            {
                connector.Id = _connectorRepository.Add(connector);
                AddConnectorVisual(connector);
            },
            undoAction: () => RemoveConnectorVisual(connector.Id)));
    }

    private void AddConnectorVisual(CanvasConnector connector)
    {
        var connectorBrush = (Brush)Application.Current.FindResource("Accent2Brush");
        var line = new Line { Stroke = connectorBrush, StrokeThickness = 2 };
        var arrowHead = new Polygon { Fill = connectorBrush };

        void DeleteThisConnector()
        {
            Execute(new CanvasCommand(
                doAction: () => RemoveConnectorVisual(connector.Id),
                undoAction: () =>
                {
                    _connectorRepository.Restore(connector);
                    AddConnectorVisual(connector);
                }));
        }

        line.MouseLeftButtonDown += (_, e) => { e.Handled = true; DeleteThisConnector(); };
        arrowHead.MouseLeftButtonDown += (_, e) => { e.Handled = true; DeleteThisConnector(); };

        Panel.SetZIndex(line, 0);
        Panel.SetZIndex(arrowHead, 0);
        ElementCanvas.Children.Add(line);
        ElementCanvas.Children.Add(arrowHead);
        _connectorVisuals[connector.Id] = new ConnectorVisual(line, arrowHead, connector.FromElementId, connector.ToElementId);
        UpdateConnectorVisual(connector.Id);
    }

    private void RemoveConnectorVisual(int connectorId)
    {
        if (_connectorVisuals.TryGetValue(connectorId, out var visual))
        {
            ElementCanvas.Children.Remove(visual.Line);
            ElementCanvas.Children.Remove(visual.ArrowHead);
            _connectorVisuals.Remove(connectorId);
        }
        _connectorRepository.Delete(connectorId);
    }

    private void UpdateConnectorsFor(int elementId)
    {
        foreach (var connectorId in _connectorVisuals
                     .Where(kv => kv.Value.FromElementId == elementId || kv.Value.ToElementId == elementId)
                     .Select(kv => kv.Key)
                     .ToList())
        {
            UpdateConnectorVisual(connectorId);
        }
    }

    private void UpdateConnectorVisual(int connectorId)
    {
        if (!_connectorVisuals.TryGetValue(connectorId, out var visual)) return;
        if (!_elementVisuals.TryGetValue(visual.FromElementId, out var fromElement)) return;
        if (!_elementVisuals.TryGetValue(visual.ToElementId, out var toElement)) return;

        var fromRect = GetBounds(fromElement);
        var toRect = GetBounds(toElement);
        var fromCenter = GetCenter(fromRect);
        var toCenter = GetCenter(toRect);

        // Clip to each box's edge so the arrow touches the border instead of crossing through the middle.
        var from = ClipToRectEdge(fromRect, fromCenter, toCenter);
        var to = ClipToRectEdge(toRect, toCenter, fromCenter);

        visual.Line.X1 = from.X;
        visual.Line.Y1 = from.Y;
        visual.Line.X2 = to.X;
        visual.Line.Y2 = to.Y;

        var angle = Math.Atan2(to.Y - from.Y, to.X - from.X);
        const double arrowLength = 12, arrowWidth = 7;
        var baseCenter = new Point(to.X - arrowLength * Math.Cos(angle), to.Y - arrowLength * Math.Sin(angle));
        var left = new Point(baseCenter.X - arrowWidth * Math.Sin(angle), baseCenter.Y + arrowWidth * Math.Cos(angle));
        var right = new Point(baseCenter.X + arrowWidth * Math.Sin(angle), baseCenter.Y - arrowWidth * Math.Cos(angle));

        visual.ArrowHead.Points = new PointCollection { to, left, right };
    }

    // Uses the explicitly-set Width/Height (not ActualWidth/ActualHeight) so this is correct even
    // before WPF has run a layout pass on a freshly-created element (e.g. right after opening a project).
    private static Rect GetBounds(FrameworkElement element) =>
        new(Canvas.GetLeft(element), Canvas.GetTop(element), element.Width, element.Height);

    private static Point GetCenter(Rect rect) => new(rect.X + rect.Width / 2, rect.Y + rect.Height / 2);

    // Finds where the line from `center` towards `target` crosses the edge of `rect` (axis-aligned box centered on `center`).
    private static Point ClipToRectEdge(Rect rect, Point center, Point target)
    {
        var dx = target.X - center.X;
        var dy = target.Y - center.Y;
        if (dx == 0 && dy == 0) return center;

        var halfWidth = rect.Width / 2;
        var halfHeight = rect.Height / 2;
        var scaleX = dx != 0 ? halfWidth / Math.Abs(dx) : double.PositiveInfinity;
        var scaleY = dy != 0 ? halfHeight / Math.Abs(dy) : double.PositiveInfinity;
        var scale = Math.Min(scaleX, scaleY);

        return new Point(center.X + dx * scale, center.Y + dy * scale);
    }

    private FrameworkElement CreateVisual(CanvasElement element)
    {
        FrameworkElement content = element.Type switch
        {
            CanvasElementType.TextNote => CreateTextNoteContent(element),
            CanvasElementType.Image => CreateImageContent(element),
            CanvasElementType.ObservationReference => CreateReferenceContent(element),
            _ => new TextBlock { Text = "?" }
        };
        // Reserve space so long text/images don't render underneath the delete button or resize handle.
        content.Margin = new Thickness(0, 0, 16, 12);

        var deleteButton = new Button
        {
            Content = "✕",
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = (Brush)Application.Current.FindResource("TextMutedBrush"),
            Cursor = Cursors.Hand,
            Padding = new Thickness(2),
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Right
        };

        var resizeHandle = new Rectangle
        {
            Width = 10,
            Height = 10,
            Fill = (Brush)Application.Current.FindResource("TextMutedBrush"),
            Cursor = Cursors.SizeNWSE,
            Tag = "ResizeHandle",
            VerticalAlignment = VerticalAlignment.Bottom,
            HorizontalAlignment = HorizontalAlignment.Right
        };

        var grid = new Grid();
        grid.Children.Add(content);
        grid.Children.Add(deleteButton);
        grid.Children.Add(resizeHandle);

        var border = new Border
        {
            Background = (Brush)Application.Current.FindResource("Surface3Brush"),
            BorderBrush = NormalBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8),
            Width = element.Width ?? DefaultTextWidth,
            Height = element.Height ?? DefaultTextHeight,
            Child = grid,
            Cursor = Cursors.SizeAll
        };
        Panel.SetZIndex(border, 1);

        Canvas.SetLeft(border, element.X);
        Canvas.SetTop(border, element.Y);

        Point? dragStart = null;
        double dragStartLeft = 0, dragStartTop = 0;

        border.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (_isConnectMode)
            {
                e.Handled = true;
                HandleConnectClick(element.Id, border);
                return;
            }

            if (ShouldSkipDrag(e.OriginalSource as DependencyObject)) return;

            dragStart = e.GetPosition(ElementCanvas);
            dragStartLeft = Canvas.GetLeft(border);
            dragStartTop = Canvas.GetTop(border);
            border.CaptureMouse();
            e.Handled = true;
        };
        border.MouseMove += (_, e) =>
        {
            if (dragStart is null || e.LeftButton != MouseButtonState.Pressed) return;
            var pos = e.GetPosition(ElementCanvas);
            Canvas.SetLeft(border, dragStartLeft + (pos.X - dragStart.Value.X));
            Canvas.SetTop(border, dragStartTop + (pos.Y - dragStart.Value.Y));
            UpdateConnectorsFor(element.Id);
        };
        border.MouseLeftButtonUp += (_, _) =>
        {
            if (dragStart is null) return;
            border.ReleaseMouseCapture();
            dragStart = null;

            var finalLeft = Canvas.GetLeft(border);
            var finalTop = Canvas.GetTop(border);
            if (Math.Abs(finalLeft - dragStartLeft) > 0.5 || Math.Abs(finalTop - dragStartTop) > 0.5)
            {
                MoveElement(element, border, dragStartLeft, dragStartTop, finalLeft, finalTop);
            }
        };

        deleteButton.Click += (_, e) =>
        {
            e.Handled = true;
            DeleteElement(element, border);
        };

        Point? resizeStart = null;
        double resizeStartWidth = 0, resizeStartHeight = 0;

        resizeHandle.PreviewMouseLeftButtonDown += (_, e) =>
        {
            resizeStart = e.GetPosition(ElementCanvas);
            resizeStartWidth = border.Width;
            resizeStartHeight = border.Height;
            resizeHandle.CaptureMouse();
            e.Handled = true;
        };
        resizeHandle.PreviewMouseMove += (_, e) =>
        {
            if (resizeStart is null || e.LeftButton != MouseButtonState.Pressed) return;
            var pos = e.GetPosition(ElementCanvas);
            border.Width = Math.Max(MinElementWidth, resizeStartWidth + (pos.X - resizeStart.Value.X));
            border.Height = Math.Max(MinElementHeight, resizeStartHeight + (pos.Y - resizeStart.Value.Y));
            UpdateConnectorsFor(element.Id);
            e.Handled = true;
        };
        resizeHandle.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (resizeStart is null) return;
            resizeHandle.ReleaseMouseCapture();
            resizeStart = null;
            if (Math.Abs(border.Width - resizeStartWidth) > 0.5 || Math.Abs(border.Height - resizeStartHeight) > 0.5)
            {
                ResizeElement(element, border, resizeStartWidth, resizeStartHeight, border.Width, border.Height);
            }
            e.Handled = true;
        };

        return border;
    }

    private FrameworkElement CreateTextNoteContent(CanvasElement element)
    {
        var textBox = new TextBox
        {
            Text = element.TextContent ?? string.Empty,
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = true,
            Background = Brushes.Transparent,
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0)
        };

        var originalText = textBox.Text;
        textBox.GotFocus += (_, _) => originalText = textBox.Text;
        textBox.LostFocus += (_, _) =>
        {
            if (textBox.Text != originalText)
            {
                EditText(element, textBox, originalText, textBox.Text);
                originalText = textBox.Text;
            }
        };

        return textBox;
    }

    private FrameworkElement CreateImageContent(CanvasElement element)
    {
        return new Image
        {
            Source = LoadImage(MediaStorage.ResolveFullPath(element.ImagePath!)),
            Stretch = Stretch.Uniform
        };
    }

    private FrameworkElement CreateReferenceContent(CanvasElement element)
    {
        var observation = element.ObservationId is int obsId ? _observationRepository.GetById(obsId) : null;
        var artwork = observation is not null ? _artworkRepository.GetById(observation.ArtworkId) : null;

        // Shown directly on the canvas (no popup) - resize the element to read more of the observation.
        var header = observation is not null && artwork is not null
            ? $"{artwork.Title} — {observation.Subject}"
            : "(deleted observation)";
        var body = observation?.Content ?? string.Empty;

        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = header,
            Foreground = Brushes.White,
            FontWeight = FontWeights.Bold,
            FontStyle = FontStyles.Italic,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 4)
        });
        if (body.Length > 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = body,
                Foreground = (Brush)Application.Current.FindResource("TextBrush"),
                TextWrapping = TextWrapping.Wrap
            });
        }

        return panel;
    }

    // --- Journal personnel ---
    // A calendar (chips mark days with an entry) + a "book" page showing one entry at a time,
    // in chronological order. Opens on today so writing a new entry is immediate.

    private DateTime _currentPageDate = DateTime.Today;

    private void RefreshCalendarChips()
    {
        EntryDateBackgroundConverter.DatesWithEntries = _journalEntryRepository.GetAll()
            .Select(entry => entry.EntryDate.Date)
            .ToHashSet();

        // WPF doesn't re-run the day-button Background binding on its own when the underlying set
        // changes - reassigning the style forces every day cell to rebuild and re-evaluate it.
        var style = EntryCalendar.CalendarDayButtonStyle;
        EntryCalendar.CalendarDayButtonStyle = null;
        EntryCalendar.CalendarDayButtonStyle = style;
    }

    private void NavigateToDate(DateTime date)
    {
        _currentPageDate = date.Date;
        _selectedEntry = _journalEntryRepository.GetAll().FirstOrDefault(entry => entry.EntryDate.Date == _currentPageDate);
        SaveActiveItem();

        PageDateText.Text = _currentPageDate.ToString("dddd d MMMM yyyy", CultureInfo.CurrentCulture);
        EntryTitleInput.Text = _selectedEntry?.Title ?? string.Empty;
        EntryTextInput.Text = _selectedEntry?.Text ?? string.Empty;
        EntryTagsInput.Text = _selectedEntry?.Tags ?? string.Empty;
        RefreshEntryPhotos();

        if (EntryCalendar.SelectedDate != _currentPageDate) EntryCalendar.SelectedDate = _currentPageDate;
        if (EntryCalendar.DisplayDate.Year != _currentPageDate.Year || EntryCalendar.DisplayDate.Month != _currentPageDate.Month)
        {
            EntryCalendar.DisplayDate = _currentPageDate;
        }
    }

    private void EntryCalendar_OnSelectedDatesChanged(object sender, SelectionChangedEventArgs e)
    {
        if (EntryCalendar.SelectedDate is { } date && date.Date != _currentPageDate)
        {
            NavigateToDate(date);
        }
    }

    private void PreviousEntryButton_OnClick(object sender, RoutedEventArgs e)
    {
        var previous = _journalEntryRepository.GetAll()
            .Where(entry => entry.EntryDate.Date < _currentPageDate)
            .OrderByDescending(entry => entry.EntryDate)
            .FirstOrDefault();
        if (previous is not null) NavigateToDate(previous.EntryDate);
    }

    private void NextEntryButton_OnClick(object sender, RoutedEventArgs e)
    {
        var next = _journalEntryRepository.GetAll()
            .Where(entry => entry.EntryDate.Date > _currentPageDate)
            .OrderBy(entry => entry.EntryDate)
            .FirstOrDefault();
        if (next is not null) NavigateToDate(next.EntryDate);
    }

    // The page for "today" (or any browsed date) has no db row until the user actually writes
    // something - avoids littering the journal with empty entries for dates merely browsed past.
    private JournalEntry EnsureSelectedEntryExists()
    {
        if (_selectedEntry is not null) return _selectedEntry;

        var entry = new JournalEntry { Title = string.Empty, EntryDate = _currentPageDate };
        entry.Id = _journalEntryRepository.Add(entry);
        _selectedEntry = entry;
        RefreshCalendarChips();
        return entry;
    }

    private void EntryTitleInput_OnLostFocus(object sender, RoutedEventArgs e)
    {
        var title = EntryTitleInput.Text.Trim();
        if (_selectedEntry is null && title.Length == 0) return;

        var entry = EnsureSelectedEntryExists();
        entry.Title = title;
        _journalEntryRepository.Update(entry.Id, entry.Title, entry.Text, entry.EntryDate, entry.Tags);
    }

    private void EntryTextInput_OnLostFocus(object sender, RoutedEventArgs e)
    {
        if (_selectedEntry is null && EntryTextInput.Text.Length == 0) return;

        var entry = EnsureSelectedEntryExists();
        entry.Text = EntryTextInput.Text;
        _journalEntryRepository.Update(entry.Id, entry.Title, entry.Text, entry.EntryDate, entry.Tags);
    }

    private void EntryTagsInput_OnLostFocus(object sender, RoutedEventArgs e)
    {
        var tags = EntryTagsInput.Text.Trim();
        if (_selectedEntry is null && tags.Length == 0) return;

        var entry = EnsureSelectedEntryExists();
        entry.Tags = tags;
        _journalEntryRepository.Update(entry.Id, entry.Title, entry.Text, entry.EntryDate, entry.Tags);
    }

    private void RefreshEntryPhotos()
    {
        EntryPhotosPanel.Children.Clear();
        if (_selectedEntry is null) return;

        foreach (var photo in _journalPhotoRepository.GetByEntry(_selectedEntry.Id))
        {
            var image = new Image
            {
                Source = LoadImage(MediaStorage.ResolveFullPath(photo.ImagePath)),
                Height = 90,
                Stretch = Stretch.Uniform,
                Margin = new Thickness(0, 0, 6, 6)
            };

            var deleteButton = new Button
            {
                Content = "✕",
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = (Brush)Application.Current.FindResource("TextMutedBrush"),
                Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Top,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            deleteButton.Click += (_, e) =>
            {
                e.Handled = true;
                _journalPhotoRepository.Delete(photo.Id);
                MediaStorage.DeleteFile(photo.ImagePath);
                RefreshEntryPhotos();
            };

            var grid = new Grid();
            grid.Children.Add(image);
            grid.Children.Add(deleteButton);
            EntryPhotosPanel.Children.Add(grid);
        }
    }

    private void AddEntryPhotoButton_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Image files|*.png;*.jpg;*.jpeg;*.bmp;*.gif" };
        if (dialog.ShowDialog() != true) return;

        var entry = EnsureSelectedEntryExists();
        var relativePath = _mediaStorage.ImportJournalPhoto(dialog.FileName);
        _journalPhotoRepository.Add(new JournalPhoto { EntryId = entry.Id, ImagePath = relativePath });
        RefreshEntryPhotos();
    }

    private void DeleteEntryButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_selectedEntry is null) return;

        foreach (var photo in _journalPhotoRepository.GetByEntry(_selectedEntry.Id))
        {
            _journalPhotoRepository.Delete(photo.Id);
            MediaStorage.DeleteFile(photo.ImagePath);
        }
        _journalEntryRepository.Delete(_selectedEntry.Id);

        RefreshCalendarChips();
        NavigateToDate(_currentPageDate);
    }

    // --- Journal de recettes ---
    // A sidebar (search + category chips + title list) and a single recipe "page", like a real
    // recipe book you flip through rather than a flat card grid.

    private List<Recipe> GetFilteredRecipes()
    {
        var recipes = _recipeRepository.GetAll();
        var search = RecipeSearchInput.Text.Trim();

        return recipes.Where(recipe =>
            (_selectedRecipeCategoryFilter is null || string.Equals(recipe.Category, _selectedRecipeCategoryFilter, StringComparison.OrdinalIgnoreCase)) &&
            (search.Length == 0 ||
             recipe.Title.Contains(search, StringComparison.OrdinalIgnoreCase) ||
             recipe.Tags.Contains(search, StringComparison.OrdinalIgnoreCase) ||
             recipe.Ingredients.Contains(search, StringComparison.OrdinalIgnoreCase))).ToList();
    }

    private void RefreshRecipesList()
    {
        var allRecipes = _recipeRepository.GetAll();

        var categories = allRecipes
            .Select(recipe => recipe.Category)
            .Where(category => category.Length > 0)
            .Distinct()
            .OrderBy(category => category);

        RecipeCategoryFilterPanel.Children.Clear();
        foreach (var category in categories)
        {
            var chip = new Button
            {
                Content = category,
                Style = (Style)FindResource("ChipButtonStyle"),
                Background = category == _selectedRecipeCategoryFilter ? (Brush)Application.Current.FindResource("AccentSoftBrush") : null
            };
            chip.Click += (_, _) =>
            {
                _selectedRecipeCategoryFilter = _selectedRecipeCategoryFilter == category ? null : category;
                RefreshRecipesList();
            };
            RecipeCategoryFilterPanel.Children.Add(chip);
        }

        var filtered = GetFilteredRecipes();
        RecipeTitleList.ItemsSource = filtered;

        if (_selectedRecipe is not null && filtered.Any(recipe => recipe.Id == _selectedRecipe.Id))
        {
            RecipeTitleList.SelectedItem = filtered.First(recipe => recipe.Id == _selectedRecipe.Id);
        }
        else if (filtered.Count > 0)
        {
            OpenRecipePage(filtered[0]);
        }
        else
        {
            OpenRecipePage(null);
        }
    }

    private void RecipeSearchInput_OnTextChanged(object sender, TextChangedEventArgs e) => RefreshRecipesList();

    private void RecipeTitleList_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RecipeTitleList.SelectedItem is Recipe recipe) OpenRecipePage(recipe);
    }

    private void NewRecipeButton_OnClick(object sender, RoutedEventArgs e)
    {
        var title = NewRecipeTitleInput.Text.Trim();
        if (title.Length == 0) return;

        var recipe = new Recipe { Title = title, CreatedAt = DateTime.Now };
        recipe.Id = _recipeRepository.Add(recipe);

        // Set before refreshing so RefreshRecipesList's "keep current selection" branch picks the
        // new recipe up (it re-fetches from the db, so matching must happen by Id, not by reference).
        _selectedRecipe = recipe;
        NewRecipeTitleInput.Clear();
        RefreshRecipesList();
    }

    private void OpenRecipePage(Recipe? recipe)
    {
        _selectedRecipe = recipe;
        SaveActiveItem();

        RecipePageTitleText.Text = recipe?.Title ?? "No recipe";
        RecipeTitleInput.Text = recipe?.Title ?? string.Empty;
        RecipeCategoryInput.Text = recipe?.Category ?? string.Empty;
        RecipeIngredientsInput.Text = recipe?.Ingredients ?? string.Empty;
        RecipeInstructionsInput.Text = recipe?.Instructions ?? string.Empty;
        RecipeNotesInput.Text = recipe?.Notes ?? string.Empty;
        RecipeTagsInput.Text = recipe?.Tags ?? string.Empty;
        RefreshRecipeCoverImage();

        var isEnabled = recipe is not null;
        foreach (var input in new Control[]
                 {
                     RecipeTitleInput, RecipeCategoryInput, RecipeIngredientsInput, RecipeInstructionsInput,
                     RecipeNotesInput, RecipeTagsInput, DeleteRecipeButton
                 })
        {
            input.IsEnabled = isEnabled;
        }
    }

    private void PreviousRecipeButton_OnClick(object sender, RoutedEventArgs e)
    {
        var filtered = GetFilteredRecipes();
        var index = _selectedRecipe is null ? -1 : filtered.FindIndex(r => r.Id == _selectedRecipe.Id);
        if (index > 0) RecipeTitleList.SelectedItem = filtered[index - 1];
    }

    private void NextRecipeButton_OnClick(object sender, RoutedEventArgs e)
    {
        var filtered = GetFilteredRecipes();
        var index = _selectedRecipe is null ? -1 : filtered.FindIndex(r => r.Id == _selectedRecipe.Id);
        if (index >= 0 && index < filtered.Count - 1) RecipeTitleList.SelectedItem = filtered[index + 1];
    }

    private void RecipeField_OnLostFocus(object sender, RoutedEventArgs e)
    {
        if (_selectedRecipe is null) return;

        _selectedRecipe.Title = RecipeTitleInput.Text.Trim();
        _selectedRecipe.Category = RecipeCategoryInput.Text.Trim();
        _selectedRecipe.Ingredients = RecipeIngredientsInput.Text;
        _selectedRecipe.Instructions = RecipeInstructionsInput.Text;
        _selectedRecipe.Notes = RecipeNotesInput.Text;
        _selectedRecipe.Tags = RecipeTagsInput.Text.Trim();
        RecipePageTitleText.Text = _selectedRecipe.Title;

        _recipeRepository.Update(_selectedRecipe.Id, _selectedRecipe.Title, _selectedRecipe.Category,
            _selectedRecipe.Ingredients, _selectedRecipe.Instructions, _selectedRecipe.Notes, _selectedRecipe.Tags);

        RefreshRecipesList();
    }

    private void RefreshRecipeCoverImage()
    {
        if (_selectedRecipe?.CoverImagePath is { } path)
        {
            RecipeCoverImage.Source = LoadImage(MediaStorage.ResolveFullPath(path));
            RecipeCoverImage.Visibility = Visibility.Visible;
            RemoveRecipeCoverButton.Visibility = Visibility.Visible;
        }
        else
        {
            RecipeCoverImage.Visibility = Visibility.Collapsed;
            RemoveRecipeCoverButton.Visibility = Visibility.Collapsed;
        }
    }

    private void SetRecipeCoverButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_selectedRecipe is null) return;

        var dialog = new OpenFileDialog { Filter = "Image files|*.png;*.jpg;*.jpeg;*.bmp;*.gif" };
        if (dialog.ShowDialog() != true) return;

        var relativePath = _mediaStorage.ImportRecipePhoto(dialog.FileName);
        _recipeRepository.UpdateCoverImage(_selectedRecipe.Id, relativePath);
        _selectedRecipe.CoverImagePath = relativePath;
        RefreshRecipeCoverImage();
    }

    private void RemoveRecipeCoverButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_selectedRecipe?.CoverImagePath is not { } path) return;

        _recipeRepository.UpdateCoverImage(_selectedRecipe.Id, null);
        _selectedRecipe.CoverImagePath = null;
        MediaStorage.DeleteFile(path);
        RefreshRecipeCoverImage();
    }

    private void DeleteRecipeButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_selectedRecipe is null) return;

        if (_selectedRecipe.CoverImagePath is { } path) MediaStorage.DeleteFile(path);
        _recipeRepository.Delete(_selectedRecipe.Id);

        _selectedRecipe = null;
        RefreshRecipesList();
    }

    // --- Recueil de poésie ---
    // A notebook, not a canvas: a sidebar (search + recueil filter + poem titles) and a single
    // poem "page" at a time, styled like verse rather than a plain form field.

    // A selectable row in the recueil filter list: either "All", "No recueil", or a real Recueil.
    private enum RecueilFilterKind { All, NoRecueil, Recueil }

    private sealed record RecueilFilterItem(string Title, Recueil? Recueil, RecueilFilterKind Kind);

    // The currently selected filter row (null while nothing meaningful is selected).
    private RecueilFilterItem? _selectedRecueilFilterItem;

    // Which of the three poetry panes is showing: the bookshelf (covers), an open book
    // (a recueil's poems), or the poem editor. The recueil currently open as a "book".
    private enum PoetryPane { Bookshelf, Book, Editor }
    private Recueil? _openRecueil;

    // A palette of cover colors, assigned deterministically by recueil id so a given
    // recueil always keeps the same spine color.
    private static readonly string[] CoverColors =
    {
        "#8B5CF6", "#3B82F6", "#EC4899", "#F59E0B", "#10B981",
        "#EF4444", "#6366F1", "#14B8A6", "#F97316", "#A855F7"
    };

    // View-model for a cover on the bookshelf.
    private sealed record RecueilCoverItem(Recueil? Recueil, string Title, string PoemCountText, Brush CoverBrush);

    // View-model for a poem card inside an open book.
    private sealed record PoemCardItem(Poem Poem, string Title, string Preview, string PoemCountText);

    private List<Poem> GetFilteredPoems()
    {
        var search = PoemSearchInput.Text.Trim();
        IEnumerable<Poem> poems = _poemRepository.GetAll();

        // "Recueil" narrows to that anthology; "No recueil" shows only standalone poems;
        // "All" leaves every poem (assigned and unassigned) in the list.
        switch (_selectedRecueilFilterItem?.Kind)
        {
            case RecueilFilterKind.Recueil:
                poems = poems.Where(p => p.RecueilId == _selectedRecueilFilterItem!.Recueil?.Id);
                break;
            case RecueilFilterKind.NoRecueil:
                poems = poems.Where(p => p.RecueilId is null);
                break;
        }

        poems = poems.Where(poem =>
            search.Length == 0 ||
            poem.Title.Contains(search, StringComparison.OrdinalIgnoreCase) ||
            poem.Tags.Contains(search, StringComparison.OrdinalIgnoreCase) ||
            poem.Text.Contains(search, StringComparison.OrdinalIgnoreCase));
        return poems.ToList();
    }

    // The recueil id a selected poem should belong to, or null when the filter is "All"/"No recueil".
    private int? GetSelectedRecueilId() => _selectedRecueilFilterItem?.Recueil?.Id;

    private void RefreshPoetry()
    {
        EnsureUnclassifiedRecueil();
        RefreshRecueilFilterList();
        RefreshBookshelf();

        // If a poem was selected (e.g. restored or deep-linked), open straight to its editor.
        if (_selectedPoem is not null)
        {
            _openRecueil = _selectedPoem.RecueilId is { } rid
                ? _recueilRepository.GetAll().FirstOrDefault(r => r.Id == rid)
                : null;
            OpenPoemPage(_selectedPoem);
        }
        else
        {
            ShowPoetryPane(PoetryPane.Bookshelf);
        }
    }

    // The always-present "Pas classé" recueil that catches unclassified poems.
    private const string UnclassifiedTitle = "Pas classé";

    private Recueil EnsureUnclassifiedRecueil()
    {
        var existing = _recueilRepository.GetAll().FirstOrDefault(r => r.Title == UnclassifiedTitle);
        if (existing is not null) return existing;

        var id = _recueilRepository.Add(new Recueil { Title = UnclassifiedTitle, CreatedAt = DateTime.Now });
        return _recueilRepository.GetAll().First(r => r.Id == id);
    }

    private Recueil GetUnclassifiedRecueil() =>
        _recueilRepository.GetAll().FirstOrDefault(r => r.Title == UnclassifiedTitle) ?? EnsureUnclassifiedRecueil();

    private static Brush CoverBrushFor(int id)
    {
        var color = (Color)ColorConverter.ConvertFromString(CoverColors[Math.Abs(id) % CoverColors.Length])!;
        return new SolidColorBrush(color);
    }

    // Switches which of the three poetry panes is visible.
    private void ShowPoetryPane(PoetryPane pane)
    {
        PoetryBookshelf.Visibility = pane == PoetryPane.Bookshelf ? Visibility.Visible : Visibility.Collapsed;
        PoetryBook.Visibility = pane == PoetryPane.Book ? Visibility.Visible : Visibility.Collapsed;
        PoemEditor.Visibility = pane == PoetryPane.Editor ? Visibility.Visible : Visibility.Collapsed;
    }

    // Builds the horizontal shelf of recueil covers.
    private void RefreshBookshelf()
    {
        var allPoems = _poemRepository.GetAll();
        var recueils = _recueilRepository.GetAll();

        var covers = recueils.Select(r =>
        {
            var count = allPoems.Count(p => p.RecueilId == r.Id);
            return new RecueilCoverItem(r, r.Title, count == 1 ? "1 poème" : $"{count} poèmes", CoverBrushFor(r.Id));
        }).ToList();

        RecueilCoverList.ItemsSource = covers;
        BookshelfCount.Text = covers.Count == 1 ? "1 recueil" : $"{covers.Count} recueils";
    }

    // Opens a recueil as a "book": shows its summary and all its poems as cards.
    private void OpenRecueilBook(Recueil recueil)
    {
        _openRecueil = recueil;
        BookTitle.Text = recueil.Title;
        BookRenameInput.Text = recueil.Title;
        BookSummaryInput.Text = recueil.Summary;

        var poems = _poemRepository.GetAll().Where(p => p.RecueilId == recueil.Id).ToList();
        BookDeleteButton.IsEnabled = recueil.Title != UnclassifiedTitle;
        BookRenameInput.IsEnabled = recueil.Title != UnclassifiedTitle;

        PoemCardList.ItemsSource = poems.Select(p => new PoemCardItem(
            p,
            p.Title,
            p.Text.Length > 160 ? p.Text[..160].Trim() + "…" : p.Text,
            string.Empty)).ToList();

        ShowPoetryPane(PoetryPane.Book);
    }

    private void RecueilCover_OnClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: RecueilCoverItem { Recueil: { } recueil } }) OpenRecueilBook(recueil);
    }

    private void PoemCard_OnClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: PoemCardItem { Poem: { } poem } }) OpenPoemPage(poem);
    }

    private void RecueilBookshelfButton_OnClick(object sender, RoutedEventArgs e)
    {
        RefreshBookshelf();
        ShowPoetryPane(PoetryPane.Bookshelf);
    }

    private void BookshelfBackButton_OnClick(object sender, RoutedEventArgs e)
    {
        RefreshBookshelf();
        ShowPoetryPane(PoetryPane.Bookshelf);
    }

    private void PoemEditorBackButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_openRecueil is not null) OpenRecueilBook(_openRecueil);
        else { RefreshBookshelf(); ShowPoetryPane(PoetryPane.Bookshelf); }
    }

    // Horizontal shelf: let the mouse wheel scroll sideways.
    private void BookshelfScrollViewer_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is ScrollViewer viewer)
        {
            viewer.ScrollToHorizontalOffset(viewer.HorizontalOffset - e.Delta);
            e.Handled = true;
        }
    }

    private void BookRenameButton_OnClick(object sender, RoutedEventArgs e) => ApplyBookRename();
    private void BookRenameInput_OnLostFocus(object sender, RoutedEventArgs e) => ApplyBookRename();

    private void ApplyBookRename()
    {
        if (_openRecueil is null || _openRecueil.Title == UnclassifiedTitle) return;

        var title = BookRenameInput.Text.Trim();
        if (title.Length == 0 || title == _openRecueil.Title)
        {
            BookRenameInput.Text = _openRecueil.Title;
            return;
        }

        _recueilRepository.Update(_openRecueil.Id, title);
        _openRecueil.Title = title;
        BookTitle.Text = title;
        RefreshRecueilFilterList();
    }

    private void BookDeleteButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_openRecueil is null || _openRecueil.Title == UnclassifiedTitle) return;

        var poemsInRecueil = _poemRepository.GetAll().Where(p => p.RecueilId == _openRecueil.Id).ToList();
        var message = poemsInRecueil.Count == 0
            ? $"Supprimer le recueil « {_openRecueil.Title} » ? Cette action est irréversible."
            : $"Supprimer le recueil « {_openRecueil.Title} » ? Ses {poemsInRecueil.Count} poème(s) seront déplacés vers « {UnclassifiedTitle} ».";

        if (MessageBox.Show(message, "Supprimer le recueil", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        // Move poems to the always-present unclassified recueil rather than orphaning them.
        var unclassified = GetUnclassifiedRecueil();
        foreach (var poem in poemsInRecueil)
        {
            _poemRepository.Update(poem.Id, unclassified.Id, poem.Title, poem.Text, poem.Tags);
        }
        _recueilRepository.Delete(_openRecueil.Id);

        _openRecueil = null;
        RefreshRecueilFilterList();
        RefreshBookshelf();
        ShowPoetryPane(PoetryPane.Bookshelf);
    }

    private void BookSummaryInput_OnLostFocus(object sender, RoutedEventArgs e)
    {
        if (_openRecueil is null) return;

        var summary = BookSummaryInput.Text.Trim();
        if (summary == _openRecueil.Summary) return;

        _recueilRepository.UpdateSummary(_openRecueil.Id, summary);
        _openRecueil.Summary = summary;
    }

    private void RefreshRecueilFilterList()
    {
        var recueils = _recueilRepository.GetAll();
        var items = new List<RecueilFilterItem>
        {
            new("Tout", null, RecueilFilterKind.All),
            new("Sans recueil", null, RecueilFilterKind.NoRecueil)
        };
        items.AddRange(recueils.Select(r => new RecueilFilterItem(r.Title, r, RecueilFilterKind.Recueil)));
        RecueilFilterList.ItemsSource = items;

        // Preserve the current selection if it still exists.
        if (_selectedRecueilFilterItem is not null)
        {
            var stillThere = items.FirstOrDefault(i =>
                i.Kind == _selectedRecueilFilterItem.Kind &&
                i.Recueil?.Id == _selectedRecueilFilterItem.Recueil?.Id);
            if (stillThere is not null) RecueilFilterList.SelectedItem = stillThere;
        }

        // The recueil picker on the page needs a "none" option in addition to the actual recueils.
        var withNone = new List<Recueil?> { null };
        withNone.AddRange(recueils);
        PoemRecueilPicker.ItemsSource = withNone;
    }

    private void RecueilFilterList_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RecueilFilterList.SelectedItem is not RecueilFilterItem item) return;

        // Remember the selection so the search box can narrow it down afterwards.
        _selectedRecueilFilterItem = item;

        switch (item.Kind)
        {
            case RecueilFilterKind.Recueil when item.Recueil is { } recueil:
                OpenRecueilBook(recueil);
                break;
            case RecueilFilterKind.NoRecueil:
                // "Sans recueil": show only the standalone poems (RecueilId is null).
                ShowStandalonePoems();
                break;
            default:
                // "Tout" just takes the user back to the shelf.
                RefreshBookshelf();
                ShowPoetryPane(PoetryPane.Bookshelf);
                break;
        }
    }

    // Shows every poem that has no recueil assigned, in the book pane.
    private void ShowStandalonePoems()
    {
        var poems = GetFilteredPoems();
        PoemCardList.ItemsSource = poems.Select(p => new PoemCardItem(
            p, p.Title, p.Text.Length > 160 ? p.Text[..160].Trim() + "…" : p.Text, string.Empty)).ToList();
        BookTitle.Text = "Sans recueil";
        BookRenameInput.IsEnabled = false;
        BookDeleteButton.IsEnabled = false;
        BookSummaryInput.Text = string.Empty;
        _openRecueil = null;
        ShowPoetryPane(PoetryPane.Book);
    }

    private void PoemSearchInput_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        var search = PoemSearchInput.Text.Trim();

        // Keep the search filter in sync with the current sidebar selection.
        _selectedRecueilFilterItem ??= RecueilFilterList.SelectedItem as RecueilFilterItem;

        // While searching, the shelf is replaced by the matching poems from across the
        // current filter (recueil, "Sans recueil", or everything).
        var matches = GetFilteredPoems();
        PoemCardList.ItemsSource = matches.Select(p => new PoemCardItem(
            p, p.Title, p.Text.Length > 160 ? p.Text[..160].Trim() + "…" : p.Text, string.Empty)).ToList();
        BookTitle.Text = search.Length == 0 ? CurrentBookTitle() : $"Search: \"{search}\"";
        BookRenameInput.IsEnabled = false;
        BookDeleteButton.IsEnabled = false;
        BookSummaryInput.Text = string.Empty;
        _openRecueil = null;
        ShowPoetryPane(PoetryPane.Book);
    }

    // Title shown at the top of the book pane for the current filter.
    private string CurrentBookTitle() => _selectedRecueilFilterItem switch
    {
        RecueilFilterItem { Kind: RecueilFilterKind.Recueil, Recueil: { } recueil } => recueil.Title,
        RecueilFilterItem { Kind: RecueilFilterKind.NoRecueil } => "Sans recueil",
        _ => "Tout"
    };

    private void NewRecueilButton_OnClick(object sender, RoutedEventArgs e)
    {
        var title = NewRecueilTitleInput.Text.Trim();
        if (title.Length == 0) return;

        _recueilRepository.Add(new Recueil { Title = title, CreatedAt = DateTime.Now });
        NewRecueilTitleInput.Clear();
        RefreshRecueilFilterList();
    }

    private void RecueilRenameButton_OnClick(object sender, RoutedEventArgs e) => ApplyRecueilRename();

    private void RecueilRenameInput_OnLostFocus(object sender, RoutedEventArgs e) => ApplyRecueilRename();

    private void ApplyRecueilRename()
    {
        if (_selectedRecueilFilterItem?.Kind is not RecueilFilterKind.Recueil || _selectedRecueilFilterItem.Recueil is not { } recueil) return;

        var title = RecueilRenameInput.Text.Trim();
        if (title.Length == 0)
        {
            RecueilRenameInput.Text = recueil.Title;
            return;
        }

        if (title != recueil.Title)
        {
            _recueilRepository.Update(recueil.Id, title);
            _selectedRecueilFilterItem!.Recueil!.Title = title;
        }

        RecueilRenameInput.Text = title;
        RefreshRecueilFilterList();
        RecueilFilterList.SelectedItem = _selectedRecueilFilterItem;
    }

    private void RecueilDeleteButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_selectedRecueilFilterItem?.Kind is not RecueilFilterKind.Recueil || _selectedRecueilFilterItem.Recueil is not { } recueil) return;

        var poemsInRecueil = _poemRepository.GetAll().Where(p => p.RecueilId == recueil.Id).ToList();
        var message = poemsInRecueil.Count == 0
            ? $"Supprimer le recueil « {recueil.Title} » ? Cette action est irréversible."
            : $"Supprimer le recueil « {recueil.Title} » ? Ses {poemsInRecueil.Count} poème(s) seront conservés mais désassignés.";

        if (MessageBox.Show(message, "Supprimer le recueil", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        // Keep the poems, just unassign them from the deleted recueil.
        _poemRepository.ClearRecueilForRecueilId(recueil.Id);
        _recueilRepository.Delete(recueil.Id);

        _selectedRecueilFilterItem = null;
        RecueilFilterList.SelectedItem = null;
        RecueilManagePanel.Visibility = Visibility.Collapsed;
        RefreshRecueilFilterList();
    }

    private void NewPoemButton_OnClick(object sender, RoutedEventArgs e)
    {
        var title = NewPoemTitleInput.Text.Trim();
        if (title.Length == 0) return;

        // A new poem lands in the open recueil, or in the always-present "Pas classé" one.
        var recueilId = _openRecueil?.Id ?? GetUnclassifiedRecueil().Id;
        var poem = new Poem { Title = title, RecueilId = recueilId, CreatedAt = DateTime.Now };
        poem.Id = _poemRepository.Add(poem);

        NewPoemTitleInput.Clear();
        _openRecueil = _recueilRepository.GetAll().First(r => r.Id == recueilId);
        OpenPoemPage(poem);
    }

    private void OpenPoemPage(Poem? poem)
    {
        _selectedPoem = poem;
        SaveActiveItem();
        var recueils = (List<Recueil?>)PoemRecueilPicker.ItemsSource;

        PoemTitleInput.Text = poem?.Title ?? string.Empty;
        PoemTextInput.Text = poem?.Text ?? string.Empty;
        PoemTagsInput.Text = poem?.Tags ?? string.Empty;
        PoemRecueilPicker.SelectedItem = recueils.FirstOrDefault(r => r?.Id == poem?.RecueilId);
        ApplyPoemFormatting(poem?.TextAlignment ?? "Left", poem?.Margin ?? 0);
        RefreshPoemImage();

        var isEnabled = poem is not null;
        foreach (var input in new Control[]
                 {
                     PoemTitleInput, PoemTextInput, PoemTagsInput, PoemRecueilPicker, DeletePoemButton,
                     PoemAlignLeftButton, PoemAlignCenterButton, PoemAlignRightButton, PoemAlignJustifyButton
                 })
        {
            input.IsEnabled = isEnabled;
        }

        if (poem is not null) ShowPoetryPane(PoetryPane.Editor);
    }

    private void ApplyPoemFormatting(string alignment, double margin)
    {
        PoemTextInput.TextAlignment = Enum.Parse<TextAlignment>(alignment);
        PoemTextInput.Margin = new Thickness(margin, 0, margin, 0);

        foreach (var button in new[] { PoemAlignLeftButton, PoemAlignCenterButton, PoemAlignRightButton, PoemAlignJustifyButton })
        {
            button.IsChecked = (string)button.Tag == alignment;
        }
    }

    private void PoemAlignButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_selectedPoem is null || sender is not ToggleButton { Tag: string alignment }) return;

        _selectedPoem.TextAlignment = alignment;
        ApplyPoemFormatting(_selectedPoem.TextAlignment, _selectedPoem.Margin);
        _poemRepository.UpdateFormatting(_selectedPoem.Id, _selectedPoem.TextAlignment, _selectedPoem.Margin);
    }

    private void PoemMarginIncreaseButton_OnClick(object sender, RoutedEventArgs e) => AdjustPoemMargin(20);
    private void PoemMarginDecreaseButton_OnClick(object sender, RoutedEventArgs e) => AdjustPoemMargin(-20);

    private void AdjustPoemMargin(double delta)
    {
        if (_selectedPoem is null) return;

        _selectedPoem.Margin = Math.Clamp(_selectedPoem.Margin + delta, 0, 160);
        ApplyPoemFormatting(_selectedPoem.TextAlignment, _selectedPoem.Margin);
        _poemRepository.UpdateFormatting(_selectedPoem.Id, _selectedPoem.TextAlignment, _selectedPoem.Margin);
    }

    private void PreviousPoemButton_OnClick(object sender, RoutedEventArgs e)
    {
        var poems = PoemsInCurrentBook();
        var index = _selectedPoem is null ? -1 : poems.FindIndex(p => p.Id == _selectedPoem.Id);
        if (index > 0) OpenPoemPage(poems[index - 1]);
    }

    private void NextPoemButton_OnClick(object sender, RoutedEventArgs e)
    {
        var poems = PoemsInCurrentBook();
        var index = _selectedPoem is null ? -1 : poems.FindIndex(p => p.Id == _selectedPoem.Id);
        if (index >= 0 && index < poems.Count - 1) OpenPoemPage(poems[index + 1]);
    }

    // The poems that make up the recueil the editor was opened from, for prev/next paging.
    private List<Poem> PoemsInCurrentBook()
    {
        var all = _poemRepository.GetAll();
        return _openRecueil is null
            ? all.ToList()
            : all.Where(p => p.RecueilId == _openRecueil.Id).ToList();
    }

    private void PoemField_OnLostFocus(object sender, RoutedEventArgs e)
    {
        if (_selectedPoem is null) return;

        _selectedPoem.Title = PoemTitleInput.Text.Trim();
        _selectedPoem.Text = PoemTextInput.Text;
        _selectedPoem.Tags = PoemTagsInput.Text.Trim();

        _poemRepository.Update(_selectedPoem.Id, _selectedPoem.RecueilId, _selectedPoem.Title, _selectedPoem.Text, _selectedPoem.Tags);
    }

    private void PoemRecueilPicker_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_selectedPoem is null || !PoemRecueilPicker.IsEnabled) return;

        var recueilId = (PoemRecueilPicker.SelectedItem as Recueil)?.Id;
        if (recueilId == _selectedPoem.RecueilId) return;

        _selectedPoem.RecueilId = recueilId;
        _poemRepository.Update(_selectedPoem.Id, _selectedPoem.RecueilId, _selectedPoem.Title, _selectedPoem.Text, _selectedPoem.Tags);
    }

    private void RefreshPoemImage()
    {
        if (_selectedPoem?.ImagePath is { } path)
        {
            PoemImage.Source = LoadImage(MediaStorage.ResolveFullPath(path));
            PoemImage.Visibility = Visibility.Visible;
            RemovePoemImageButton.Visibility = Visibility.Visible;
        }
        else
        {
            PoemImage.Visibility = Visibility.Collapsed;
            RemovePoemImageButton.Visibility = Visibility.Collapsed;
        }
    }

    private void SetPoemImageButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_selectedPoem is null) return;

        var dialog = new OpenFileDialog { Filter = "Image files|*.png;*.jpg;*.jpeg;*.bmp;*.gif" };
        if (dialog.ShowDialog() != true) return;

        var relativePath = _mediaStorage.ImportPoemImage(dialog.FileName);
        _poemRepository.UpdateImage(_selectedPoem.Id, relativePath);
        _selectedPoem.ImagePath = relativePath;
        RefreshPoemImage();
    }

    private void RemovePoemImageButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_selectedPoem?.ImagePath is not { } path) return;

        _poemRepository.UpdateImage(_selectedPoem.Id, null);
        _selectedPoem.ImagePath = null;
        MediaStorage.DeleteFile(path);
        RefreshPoemImage();
    }

    private void DeletePoemButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_selectedPoem is null) return;

        if (_selectedPoem.ImagePath is { } path) MediaStorage.DeleteFile(path);
        _poemRepository.Delete(_selectedPoem.Id);

        _selectedPoem = null;
        if (_openRecueil is not null) OpenRecueilBook(_openRecueil);
        else { RefreshBookshelf(); ShowPoetryPane(PoetryPane.Bookshelf); }
    }
}
