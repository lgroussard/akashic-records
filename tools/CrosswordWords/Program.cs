// Builds android/app/src/main/assets/crossword/fr-wiktionnaire.tsv (WORD <TAB> clue) from kaikki.org's extraction
// of the French Wiktionary (JSONL, one entry per line). Definitions © Wiktionnaire contributors, CC BY-SA.
//
// Usage: dotnet run -- <kaikki French .jsonl> <output .tsv> [minTranslations]
//
// Kept: common French nouns, verbs, adjectives and adverbs of 3 to 9 letters (no proper nouns, no hyphen/space/
// apostrophe), "common" meaning the entry has at least minTranslations translations (default 3). Their clue is the
// first sense that is neither an inflected form nor marked old, rare or slang, cut to its first sentence; a clue
// longer than 70 characters or containing the word itself is not used.
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

if (args.Length < 2)
{
    Console.Error.WriteLine("Usage: dotnet run -- <kaikki French .jsonl> <output .tsv> [minTranslations]");
    return 1;
}
var input = args[0];
var output = args[1];
var minTranslations = args.Length > 2 ? int.Parse(args[2]) : 3;

var keptPos = new HashSet<string> { "noun", "verb", "adj", "adv" };
var skipTags = new HashSet<string>
{
    "form-of", "obsolete", "archaic", "dated", "rare", "slang", "vulgar", "familiar", "pejorative", "regional",
    "colloquial", "informal", "historical", "alt-of", "abbreviation", "error", "figuratively", "Biblical", "ironic",
};
var skipStarts = new[]
{
    "Pluriel de", "Féminin", "Masculin", "Variante", "Ancienne orthographe", "Graphie", "Participe", "Forme",
    "Première personne", "Deuxième personne", "Troisième personne", "Abréviation", "Sigle", "Synonyme de",
};
var wordShape = new Regex("^[a-zàâäçéèêëîïôöùûüÿœæ]+$");
// A clue that only makes sense next to another one, or carries codes: not usable alone in a grid.
// Senses from these fields are too technical for a grid ("Groupement alkyle dérivé du propane"): skipped, and a word
// with only such senses is left out. Everyday fields (zoology, botany, cooking, music, astronomy…) stay: they carry
// the usual sense of CHIEN, ROSE or LUNE.
var technical = new HashSet<string>
{
    "chemistry", "biochemistry", "medicine", "surgery", "pharmacology", "linguistic", "grammar", "computing",
    "programming", "mineralogy", "geology", "metrology", "mathematics", "geometry", "technical", "heraldry",
    "electricity", "mechanical", "telecommunications", "metallurgy", "typography", "astronautics", "biology",
    "sexuality", "law", "finance",
};
var refersElsewhere = new Regex(@"#|\d{3,}|\b(ces|cette|celui-ci|celle-ci|ceux-ci|cet|de ce|à ce|ce dernier|cette dernière|idem|voir)\b", RegexOptions.IgnoreCase);

// Best clue per normalized word: the one from the entry with the most translations (the most current sense).
var best = new Dictionary<string, (string Clue, int Translations)>();
long lines = 0;

using (var reader = new StreamReader(input, Encoding.UTF8))
{
    string? line;
    while ((line = reader.ReadLine()) is not null)
    {
        lines++;
        if (lines % 200_000 == 0) Console.Error.WriteLine($"  {lines:N0} lignes, {best.Count:N0} mots");
        // Cheap prefilter before parsing: most lines are other parts of speech or forms.
        if (!line.Contains("\"lang_code\": \"fr\"")) continue;
        using var doc = JsonDocument.Parse(line);
        var e = doc.RootElement;
        if (Str(e, "lang_code") != "fr" || !keptPos.Contains(Str(e, "pos"))) continue;
        var word = Str(e, "word");
        if (!wordShape.IsMatch(word)) continue;
        var key = Normalize(word);
        if (key.Length is < 3 or > 9) continue;
        var translations = e.TryGetProperty("translations", out var tr) && tr.ValueKind == JsonValueKind.Array ? tr.GetArrayLength() : 0;
        if (translations < minTranslations) continue;
        if (best.TryGetValue(key, out var known) && known.Translations >= translations) continue;
        if (!e.TryGetProperty("senses", out var senses) || senses.ValueKind != JsonValueKind.Array) continue;

        // Senses in the Wiktionnaire's order: the usual one comes first (often tagged with its field, so no skipping on it).
        string? found = null;
        foreach (var s in senses.EnumerateArray())
        {
            if (s.TryGetProperty("form_of", out _)) continue;
            if (s.TryGetProperty("topics", out var topics) && topics.EnumerateArray().Any(t => technical.Contains(t.GetString() ?? ""))) continue;
            if (s.TryGetProperty("tags", out var tags) && tags.EnumerateArray().Any(t => skipTags.Contains(t.GetString() ?? ""))) continue;
            if (!s.TryGetProperty("glosses", out var glosses) || glosses.GetArrayLength() == 0) continue;
            // The first gloss: a sub-sense lists its parent's first, and its own often refers back to it ("ces…").
            var clue = Clean(glosses[0].GetString() ?? "");
            if (clue is null || skipStarts.Any(p => clue.StartsWith(p, StringComparison.Ordinal))) continue;
            if (refersElsewhere.IsMatch(clue)) continue;
            if (Normalize(clue).Contains(key)) continue; // the clue would give the answer away
            found = clue;
            break;
        }
        if (found is not null) best[key] = (found, translations);
    }
}

var rows = best.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}\t{kv.Value.Clue}").ToList();
File.WriteAllLines(output, new[]
{
    "# Mots croisés : MOT <TAB> définition, tirées du Wiktionnaire (https://fr.wiktionary.org),",
    "# contributeurs du Wiktionnaire, licence CC BY-SA. Fichier généré par build-crossword-words.ps1 — ne pas modifier à la main.",
}.Concat(rows), new UTF8Encoding(false));

Console.Error.WriteLine($"{lines:N0} lignes lues, {rows.Count:N0} mots gardés.");
foreach (var g in best.Keys.GroupBy(k => k.Length).OrderBy(g => g.Key)) Console.Error.WriteLine($"  {g.Key} lettres : {g.Count():N0}");
return 0;

static string Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";

// Upper case, accents removed, Œ/Æ spelled out: how the grid writes a word.
static string Normalize(string s)
{
    var d = s.ToUpperInvariant().Replace("Œ", "OE").Replace("Æ", "AE").Normalize(NormalizationForm.FormD);
    var sb = new StringBuilder(d.Length);
    foreach (var ch in d)
        if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark && ch is >= 'A' and <= 'Z') sb.Append(ch);
    return sb.ToString();
}

// First sentence, no parentheses, at most 70 characters (else null).
static string? Clean(string gloss)
{
    var g = Regex.Replace(gloss, "\\([^)]*\\)", "");
    g = Regex.Replace(g, "\\s+", " ").Trim();
    var cut = Regex.Match(g, "^(.{8,}?)(\\.\\s|;\\s|\\s:\\s)");
    if (cut.Success) g = cut.Groups[1].Value;
    g = g.TrimEnd('.', ' ', ',', ';', ':');
    // Too long for a grid: keep the first clause ("Mammifère carnivore de la famille des Canidés, apparenté au loup…"
    // → "Mammifère carnivore de la famille des Canidés"); with no usable clause the sense is skipped.
    if (g.Length > 70)
    {
        var comma = g.IndexOf(", ", StringComparison.Ordinal);
        if (comma < 12 || comma > 70) return null;
        g = g[..comma];
    }
    if (g.Length < 4) return null;
    return char.ToUpperInvariant(g[0]) + g[1..];
}
