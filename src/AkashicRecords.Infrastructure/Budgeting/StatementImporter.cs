using System.Globalization;
using System.Text;

namespace AkashicRecords.Infrastructure.Budgeting;

// One raw row lifted out of a bank statement export, before categorization.
public sealed record ParsedRow(DateTime Date, string Label, decimal Amount);

// Outcome of parsing one statement file: the clean rows plus everything the UI needs to show what
// was assumed (delimiter, chosen columns, skipped lines) — the substance of the mapping assistant.
public sealed record StatementParseResult(
    bool Success,
    string? Error,
    IReadOnlyList<ParsedRow> Rows,
    string DelimiterName,
    string? DateColumn,
    string? LabelColumn,
    string? AmountColumn,
    string? DebitColumn,
    string? CreditColumn,
    bool AmountIsInCents,
    int SkippedLines);

// Parses generic bank statement exports (CSV/TSV, FR/BE conventions — HelloBank exports included).
// Nothing is hardcoded to one bank: the delimiter is sniffed, header aliases map known column
// names in French AND Dutch, unknown layouts fall back to a positional heuristic (the column that
// date-parses wins, the one that number-parses wins, the longest-text column is the label).
// Amounts tolerate "1.234,56", "-1 234,56", "(50,00)", "45,00 DR", split debit/credit columns,
// and whole-column cent values (÷100 when the column holds bare large integers).
// Re-import idempotence: ExternalId = normalized "date|label|amount" string, checked before insert.
public sealed class StatementImporter
{
    private static readonly char[] CandidateDelimiters = [',', ';', '\t'];

    private static readonly string[] DateAliases =
        ["date", "datum", "valuta", "boekingsdatum", "date de valeur", "bookingdate", "transactiondate", "datevaleur"];
    private static readonly string[] LabelAliases =
        ["libelle", "libellé", "description", "omschrijving", "communication", "detail", "détail", "label", "naam", "recap", "remise", "structeredinfo", "informationstructuree"];
    private static readonly string[] AmountAliases =
        ["montant", "bedrag", "amount", "montantdeloperation", "transactionamount"];
    private static readonly string[] DebitAliases =
        ["montantdebit", "debit", "débittocancel", "debittocancel", "montantdepense", "amountout", "uitgave", "depense"];
    private static readonly string[] CreditAliases =
        ["montantcredit", "credit", "crédittocancel", "credittocancel", "montantderecette", "amountin", "ontvangst", "recette"];
    private static readonly string[] BalanceAliases =
        ["solde", "soldeapreparer", "balance", "saldo", "soldeactuel"];

    // Date parsing is done manually (see TryDate) rather via TryParseExact: the dialect exposes
    // no (string,string,out) overload, and manual parsing covers yyyy-MM-dd / dd-MM-yyyy /
    // dd.MM.yyyy / d/M/yy / yyyyMMdd in one pass without culture games.

    public StatementParseResult ParseFile(string filePath)
    {
        try
        {
            // PDF account statements are vector text, not delimited rows: they go to the geometric
            // reader (see PdfStatementReader). Everything else is treated as a text export.
            if (string.Equals(Path.GetExtension(filePath), ".pdf", StringComparison.OrdinalIgnoreCase))
                return PdfStatementReader.Parse(File.ReadAllBytes(filePath), Path.GetFileNameWithoutExtension(filePath));

            // Bank exports are utf8 or latin1 in the wild; the latin1 fallback never throws.
            var bytes = File.ReadAllBytes(filePath);
            var text = Utf8Safe(bytes);
            return ParseText(text, Path.GetFileNameWithoutExtension(filePath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Failure(ex.Message);
        }
    }

    public StatementParseResult ParseText(string text, string sourceName)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n')
            .Select(l => l.Trim('\uFEFF').TrimEnd())
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToList();
        if (lines.Count == 0) return Failure("Fichier vide.");

        var delimiter = SniffDelimiter(lines);
        var table = lines.Select(l => SplitRow(l, delimiter)).ToList();
        var width = table.Max(r => r.Length);
        if (width < 2) return Failure("Impossible de séparer les colonnes (séparateur non reconnu).");

        // Locate the header row: the first among the top lines exposing a known alias column.
        int headerIndex = -1;
        for (var i = 0; i < Math.Min(8, table.Count); i++)
        {
            var cells = table[i].Select(Clean).ToList();
            if (cells.Any(c => DateAliases.Contains(c) || LabelAliases.Contains(c) || AmountAliases.Contains(c)
                              || DebitAliases.Contains(c) || CreditAliases.Contains(c) || BalanceAliases.Contains(c)))
            {
                headerIndex = i;
                break;
            }
        }

        int dateCol = -1, labelCol = -1, amountCol = -1, debitCol = -1, creditCol = -1, balanceCol = -1;
        string? dateName = null, labelName = null, amountName = null, debitName = null, creditName = null;
        if (headerIndex >= 0)
        {
            var header = table[headerIndex].Select(Clean).ToList();
            dateCol = IndexOfAny(header, DateAliases);
            labelCol = IndexOfAny(header, LabelAliases);
            amountCol = IndexOfAny(header, AmountAliases);
            debitCol = IndexOfAny(header, DebitAliases);
            creditCol = IndexOfAny(header, CreditAliases);
            balanceCol = IndexOfAny(header, BalanceAliases);
            if (dateCol >= 0) dateName = table[headerIndex][dateCol].Trim().Trim('"');
            if (labelCol >= 0) labelName = table[headerIndex][labelCol].Trim().Trim('"');
            if (amountCol >= 0) amountName = table[headerIndex][amountCol].Trim().Trim('"');
            if (debitCol >= 0) debitName = table[headerIndex][debitCol].Trim().Trim('"');
            if (creditCol >= 0) creditName = table[headerIndex][creditCol].Trim().Trim('"');
            // The balance column stays detected purely so the positional label guess can exclude it.
        }

        var body = (headerIndex >= 0 ? table.Skip(headerIndex + 1) : table).ToList();
        if (body.Count == 0) return Failure("Aucune ligne de données après l'en-tête.");
        var sample = body.Take(30).ToList();

        var dualColumns = debitCol >= 0 && creditCol >= 0 && amountCol < 0;
        if (dateCol < 0) dateCol = GuessColumn(sample, width, s => TryDate(s, out _));
        if (amountCol < 0 && !dualColumns) amountCol = GuessColumn(sample, width, s => TryAmount(s, out _, out _));
        if (labelCol < 0) labelCol = GuessLabelColumn(sample, width, dateCol, amountCol, debitCol, creditCol, balanceCol);
        if (dateCol < 0 || labelCol < 0 || (amountCol < 0 && !dualColumns))
            return Failure("Colonnes date / montant / libellé non identifiées — vérifiez le fichier.");

        // Whole-column cents heuristic: no decimal separators anywhere AND bare values look 100x.
        var amountIsCents = false;
        if (!dualColumns)
        {
            var rawAmounts = body.Select(r => CellAt(r, amountCol)).Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
            if (rawAmounts.Count >= 3 &&
                rawAmounts.All(s => !s.Contains(',') && !s.Contains('.')) &&
                rawAmounts.Any(s => TryAmount(s, out var v, out _) && Math.Abs(v) >= 500))
            {
                amountIsCents = true;
            }
        }

        var rows = new List<ParsedRow>();
        var skipped = 0;
        foreach (var cells in body)
        {
            var dateText = CellAt(cells, dateCol);
            var labelText = CellAt(cells, labelCol);
            decimal amount;
            if (dualColumns)
            {
                var debitText = CellAt(cells, debitCol);
                var creditText = CellAt(cells, creditCol);
                var hasDebit = TryAmount(debitText, out var debitValue, out _);
                var hasCredit = TryAmount(creditText, out var creditValue, out _);
                if (!hasDebit && !hasCredit) { skipped++; continue; }
                amount = hasCredit && creditValue != 0 ? Math.Abs(creditValue) : -Math.Abs(debitValue);
            }
            else if (TryAmount(CellAt(cells, amountCol), out var single, out _))
            {
                amount = amountIsCents ? single / 100m : single;
            }
            else { skipped++; continue; }

            if (!TryDate(dateText, out var date)) { skipped++; continue; }
            if (amount == 0 && !dualColumns) { skipped++; continue; }

            var label = string.IsNullOrWhiteSpace(labelText) ? date.ToString("dd/MM") + " — montant " + amount.ToString("0.00") : labelText.Trim();
            rows.Add(new ParsedRow(date, label, amount));
        }

        if (rows.Count == 0)
            return Failure("Aucune opération reconnue — vérifiez que le fichier contient bien les mouvements (pas un tableau de soldes).");
        return new StatementParseResult(true, null, rows, DelimiterName(delimiter),
            dateName, labelName, amountName, debitName, creditName, amountIsCents, skipped);
    }

    // Stable identity of a row: re-importing the same statement never duplicates.
    public static string ExternalIdOf(ParsedRow row) =>
        $"{row.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}|{row.Label.Trim()}|{row.Amount.ToString("0.00", CultureInfo.InvariantCulture)}";

    private static string Clean(string cell)
    {
        // Header comparison must ignore accents, spaces and punctuation: real FR/BE exports write
        // "Montant débit", "Montant d'opération", "Crédit", "Date de valeur" — none equal the compact
        // alias unless reduced to bare lowercase alphanumeric letters. Lowercase FIRST (proven
        // string.ToLowerInvariant) so accented uppercase like 'É' folds correctly, then map the
        // handful of banking diacritics to base letters, then keep only a-z and digits.
        var s = cell.Trim().Trim('"').Trim().ToLowerInvariant();
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            var folded = ch switch
            {
                'é' or 'è' or 'ê' or 'ë' => 'e',
                'à' or 'â' => 'a',
                'ù' or 'û' => 'u',
                'î' or 'ï' => 'i',
                'ô' or 'ö' => 'o',
                'ç' => 'c',
                _ => ch,
            };
            if (char.IsDigit(folded) || (folded >= 'a' && folded <= 'z')) sb.Append(folded);
        }
        return sb.ToString();
    }

    private static char SniffDelimiter(List<string> lines)
    {
        char best = ',';
        var bestScore = -1;
        foreach (var d in CandidateDelimiters)
        {
            var counts = lines.Take(20).Select(l => l.Count(c => c == d)).ToList();
            if (counts.Count == 0) continue;
            var score = counts.Min() * 2 + (int)counts.Average();
            if (counts.Average() >= 2 && score > bestScore) { bestScore = score; best = d; }
        }
        return best;
    }

    private static string[] SplitRow(string line, char delimiter)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        foreach (var c in line)
        {
            if (c == '"') { inQuotes = !inQuotes; continue; }
            if (c == delimiter && !inQuotes) { parts.Add(current.ToString()); current.Clear(); continue; }
            current.Append(c);
        }
        parts.Add(current.ToString());
        return parts.ToArray();
    }

    private static string CellAt(string[] cells, int index) =>
        index >= 0 && index < cells.Length ? cells[index] : string.Empty;

    private static int IndexOfAny(List<string> headerCells, string[] aliases)
    {
        for (var i = 0; i < headerCells.Count; i++)
            if (Array.IndexOf(aliases, headerCells[i]) >= 0) return i;
        return -1;
    }

    private static int GuessColumn(List<string[]> sample, int width, Func<string, bool> accepts)
    {
        for (var col = 0; col < width; col++)
        {
            var cells = sample.Select(r => CellAt(r, col)).Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
            if (cells.Count >= 3 && cells.All(accepts)) return col;
        }
        return -1;
    }

    private static int GuessLabelColumn(List<string[]> sample, int width, params int[] taken)
    {
        var best = -1;
        long bestScore = -1;
        for (var col = 0; col < width; col++)
        {
            if (Array.IndexOf(taken, col) >= 0) continue;
            var cells = sample.Select(r => CellAt(r, col)).Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
            if (cells.Count < 3) continue;
            var score = cells.Sum(c => (long)c.Length);
            if (score > bestScore) { bestScore = score; best = col; }
        }
        return best;
    }

    internal static bool TryDate(string raw, out DateTime date)
    {
        date = default;
        var s = raw.Trim().Trim('"');
        if (s.Length == 0) return false;

        // yyyyMMdd compact form (some exports).
        if (s.Length == 8 && s.All(char.IsDigit) && int.TryParse(s, out var compact))
        {
            var cy = compact / 10000;
            var cm = compact / 100 % 100;
            var cd = compact % 100;
            if (TryBuildDate(cy, cm, cd, out date)) return true;
        }

        var parts = s.Replace('/', '-').Replace('.', '-').Split('-')
            .Where(p => p.Trim().Length > 0)
            .Select(p => p.Trim())
            .ToList();
        if (parts.Count != 3) return false;
        if (!int.TryParse(parts[0], out var a) || !int.TryParse(parts[1], out var b) || !int.TryParse(parts[2], out var c))
            return false;

        int year, month, day;
        if (a >= 1000) { year = a; month = b; day = c; }                       // yyyy-MM-dd
        else if (c >= 1000) { year = c; month = b; day = a; }                  // dd-MM-yyyy
        else if (c < 100 && a < 100)                                          // dd-MM-yy
        { year = 2000 + c; month = b; day = a; if (year > DateTime.Now.Year + 1) year -= 100; }
        else return false;

        return TryBuildDate(year, month, day, out date);
    }

    // Guarded construction: out-of-range months/days (13, 31/02…) simply fail the parse.
    private static bool TryBuildDate(int year, int month, int day, out DateTime date)
    {
        date = default;
        if (year < 1990 || year > DateTime.Now.Year + 1 || month is < 1 or > 12 || day is < 1 or > 31)
            return false;
        try { date = new DateTime(year, month, day); return true; }
        catch (ArgumentOutOfRangeException) { return false; }
    }

    // FR/BE numeric text → decimal. Handles "1.234,56", "-1 234,56", "(50,00)", "1234.56", "45,00",
    // trailing/leading signs, thousands in either position, nbsp thousands.
    internal static bool TryAmount(string raw, out decimal value, out bool wasParenthesized)
    {
        value = 0;
        wasParenthesized = false;
        var s = raw.Trim().Trim('"')
            .Replace("\u00A0", "").Replace("\u202F", "").Replace(" ", "")
            .Replace("\u2212", "-");
        if (s.StartsWith('(') && s.EndsWith(')')) { wasParenthesized = true; s = s[1..^1]; }
        if (s.Length == 0) return false;

        var trailingMinus = s.EndsWith('-');
        if (trailingMinus) s = s[..^1];

        var lastComma = s.LastIndexOf(',');
        var lastDot = s.LastIndexOf('.');
        if (lastComma >= 0 && lastDot >= 0)
        {
            // Both present → the LAST one is the decimal separator, the other one thousands marks.
            s = lastComma > lastDot ? s.Replace(".", "").Replace(',', '.') : s.Replace(",", "");
        }
        else if (lastComma >= 0)
        {
            var tail = s.Length - lastComma - 1;
            s = tail <= 2 ? s.Replace(',', '.') : s.Replace(",", "");
        }
        else if (lastDot >= 0)
        {
            var tail = s.Length - lastDot - 1;
            if (tail > 2) s = s.Replace(".", ""); // "12.345" is twelve-thousand, not twelve-point
        }

        if (!decimal.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) return false;
        if (wasParenthesized || trailingMinus) value = -Math.Abs(value);
        return true;
    }

    private static string DelimiterName(char d) => d switch { ';' => "point-virgule", '\t' => "tabulation", _ => "virgule" };

    private static StatementParseResult Failure(string error) =>
        new(false, error, [], "virgule", null, null, null, null, null, false, 0);

    // Strict utf8: throws DecoderFallbackException on byte sequences that are not valid UTF-8, so
    // the latin1 fallback below can actually fire. (The default Encoding.UTF8 never throws — it
    // silently emits U+FFFD — which is what mangled Excel-authored windows-1252 FR/BE exports.)
    // NOTE: the encoder fallback must be a real value, not null — passing null throws ArgumentNull.
    private static readonly Encoding StrictUtf8 =
        Encoding.GetEncoding("utf-8", EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback)!;

    private static string Utf8Safe(byte[] bytes)
    {
        // Excel-authored FR/BE bank exports are frequently windows-1252 / latin1. Decoding those
        // as UTF-8 would mangle every accented label ("É" -> U+FFFD) with no error to catch, so the
        // strict decoder above is required to make the latin1 fallback reachable and meaningful.
        try { return StrictUtf8.GetString(bytes); }
        catch (DecoderFallbackException) { return Encoding.Latin1.GetString(bytes); }
    }
}
