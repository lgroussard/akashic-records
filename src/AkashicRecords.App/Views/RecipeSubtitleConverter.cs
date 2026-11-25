using System;
using System.Globalization;
using System.Linq;
using System.Windows.Controls;
using System.Windows.Data;
using AkashicRecords.Domain;

namespace AkashicRecords.App.Views;

// Renders a recipe list item's subtitle: the category plus how many ingredients it has
// (e.g. "Dessert · 6 ingrédients"), matching the journaux mockup.
public sealed class RecipeSubtitleConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values is null || values.Length < 2 || values[0] is null || values[1] is null)
            return string.Empty;

        var category = values[0] as string;
        var ingredients = values[1] as string;

        // One ingredient per line, as they are typed; « Pour 4 personnes » and titles ending with a colon
        // are not ingredients. A single line is an old comma-separated list.
        var lines = (ingredients ?? string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !line.EndsWith(':') && !line.StartsWith("Pour ", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var ingredientCount = lines.Count == 1
            ? lines[0].Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length
            : lines.Count;

        category = string.IsNullOrWhiteSpace(category) ? "Sans catégorie" : category;
        return $"{category} · {ingredientCount} ingrédient{(ingredientCount > 1 ? "s" : string.Empty)}";
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}