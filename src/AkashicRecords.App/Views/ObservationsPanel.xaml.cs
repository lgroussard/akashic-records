using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AkashicRecords.Domain;
using AkashicRecords.Infrastructure.Persistence;

namespace AkashicRecords.App.Views;

// The observations of one item, whatever its kind (see Observation.OwnerKind), and the artistic projects
// each one is linked to. One control for every host page, so an observation is written, edited and
// linked the same way everywhere.
public partial class ObservationsPanel : UserControl
{
    private readonly ObservationRepository _observationRepository = new(new SqliteConnectionFactory());
    private readonly ReferenceRepository _referenceRepository = new(new SqliteConnectionFactory());
    private readonly ArtisticProjectRepository _projectRepository = new(new SqliteConnectionFactory());

    private string _ownerKind = Observation.ArtworkOwner;
    private int _ownerId;
    private int? _editingId;
    private int? _highlightedId;
    // The observation whose "new project" name box is open, if any.
    private int? _namingProjectFor;

    // Raised after any add / edit / delete / link change, so a host can refresh a count or a badge.
    public event EventHandler? Changed;

    public ObservationsPanel()
    {
        InitializeComponent();
        IsEnabled = false;
    }

    public int Count { get; private set; }

    // Points the panel at an item. ownerId 0 (nothing selected, or an item not saved yet) empties it.
    public void SetOwner(string ownerKind, int ownerId)
    {
        if (ownerKind == _ownerKind && ownerId == _ownerId)
        {
            Refresh();
            return;
        }
        _ownerKind = ownerKind;
        _ownerId = ownerId;
        _highlightedId = null;
        _namingProjectFor = null;
        StopEditing();
        IsEnabled = ownerId > 0;
        Refresh();
    }

    public void Clear() => SetOwner(_ownerKind, 0);

    // Scrolls to one observation and outlines it (a global-search hit on an observation).
    public void Highlight(int observationId)
    {
        _highlightedId = observationId;
        Refresh();
    }

    public void Refresh()
    {
        ItemsHost.Children.Clear();
        var observations = _ownerId > 0
            ? _observationRepository.GetByOwner(_ownerKind, _ownerId)
            : (IReadOnlyList<Observation>)Array.Empty<Observation>();
        Count = observations.Count;
        HeaderText.Text = Count > 0 ? $"OBSERVATIONS · {Count}" : "OBSERVATIONS";
        ListScroll.Visibility = Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (Count == 0) return;

        var projects = _projectRepository.GetAll();
        foreach (var observation in observations)
        {
            var row = BuildRow(observation, projects);
            ItemsHost.Children.Add(row);
            if (observation.Id == _highlightedId)
            {
                Dispatcher.BeginInvoke(() => row.BringIntoView(), System.Windows.Threading.DispatcherPriority.Loaded);
            }
        }
    }

    private Border BuildRow(Observation observation, IReadOnlyList<ArtisticProject> projects)
    {
        var text = new StackPanel();
        if (observation.Subject.Length > 0)
        {
            text.Children.Add(new TextBlock
            {
                Text = observation.Subject, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)FindResource("TextBrush"),
            });
        }
        text.Children.Add(new TextBlock
        {
            Text = observation.Content, TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)FindResource("TextBrush"),
        });

        // Linked projects as chips (click = unlink), then "＋ projet".
        var linkedIds = _referenceRepository.GetByObservation(observation.Id).Select(r => r.ProjectId).ToHashSet();
        var chips = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        foreach (var project in projects.Where(p => linkedIds.Contains(p.Id)))
        {
            var chip = new Button
            {
                Content = $"◆ {project.Title}", Margin = new Thickness(0, 0, 6, 4),
                Style = (Style)FindResource("ChipButtonStyle"),
                ToolTip = $"Lié au projet « {project.Title} » — cliquer pour délier",
            };
            chip.Click += (_, _) =>
            {
                _referenceRepository.Unlink(observation.Id, project.Id);
                OnChanged();
            };
            chips.Children.Add(chip);
        }

        var addLink = new Button
        {
            Content = "＋ projet", Margin = new Thickness(0, 0, 6, 4), Style = (Style)FindResource("MiniButtonStyle"),
            ToolTip = "Lier cette observation à un projet artistique",
        };
        addLink.Click += (_, _) => OpenProjectMenu(addLink, observation.Id, projects, linkedIds);
        chips.Children.Add(addLink);

        if (_namingProjectFor == observation.Id)
        {
            var nameInput = new TextBox { Width = 170, Margin = new Thickness(0, 0, 6, 4), Style = (Style)FindResource("DarkTextBoxStyle") };
            nameInput.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter) { CreateProjectAndLink(observation.Id, nameInput.Text); e.Handled = true; }
                else if (e.Key == Key.Escape) { _namingProjectFor = null; Refresh(); e.Handled = true; }
            };
            chips.Children.Add(nameInput);
            var ok = new Button { Content = "OK", Margin = new Thickness(0, 0, 0, 4), Style = (Style)FindResource("MiniButtonStyle") };
            ok.Click += (_, _) => CreateProjectAndLink(observation.Id, nameInput.Text);
            chips.Children.Add(ok);
            Dispatcher.BeginInvoke(() => nameInput.Focus(), System.Windows.Threading.DispatcherPriority.Input);
        }
        text.Children.Add(chips);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
        buttons.Children.Add(IconButton("✎", "Modifier", () => StartEditing(observation)));
        buttons.Children.Add(IconButton("✕", "Supprimer", () => Delete(observation)));
        DockPanel.SetDock(buttons, Dock.Right);

        var dock = new DockPanel();
        dock.Children.Add(buttons);
        dock.Children.Add(text);

        var highlighted = observation.Id == _highlightedId || observation.Id == _editingId;
        return new Border
        {
            Background = (Brush)FindResource("Surface2Brush"),
            BorderBrush = (Brush)FindResource(highlighted ? "AccentBrush" : "AppBorderBrush"),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 8, 10, 4), Margin = new Thickness(0, 0, 0, 6),
            Child = dock,
        };
    }

    private Button IconButton(string glyph, string tip, Action action)
    {
        var button = new Button
        {
            Content = glyph, ToolTip = tip, Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Foreground = (Brush)FindResource("TextFaintBrush"), Cursor = Cursors.Hand, Padding = new Thickness(4, 0, 4, 0),
        };
        button.Click += (_, _) => action();
        return button;
    }

    private void OpenProjectMenu(Button anchor, int observationId, IReadOnlyList<ArtisticProject> projects, HashSet<int> linkedIds)
    {
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        foreach (var project in projects)
        {
            var item = new MenuItem { Header = project.Title, IsCheckable = true, IsChecked = linkedIds.Contains(project.Id) };
            item.Click += (_, _) =>
            {
                if (linkedIds.Contains(project.Id)) _referenceRepository.Unlink(observationId, project.Id);
                else _referenceRepository.Link(observationId, project.Id);
                OnChanged();
            };
            menu.Items.Add(item);
        }
        if (projects.Count > 0) menu.Items.Add(new Separator());
        var create = new MenuItem { Header = "Nouveau projet…" };
        create.Click += (_, _) => { _namingProjectFor = observationId; Refresh(); };
        menu.Items.Add(create);
        menu.IsOpen = true;
    }

    private void CreateProjectAndLink(int observationId, string name)
    {
        name = name.Trim();
        _namingProjectFor = null;
        if (name.Length > 0)
        {
            var projectId = _projectRepository.Add(new ArtisticProject { Title = name, CreatedAt = DateTime.Now });
            _referenceRepository.Link(observationId, projectId);
        }
        OnChanged();
    }

    private void StartEditing(Observation observation)
    {
        _editingId = observation.Id;
        SubjectInput.Text = observation.Subject;
        ContentInput.Text = observation.Content;
        SaveButton.Content = "Enregistrer";
        CancelEditButton.Visibility = Visibility.Visible;
        Refresh();
        ContentInput.Focus();
    }

    private void StopEditing()
    {
        _editingId = null;
        SubjectInput.Clear();
        ContentInput.Clear();
        SaveButton.Content = "＋ Ajouter l'observation";
        CancelEditButton.Visibility = Visibility.Collapsed;
    }

    private void Delete(Observation observation)
    {
        var linked = _referenceRepository.GetByObservation(observation.Id).Count;
        var question = linked > 0
            ? $"Supprimer cette observation ? Elle est liée à {linked} projet(s) et en sera retirée."
            : "Supprimer cette observation ?";
        if (MessageBox.Show(question, "Observations", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        _observationRepository.Delete(observation.Id);
        if (_editingId == observation.Id) StopEditing();
        OnChanged();
    }

    private void SaveButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_ownerId <= 0) return;
        var subject = SubjectInput.Text.Trim();
        var content = ContentInput.Text.Trim();
        if (content.Length == 0) return;

        if (_editingId is int id)
        {
            _observationRepository.Update(id, subject, content);
        }
        else
        {
            _observationRepository.Add(new Observation
            {
                OwnerKind = _ownerKind, OwnerId = _ownerId, Subject = subject, Content = content, CreatedAt = DateTime.Now,
            });
        }
        StopEditing();
        OnChanged();
    }

    private void CancelEditButton_OnClick(object sender, RoutedEventArgs e)
    {
        StopEditing();
        Refresh();
    }

    private void FoldButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (Body.Visibility == Visibility.Visible) Collapse(); else Expand();
    }

    // Folded: only the header (with the count) shows. Used by pages where the item itself comes first.
    public void Collapse()
    {
        Body.Visibility = Visibility.Collapsed;
        FoldButton.Content = "▸";
    }

    public void Expand()
    {
        Body.Visibility = Visibility.Visible;
        FoldButton.Content = "▾";
    }

    private void OnChanged()
    {
        Refresh();
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
