using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace AkashicRecords.Infrastructure.Budgeting;

// Reads a HelloBank (BNP Paribas) PDF account statement and lifts the movement table out of it.
// These PDFs are vector text (not scans): the glyphs are real text-showing operators, so the
// table is recovered geometrically rather than by OCR. Proven on the real "RLV_CHQ_*.pdf" file.
//
// Approach, in order:
//   1. pull every "stream ... endstream" body, inflate the Flate ones (zlib header 0x78 ...);
//   2. tokenize the content stream, tracking the text matrix (BT/ET + Tm/Td/TD/TL/T*) and the CTM
//      (q/Q + cm) to get each drawn fragment's page X/Y, tagging it with its BT run number;
//   3. cluster fragments into visual rows by (run, Y-band): two movements the bank stacks <1pt
//      apart across separate BT runs must NOT merge, yet a genuine vertical break inside one run
//      must split — the run id is what makes both cases separable;
//   4. for each candidate row read the value-date cell (dd.MM at X<60), the amount cell from
//      whichever of the TWO right-aligned columns holds a figure — Debit (~X400-470) or Credit
//      (~X480-545) — and the label (X in [60,400), minus the Visa disclaimer prose);
//   5. sign: the CREDIT column's figure is money-in (+), the DEBIT column's is money-out (-).
//
// Known limits, by evidence on the real file:
//   - a genuine movement fills exactly ONE of the two amount columns with exactly one money cell;
//     a row that fills both, or fills one with several figures, or carries a money cell outside
//     both bands, is the superposed stacked block the bank paints at one Y and is rejected as a
//     bounded, review-visible loss (never an invented or mis-signed figure);
//   - the header's month name loses its accent glyph ("aout"->"ao?t"), so the year is NOT taken
//     from the header but from the modal ddmmyy token that rides inside every label;
//   - two movements the bank emits inside a SINGLE BT run with no advance between them (same X/Y)
//     are physically superposed and cannot be split: one of the two amounts is dropped. A dropped
//     amount is a bounded, review-visible loss (never an invented row); the review step corrects it.
public static class PdfStatementReader
{
    private static readonly Encoding Latin1 = Encoding.GetEncoding(28591)!;

    // Money cell: optional minus, thin-space thousands, French decimal comma (e.g. "1 234,56").
    private static readonly System.Text.RegularExpressions.Regex MoneyRegex =
        new(@"^-?\s?\d{1,3}([ .]\d{3})*,\d{2}$", System.Text.RegularExpressions.RegexOptions.Compiled);

    // Value-date token riding inside a label: six digits that read as (01..31)(01..12)(yy).
    private static readonly System.Text.RegularExpressions.Regex ValueDateToken =
        new(@"(?<!\d)(0[1-9]|[12]\d|3[01])(0[1-9]|1[0-2])(\d{2})(?!\d)", System.Text.RegularExpressions.RegexOptions.Compiled);

    // The Visa-Infinite / "VOTRE ENGAGEMENT" disclaimer shares the table's X band. Drop any cell
    // that reads as prose (two consecutive capitalised words), carries the '?' replacement glyph,
    // or is a URL — these never appear in a real movement label.
    private static readonly System.Text.RegularExpressions.Regex NoticeWord =
        new(@"\p{Lu}\p{Ll}{3,}\s+\p{Lu}\p{Ll}{2,}|www\.|http|\?|\bles\b|\bvotre\b|\bconditions\b|\bautorisation\b|\bTAEG\b|\bsommes\b|\blors\b|\bcompte au\b",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    // Rows that are page furniture, not movements.
    private static readonly System.Text.RegularExpressions.Regex NoiseRow =
        new(@"TOTAL|SOLDE|PERSONNES|CREDITEUR|Monnaie|Conciergerie|www\.", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex DateCellRegex =
        new(@"^(\d{2})\.(\d{2})$", System.Text.RegularExpressions.RegexOptions.Compiled);

    public static StatementParseResult Parse(byte[] bytes, string sourceName)
    {
        try
        {
            var text = Latin1.GetString(bytes);
            var cells = new List<Cell>();
            // The run counter must be GLOBAL across content streams: one stream per page, and every
            // page restarts its first movement at the same (Tm) position — the first rows of page 1 and
            // page 2 both sit at Y≈674.6 in their own BT #3. Restarting runId per stream made them share
            // the (run, Y) key, so BuildRows fused the two pages' first rows into one monster row (five
            // dates, debit and credit figures mixed) that the overload guard then rejected wholesale —
            // silently swallowing the salary and the incoming transfers riding in those rows.
            int runBase = 0;
            foreach (var content in ContentStreams(text, bytes))
                runBase = Extract(cells, content, runBase);

            if (cells.Count == 0)
                return Fail("aucun fragment de texte lu — PDF scanne ?", sourceName);

            // Year from the modal ddmmyy token across all cells (the header month is unreliable, see
            // class note). Guards against a stray future-dated token skewing the mode.
            var yearCounts = new Dictionary<int, int>();
            foreach (var c in cells)
            {
                var gm = ValueDateToken.Match(Clean(c.T));
                if (gm.Success)
                {
                    int yy = 2000 + int.Parse(gm.Groups[3].Value, CultureInfo.InvariantCulture);
                    yearCounts[yy] = yearCounts.TryGetValue(yy, out var n) ? n + 1 : 1;
                }
            }
            int year = DateTime.Now.Year, best = 0;
            foreach (var kv in yearCounts) if (kv.Value > best) { best = kv.Value; year = kv.Key; }

            var rows = BuildRows(cells, 3.0);
            var parsed = new List<ParsedRow>();
            int skipped = 0;

            foreach (var r in rows)
            {
                var joined = string.Join(" ", r.Cells.Select(c => Clean(c.T)));
                if (NoiseRow.IsMatch(joined)) { skipped++; continue; }

                var dateCells = r.Cells.Where(c =>
                    c.X < 60 && DateCellRegex.IsMatch(Clean(c.T))).ToList();
                if (dateCells.Count == 0) { skipped++; continue; }

                // The statement prints TWO right-aligned amount columns: Débit (~X400-470) and Crédit
                // (~X480-545). The direction is carried by WHICH column holds the figure, so the sign is
                // read from the geometry, never guessed from the label. A genuine single movement fills
                // exactly one of the two columns with exactly one money cell; anything else is the superposed
                // header/footer block the bank stacks at one Y and cannot be split — rejected as a bounded,
                // review-visible loss (never a wrong invented figure).
                var debitCells = r.Cells.Where(c => c.X >= 400 && c.X < 470 && MoneyRegex.IsMatch(Clean(c.T))).ToList();
                var creditCells = r.Cells.Where(c => c.X >= 480 && c.X < 560 && MoneyRegex.IsMatch(Clean(c.T))).ToList();
                bool stray = r.Cells.Any(c => c.X > 380 && MoneyRegex.IsMatch(Clean(c.T)) &&
                                             !((c.X >= 400 && c.X < 470) || (c.X >= 480 && c.X < 560)));
                if (stray || debitCells.Count > 1 || creditCells.Count > 1
                    || (debitCells.Count > 0 && creditCells.Count > 0)
                    || (debitCells.Count == 0 && creditCells.Count == 0))
                { skipped++; continue; }

                var dm = DateCellRegex.Match(Clean(dateCells[0].T));
                int d1 = int.Parse(dm.Groups[1].Value, CultureInfo.InvariantCulture);
                int m1 = int.Parse(dm.Groups[2].Value, CultureInfo.InvariantCulture);
                var date = BuildDate(d1, m1, year);

                bool isCredit = creditCells.Count == 1;
                decimal magnitude = BandMagnitude(r.Cells, isCredit ? 480 : 400, isCredit ? 560 : 470);

                var label = string.Join(" ", r.Cells
                        .Where(c => c.X >= 60 && c.X < 400 && !IsNotice(c.T))
                        .Select(c => Clean(c.T)))
                    .Replace("  ", " ").Trim();

                // A label of only digits/separators is the footer or a mis-grouped row, not a movement.
                if (label.Length < 3 || System.Text.RegularExpressions.Regex.IsMatch(label, @"^[\d.,\ ]+$"))
                { skipped++; continue; }

                decimal signed = isCredit ? magnitude : -magnitude;
                parsed.Add(new ParsedRow(date, label, signed));
            }

            if (parsed.Count == 0)
                return Fail("aucun mouvment reconnu (mise en page inattendue ?)", sourceName);

            parsed.Sort((a, b) => a.Date.CompareTo(b.Date));
            return new StatementParseResult(true, null, parsed, "pdf",
                "date (dd.MM)", "libelle", "montant (debit X400-470 | credit X480-560)", null, null, false, skipped);
        }
        catch (Exception ex)
        {
            return Fail(ex.Message, sourceName);
        }
    }

    private static StatementParseResult Fail(string err, string sourceName)
        => new(false, err, [], "pdf", null, null, null, null, null, false, 0);

    // Reconstructs one column's figure from the (possibly split) cells inside its X band. A large
    // amount is emitted as separate fragments — "2" then "150,78" for 2 150,78 — so every pure-number
    // cell in the band is concatenated left-to-right before the money parse.
    private static decimal BandMagnitude(List<Cell> cells, double lo, double hi)
    {
        var parts = cells.Where(c => c.X >= lo && c.X < hi
            && System.Text.RegularExpressions.Regex.IsMatch(Clean(c.T), @"^[\d\s.,]+$")).ToList();
        parts.Sort((a, b) => a.X.CompareTo(b.X));
        var joined = string.Join(string.Empty, parts.Select(c => Clean(c.T)));
        return ParseMoney(joined);
    }

    private static decimal ParseMoney(string raw)
    {
        var s = Clean(raw).Replace(" ", "").Replace("EUR", "").Replace(",", ".");
        return decimal.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0m;
    }

    private static bool IsNotice(string t)
    {
        var c = Clean(t);
        return c.Length == 0 || NoticeWord.IsMatch(c);
    }

    private static DateTime BuildDate(int d, int m, int y)
    {
        if (d < 1) d = 1; if (d > 28) d = Math.Min(d, 31);
        if (m < 1) m = 1; if (m > 12) m = 12;
        try { return new DateTime(y, m, d); } catch { return new DateTime(y, 12, 31); }
    }

    // Non-printable / non-ASCII bytes become '?' ; the '|' pipe is reserved as our join separator.
    private static string Clean(string t)
    {
        var sb = new StringBuilder(t.Length);
        foreach (var ch in t)
            sb.Append(ch >= 0x20 && ch <= 0x7E ? (ch == '|' ? '/' : ch) : '?');
        return sb.ToString();
    }

    // ---- stream extraction ---------------------------------------------------------------

    private static IEnumerable<string> ContentStreams(string text, byte[] bytes)
    {
        int pos = 0;
        while (true)
        {
            int i = text.IndexOf("stream", pos, StringComparison.Ordinal);
            if (i < 0) yield break;
            if (i >= 3 && text.Substring(i - 3, 3) == "end") { pos = i + 6; continue; }
            int start = i + 6;
            if (start < text.Length && text[start] == '\r') start++;
            if (start < text.Length && text[start] == '\n') start++;
            int end = text.IndexOf("endstream", start, StringComparison.Ordinal);
            if (end < 0) yield break;
            pos = end + 9;
            int len = end - start;
            if (len <= 2) continue;

            var seg = new byte[len];
            Array.Copy(bytes, start, seg, 0, len);
            // zlib-wrapped deflate: 0x78 is the only CMF byte seen for FlateDecode streams here.
            if (!(seg.Length > 2 && seg[0] == 0x78)) continue;
            using var ms = new MemoryStream(seg, 2, seg.Length - 2);
            using var dfs = new DeflateStream(ms, CompressionMode.Decompress);
            using var sr = new StreamReader(dfs, Latin1);
            string outText;
            try { outText = sr.ReadToEnd(); } catch { continue; }
            if (outText.Length > 0) yield return outText;
        }
    }

    // ---- content-stream tokenizer + text/graphics matrix tracking -----------------------

    // Returns the next global run id so the following content stream (page) continues the counter —
    // see the Parse comment on why run ids must never restart per page.
    private static int Extract(List<Cell> cells, string s, int runBase)
    {
        double[] ctm = { 1, 0, 0, 1, 0, 0 };
        var stack = new Stack<double[]>();
        double[] tlm = { 1, 0, 0, 1, 0, 0 };
        double tl = 0;
        bool inText = false;
        int runId = runBase;
        double textX = 0, textY = 0;

        var nums = new List<double>();
        var strs = new List<string>();
        int k = 0;

        while (k < s.Length)
        {
            char ch = s[k];
            if (ch == ' ' || ch == '\t' || ch == '\r' || ch == '\n' || ch == '\f' || ch == '\0') { k++; continue; }
            if (ch == '%') { while (k < s.Length && s[k] != '\n') k++; continue; }

            // literal string ( ... ) with escapes and balanced parens
            if (ch == '(')
            {
                var sb = new StringBuilder();
                k++;
                int depth = 1;
                while (k < s.Length && depth > 0)
                {
                    char c = s[k];
                    if (c == '\\')
                    {
                        if (k + 1 < s.Length) { sb.Append(Unescape(s[k + 1])); k += 2; }
                        else k++;
                        continue;
                    }
                    if (c == '(') depth++;
                    else if (c == ')') { depth--; if (depth == 0) { k++; break; } }
                    sb.Append(c);
                    k++;
                }
                strs.Add(sb.ToString());
                continue;
            }

            // hex string < ... >
            if (ch == '<' && k + 1 < s.Length && s[k + 1] != '<')
            {
                int gt = s.IndexOf('>', k + 1);
                if (gt < 0) { k++; continue; }
                var sb = new StringBuilder();
                for (int h = k + 1; h + 1 < gt; h += 2)
                {
                    if (int.TryParse(s.Substring(h, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int v))
                        sb.Append((char)v);
                }
                strs.Add(sb.ToString());
                k = gt + 1;
                continue;
            }

            if (ch == '[' || ch == ']' || ch == '{' || ch == '}') { k++; continue; }
            // /Name  — advance PAST the leading slash first, then consume up to a delimiter
            if (ch == '/') { k++; while (k < s.Length && !IsDelim(s[k])) k++; continue; }

            // number
            if (char.IsDigit(ch) || ch == '.' || ch == '-' || ch == '+')
            {
                int e = k;
                while (e < s.Length && (char.IsDigit(s[e]) || s[e] == '.' || s[e] == '-' || s[e] == '+')) e++;
                if (double.TryParse(s.Substring(k, e - k), NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                    nums.Add(v);
                k = e;
                continue;
            }

            // keyword / operator
            {
                int e = k;
                while (e < s.Length && !IsDelim(s[e])) e++;
                if (e == k) { k++; continue; }   // lone delimiter (e.g. '<' of '<<'): skip, never stall
                var op = s.Substring(k, e - k);
                k = e;

                switch (op)
                {
                    case "q": stack.Push((double[])ctm.Clone()); break;
                    case "Q": if (stack.Count > 0) ctm = stack.Pop(); break;
                    case "cm" when nums.Count >= 6:
                        ctm = Mul(Tail(nums, 6), ctm);
                        break;
                    case "BT":
                        inText = true;
                        runId++;
                        tlm = new double[] { 1, 0, 0, 1, 0, 0 };
                        break;
                    case "ET":
                        inText = false;
                        break;
                    case "Tm" when nums.Count >= 6:
                        tlm = Tail(nums, 6);
                        break;
                    case "Td" when nums.Count >= 2:
                        tlm = Mul(new[] { 1.0, 0.0, 0.0, 1.0, nums[nums.Count - 2], nums[nums.Count - 1] }, tlm);
                        break;
                    case "TD" when nums.Count >= 2:
                        tlm = Mul(new[] { 1.0, 0.0, 0.0, 1.0, nums[nums.Count - 2], nums[nums.Count - 1] }, tlm);
                        tl = -nums[nums.Count - 1];
                        break;
                    case "TL" when nums.Count >= 1:
                        tl = nums[nums.Count - 1];
                        break;
                    case "T*":
                        tlm = Mul(new[] { 1.0, 0.0, 0.0, 1.0, 0.0, -tl }, tlm);
                        break;
                    case "Tj" or "TJ" when strs.Count > 0:
                        if (inText)
                        {
                            textX = tlm[4] * ctm[0] + tlm[5] * ctm[2] + ctm[4];
                            textY = tlm[4] * ctm[1] + tlm[5] * ctm[3] + ctm[5];
                            foreach (var str in strs)
                                if (str.Length > 0) cells.Add(new Cell(str, textX, textY, runId));
                        }
                        break;
                    // ' and " move to the next line (a T* by the current leading) BEFORE showing the
                    // string. Treating them as a bare Tj left every wrapped line of a multi-line block at
                    // the last Tm's Y — which is what piled five movements onto a single Y and dropped the
                    // salary row riding in that block.
                    case "'" or "\"" when strs.Count > 0:
                        if (inText)
                        {
                            tlm = Mul(new[] { 1.0, 0.0, 0.0, 1.0, 0.0, -tl }, tlm);
                            textX = tlm[4] * ctm[0] + tlm[5] * ctm[2] + ctm[4];
                            textY = tlm[4] * ctm[1] + tlm[5] * ctm[3] + ctm[5];
                            foreach (var str in strs)
                                if (str.Length > 0) cells.Add(new Cell(str, textX, textY, runId));
                        }
                        break;
                }
                nums.Clear();
                strs.Clear();
                continue;
            }
        }
        return runId;
    }

    private static double[] Tail(List<double> nums, int n)
    {
        var r = new double[n];
        for (int i = 0; i < n; i++) r[i] = nums[nums.Count - n + i];
        return r;
    }

    // 2x3 affine matrix multiply: (a ∘ b), row-vector convention used by PDF.
    private static double[] Mul(double[] a, double[] b) => new[]
    {
        a[0] * b[0] + a[2] * b[1],
        a[1] * b[0] + a[3] * b[1],
        a[0] * b[2] + a[2] * b[3],
        a[1] * b[2] + a[3] * b[3],
        a[0] * b[4] + a[2] * b[5] + a[4],
        a[1] * b[4] + a[3] * b[5] + a[5],
    };

    private static bool IsDelim(char c) =>
        c == ' ' || c == '\t' || c == '\r' || c == '\n' || c == '\f' || c == '\0'
        || c == '(' || c == ')' || c == '<' || c == '>' || c == '[' || c == ']' || c == '{' || c == '}' || c == '/' || c == '%';

    private static char Unescape(char n) => n switch
    {
        'n' => '\n', 'r' => '\r', 't' => '\t', 'b' => '\b', 'f' => '\f', _ => n,
    };

    // ---- geometry: cluster cells into visual rows, keyed by (run, Y-band) ---------------

    private static List<Row> BuildRows(List<Cell> cells, double tol)
    {
        // Group by (text-run, Y-band): cells from different BT blocks never merge even when their Y
        // bands nearly touch (the bank stacks two movements <1pt apart across separate runs), while a
        // genuine vertical break inside one run still opens a new row. Sort run-major so a run's cells
        // are contiguous; within a run sort Y descending so the anchor walks down the page.
        var sorted = new List<Cell>(cells);
        sorted.Sort((a, b) => a.Run != b.Run ? a.Run.CompareTo(b.Run) : b.Y.CompareTo(a.Y));
        var rows = new List<Row>();
        foreach (var c in sorted)
        {
            if (rows.Count > 0 && rows[rows.Count - 1].Run == c.Run && Math.Abs(rows[rows.Count - 1].Y - c.Y) <= tol)
                rows[rows.Count - 1].Cells.Add(c);
            else
                rows.Add(new Row(c.Y) { Run = c.Run, Cells = { c } });
        }
        rows.Sort((a, b) => b.Y.CompareTo(a.Y));
        foreach (var r in rows) r.Cells.Sort((a, b) => a.X.CompareTo(b.X));
        return rows;
    }

    private sealed class Cell(string t, double x, double y, int run)
    {
        public string T { get; } = t;
        public double X { get; } = x;
        public double Y { get; } = y;
        public int Run { get; } = run;
    }

    private sealed class Row(double y)
    {
        public double Y { get; } = y;
        public int Run { get; set; }
        public List<Cell> Cells { get; } = [];
    }
}
