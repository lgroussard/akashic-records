using System.Windows;
using System.Windows.Controls.Primitives;
using AkashicRecords.Infrastructure.Configuration;

namespace AkashicRecords.App.Views;

/// <summary>Sub-tab side of the module settings (see Modules.Catalog), shared by the section views.</summary>
public static class ModuleTabs
{
    /// <summary>
    /// Hides the section's switched-off sub-tab buttons (each button's Tag is its sub-tab key) and returns the tab
    /// the view should open: <paramref name="saved"/> when it (or, if null, <paramref name="defaultTab"/>) is still on,
    /// otherwise the first sub-tab that is. Views store the result back as their saved tab BEFORE restoring, so the
    /// restore (and its item pointer, which then belongs to another tab) never lands on a hidden tab.
    /// </summary>
    public static string? Apply(AppConfig config, string section, string? saved, string defaultTab, params ToggleButton[] tabs)
    {
        foreach (var tab in tabs)
        {
            tab.Visibility = tab.Tag is string key && !config.IsEnabled(section, key) ? Visibility.Collapsed : Visibility.Visible;
        }

        return config.IsEnabled(section, saved ?? defaultTab) ? saved : config.FirstEnabledTab(section);
    }
}
