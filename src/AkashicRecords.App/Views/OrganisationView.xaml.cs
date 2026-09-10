using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using AkashicRecords.Domain;
using AkashicRecords.Infrastructure.Configuration;
using AkashicRecords.Infrastructure.Persistence;

namespace AkashicRecords.App.Views;

public partial class OrganisationView : UserControl, ISearchNavigable
{
    // Display-only wrappers so list items carry their db Id plus pre-computed progress/status text.
    private sealed record ProjectListItem(int Id, string Title, string StatusLabel, string SubText, double ProgressPercent);
    private sealed record TransitionListItem(int Id, string Title, int ProgressPercent, string ProgressText, bool ShowOnDesktop)
    {
        public Visibility ReminderVisibility => ShowOnDesktop ? Visibility.Visible : Visibility.Collapsed;
    }

    // Explicit ComboBox index <-> enum mapping so the Status dropdown never depends on enum declaration order.
    private static readonly ProjectStatus[] StatusOrder = { ProjectStatus.Active, ProjectStatus.Done, ProjectStatus.Archived };

    private readonly PersonalProjectRepository _projectRepo = new(new SqliteConnectionFactory());
    private readonly ProjectTaskRepository _taskRepo = new(new SqliteConnectionFactory());
    private readonly TransitionRepository _transitionRepo = new(new SqliteConnectionFactory());
    private readonly TransitionStepRepository _stepRepo = new(new SqliteConnectionFactory());

    private PersonalProject? _selectedProject;
    private Transition? _selectedTransition;

    // Suppresses auto-save/selection handlers while the detail panels are being populated programmatically.
    private bool _isInitializingDetail;
    private bool _isRefreshingList;

    // Live status label shown on the project detail pill (Active/Done/Archived).
    private string _selectedStatusLabel = "Active";
    public string SelectedStatusLabel => _selectedStatusLabel;

    // Deadline label shown next to the deadline picker ("Échéance à définir" or the formatted date).
    public string DeadlineText => _selectedProject?.Deadline is { } d
        ? $"Échéance : {d:dd/MM/yyyy}"
        : "Échéance à définir";

    private SearchResult? _pendingSearchResult;

    private readonly AppConfig _config;

    public OrganisationView(AppConfig config)
    {
        InitializeComponent();
        _config = config;
        // RestoreActiveItem already refreshes the active sub-tab; the inactive one is refreshed
        // lazily on first switch (SubTab_OnClick) so it never auto-selects and clobbers the config.
        Loaded += (_, _) => RestoreActiveItem();
        Loaded += (_, _) => ApplyPendingSearchResult();
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
        if (result.Kind == SearchResultKind.PersonalProject)
        {
            ShowProjetsTab(true);
            RefreshProjects(result.PrimaryId);
            OpenProjectDetail(result.PrimaryId);
        }
        else if (result.Kind == SearchResultKind.Transition)
        {
            ShowProjetsTab(false);
            RefreshTransitions(result.PrimaryId);
            OpenTransitionDetail(result.PrimaryId);
        }
    }

    private void ShowProjetsTab(bool projets)
    {
        ProjetsTab.IsChecked = projets;
        TransitionsTab.IsChecked = !projets;
        ProjetsRoot.Visibility = projets ? Visibility.Visible : Visibility.Collapsed;
        TransitionsRoot.Visibility = projets ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SubTab_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton clicked) return;

        ProjetsTab.IsChecked = clicked == ProjetsTab;
        TransitionsTab.IsChecked = clicked == TransitionsTab;

        var showProjets = clicked == ProjetsTab;
        ProjetsRoot.Visibility = showProjets ? Visibility.Visible : Visibility.Collapsed;
        TransitionsRoot.Visibility = showProjets ? Visibility.Collapsed : Visibility.Visible;

        // Persists the tab now shown and whatever item is currently displayed in it (possibly
        // none), without touching the other tab's saved id - it wasn't cleared, so it must not
        // be clobbered here (previously reset both ids to null on every switch, losing the exact
        // item to resume on if the app closed after switching back without reselecting).
        SaveActiveItem();

        // Guarantees the newly shown sub-tab isn't a dead zone even if nothing was auto-selected yet.
        if (showProjets && _selectedProject is null) RefreshProjects();
        else if (!showProjets && _selectedTransition is null) RefreshTransitions();
    }

    // Switches to an Organisation sub-tab through the real path, for headless capture (see App --screenshot).
    // Also selects the first item so the detail panel is verifiable, not just the shelf.
    public void ShowTabForScreenshot(string tab)
    {
        var isProjets = tab != "Transitions";
        ShowProjetsTab(isProjets);

        if (isProjets)
        {
            RefreshProjects();
            if (_selectedProject is null && ProjectList.Items.Count > 0
                && ProjectList.Items[0] is ProjectListItem first) OpenProjectDetail(first.Id);
        }
        else
        {
            RefreshTransitions();
            if (_selectedTransition is null && TransitionListBox.Items.Count > 0
                && TransitionListBox.Items[0] is TransitionListItem first) OpenTransitionDetail(first.Id);
        }
    }

    // Records the exact project/transition the user is viewing so reopening Organisation
    // returns them to the same detail, not just the right sub-tab. Reads the currently DISPLAYED
    // sub-tab (not just whichever field is non-null - both can be non-null at once since neither
    // is cleared on tab switch) so it never persists the wrong tab or clobbers the inactive one.
    private void SaveActiveItem()
    {
        if (ProjetsRoot.Visibility == Visibility.Visible)
        {
            _config.OrganisationActiveTab = "Projets";
            _config.OrganisationActiveProjectId = _selectedProject?.Id;
        }
        else
        {
            _config.OrganisationActiveTab = "Transitions";
            _config.OrganisationActiveTransitionId = _selectedTransition?.Id;
        }
    }

    // Reopens the exact project/transition the user last viewed (after the sub-tab).
    private void RestoreActiveItem()
    {
        switch (_config.OrganisationActiveTab)
        {
            case "Projets":
                ShowProjetsTab(true);
                if (_config.OrganisationActiveProjectId is { } projectId)
                {
                    RefreshProjects(projectId);
                    OpenProjectDetail(projectId);
                }
                else
                {
                    RefreshProjects();
                }
                break;
            case "Transitions":
                ShowProjetsTab(false);
                if (_config.OrganisationActiveTransitionId is { } transitionId)
                {
                    RefreshTransitions(transitionId);
                    OpenTransitionDetail(transitionId);
                }
                else
                {
                    RefreshTransitions();
                }
                break;
            default:
                // First launch / unrecognized value: default to Projets (matches the XAML's default-checked state).
                ShowProjetsTab(true);
                RefreshProjects();
                break;
        }
    }

    // ----- Projets -----

    private void RefreshProjects(int? selectId = null)
    {
        _isRefreshingList = true;

        var items = _projectRepo.GetAll().Select(project =>
        {
            var tasks = _taskRepo.GetByProject(project.Id);
            var done = tasks.Count(t => t.IsDone);
            var progress = tasks.Count == 0 ? "Aucune tâche" : $"{done}/{tasks.Count} tâches";
            var percent = tasks.Count == 0 ? 0 : 100.0 * done / tasks.Count;
            var subText = project.Deadline is { } deadline
                ? $"Échéance : {deadline:yyyy-MM-dd} · {progress}"
                : progress;
            return new ProjectListItem(project.Id, project.Title, project.Status.ToString(), subText, percent);
        }).ToList();

        ProjectList.ItemsSource = items;

        if (selectId is { } id)
        {
            ProjectList.SelectedItem = items.FirstOrDefault(i => i.Id == id);
        }

        // Auto-select the first item when nothing is selected/persisted, so the detail panel
        // fills the column instead of leaving ~80% of the screen empty (Doctrine §1). Done while
        // still guarded to avoid a redundant double-fire through SelectionChanged.
        var autoSelectFirst = selectId is null && _selectedProject is null && items.Count > 0;
        if (autoSelectFirst)
        {
            ProjectList.SelectedItem = items[0];
        }

        _isRefreshingList = false;

        if (autoSelectFirst)
        {
            OpenProjectDetail(items[0].Id);
        }
        else if (items.Count == 0)
        {
            _selectedProject = null;
            ProjectDetailPanel.Visibility = Visibility.Collapsed;
            ShowProjectEmptyState("Aucun projet — cliquez ＋ Nouveau");
        }
        else if (_selectedProject is null)
        {
            ShowProjectEmptyState("Sélectionnez un projet…");
        }
    }

    private void ShowProjectEmptyState(string text)
    {
        ProjectEmptyStateText.Text = text;
        ProjectEmptyState.Visibility = Visibility.Visible;
    }

    // The shared "＋ Nouveau" CTA dispatches to project or transition creation based on the active sub-tab.
    private void NewItemButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (TransitionsTab.IsChecked == true) NewTransitionButton_OnClick(sender, e);
        else NewProjectButton_OnClick(sender, e);
    }

    // Creates a blank project and focuses its title (no permanent input field - Doctrine §2).
    private void NewProjectButton_OnClick(object sender, RoutedEventArgs e)
    {
        var id = _projectRepo.Add(new PersonalProject
        {
            Title = "Nouveau projet",
            Status = ProjectStatus.Active,
            CreatedAt = DateTime.Now
        });

        RefreshProjects(id);
        OpenProjectDetail(id);
        ProjectTitleInput.Focus();
        ProjectTitleInput.SelectAll();
    }

    private void ProjectList_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isRefreshingList) return;

        if (ProjectList.SelectedItem is ProjectListItem item)
        {
            OpenProjectDetail(item.Id);
        }
        else
        {
            _selectedProject = null;
            ProjectDetailPanel.Visibility = Visibility.Collapsed;
            ShowProjectEmptyState(ProjectList.Items.Count > 0 ? "Sélectionnez un projet…" : "Aucun projet — cliquez ＋ Nouveau");
        }
    }

    private void OpenProjectDetail(int id)
    {
        var project = _projectRepo.GetById(id);
        if (project is null) return;

        _selectedProject = project;
        SaveActiveItem();
        _isInitializingDetail = true;

        ProjectTitleInput.Text = project.Title;
        ProjectDescriptionInput.Text = project.Description;
        ProjectStatusInput.SelectedIndex = Math.Max(0, Array.IndexOf(StatusOrder, project.Status));
        ProjectDeadlineInput.SelectedDate = project.Deadline;
        _selectedStatusLabel = project.Status.ToString();
        UpdateDeadlineWatermark();

        _isInitializingDetail = false;

        RefreshTasks();
        ProjectEmptyState.Visibility = Visibility.Collapsed;
        ProjectDetailPanel.Visibility = Visibility.Visible;
    }

    private void SaveCurrentProject()
    {
        if (_isInitializingDetail || _selectedProject is null) return;

        var title = ProjectTitleInput.Text.Trim();
        if (title.Length == 0) title = _selectedProject.Title;

        var index = ProjectStatusInput.SelectedIndex;
        var status = index >= 0 && index < StatusOrder.Length ? StatusOrder[index] : ProjectStatus.Active;
        var deadline = ProjectDeadlineInput.SelectedDate;

        _projectRepo.Update(_selectedProject.Id, title, ProjectDescriptionInput.Text, status, deadline);
        _selectedProject.Title = title;
        _selectedProject.Description = ProjectDescriptionInput.Text;
        _selectedProject.Status = status;
        _selectedProject.Deadline = deadline;
        _selectedStatusLabel = status.ToString();

        RefreshProjects(_selectedProject.Id);
    }

    private void ProjectField_OnLostFocus(object sender, RoutedEventArgs e) => SaveCurrentProject();

    private void ProjectStatusInput_OnSelectionChanged(object sender, SelectionChangedEventArgs e) => SaveCurrentProject();

    // Toggles the status order (Active → Done → Archived) on each click of the status pill.
    private void ProjectStatusPill_OnClick(object sender, RoutedEventArgs e)
    {
        if (_selectedProject is null || _isInitializingDetail) return;

        var current = Array.IndexOf(StatusOrder, _selectedProject.Status);
        var next = (current + 1) % StatusOrder.Length;
        _selectedProject.Status = StatusOrder[next];
        _selectedStatusLabel = _selectedProject.Status.ToString();
        SaveCurrentProject();
    }

    // Clears the placeholder text as soon as the user starts typing a new task.
    private void NewTaskInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is TextBox { Text: "" } tb) tb.Clear();
    }

    // Placeholder behaviour for the add-task field: show the hint on focus, clear it on typing.
    private void AddTaskPlaceholder_OnGotFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox { Text: "Ajouter une tâche" } tb)
        {
            tb.Text = "";
            tb.Foreground = (SolidColorBrush)new BrushConverter().ConvertFromString("#FFECECF4");
        }
    }

    private void AddTaskPlaceholder_OnLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb && tb.Text.Trim().Length == 0)
        {
            tb.Text = "Ajouter une tâche";
            tb.Foreground = (SolidColorBrush)new BrushConverter().ConvertFromString("#FFA0A0BD");
        }
    }

    private void ProjectDeadlineInput_OnSelectedDateChanged(object sender, SelectionChangedEventArgs e) {
        UpdateDeadlineWatermark();
        SaveCurrentProject();
    }

    private void ClearProjectDeadlineButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_selectedProject is null) return;
        ProjectDeadlineInput.SelectedDate = null;
        UpdateDeadlineWatermark();
        SaveCurrentProject();
    }

    // Hides the "Sélectionner une date" watermark once a deadline is set.
    private void UpdateDeadlineWatermark()
    {
        DeadlineWatermark.Visibility = ProjectDeadlineInput.SelectedDate is null
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void DeleteProjectButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_selectedProject is null) return;

        _projectRepo.Delete(_selectedProject.Id);
        _selectedProject = null;
        ProjectDetailPanel.Visibility = Visibility.Collapsed;
        RefreshProjects();
    }

    private void RefreshTasks()
    {
        if (_selectedProject is null) return;
        TaskList.ItemsSource = _taskRepo.GetByProject(_selectedProject.Id);
    }

    private void AddTaskButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_selectedProject is null) return;

        var text = NewTaskInput.Text.Trim();
        if (text.Length == 0) return;

        var nextOrder = _taskRepo.GetByProject(_selectedProject.Id).Count;
        _taskRepo.Add(new ProjectTask { ProjectId = _selectedProject.Id, Text = text, SortOrder = nextOrder });

        NewTaskInput.Clear();
        RefreshTasks();
        RefreshProjects(_selectedProject.Id);
    }

    private void TaskCheck_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { Tag: int id } checkBox) return;

        _taskRepo.SetDone(id, checkBox.IsChecked == true);
        if (_selectedProject is not null) RefreshProjects(_selectedProject.Id);
        RefreshTasks();
    }

    private void TaskText_OnLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox { Tag: int id } textBox) return;
        _taskRepo.UpdateText(id, textBox.Text);
    }

    private void DeleteTaskButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: int id }) return;

        _taskRepo.Delete(id);
        RefreshTasks();
        if (_selectedProject is not null) RefreshProjects(_selectedProject.Id);
    }

    // ----- Transitions -----

    private void RefreshTransitions(int? selectId = null)
    {
        _isRefreshingList = true;

        var items = _transitionRepo.GetAll().Select(transition =>
        {
            var steps = _stepRepo.GetByTransition(transition.Id);
            var done = steps.Count(s => s.IsDone);
            var percent = steps.Count == 0 ? 0 : (int)Math.Round(100.0 * done / steps.Count);
            var progressText = steps.Count == 0 ? "Aucune étape" : $"{done}/{steps.Count} étapes";
            return new TransitionListItem(transition.Id, transition.Title, percent, progressText, transition.ShowOnDesktop);
        }).ToList();

        TransitionListBox.ItemsSource = items;

        if (selectId is { } id)
        {
            TransitionListBox.SelectedItem = items.FirstOrDefault(i => i.Id == id);
        }

        // Auto-select the first item when nothing is selected/persisted, so the detail panel
        // fills the column instead of leaving ~80% of the screen empty (Doctrine §1). Done while
        // still guarded to avoid a redundant double-fire through SelectionChanged.
        var autoSelectFirst = selectId is null && _selectedTransition is null && items.Count > 0;
        if (autoSelectFirst)
        {
            TransitionListBox.SelectedItem = items[0];
        }

        _isRefreshingList = false;

        if (autoSelectFirst)
        {
            OpenTransitionDetail(items[0].Id);
        }
        else if (items.Count == 0)
        {
            _selectedTransition = null;
            TransitionDetailPanel.Visibility = Visibility.Collapsed;
            ShowTransitionEmptyState("Aucune transition — cliquez ＋ Nouveau");
        }
        else if (_selectedTransition is null)
        {
            ShowTransitionEmptyState("Sélectionnez une transition…");
        }
    }

    private void ShowTransitionEmptyState(string text)
    {
        TransitionEmptyStateText.Text = text;
        TransitionEmptyState.Visibility = Visibility.Visible;
    }

    private void NewTransitionButton_OnClick(object sender, RoutedEventArgs e)
    {
        var id = _transitionRepo.Add(new Transition { Title = "Nouvelle transition", CreatedAt = DateTime.Now });

        RefreshTransitions(id);
        OpenTransitionDetail(id);
        TransitionTitleInput.Focus();
        TransitionTitleInput.SelectAll();
    }

    private void TransitionListBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isRefreshingList) return;

        if (TransitionListBox.SelectedItem is TransitionListItem item)
        {
            OpenTransitionDetail(item.Id);
        }
        else
        {
            _selectedTransition = null;
            SaveActiveItem();
            TransitionDetailPanel.Visibility = Visibility.Collapsed;
            ShowTransitionEmptyState(TransitionListBox.Items.Count > 0 ? "Sélectionnez une transition…" : "Aucune transition — cliquez ＋ Nouveau");
        }
    }

    private void OpenTransitionDetail(int id)
    {
        var transition = _transitionRepo.GetById(id);
        if (transition is null) return;

        _selectedTransition = transition;
        SaveActiveItem();
        _isInitializingDetail = true;

        TransitionTitleInput.Text = transition.Title;
        TransitionDescriptionInput.Text = transition.Description;
        TransitionCurrentStateInput.Text = transition.CurrentState;
        TransitionDesiredStateInput.Text = transition.DesiredState;
        TransitionNotesInput.Text = transition.Notes;
        TransitionResourcesInput.Text = transition.Resources;
        ShowOnDesktopInput.IsChecked = transition.ShowOnDesktop;
        ReminderTextInput.Text = transition.ReminderText;

        _isInitializingDetail = false;

        RefreshSteps();
        TransitionEmptyState.Visibility = Visibility.Collapsed;
        TransitionDetailPanel.Visibility = Visibility.Visible;
    }

    private void SaveCurrentTransition()
    {
        if (_isInitializingDetail || _selectedTransition is null) return;

        var title = TransitionTitleInput.Text.Trim();
        if (title.Length == 0) title = _selectedTransition.Title;

        _transitionRepo.Update(
            _selectedTransition.Id,
            title,
            TransitionDescriptionInput.Text,
            TransitionCurrentStateInput.Text,
            TransitionDesiredStateInput.Text,
            TransitionNotesInput.Text,
            TransitionResourcesInput.Text,
            ReminderTextInput.Text,
            ShowOnDesktopInput.IsChecked == true);

        _selectedTransition.Title = title;
        RefreshTransitions(_selectedTransition.Id);
    }

    private void TransitionField_OnLostFocus(object sender, RoutedEventArgs e) => SaveCurrentTransition();

    private void ShowOnDesktopInput_OnClick(object sender, RoutedEventArgs e) => SaveCurrentTransition();

    private void DeleteTransitionButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_selectedTransition is null) return;

        _transitionRepo.Delete(_selectedTransition.Id);
        _selectedTransition = null;
        TransitionDetailPanel.Visibility = Visibility.Collapsed;
        RefreshTransitions();
    }

    private void RefreshSteps()
    {
        if (_selectedTransition is null) return;

        var steps = _stepRepo.GetByTransition(_selectedTransition.Id);
        StepList.ItemsSource = steps;

        var done = steps.Count(s => s.IsDone);
        TransitionProgressBar.Value = steps.Count == 0 ? 0 : 100.0 * done / steps.Count;
        TransitionProgressText.Text = steps.Count == 0 ? "Aucune étape" : $"{done}/{steps.Count} étapes";
    }

    private void AddStepButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_selectedTransition is null) return;

        var text = NewStepInput.Text.Trim();
        if (text.Length == 0) return;

        var nextOrder = _stepRepo.GetByTransition(_selectedTransition.Id).Count;
        _stepRepo.Add(new TransitionStep { TransitionId = _selectedTransition.Id, Text = text, SortOrder = nextOrder });

        NewStepInput.Clear();
        RefreshSteps();
        RefreshTransitions(_selectedTransition.Id);
    }

    private void StepCheck_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { Tag: int id } checkBox) return;

        _stepRepo.SetDone(id, checkBox.IsChecked == true);
        RefreshSteps();
        if (_selectedTransition is not null) RefreshTransitions(_selectedTransition.Id);
        RefreshSteps();
    }

    private void StepText_OnLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox { Tag: int id } textBox) return;
        _stepRepo.UpdateText(id, textBox.Text);
    }

    private void DeleteStepButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: int id }) return;

        _stepRepo.Delete(id);
        RefreshSteps();
        if (_selectedTransition is not null) RefreshTransitions(_selectedTransition.Id);
    }
}
