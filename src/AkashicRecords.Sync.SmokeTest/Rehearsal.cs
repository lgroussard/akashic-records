using System.Security.Cryptography;
using System.Text;
using AkashicRecords.Hub;
using AkashicRecords.Sync;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AkashicRecords.Sync.SmokeTest;

/// <summary>
/// Dress rehearsal on real data, with no effect on anything real: the given database is only READ
/// (SQLite backup into a temp folder), the two "devices" are copies of it, and the hub is a
/// throw-away one on this machine. Shows what a first sync of PC then phone would do.
/// </summary>
public static class Rehearsal
{
    public static async Task<int> RunAsync(string realDb)
    {
        if (!File.Exists(realDb)) { Console.Error.WriteLine($"Base introuvable : {realDb}"); return 2; }
        var dir = Path.Combine(Path.GetTempPath(), "akashic-sync-rehearsal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        WebApplication? app = null;
        try
        {
            var pcDb = Path.Combine(dir, "pc.db");
            var phoneDb = Path.Combine(dir, "phone.db");
            CopyReadOnly(realDb, pcDb);
            File.Copy(pcDb, phoneDb);
            Console.WriteLine($"Copie de travail : {dir} (la base d'origine n'est que lue)");

            var before = Fingerprint(pcDb);
            Console.WriteLine($"Recettes dans la base : {before.Count}");

            var options = new HubOptions { DataDir = Path.Combine(dir, "hub"), Url = "http://127.0.0.1:0", EntityTypes = new() { "Recipe", "JournalEntry" } };
            app = HubApp.Build(new[] { "--Logging:LogLevel:Default=Warning" }, options);
            await app.StartAsync();
            var url = app.Urls.First();
            var store = app.Services.GetRequiredService<HubStore>();

            async Task<SyncEngine> Device(string name, string db)
            {
                using var anon = new HubClient(url);
                var paired = await anon.PairAsync(store.CreatePairCode(10), name, name);
                return new SyncEngine(db, new HubClient(url, paired.Token), Lab.Tables);
            }

            var pc = await Device("pc", pcDb);
            var phone = await Device("telephone", phoneDb);

            var r1 = await pc.RunAsync();
            Console.WriteLine($"1re synchro du PC        : {r1}");
            var r2 = await phone.RunAsync();
            Console.WriteLine($"1re synchro du telephone : {r2}");
            var r3 = await pc.RunAsync();
            var r4 = await phone.RunAsync();
            Console.WriteLine($"2e passage PC / telephone : {(r3.NothingToDo && r4.NothingToDo ? "rien a faire" : $"{r3} / {r4}")}");

            SqliteConnection.ClearAllPools();
            var afterPc = Fingerprint(pcDb);
            var afterPhone = Fingerprint(phoneDb);
            var ok = r1.Pushed == before.Count && r2.Adopted == before.Count && r2.Inserted == 0 && r2.Pushed == 0
                     && r3.NothingToDo && r4.NothingToDo
                     && afterPc.Hash == before.Hash && afterPhone.Hash == before.Hash && store.HeadSeq() == before.Count;
            Console.WriteLine($"Table Recipe apres synchro : PC {(afterPc.Hash == before.Hash ? "inchangee" : "MODIFIEE")}, telephone {(afterPhone.Hash == before.Hash ? "inchangee" : "MODIFIEE")}");
            Console.WriteLine($"Journal du hub : {store.HeadSeq()} lignes pour {before.Count} recettes");
            Console.WriteLine(ok ? "REPETITION REUSSIE : aucun doublon, aucune recette modifiee." : "REPETITION EN ECHEC : voir les lignes ci-dessus.");
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

    private static (int Count, string Hash) Fingerprint(string db)
    {
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = db }.ToString());
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT * FROM Recipe ORDER BY Id";
        using var r = cmd.ExecuteReader();
        var sb = new StringBuilder();
        var n = 0;
        while (r.Read())
        {
            n++;
            for (var i = 0; i < r.FieldCount; i++) sb.Append(r.IsDBNull(i) ? "<null>" : r.GetValue(i)).Append('\u001f');
            sb.Append('\n');
        }
        return (n, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))));
    }
}
