using System.Text;

namespace AkashicRecords.Infrastructure.Budgeting;

// Accent-fold + whole-word matcher shared by every budget classifier path (user rules AND the
// built-in merchant map). SQLite's lower() is ASCII-only and LIKE has no word boundary, so a rule
// typed "noël" never matched a label "NOEL", and "FREE" wrongly matched "FREIN". Both are fixed in
// managed code here, so the two classifiers behave identically and predictably.
public static class LabelMatcher
{
    // Lowercases, folds the banking diacritics down to base letters, and reduces every run of
    // non-alphanumerics (spaces, punctuation, currency, the nbsp the FR exports carry) to one space.
    // Result is a clean token string like "cb noel store 12" ready for the boundary test below.
    public static string Fold(string? raw)
    {
        var s = (raw ?? string.Empty).ToLowerInvariant();
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            var folded = ch switch
            {
                'é' or 'è' or 'ê' or 'ë' or 'ę' => 'e',
                'à' or 'â' or 'ä' or 'ã' => 'a',
                'ù' or 'û' or 'ü' or 'ò' or 'ö' => ch is 'ò' or 'ö' ? 'o' : 'u',
                'î' or 'ï' or 'í' => 'i',
                'ç' => 'c',
                'ñ' => 'n',
                _ => ch,
            };
            if (folded >= 'a' && folded <= 'z' || char.IsDigit(folded)) sb.Append(folded);
            else if (sb.Length > 0 && sb[^1] != ' ') sb.Append(' ');
        }
        return sb.ToString().Trim();
    }

    // A distinctive keyword (5+ letters: CARREFOUR, NETFLIX, PLAYSTATION) may match inside a glued
    // token ("CARREFOURMARKET"). A short, ambiguous one (FREE, SNCF, KOKO) must occupy a whole token
    // so "free" cannot be hidden inside "frein". That threshold is the false-positive shield.
    public static bool Matches(string foldedLabel, string foldedKeyword)
    {
        if (foldedKeyword.Length == 0 || foldedLabel.Length == 0) return false;
        return foldedKeyword.Length >= 5
            ? foldedLabel.Contains(foldedKeyword, StringComparison.Ordinal)
            : $" {foldedLabel} ".Contains($" {foldedKeyword} ", StringComparison.Ordinal);
    }
}
