using System;
using System.Globalization;
using System.Windows.Controls;
using System.Windows.Data;
using AkashicRecords.Domain;

namespace AkashicRecords.App.Views;

// Renders a recipe list item's subtitle: the category plus how many comma-separated
// ingredients it has (e.g. "Dessert · 6 ingrédients"), matching the journaux mockup.
public sealed class RecipeSubtitleConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values is null || values.Length < 2 || values[0] is null || values[1] is null)
            return string.Empty;

        var category = values[0] as string;
        var ingredients = values[1] as string;

        var ingredientCount = string.IsNullOrWhiteSpace(ingredients)
            ? 0
            : ingredients.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;

        category = string.IsNullOrWhiteSpace(category) ? "Sans catégorie" : category;
        return $"{category} · {ingredientCount} ingréd{(ingredientCount == 1 ? "it" : "its")}";
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}