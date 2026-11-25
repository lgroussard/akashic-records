using System.Windows;

namespace AkashicRecords.App.Views;

// Deletes are final (no trash, no undo outside the canvas): every one that removes something the user
// wrote or imported asks first, "Non" being the default answer.
internal static class ConfirmDelete
{
    public static bool Ask(string question) =>
        MessageBox.Show(question + "\n\nCette action est définitive.", "Supprimer",
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
}
