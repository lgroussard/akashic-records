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
//   device <db> <hubUrl> <token> <appFolder>   the same, with the files of that app folder (what the app does)
if (args.Length is 4 or 5 && args[0] == "device")
{
    using var hub = new HubClient(args[2], args[3]);
    if (args.Length == 4)
    {
        Console.WriteLine(await new SyncEngine(args[1], hub, AkashicCatalog.Tables).RunAsync());
        return 0;
    }
    FileSync.EnsureSchema(args[1]);
    var fileSync = new FileSync(args[1], args[4], hub, AkashicCatalog.FileColumns);
    var files = new FileSyncReport();
    await fileSync.PublishAsync(files);
    var rows = await new SyncEngine(args[1], hub, AkashicCatalog.AllTables).RunAsync();
    await fileSync.FetchAsync(files);
    Console.WriteLine($"{rows} ; {files}");
    return 0;
}
//   dump <db>                      the synchronised content as it travels: rows per table, and one fingerprint
if (args.Length == 2 && args[0] == "dump")
{
    var lines = SyncEngine.WireDump(args[1], AkashicCatalog.Tables);
    foreach (var g in lines.GroupBy(l => l[..l.IndexOf('|')]).OrderBy(g => g.Key))
        Console.WriteLine($"{g.Key} {g.Count()} {Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join("\n", g))))[..12]}");
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

await Scenario("8. Fusion champ par champ", async lab =>
{
    var (pc, phone) = await lab.SeededPair("Houmous", "Falafels", "Taboule");

    // Different fields of the same recipe, each device offline.
    pc.Set("Houmous", "Ingredients", "pois chiches, tahin, CITRON");
    phone.Set("Houmous", "Notes", "servir tiede");
    await pc.Sync();
    var r = await phone.Sync();
    await pc.Sync();
    Check(pc.Field("Houmous", "Ingredients") == "pois chiches, tahin, CITRON" && pc.Field("Houmous", "Notes") == "servir tiede"
          && phone.Field("Houmous", "Ingredients") == "pois chiches, tahin, CITRON" && phone.Field("Houmous", "Notes") == "servir tiede",
        "ingredients modifies sur le PC, notes sur le telephone : les deux modifications sont gardees partout");
    Check(r.Conflicts == 1 && r.SetAside == 0 && phone.SetAside().Count == 0 && pc.SetAside().Count == 0,
        "aucune version mise de cote : rien n'etait en concurrence");

    // The same field on both sides.
    pc.Set("Falafels", "Notes", "version PC");
    phone.Set("Falafels", "Notes", "version telephone");
    phone.Set("Falafels", "Tags", "street food");
    await pc.Sync();
    r = await phone.Sync();
    await pc.Sync();
    Check(pc.Field("Falafels", "Notes") == "version telephone" && phone.Field("Falafels", "Notes") == "version telephone"
          && pc.Field("Falafels", "Tags") == "street food",
        "meme champ modifie des deux cotes : le dernier a synchroniser garde le sien, le reste est fusionne");
    var aside = phone.SetAside();
    Check(r.SetAside == 1 && aside.Count == 1 && aside[0] is { Field: "Notes", LostValue: "version PC", KeptValue: "version telephone", Label: "Falafels" }
          && aside[0].LostDevice.StartsWith("pc-"),
        "la version ecartee (celle du PC) est conservee sur le telephone, avec la recette, le champ et l'appareil d'origine");
    SyncEngine.DismissConflict(phone.DbPath, aside[0].Id);
    Check(phone.SetAside().Count == 0, "une fois lue, elle peut etre effacee");

    // Edit against delete, both ways: the edit wins.
    pc.DeleteByTitle("Taboule");
    phone.Set("Taboule", "Notes", "persil plat");
    await pc.Sync(); await phone.Sync(); await pc.Sync();
    phone.DeleteByTitle("Houmous");
    pc.Set("Houmous", "Notes", "avec du cumin");
    await pc.Sync(); await phone.Sync(); await pc.Sync();
    Check(pc.Field("Taboule", "Notes") == "persil plat" && phone.Field("Houmous", "Notes") == "avec du cumin" && pc.Count() == 3 && phone.Count() == 3,
        "modification contre suppression, dans les deux sens : la modification gagne, la recette revient");
    Check(pc.Dump() == phone.Dump() && (await pc.Sync()).NothingToDo && (await phone.Sync()).NothingToDo, "contenus identiques, plus rien a echanger");
});

await Scenario("9. Journal : la meme journee ecrite sur deux appareils", async lab =>
{
    var pc = await lab.Device("pc");
    var phone = await lab.Device("phone");

    // The PC wrote the day; the phone only opened it (an empty entry, created at another time).
    pc.WriteDay("2026-11-25", "Mini-PC installe, synchro en route.", "Mercredi");
    phone.WriteDay("2026-11-25", "", time: "T21:14:03.1234567");
    pc.WriteDay("2026-11-24", "Journee calme.");
    await pc.Sync(); await phone.Sync(); await pc.Sync();
    Check(phone.Day("2026-11-25").SequenceEqual(new[] { "Mini-PC installe, synchro en route." }) && pc.Day("2026-11-25").Count == 1,
        "une entree vide d'un cote, ecrite de l'autre : une seule entree pour ce jour, avec le texte");
    Check(phone.SetAside().Count == 0 && phone.Day("2026-11-24").Count == 1, "rien n'est mis de cote, et l'autre journee arrive normalement");

    // Both wrote that same day, differently, before ever syncing.
    pc.WriteDay("2026-11-26", "Texte ecrit sur le PC.");
    phone.WriteDay("2026-11-26", "Texte ecrit sur le telephone.", time: "T08:30:00.0000000");
    await pc.Sync();
    var r = await phone.Sync();
    await pc.Sync();
    Check(pc.Day("2026-11-26").Count == 1 && phone.Day("2026-11-26").Count == 1 && pc.Day("2026-11-26")[0] == phone.Day("2026-11-26")[0],
        "deux textes differents pour le meme jour : toujours une seule entree, identique des deux cotes");
    Check(r.SetAside >= 1 && phone.SetAside().Any(c => c.Field == "Text" && c.LostValue == "Texte ecrit sur le PC."),
        "le texte qui n'a pas ete retenu est mis de cote, pas perdu");

    // From then on it is one entry: ordinary edits flow.
    pc.RewriteDay("2026-11-24", "Journee calme. Promenade le soir.");
    await pc.Sync(); await phone.Sync();
    Check(phone.Day("2026-11-24")[0] == "Journee calme. Promenade le soir.", "ensuite, une modification du journal circule comme pour une recette");
});

await Scenario("11. Premier rapprochement de deux appareils qui n'ont pas tout a fait les memes donnees", async lab =>
{
    var pc = await lab.Device("pc");
    var phone = await lab.Device("phone");
    // The phone still has last week's copy of one recipe; the PC has edited it since.
    pc.Add("Gaspacho"); phone.Add("Gaspacho");
    pc.Add("Houmous"); phone.Add("Houmous");
    pc.Set("Gaspacho", "Instructions", "1. Mixer. 2. Servir frais. 3. Ajouter des croutons.");
    pc.Set("Gaspacho", "Notes", "meilleur le lendemain");
    await pc.Sync();
    var r = await phone.Sync();
    await pc.Sync();
    Check(pc.Count() == 2 && phone.Count() == 2, "meme titre des deux cotes : une seule recette, pas de doublon");
    Check(phone.Field("Gaspacho", "Instructions") == "1. Mixer. 2. Servir frais. 3. Ajouter des croutons." && phone.Field("Gaspacho", "Notes") == "meilleur le lendemain"
          && pc.Field("Gaspacho", "Instructions") == "1. Mixer. 2. Servir frais. 3. Ajouter des croutons.",
        "la version la plus complete est gardee partout, quel que soit l'appareil qui synchronise en dernier");
    Check(r.SetAside == 1 && phone.SetAside().Single() is { Field: "Instructions", LostValue: "etapes" },
        "l'ancienne version du champ est mise de cote sur le telephone, pas jetee");
    Check(pc.Dump() == phone.Dump() && (await pc.Sync()).NothingToDo && (await phone.Sync()).NothingToDo, "contenus identiques, plus rien a echanger");
});

await Scenario("12. Fichiers : photos, couvertures", async lab =>
{
    var pc = await lab.Device("pc");
    var phone = await lab.Device("phone");
    var cover = new byte[40_000]; new Random(1).NextBytes(cover);
    var shot = new byte[25_000]; new Random(2).NextBytes(shot);

    pc.AddPhoto(@"media\covers\aaa111.jpg", cover, "couverture");
    await pc.Sync();
    await phone.Sync();
    Check(pc.LastFiles.Uploaded == 1 && phone.LastFiles.Downloaded == 1 && phone.FileBytes("media/covers/aaa111.jpg")?.SequenceEqual(cover) == true,
        "une image ajoutee sur le PC arrive sur le telephone, identique au bit pres");

    phone.AddPhoto("references/bbb222.jpg", shot, "capture");
    await phone.Sync();
    await pc.Sync();
    Check(pc.FileBytes(@"references\bbb222.jpg")?.SequenceEqual(shot) == true && pc.PhotoRows() == 2 && phone.PhotoRows() == 2,
        "une image ajoutee sur le telephone arrive sur le PC (chemin a barres obliques compris)");

    await pc.Sync(); await phone.Sync();
    Check(pc.LastFiles is { Uploaded: 0, Downloaded: 0 } && phone.LastFiles is { Uploaded: 0, Downloaded: 0 }, "ensuite, aucun fichier n'est renvoye ni retelecharge");

    // The same picture under two names: its content is stored once on the hub.
    pc.AddPhoto(@"media\covers\ccc333.jpg", cover, "meme image, autre nom");
    await pc.Sync(); await phone.Sync();
    Check(pc.LastFiles.Uploaded == 0 && phone.FileBytes("media/covers/ccc333.jpg")?.SequenceEqual(cover) == true,
        "meme contenu sous un autre nom : rien n'est renvoye au hub, le telephone le recoit quand meme");

    // A row pointing at a file that does not exist here (a photo deleted by hand): no crash, nothing invented.
    pc.AddPhotoRow(@"media\photos\absente.jpg", "fichier manquant");
    await pc.Sync(); await phone.Sync();
    Check(phone.PhotoRows() == 4 && phone.FileBytes("media/photos/absente.jpg") is null, "une ligne dont le fichier manque circule quand meme, sans fichier");

    // A path that tries to leave the app folder is never read nor written.
    phone.AddPhotoRow(@"..\..\ailleurs.txt", "chemin douteux");
    phone.AddPhotoRow(@"C:\Windows\win.ini", "chemin absolu");
    await phone.Sync(); await pc.Sync();
    Check(FileSync.Normalize(@"..\..\ailleurs.txt") is null && FileSync.Normalize(@"C:\Windows\win.ini") is null && FileSync.Normalize(@"media\..\..\x") is null
          && FileSync.Normalize(@"media\covers\x.jpg") == "media/covers/x.jpg",
        "un chemin qui sort du dossier de l'app est ignore");
});

await Scenario("10. Mise a niveau depuis la premiere version du moteur", async lab =>
{
    var (pc, phone) = await lab.SeededPair("Houmous", "Falafels");
    pc.ForgetAncestors(); phone.ForgetAncestors(); // as written by the first engine: a hash, no ancestor

    Check((await pc.Sync()).NothingToDo && (await phone.Sync()).NothingToDo, "premier passage du nouveau moteur : rien n'est renvoye");
    pc.Set("Houmous", "Ingredients", "pois chiches, tahin, citron vert");
    phone.Set("Houmous", "Notes", "servir tiede");
    await pc.Sync(); await phone.Sync(); await pc.Sync();
    Check(pc.Field("Houmous", "Notes") == "servir tiede" && phone.Field("Houmous", "Ingredients") == "pois chiches, tahin, citron vert"
          && phone.SetAside().Count == 0 && pc.Dump() == phone.Dump(),
        "les recettes deja synchronisees profitent de la fusion champ par champ");
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
