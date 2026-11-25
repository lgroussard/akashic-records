using System.Net;
using AkashicRecords.Hub;
using AkashicRecords.Sync;
using AkashicRecords.Sync.SmokeTest;
using Microsoft.Data.Sqlite;

// Test of the sync engine. Each "device" is a real SQLite file with the app's Recipe table; the
// hub is the real one, in-process, on a free port, with a throw-away data folder.

if (args.Length >= 2 && args[0] == "rehearse") return await Rehearsal.RunAsync(args[1]);

// Hand tools for end-to-end checks against a hub that is already running:
//   device <db> <hubUrl> <token>   one sync of that database, as a second device would do
//   sql <db> "<statement>"         run a statement on a test database and print what it returns
if (args.Length == 4 && args[0] == "device")
{
    using var hub = new HubClient(args[2], args[3]);
    Console.WriteLine(await new SyncEngine(args[1], hub, Lab.Tables).RunAsync());
    return 0;
}
if (args.Length == 3 && args[0] == "sql")
{
    using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = args[1] }.ToString());
    c.Open();
    using var cmd = c.CreateCommand();
    cmd.CommandText = args[2];
    using var r = cmd.ExecuteReader();
    while (r.Read()) Console.WriteLine(string.Join(" | ", Enumerable.Range(0, r.FieldCount).Select(i => r.IsDBNull(i) ? "<null>" : r.GetValue(i).ToString())));
    return 0;
}

var failures = 0;
var passed = 0;

void Check(bool ok, string what)
{
    if (ok) { passed++; Console.WriteLine($"    ok    {what}"); }
    else { failures++; Console.WriteLine($"    FAIL  {what}"); }
}

async Task Scenario(string name, Func<Lab, Task> body)
{
    Console.WriteLine($"\n[{name}]");
    await using var lab = new Lab();
    try
    {
        await lab.StartAsync();
        await body(lab);
    }
    catch (Exception ex)
    {
        failures++;
        Console.WriteLine($"    FAIL  exception: {ex.GetType().Name}: {ex.Message}");
    }
}

// ------------------------------------------------------------------------------------------

await Scenario("1. Depart : PC et telephone ont deja les memes recettes", async lab =>
{
    var pc = await lab.Device("pc");
    var phone = await lab.Device("phone");
    // Same content, different row ids: the phone's rows were not created in the same order.
    pc.Add("Ramen shoyu"); pc.Add("Houmous"); pc.Add("Taboule");
    phone.Add("Taboule"); phone.Add("Zzz bouche-trou"); phone.Add("Ramen shoyu"); phone.Add("Houmous");
    phone.DeleteByTitle("Zzz bouche-trou");

    var r1 = await pc.Sync();
    Check(r1.Pushed == 3 && lab.HubRows() == 3, "le PC envoie ses 3 recettes");
    var r2 = await phone.Sync();
    Check(r2.Adopted == 3 && r2.Inserted == 0 && r2.Pushed == 0, $"le telephone reconnait les 3 siennes : aucun envoi, aucun doublon [{r2}]");
    Check(phone.Count() == 3 && pc.Count() == 3 && lab.HubRows() == 3, "toujours 3 recettes partout");

    var again = new[] { await pc.Sync(), await phone.Sync(), await pc.Sync() };
    Check(again.All(r => r.NothingToDo) && lab.HubRows() == 3, "relancer la synchro ne fait plus rien");
});

await Scenario("2. Modification, ajout, suppression dans les deux sens", async lab =>
{
    var (pc, phone) = await lab.SeededPair("Ramen shoyu", "Houmous", "Taboule");
    phone.SetCover("Houmous", "/storage/emulated/0/photo-houmous.jpg");

    pc.Edit("Houmous", notes: "plus de citron");
    await pc.Sync(); await phone.Sync();
    Check(phone.Get("Houmous")?.Notes == "plus de citron", "modif sur le PC -> arrive sur le telephone");
    Check(phone.Get("Houmous")?.Cover == "/storage/emulated/0/photo-houmous.jpg" && pc.Get("Houmous")?.Cover is null,
        "le chemin d'image du telephone reste sur le telephone (colonne locale)");

    phone.Add("Falafels", servings: 4);
    phone.Edit("Taboule", notes: "persil plat");
    await phone.Sync(); await pc.Sync();
    Check(pc.Get("Falafels")?.Servings == 4, "ajout sur le telephone -> arrive sur le PC");
    Check(pc.Get("Taboule")?.Notes == "persil plat", "modif sur le telephone -> arrive sur le PC");

    pc.DeleteByTitle("Ramen shoyu");
    await pc.Sync(); await phone.Sync();
    Check(phone.Get("Ramen shoyu") is null && phone.Count() == 3, "suppression sur le PC -> supprimee sur le telephone");
    Check(pc.Dump() == phone.Dump(), "les deux bases ont le meme contenu");
});

await Scenario("3. Conflits : dernier arrive gagne, par recette", async lab =>
{
    var (pc, phone) = await lab.SeededPair("Ramen shoyu", "Houmous");

    pc.Edit("Houmous", notes: "version PC");
    phone.Edit("Houmous", notes: "version telephone");
    await pc.Sync();
    var r = await phone.Sync(); // the phone syncs last
    await pc.Sync();
    Check(r.Conflicts == 1, "le conflit est detecte et compte");
    Check(pc.Get("Houmous")?.Notes == "version telephone" && phone.Get("Houmous")?.Notes == "version telephone",
        "les deux gardent la version du dernier arrive (le telephone)");

    pc.DeleteByTitle("Ramen shoyu");
    phone.Edit("Ramen shoyu", notes: "editee pendant que le PC la supprimait");
    await pc.Sync(); await phone.Sync(); await pc.Sync();
    Check(pc.Get("Ramen shoyu")?.Notes == "editee pendant que le PC la supprimait" && phone.Count() == 2,
        "suppression contre modification : la modification arrivee en dernier gagne, la recette revient sur le PC");
    Check(pc.Dump() == phone.Dump() && (await pc.Sync()).NothingToDo && (await phone.Sync()).NothingToDo, "contenus identiques, plus rien a echanger");
});

await Scenario("4. Coupure reseau en plein envoi", async lab =>
{
    var pc = await lab.Device("pc");
    pc.Add("Ramen shoyu"); pc.Add("Houmous");

    pc.DropNextPushAnswer(); // the hub commits, the answer never arrives
    var failed = false;
    try { await pc.Sync(); } catch (HubException) { failed = true; }
    Check(failed && lab.HubRows() == 2, "la reponse se perd alors que le hub a bien enregistre les 2 recettes");

    var retry = await pc.Sync();
    Check(lab.HubRows() == 2, "au nouvel essai, le hub n'enregistre rien en double");
    Check(retry.Conflicts == 0 && (await pc.Sync()).NothingToDo, "le PC reconnait son propre envoi et se remet d'aplomb tout seul");

    var phone = await lab.Device("phone");
    await phone.Sync();
    Check(phone.Count() == 2 && phone.Dump() == pc.Dump(), "un telephone vide recoit les 2 recettes, une fois chacune");
});

await Scenario("5. Garde-fous", async lab =>
{
    var names = Enumerable.Range(1, 10).Select(i => $"Recette {i}").ToArray();
    var (pc, phone) = await lab.SeededPair(names);

    // The phone's table is emptied under the engine (what a botched import would look like).
    foreach (var n in names) phone.DeleteByTitle(n);
    var stopped = false;
    try { await phone.Sync(); } catch (SyncSafetyException) { stopped = true; }
    await pc.Sync();
    Check(stopped && pc.Count() == 10, "10 recettes disparues d'un coup : la synchro s'arrete, rien n'est supprime ailleurs");

    // Deleting a couple of recipes on purpose still works.
    var (pc2, phone2) = await lab.SeededPair(names.Select(n => "B " + n).ToArray());
    phone2.DeleteByTitle("B Recette 1"); phone2.DeleteByTitle("B Recette 2");
    await phone2.Sync(); await pc2.Sync();
    Check(pc2.Get("B Recette 1") is null && pc2.Get("B Recette 3") is not null, "2 suppressions voulues passent normalement");

    // A device that already synced, pointed at another (empty) hub.
    await using var other = new Lab();
    await other.StartAsync();
    var lost = await other.Device("pc-egare", existingDb: pc2.DbPath);
    var refused = false;
    try { await lost.Sync(); } catch (SyncSafetyException) { refused = true; }
    Check(refused && other.HubRows() == 0, "base deja synchronisee branchee sur un autre hub : refus, rien n'est envoye");
});

await Scenario("6. Versions differentes de l'app", async lab =>
{
    var pc = await lab.Device("pc");
    var old = await lab.Device("vieux-telephone", withoutServings: true); // a build from before the Servings column
    pc.Add("Falafels", servings: 4);
    await pc.Sync(); await old.Sync();
    Check(old.Get("Falafels") is not null, "un appareil sans la colonne 'Servings' recoit quand meme la recette");

    old.Edit("Falafels", notes: "au four");
    await old.Sync(); await pc.Sync();
    var got = pc.Get("Falafels");
    Check(got?.Notes == "au four" && got.Servings == 4, "sa modification arrive sur le PC sans effacer le champ qu'il ne connait pas");
});

await Scenario("7. Usage melange (30 tirages aleatoires)", async lab =>
{
    var bad = 0;
    for (var seed = 1; seed <= 30; seed++)
    {
        var rng = new Random(seed);
        var pc = await lab.Device($"pc{seed}");
        var phone = await lab.Device($"tel{seed}");
        var devices = new[] { pc, phone };
        for (var step = 0; step < 40; step++)
        {
            var d = devices[rng.Next(2)];
            var titles = d.Titles().Where(t => t.StartsWith($"S{seed}-")).ToList();
            switch (rng.Next(8))
            {
                case 0 or 1: d.Add($"S{seed}-{step}"); break;
                case 2 or 3 when titles.Count > 0: d.Edit(titles[rng.Next(titles.Count)], notes: $"{d.Name} pas {step}"); break;
                case 4 when titles.Count > 0: d.DeleteByTitle(titles[rng.Next(titles.Count)]); break;
                default: await d.Sync(); break;
            }
        }
        foreach (var d in devices.OrderBy(_ => rng.Next())) await d.Sync();
        foreach (var d in devices) await d.Sync();
        if (pc.Dump() != phone.Dump() || !(await pc.Sync()).NothingToDo) bad++;
    }
    Check(bad == 0, "30 tirages d'ajouts, modifs, suppressions et synchros dans le desordre : PC et telephone toujours identiques");
});

Console.WriteLine($"\n{passed} verifications reussies, {failures} en echec.");
return failures == 0 ? 0 : 1;
