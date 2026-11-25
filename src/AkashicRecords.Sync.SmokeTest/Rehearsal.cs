using AkashicRecords.Hub;
using AkashicRecords.Sync;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AkashicRecords.Sync.SmokeTest;

/// <summary>
/// Dress rehearsal on real data, with no effect on anything real: the given database is only READ
/// (SQLite backup into a temp folder), every "device" is a copy of it, and the hub is a throw-away
/// one on this machine. Plays what the real devices are about to do with the whole catalog:
///   1. the PC sends everything;
///   2. a phone holding the same data recognises all of it (nothing added, nothing changed);
///   3. a brand-new device, empty, rebuilds everything from the hub alone - links included;
///   4. something written on that new device, linked to something else, comes back to the PC.
/// </summary>
public static class Rehearsal
{
    public static async Task<int> RunAsync(string realDb)
    {
        if (!File.Exists(realDb)) { Console.Error.WriteLine($"Base introuvable : {realDb}"); return 2; }
        var dir = Path.Combine(Path.GetTempPath(), "akashic-sync-rehearsal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        WebApplication? app = null;
        var ok = true;
        void Check(bool good, string what) { ok &= good; Console.WriteLine($"  {(good ? "ok  " : "FAIL")}  {what}"); }

        try
        {
            var tables = AkashicCatalog.Tables;
            var pcDb = Path.Combine(dir, "pc.db");
            var phoneDb = Path.Combine(dir, "phone.db");
            var newDb = Path.Combine(dir, "new.db");
            CopyReadOnly(realDb, pcDb);
            Sql(pcDb, "DROP TABLE IF EXISTS sync_map", "DROP TABLE IF EXISTS sync_state", "DROP TABLE IF EXISTS sync_conflict", "DROP TABLE IF EXISTS sync_deferred");
            SqliteConnection.ClearAllPools();
            File.Copy(pcDb, phoneDb);
            File.Copy(pcDb, newDb);
            // The new device: same tables, no rows. Its row ids will be its own.
            Sql(newDb, tables.Select(t => $"DELETE FROM \"{t.Table}\"").Append("DELETE FROM sqlite_sequence").ToArray());

            var before = SyncEngine.WireDump(pcDb, tables);
            var perTable = before.GroupBy(l => l[..l.IndexOf('|')]).OrderByDescending(g => g.Count()).ToList();
            Console.WriteLine($"Copie de travail : {dir} (la base d'origine n'est que lue)");
            Console.WriteLine($"{before.Count} lignes dans {perTable.Count} tables : {string.Join(", ", perTable.Select(g => $"{g.Key} {g.Count()}"))}");

            var options = new HubOptions { DataDir = Path.Combine(dir, "hub"), Url = "http://127.0.0.1:0", EntityTypes = new() { "*" } };
            app = HubApp.Build(new[] { "--Logging:LogLevel:Default=Warning" }, options);
            await app.StartAsync();
            var url = app.Urls.First();
            var store = app.Services.GetRequiredService<HubStore>();

            async Task<SyncEngine> Device(string name, string db)
            {
                using var anon = new HubClient(url);
                var paired = await anon.PairAsync(store.CreatePairCode(10), name, name);
                return new SyncEngine(db, new HubClient(url, paired.Token), tables);
            }

            var pc = await Device("pc", pcDb);
            var phone = await Device("telephone", phoneDb);
            var fresh = await Device("neuf", newDb);

            var r1 = await pc.RunAsync();
            Console.WriteLine($"1. PC         : {r1}");
            SqliteConnection.ClearAllPools();
            var rows = before.Count;
            before = SyncEngine.WireDump(pcDb, tables); // with the links now written as sync ids
            Check(r1.Pushed == rows && store.HeadSeq() == rows && before.Count == rows, "le PC envoie tout, une ligne de journal par element");

            var r2 = await phone.RunAsync();
            Console.WriteLine($"2. telephone  : {r2}");
            Check(r2.Adopted == before.Count && r2.Inserted + r2.Updated + r2.Deleted + r2.Pushed == 0 && r2.Waiting == 0,
                "un telephone qui a les memes donnees reconnait tout : rien n'est ajoute, modifie ni renvoye");
            SqliteConnection.ClearAllPools();
            Check(SyncEngine.WireDump(phoneDb, tables).SequenceEqual(before) && SyncEngine.WireDump(pcDb, tables).SequenceEqual(before),
                "les donnees du PC et du telephone sont exactement celles d'avant");

            var r3 = await fresh.RunAsync();
            Console.WriteLine($"3. appareil neuf : {r3}");
            Check(r3.Inserted == before.Count && r3.Waiting == 0 && r3.Pushed == 0, "un appareil vide recoit tout depuis le hub, aucune ligne en attente");
            SqliteConnection.ClearAllPools();
            var rebuilt = SyncEngine.WireDump(newDb, tables);
            var missing = before.Except(rebuilt).Take(3).ToList();
            Check(rebuilt.SequenceEqual(before), "son contenu est identique a celui du PC, liens entre elements compris"
                + (missing.Count == 0 ? "" : " -- ex. : " + missing[0][..Math.Min(160, missing[0].Length)]));

            // Something new on the new device, attached to an existing item, must come back attached.
            var hasPoetry = Scalar(newDb, "SELECT COUNT(*) FROM Recueil") > 0;
            if (hasPoetry)
            {
                Sql(newDb, "INSERT INTO Poem (RecueilId, Title, Text, Tags, CreatedAt) SELECT Id, 'REPETITION poeme', 'vers', '', '2026-11-25T23:00:00.0000000' FROM Recueil ORDER BY Title LIMIT 1");
                SqliteConnection.ClearAllPools();
                await fresh.RunAsync();
                var r4 = await pc.RunAsync();
                SqliteConnection.ClearAllPools();
                var sameRecueil = Text(newDb, "SELECT r.Title FROM Poem p JOIN Recueil r ON r.Id = p.RecueilId WHERE p.Title = 'REPETITION poeme'")
                                  == Text(pcDb, "SELECT r.Title FROM Poem p JOIN Recueil r ON r.Id = p.RecueilId WHERE p.Title = 'REPETITION poeme'");
                Check(r4.Inserted == 1 && sameRecueil, "un poeme ecrit sur l'appareil neuf arrive sur le PC dans le bon recueil");
            }

            var again = new[] { await pc.RunAsync(), await phone.RunAsync(), await fresh.RunAsync(), await pc.RunAsync() };
            Check(again.Skip(1).Take(2).All(r => r.Pushed == 0) && again[3].NothingToDo, "ensuite, plus rien a echanger");

            Console.WriteLine(ok ? "REPETITION REUSSIE." : "REPETITION EN ECHEC : voir les lignes FAIL.");
            return ok ? 0 : 1;
        }
        finally
        {
            if (app is not null) { await app.StopAsync(); await app.DisposeAsync(); }
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    private static void CopyReadOnly(string source, string target)
    {
        using var src = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = source, Mode = SqliteOpenMode.ReadOnly }.ToString());
        using var dst = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = target }.ToString());
        src.Open();
        dst.Open();
        src.BackupDatabase(dst);
    }

    private static void Sql(string db, params string[] statements)
    {
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = db }.ToString());
        c.Open();
        foreach (var s in statements)
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = s;
            cmd.ExecuteNonQuery();
        }
    }

    private static long Scalar(string db, string sql)
    {
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = db }.ToString());
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return (long)cmd.ExecuteScalar()!;
    }

    private static string? Text(string db, string sql)
    {
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = db }.ToString());
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar() as string;
    }
}
