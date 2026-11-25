using System.Globalization;

namespace AkashicRecords.Infrastructure.Persistence;

// Budget amounts are stored as text (exact decimals). The text is always written with a point, whatever
// the language of the machine: decimal.ToString() and decimal.Parse() follow the current culture, so a
// French Windows wrote "-45,90", which a machine set to English reads as -4590.
// Reading still accepts the comma, for a row written before this (SchemaInitializer rewrites them).
internal static class MoneyText
{
    public static string Format(decimal value) => value.ToString(CultureInfo.InvariantCulture);

    public static decimal Parse(string text) =>
        decimal.Parse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture);
}
