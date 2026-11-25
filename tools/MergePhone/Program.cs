// Three-way merge of the phone's database into the PC's (see merge-phone.ps1 at the repo root, which drives it).
//   MergePhone --pc <akashic.db> --phone <akashic.db> --out <merged.db> --base <dump.db> [--base <dump.db> …]
//              [--phone-wins Table.Id …]
//
// The PC database is copied to --out and only the copy is written. --base is the desktop dump the phone last
// imported; given several candidates, the one the phone is closest to is used. For each row, by Id:
//   - a column changed on one side only since the base takes that side's value;
//   - a column changed on both sides to different values is a conflict: the PC value is kept and the conflict
//     is listed, unless --phone-wins Table.Id says otherwise;
//   - a row created on the phone is added; if the PC created another row under the same Id, the phone's row
//     gets a new Id and the rows pointing at it follow;
//   - a row deleted on the phone is deleted on the PC if the PC did not change it.
// Exit code: 0 merged, 2 merged with conflicts left on the PC value, 1 error.
using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;

// Rows that describe files living on one device only (the phone's own music folder, its books, its reference
// images): the export does not carry those files, so the rows stay where they are. Counted in the report.
string[] deviceLocal = ["MusicTrack", "DownloadItem", "DownloadFolder", "Book", "BookHighlight", "RefBoard", "RefImage"];
// Tables only the phone writes, whose files (if any) are under media/: the PC copy follows the phone's.
string[] phoneOwned = ["Ticket", "CrosswordGame", "ShoppingItem"];
string[] ignored = ["sqlite_sequence", "SchemaFlag", "room_master_table", "android_metadata"];

// Columns of the PC's own layout of a poem: the phone has Mobile* columns for its own, and what an old phone
// build wrote into these is not a choice made on the PC. Never taken from the phone on an existing poem.
var pcLayout = new HashSet<string> { "Poem.TextAlignment", "Poem.Margin", "Poem.FontSize", "Poem.FontFamily", "Poem.Bold", "Poem.Italic", "Poem.EditorWidth" };

string[] poemText = ["Text", "RichContent"];

// child column -> parent table, declared in the schema or not.
var parents = new Dictionary<string, string>
{
    ["ArtworkEvaluation.ArtworkId"] = "Artwork", ["ArtworkEvaluation.AxisId"] = "EvaluationAxis",
    ["Reference.ObservationId"] = "Observation", ["Reference.ProjectId"] = "ArtisticProject",
    ["CanvasElement.ProjectId"] = "ArtisticProject", ["CanvasElement.ObservationId"] = "Observation",
    ["CanvasConnector.ProjectId"] = "ArtisticProject", ["CanvasConnector.FromElementId"] = "CanvasElement",
    ["CanvasConnector.ToElementId"] = "CanvasElement", ["JournalPhoto.EntryId"] = "JournalEntry",
    ["Poem.RecueilId"] = "Recueil", ["ProjectTask.ProjectId"] = "PersonalProject",
    ["TransitionStep.TransitionId"] = "Transition", ["Photo.AlbumId"] = "PhotoAlbum",
    ["BudgetTransaction.CategoryId"] = "BudgetCategory", ["BudgetRule.CategoryId"] = "BudgetCategory",
    ["BudgetPlanLine.PlanId"] = "BudgetPlan", ["BudgetPlanLine.CategoryId"] = "BudgetCategory",
    ["Observation.ArtworkId"] = "Artwork", ["Ticket.EventId"] = "CalendarEvent",
};

string? pcPath = null, phonePath = null, outPath = null;
var bases = new List<string>();
var phoneWins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
for (var i = 0; i + 1 < args.Length; i += 2)
{
    switch (args[i])
    {
        case "--pc": pcPath = args[i + 1]; break;
        case "--phone": phonePath = args[i + 1]; break;
        case "--out": outPath = args[i + 1]; break;
        case "--base": bases.Add(args[i + 1]); break;
        case "--phone-wins": phoneWins.Add(args[i + 1]); break;
        default: Console.Error.WriteLine($"Option inconnue : {args[i]}"); return 1;
    }
}
if (pcPath is null || phonePath is null || outPath is null || bases.Count == 0 || args.Length % 2 != 0)
{
    Console.Error.WriteLine("Usage: MergePhone --pc <db> --phone <db> --out <db> --base <db> [--base <db> …] [--phone-wins Table.Id …]");
    return 1;
}
foreach (var f in bases.Append(pcPath).Append(phonePath))
    if (!File.Exists(f)) { Console.Error.WriteLine($"Fichier introuvable : {f}"); return 1; }

Console.OutputEncoding = Encoding.UTF8;
var basePath = PickBase();
File.Copy(pcPath, outPath, overwrite: true);

using var db = Open(outPath);
Exec($"ATTACH DATABASE '{basePath.Replace("'", "''")}' AS b");
Exec($"ATTACH DATABASE '{phonePath.Replace("'", "''")}' AS p");

var conflicts = 0;
var remap = new Dictionary<string, Dictionary<long, long>>();
var tables = Names("main").Where(t => Names("p").Contains(t) && !ignored.Contains(t)).ToList();
var merged = tables.Where(t => !deviceLocal.Contains(t) && !phoneOwned.Contains(t)).ToList();

Console.WriteLine($"Base commune : {basePath}");
Console.WriteLine();

using (var tx = db.BeginTransaction())
{
    // Pass 1: new phone rows whose Id the PC also used for a row of its own get a free Id.
    foreach (var t in merged)
    {
        var b = Ids("b", t); var c = Ids("main", t); var p = Ids("p", t);
        var next = Math.Max(c.DefaultIfEmpty(0).Max(), p.DefaultIfEmpty(0).Max()) + 1;
        foreach (var id in p.Where(id => !b.Contains(id) && c.Contains(id)).Order())
        {
            if (SameRow(t, id)) continue; // already merged by an earlier run
            (remap.TryGetValue(t, out var m) ? m : remap[t] = [])[id] = next++;
        }
    }

    // Pass 2: rows.
    foreach (var t in merged)
    {
        var cols = Cols("main", t).Intersect(Cols("p", t)).ToList();
        var baseCols = Names("b").Contains(t) ? Cols("b", t) : [];
        var b = baseCols.Count > 0 ? Rows("b", t, cols.Where(baseCols.Contains).ToList()) : [];
        var c = Rows("main", t, cols);
        var p = Rows("p", t, cols);
        var lines = new List<string>();

        foreach (var (id, prow) in p.OrderBy(r => r.Key))
        {
            if (!b.TryGetValue(id, out var brow))
            {
                // Created on the phone.
                var newId = remap.GetValueOrDefault(t)?.GetValueOrDefault(id, id) ?? id;
                if (newId == id && c.ContainsKey(id)) continue; // same row already there
                var values = cols.ToDictionary(col => col, col => col == "Id" ? newId : Mapped(t, col, prow));
                Insert(t, values);
                lines.Add($"  + ajouté : {Label(prow)}" + (newId != id ? $"  (Id {id} -> {newId} : le PC avait déjà une autre ligne {id})" : ""));
                continue;
            }
            if (!c.TryGetValue(id, out var crow))
            {
                if (cols.Any(col => brow.ContainsKey(col) && !Same(prow[col], brow[col])))
                {
                    conflicts++;
                    lines.Add($"  ! CONFLIT : {Label(prow)} modifié sur le téléphone mais supprimé sur le PC — laissé supprimé");
                }
                continue;
            }
            var phoneFirst = phoneWins.Contains($"{t}.{id}");
            // A poem's Text and RichContent are one thing written twice: when one of them is in conflict and stays
            // on the PC version, the other one stays with it, or the poem would show two different texts.
            bool Clash(string col) => brow.TryGetValue(col, out var bv) && !Same(prow[col], bv) && !Same(crow[col], bv) && !Same(prow[col], crow[col]);
            var held = t == "Poem" && !phoneFirst && poemText.Any(Clash) ? poemText : [];
            foreach (var col in cols.Where(col => col != "Id"))
            {
                var pv = prow[col]; var cv = crow[col];
                if (Same(pv, cv)) continue;
                if (held.Contains(col) && !Clash(col)) continue;
                if (!brow.TryGetValue(col, out var bv))
                {
                    // A column the base did not have yet: the side that filled it wins, the PC if both did.
                    if (IsBlank(cv) && !IsBlank(pv)) { Update(t, id, col, Mapped(t, col, prow)); lines.Add($"  ~ {Label(crow)} : {col} repris du téléphone"); }
                    continue;
                }
                if (Same(pv, bv)) continue; // changed on the PC only: already in place
                if (pcLayout.Contains($"{t}.{col}"))
                {
                    lines.Add($"  · {Label(crow)} : {col} = {Short(pv)} sur le téléphone, ignoré (mise en page du PC : {Short(cv)})");
                    continue;
                }
                if (Same(cv, bv)) { Update(t, id, col, Mapped(t, col, prow)); lines.Add($"  ~ {Label(crow)} : {col} modifié sur le téléphone"); continue; }
                if (phoneFirst) { Update(t, id, col, Mapped(t, col, prow)); lines.Add($"  ~ {Label(crow)} : {col} modifié des deux côtés, version du téléphone prise (--phone-wins)"); continue; }
                conflicts++;
                lines.Add($"  ! CONFLIT : {Label(crow)} : {col} modifié des deux côtés — version du PC gardée");
                lines.Add($"      base      : {Short(bv)}");
                lines.Add($"      PC        : {Short(cv)}");
                lines.Add($"      téléphone : {Short(pv)}");
            }
        }

        foreach (var (id, brow) in b.Where(r => !p.ContainsKey(r.Key) && c.ContainsKey(r.Key)).OrderBy(r => r.Key))
        {
            var crow = c[id];
            if (brow.Keys.All(col => Same(brow[col], crow[col])))
            {
                Run($"DELETE FROM \"{t}\" WHERE Id = $id", ("$id", id));
                lines.Add($"  - supprimé (comme sur le téléphone) : {Label(crow)}");
            }
            else
            {
                conflicts++;
                lines.Add($"  ! CONFLIT : {Label(crow)} supprimé sur le téléphone mais modifié sur le PC — gardé");
            }
        }

        var pcNew = c.Keys.Count(id => !b.ContainsKey(id));
        var pcChanged = c.Count(r => b.TryGetValue(r.Key, out var brow) && brow.Keys.Any(col => !Same(brow[col], r.Value[col])));
        if (lines.Count == 0 && pcNew == 0 && pcChanged == 0) continue;
        Console.WriteLine($"{t}  (PC depuis la base : {pcNew} ajoutés, {pcChanged} modifiés — gardés)");
        lines.ForEach(Console.WriteLine);
    }

    // Tables only the phone writes: the PC copy becomes the phone's.
    foreach (var t in tables.Where(phoneOwned.Contains))
    {
        var cols = Cols("main", t).Intersect(Cols("p", t)).ToList();
        var c = Rows("main", t, cols); var p = Rows("p", t, cols);
        var added = 0; var updated = 0;
        foreach (var (id, prow) in p)
        {
            var values = cols.ToDictionary(col => col, col => Mapped(t, col, prow));
            if (!c.TryGetValue(id, out var crow)) { Insert(t, values); added++; }
            else if (cols.Any(col => !Same(values[col], crow[col])))
            {
                foreach (var col in cols.Where(col => col != "Id" && !Same(values[col], crow[col]))) Update(t, id, col, values[col]);
                updated++;
            }
        }
        var gone = c.Keys.Where(id => !p.ContainsKey(id)).ToList();
        foreach (var id in gone) Run($"DELETE FROM \"{t}\" WHERE Id = $id", ("$id", id));
        if (added + updated + gone.Count > 0)
            Console.WriteLine($"{t}  (table du téléphone, recopiée) : {added} ajoutés, {updated} mis à jour, {gone.Count} retirés");
    }

    if (remap.Count > 0 && Names("p").Contains("CalendarEventLink"))
    {
        // Its PrimaryId/SecondaryId point at rows of a table named by another column: not followed by the renumbering.
        var links = Scalar("SELECT count(*) FROM p.CalendarEventLink WHERE Id NOT IN (SELECT Id FROM b.CalendarEventLink)");
        if (links > 0) Console.WriteLine($"ATTENTION : {links} lien(s) de calendrier créés sur le téléphone, à vérifier à la main (lignes renumérotées : {string.Join(", ", remap.Keys)}).");
    }
    tx.Commit();
}

Console.WriteLine();
Console.WriteLine("Laissé tel quel de chaque côté (fichiers propres à l'appareil, absents de l'export) :");
foreach (var t in tables.Where(deviceLocal.Contains))
{
    var hasBase = Names("b").Contains(t);
    var fresh = hasBase ? Scalar($"SELECT count(*) FROM p.\"{t}\" WHERE Id NOT IN (SELECT Id FROM b.\"{t}\")") : Scalar($"SELECT count(*) FROM p.\"{t}\"");
    if (fresh > 0) Console.WriteLine($"  {t} : {fresh} ligne(s) créée(s) sur le téléphone");
}

var broken = new List<string>();
using (var check = db.CreateCommand())
{
    check.CommandText = "PRAGMA main.foreign_key_check";
    using var r = check.ExecuteReader();
    while (r.Read()) broken.Add($"{r.GetString(0)} ligne {r.GetValue(1)} -> {r.GetString(2)}");
}
if (broken.Count > 0)
{
    Console.WriteLine();
    Console.WriteLine($"ATTENTION : {broken.Count} référence(s) cassée(s) dans la base fusionnée :");
    broken.Take(20).ToList().ForEach(l => Console.WriteLine("  " + l));
}
if (Scalar("SELECT count(*) FROM pragma_integrity_check WHERE integrity_check <> 'ok'") > 0)
{
    Console.Error.WriteLine("La base fusionnée ne passe pas le contrôle d'intégrité.");
    return 1;
}

Console.WriteLine();
Console.WriteLine(conflicts == 0 ? "Aucun conflit." : $"{conflicts} conflit(s) : la version du PC est gardée. Pour prendre celle du téléphone : -PhoneWins Table.Id");
Console.WriteLine($"Base fusionnée : {outPath}");
return conflicts == 0 ? 0 : 2;

// ---- helpers ---------------------------------------------------------------

SqliteConnection Open(string path, bool readOnly = false)
{
    var c = new SqliteConnection(new SqliteConnectionStringBuilder
    {
        DataSource = path, Pooling = false, Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite,
    }.ToString());
    c.Open();
    return c;
}

// The candidate sharing the most identical rows with the phone is the dump it started from.
string PickBase()
{
    if (bases.Count == 1) return bases[0];
    var best = bases[0]; var bestScore = -1L;
    foreach (var candidate in bases)
    {
        using var c = Open(candidate, readOnly: true);
        using (var a = c.CreateCommand()) { a.CommandText = $"ATTACH DATABASE '{phonePath!.Replace("'", "''")}' AS p"; a.ExecuteNonQuery(); }
        long score = 0;
        foreach (var t in ListOf(c, "SELECT name FROM main.sqlite_master WHERE type='table' AND name IN (SELECT name FROM p.sqlite_master WHERE type='table')").Where(t => !ignored.Contains(t)))
        {
            var mine = ListOf(c, $"SELECT name FROM pragma_table_info('{t}', 'main')");
            var cols = string.Join(", ", ListOf(c, $"SELECT name FROM pragma_table_info('{t}', 'p')").Where(mine.Contains).Select(n => $"\"{n}\""));
            using var q = c.CreateCommand();
            q.CommandText = $"SELECT count(*) FROM (SELECT {cols} FROM main.\"{t}\" INTERSECT SELECT {cols} FROM p.\"{t}\")";
            score += (long)q.ExecuteScalar()!;
        }
        // On a tie the earliest candidate stays: the phone cannot have started from a later dump with the same rows… and if it did, the two are the same.
        if (score > bestScore) { bestScore = score; best = candidate; }
    }
    return best;
}

List<string> ListOf(SqliteConnection c, string sql)
{
    using var q = c.CreateCommand();
    q.CommandText = sql;
    using var r = q.ExecuteReader();
    var list = new List<string>();
    while (r.Read()) list.Add(r.GetString(0));
    return list;
}

void Exec(string sql) { using var q = db.CreateCommand(); q.CommandText = sql; q.ExecuteNonQuery(); }

void Run(string sql, params (string Name, object? Value)[] ps)
{
    using var q = db.CreateCommand();
    q.CommandText = sql;
    foreach (var (name, value) in ps) q.Parameters.AddWithValue(name, value ?? DBNull.Value);
    q.ExecuteNonQuery();
}

long Scalar(string sql) { using var q = db.CreateCommand(); q.CommandText = sql; return Convert.ToInt64(q.ExecuteScalar(), CultureInfo.InvariantCulture); }

List<string> Names(string schema) => ListOf(db, $"SELECT name FROM {schema}.sqlite_master WHERE type='table'");

List<string> Cols(string schema, string table) => ListOf(db, $"SELECT name FROM pragma_table_info('{table}', '{schema}')");

HashSet<long> Ids(string schema, string table)
{
    var set = new HashSet<long>();
    if (!Names(schema).Contains(table)) return set;
    using var q = db.CreateCommand();
    q.CommandText = $"SELECT Id FROM {schema}.\"{table}\"";
    using var r = q.ExecuteReader();
    while (r.Read()) set.Add(r.GetInt64(0));
    return set;
}

Dictionary<long, Dictionary<string, object?>> Rows(string schema, string table, List<string> cols)
{
    var rows = new Dictionary<long, Dictionary<string, object?>>();
    using var q = db.CreateCommand();
    q.CommandText = $"SELECT {string.Join(", ", cols.Select(c => $"\"{c}\""))} FROM {schema}.\"{table}\"";
    using var r = q.ExecuteReader();
    while (r.Read())
    {
        var row = new Dictionary<string, object?>();
        for (var i = 0; i < cols.Count; i++) row[cols[i]] = r.IsDBNull(i) ? null : r.GetValue(i);
        rows[Convert.ToInt64(row["Id"], CultureInfo.InvariantCulture)] = row;
    }
    return rows;
}

// The phone's row and the PC's row of that Id, column for column.
bool SameRow(string table, long id)
{
    var cols = Cols("main", table).Intersect(Cols("p", table)).ToList();
    return Rows("main", table, cols)[id] is var c && Rows("p", table, cols)[id] is var p && cols.All(col => Same(c[col], p[col]));
}

bool Same(object? x, object? y)
{
    if (x is null || y is null) return x is null && y is null;
    if (x is byte[] bx && y is byte[] by) return bx.AsSpan().SequenceEqual(by);
    if (x is string sx && y is string sy) return sx == sy;
    if (x is string || y is string) return false;
    return Convert.ToDouble(x, CultureInfo.InvariantCulture) == Convert.ToDouble(y, CultureInfo.InvariantCulture);
}

bool IsBlank(object? v) => v is null or "" || (v is not string and not byte[] && Convert.ToDouble(v, CultureInfo.InvariantCulture) == 0);

// A phone value, with the Id of a renumbered row it points at replaced by the new one.
object? Mapped(string table, string col, Dictionary<string, object?> row)
{
    var v = row[col];
    if (v is null) return null;
    string? parent = parents.GetValueOrDefault($"{table}.{col}");
    if (table == "Observation" && col == "OwnerId") parent = row.GetValueOrDefault("OwnerKind") as string;
    if (parent is null || !remap.TryGetValue(parent, out var map)) return v;
    var id = Convert.ToInt64(v, CultureInfo.InvariantCulture);
    return map.GetValueOrDefault(id, id);
}

void Insert(string table, Dictionary<string, object?> values)
{
    var names = values.Keys.ToList();
    Run($"INSERT INTO \"{table}\" ({string.Join(", ", names.Select(n => $"\"{n}\""))}) VALUES ({string.Join(", ", names.Select((_, i) => $"$v{i}"))})",
        names.Select((n, i) => ($"$v{i}", values[n])).ToArray());
}

void Update(string table, long id, string col, object? value) =>
    Run($"UPDATE \"{table}\" SET \"{col}\" = $v WHERE Id = $id", ("$v", value), ("$id", id));

string Label(Dictionary<string, object?> row)
{
    var name = new[] { "Title", "Name", "Subject", "Text", "ImagePath" }.Select(k => row.GetValueOrDefault(k) as string).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s));
    return $"#{row["Id"]} {Short(name ?? "(sans titre)", 60)}";
}

string Short(object? v, int max = 110)
{
    if (v is null) return "(vide)";
    var s = Convert.ToString(v, CultureInfo.InvariantCulture)!.Replace("\r", "").Replace("\n", " / ").Trim();
    return s.Length <= max ? s : s[..max] + $"… ({s.Length} car.)";
}
