using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AkashicRecords.Domain;
using AkashicRecords.Hub;
using AkashicRecords.Hub.SmokeTest;

// Smoke test of the Akashic sync hub (MOBILITE-SYNC.md, section 8).
// Starts the real hub (Kestrel + SQLite + blob files) on a free local port with a throw-away data
// folder, then plays simulated devices ("pc", "phone") against it over HTTP, each one paired and
// carrying its own token.

var failures = 0;
var passed = 0;

void Check(bool ok, string what)
{
    if (ok) { passed++; Console.WriteLine($"    ok    {what}"); }
    else { failures++; Console.WriteLine($"    FAIL  {what}"); }
}

async Task Scenario(string name, Func<TestHub, Task> body)
{
    Console.WriteLine($"\n[{name}]");
    var hub = new TestHub();
    try
    {
        await hub.StartAsync();
        await body(hub);
    }
    catch (Exception ex)
    {
        failures++;
        Console.WriteLine($"    FAIL  exception: {ex.GetType().Name}: {ex.Message}");
    }
    finally
    {
        await hub.DisposeAsync();
    }
}

Recipe Ramen(string title = "Ramen shoyu", string notes = "") => new()
{
    Title = title,
    Category = "Ramen",
    Ingredients = "nouilles\nbouillon\nsauce soja",
    Instructions = "Chauffer le bouillon. Cuire les nouilles.",
    Notes = notes,
    Tags = "japon, soupe",
    Servings = 2,
    CreatedAt = new DateTime(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc),
};

HttpClient WithToken(TestHub hub, string token)
{
    var c = hub.Anonymous();
    c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    return c;
}

// ------------------------------------------------------------------------------------------

await Scenario("1. Aller simple : le PC cree, le telephone recoit", async hub =>
{
    var pc = await hub.Device("pc");
    var phone = await hub.Device("phone");

    var id = pc.Create(Ramen());
    await pc.SyncAsync();
    await phone.SyncAsync();

    var got = phone.Get(id);
    Check(got is not null && got.Title == "Ramen shoyu" && got.Servings == 2 && got.Ingredients.Contains("bouillon"),
        "le telephone lit la recette complete creee sur le PC");
    Check(phone.State[id].Rev == 1 && pc.State[id].Rev == 1, "revision 1 attribuee par le hub, connue des deux");
    Check(pc.Snapshot() == phone.Snapshot(), "les deux repliques sont identiques");
});

await Scenario("2. Idempotence : pousser deux fois le meme lot", async hub =>
{
    var pc = await hub.Device("pc");
    pc.Create(Ramen("A"));
    pc.Create(Ramen("B"));
    pc.Create(Ramen("C"));
    var batch = pc.Outbox.ToList();

    var first = await pc.PushRawAsync(batch);
    var journalBefore = await pc.PullRawAsync(0, 1000);

    // Same batch again, same mutation ids: what a client does when the answer got lost.
    var second = await pc.PushRawAsync(batch);
    var third = await pc.PushRawAsync(batch);
    var journalAfter = await pc.PullRawAsync(0, 1000);

    Check(first.Results.All(r => r.Status == MutationStatus.Accepted), "1er envoi : 3 mutations acceptees");
    Check(second.Results.All(r => r.Status == MutationStatus.Duplicate) && third.Results.All(r => r.Status == MutationStatus.Duplicate),
        "2e et 3e envois : reconnus comme doublons");
    Check(second.Results.Select(r => (r.Rev, r.Seq)).SequenceEqual(first.Results.Select(r => (r.Rev, r.Seq))),
        "le doublon recoit la meme revision et le meme seq que l'original");
    Check(journalAfter.Rows.Count == 3 && journalAfter.HeadSeq == journalBefore.HeadSeq,
        "le journal n'a pas grossi (3 lignes, meme tete)");
    Check(journalAfter.Rows.All(r => r.Rev == 1), "aucune revision n'a ete incrementee par le renvoi");
});

await Scenario("3. Conflit : base_rev perimee", async hub =>
{
    var pc = await hub.Device("pc");
    var phone = await hub.Device("phone");
    var id = pc.Create(Ramen());
    await pc.SyncAsync();
    await phone.SyncAsync();

    // Both edit revision 1 while "offline".
    pc.Save(id, Ramen(notes: "version PC"));
    phone.Save(id, Ramen(notes: "version telephone"));

    await pc.SyncAsync(); // the PC gets there first -> rev 2
    var headBefore = (await pc.PullRawAsync(0, 1)).HeadSeq;
    var answer = await phone.PushOnceAsync(); // base_rev 1, hub is at 2
    var res = answer.Results.Single();

    Check(res.Status == MutationStatus.Conflict, "le hub repond 'conflict'");
    Check(res.Rev == 2 && res.Current is not null && res.Current.Rev == 2, "il renvoie la revision courante (2)");
    Check(res.Current?.LastDevice == "pc" && res.Current.Payload?.GetProperty("Notes").GetString() == "version PC",
        "il renvoie l'etat courant : la version du PC, auteur 'pc'");
    Check(answer.HeadSeq == headBefore, "rien n'a ete ecrit dans le journal");

    // Last-writer-wins per entity: the phone re-pushes its whole version on top of rev 2.
    await phone.SyncAsync();
    await pc.SyncAsync();
    Check(pc.Get(id)?.Notes == "version telephone" && phone.Get(id)?.Notes == "version telephone",
        "apres resolution (dernier gagnant par entite) les deux ont la version du telephone");
    Check(pc.State[id].Rev == 3 && pc.Snapshot() == phone.Snapshot(), "revision 3 partout, repliques identiques");
});

await Scenario("4. Pierres tombales via pull", async hub =>
{
    var pc = await hub.Device("pc");
    var phone = await hub.Device("phone");
    var keep = pc.Create(Ramen("A garder"));
    var gone = pc.Create(Ramen("A supprimer"));
    await pc.SyncAsync();
    await phone.SyncAsync();
    Check(phone.Get(gone) is not null, "le telephone a la recette avant suppression");

    pc.Delete(gone);
    await pc.SyncAsync();

    var delta = await phone.PullRawAsync(phone.Cursor, 100);
    var tomb = delta.Rows.SingleOrDefault();
    Check(tomb is not null && tomb.Deleted && tomb.EntityId == gone && tomb.Payload is null && tomb.Rev == 2,
        "le pull livre une ligne tombale (deleted=true, sans payload, rev 2)");

    await phone.SyncAsync();
    Check(phone.Get(gone) is null && phone.State[gone].Deleted, "le telephone applique la suppression");
    Check(phone.Get(keep) is not null, "l'autre recette est intacte");

    // An edit made against the pre-delete revision must not silently resurrect it.
    var late = await hub.Device("tablet");
    var stale = await late.PushRawAsync(new[]
    {
        new Mutation(Guid.NewGuid().ToString(), "Recipe", gone, 1, JsonSerializer.SerializeToElement(Ramen("zombie")), false),
    });
    Check(stale.Results.Single().Status == MutationStatus.Conflict && stale.Results.Single().Current?.Deleted == true,
        "une edition basee sur l'ancienne revision est refusee, avec l'etat 'supprime'");

    // A brand-new device replaying the whole journal ends in the same state.
    var fresh = await hub.Device("fresh");
    await fresh.SyncAsync();
    Check(fresh.Snapshot() == pc.Snapshot() && pc.Snapshot() == phone.Snapshot(),
        "un appareil neuf qui rejoue tout le journal arrive au meme etat");
});

await Scenario("5. Convergence : editions entrelacees, ordre de synchro aleatoire (40 tirages)", async hub =>
{
    var diverged = 0;
    var conflicts = 0;
    var mutations = 0;
    for (var seed = 1; seed <= 40; seed++)
    {
        var rng = new Random(seed);
        // Small pull pages on purpose, to exercise the cursor.
        var pc = await hub.Device($"pc-{seed}", 3);
        var phone = await hub.Device($"phone-{seed}", 2);
        var devices = new[] { pc, phone };
        var ids = new List<string>();

        for (var step = 0; step < 60; step++)
        {
            var d = devices[rng.Next(2)];
            var known = d.State.Where(kv => !kv.Value.Deleted).Select(kv => kv.Key).OrderBy(x => x, StringComparer.Ordinal).ToList();
            switch (rng.Next(10))
            {
                case 0 or 1:
                    ids.Add(d.Create(Ramen($"R{seed}-{step}")));
                    mutations++;
                    break;
                case 2 when known.Count > 0:
                    d.Delete(known[rng.Next(known.Count)]);
                    mutations++;
                    break;
                case 3 or 4 or 5 or 6 when known.Count > 0:
                    d.Save(known[rng.Next(known.Count)], Ramen($"R{seed}", notes: $"{d.Id} pas {step}"));
                    mutations++;
                    break;
                case 7:
                    await d.SyncAsync();
                    break;
                case 8:
                    await d.PullAsync(); // pull only, pending edits stay in the outbox
                    break;
                default:
                    if (d.Outbox.Count > 0) await d.PushOnceAsync(); // push only, conflicts left pending
                    break;
            }
        }

        // Silence: everybody syncs, in a random order, until nothing moves.
        foreach (var d in devices.OrderBy(_ => rng.Next())) await d.SyncAsync();
        foreach (var d in devices.OrderBy(_ => rng.Next())) await d.SyncAsync();

        conflicts += pc.ConflictsSeen + phone.ConflictsSeen;
        // Whole replicas are compared: the hub is shared across the 40 draws, so each pair also
        // carries (and edits) the entities left by the previous pairs.
        if (pc.Snapshot() != phone.Snapshot() || pc.Outbox.Count + phone.Outbox.Count > 0) diverged++;
        else if (ids.Any(i => !pc.State.ContainsKey(i))) diverged++;
    }
    Check(diverged == 0, $"40 tirages, {mutations} mutations, {conflicts} conflits resolus : PC et telephone identiques a chaque fois");

    var all = new List<LogRow>();
    var probe = await hub.Device("probe");
    long cursor = 0;
    while (true)
    {
        var page = await probe.PullRawAsync(cursor, 7);
        all.AddRange(page.Rows);
        cursor = page.NextCursor;
        if (!page.HasMore) break;
    }
    Check(all.Count > 0 && all.Zip(all.Skip(1)).All(p => p.Second.Seq > p.First.Seq),
        $"journal pagine ({all.Count} lignes) : seq strictement croissant, aucune ligne en double");
    Check(all.GroupBy(r => r.EntityId).All(g => g.Select(r => r.Rev).SequenceEqual(Enumerable.Range(1, g.Count()).Select(i => (long)i))),
        "par entite, les revisions du journal sont 1, 2, 3... sans trou ni doublon");

    // The snapshot path must land on exactly the same state as replaying the journal.
    var viaJournal = await hub.Device("via-journal");
    await viaJournal.SyncAsync();
    var viaSnapshot = await hub.Device("via-snapshot");
    var pages = await viaSnapshot.ResyncFromSnapshotAsync(pageSize: 25);
    Check(pages > 1 && viaSnapshot.Snapshot() == viaJournal.Snapshot() && viaSnapshot.Cursor == viaJournal.Cursor,
        $"un appareil charge par /snapshot ({pages} pages) a le meme etat et le meme curseur qu'un appareil qui rejoue le journal");
});

await Scenario("6. Horloges : l'ordre vient du seq, pas de l'heure", async hub =>
{
    var pc = await hub.Device("pc");
    var phone = await hub.Device("phone");
    var id = pc.Create(Ramen());
    await pc.SyncAsync();
    await phone.SyncAsync();

    // The phone "lives" ten years in the past; it still writes last, so it must be last.
    var old = Ramen(notes: "ecrit par un telephone dont l'horloge retarde");
    old.CreatedAt = new DateTime(2016, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    pc.Save(id, Ramen(notes: "PC, horloge juste"));
    await pc.SyncAsync();
    await phone.SyncAsync();
    phone.Save(id, old);
    await phone.SyncAsync();
    await pc.SyncAsync();

    var rows = (await pc.PullRawAsync(0, 100)).Rows;
    Check(rows.Select(r => r.Rev).SequenceEqual(new long[] { 1, 2, 3 }) && rows[^1].DeviceId == "phone",
        "la mutation arrivee en dernier a le seq et la revision les plus hauts");
    Check(pc.Get(id)?.Notes.StartsWith("ecrit par un telephone") == true, "le PC adopte la version arrivee en dernier");
});

await Scenario("7. Redemarrage du hub : journal et appairages survivent", async hub =>
{
    var pc = await hub.Device("pc");
    var id = pc.Create(Ramen());
    await pc.SyncAsync();
    pc.Save(id, Ramen(notes: "v2"));
    await pc.SyncAsync();
    var before = await pc.PullRawAsync(0, 100);

    await hub.RestartAsync(); // stops Kestrel, reopens the same data folder

    var after = await pc.PullRawAsync(0, 100); // same token as before the restart
    Check(before.Rows.Count == 2 && after.Rows.Select(r => (r.Seq, r.Rev, r.DeviceId)).SequenceEqual(before.Rows.Select(r => (r.Seq, r.Rev, r.DeviceId))),
        "apres redemarrage, le jeton du PC marche encore et le journal est identique");

    var phone = await hub.Device("phone");
    await phone.SyncAsync();
    phone.Save(id, Ramen(notes: "v3 apres redemarrage"));
    await phone.SyncAsync();
    var rows = (await phone.PullRawAsync(0, 100)).Rows;
    Check(rows.Count == 3 && rows[^1].Rev == 3 && rows[^1].Seq > before.HeadSeq,
        "le hub relance reprend la numerotation la ou elle s'etait arretee (rev 3)");
});

await Scenario("8. Entrees invalides", async hub =>
{
    var pc = await hub.Device("pc");
    var http = pc.Http;

    var bad = await http.PostAsJsonAsync("/sync/push", new PushRequest(null, new List<Mutation>
    {
        new(Guid.NewGuid().ToString(), "Film", "x", 0, JsonSerializer.SerializeToElement(new { a = 1 }), false),
    }), SimDevice.Wire);
    Check(bad.StatusCode == HttpStatusCode.BadRequest, "type d'entite absent de la configuration -> 400");

    var noList = await http.PostAsync("/sync/push", new StringContent("{}", Encoding.UTF8, "application/json"));
    Check(noList.StatusCode == HttpStatusCode.BadRequest, "lot sans mutations -> 400");

    var notObject = await http.PostAsJsonAsync("/sync/push", new PushRequest(null, new List<Mutation>
    {
        new(Guid.NewGuid().ToString(), "Recipe", "x", 0, JsonSerializer.SerializeToElement("texte"), false),
    }), SimDevice.Wire);
    Check(notObject.StatusCode == HttpStatusCode.BadRequest, "payload qui n'est pas un objet JSON -> 400");

    var neg = await http.GetAsync("/sync/pull?since=-1");
    Check(neg.StatusCode == HttpStatusCode.BadRequest, "curseur negatif -> 400");

    var empty = await http.GetFromJsonAsync<PullResponse>("/sync/pull", SimDevice.Wire);
    Check(empty is { Rows.Count: 0, NextCursor: 0, HasMore: false }, "hub vide : pull renvoie un journal vide, curseur 0");
});

await Scenario("9. Appairage et jetons", async hub =>
{
    var anon = hub.Anonymous();
    var push = new PushRequest(null, new List<Mutation>
    {
        new(Guid.NewGuid().ToString(), "Recipe", Guid.NewGuid().ToString(), 0, JsonSerializer.SerializeToElement(Ramen("intrus")), false),
    });

    // --- no token
    var health = await anon.GetAsync("/health");
    Check(health.StatusCode == HttpStatusCode.OK, "/health repond sans jeton");
    var codes = new[]
    {
        (await anon.PostAsJsonAsync("/sync/push", push, SimDevice.Wire)).StatusCode,
        (await anon.GetAsync("/sync/pull?since=0")).StatusCode,
        (await anon.GetAsync("/snapshot")).StatusCode,
        (await anon.PostAsync("/media", new ByteArrayContent(new byte[] { 1, 2, 3 }))).StatusCode,
        (await anon.GetAsync("/media/" + new string('a', 64))).StatusCode,
    };
    Check(codes.All(c => c == HttpStatusCode.Unauthorized), "sans jeton : push, pull, snapshot, depot et lecture de blob -> 401");

    // --- wrong token
    var wrong = WithToken(hub, "ceci-n-est-pas-un-jeton");
    Check((await wrong.PostAsJsonAsync("/sync/push", push, SimDevice.Wire)).StatusCode == HttpStatusCode.Unauthorized
          && (await wrong.GetAsync("/sync/pull")).StatusCode == HttpStatusCode.Unauthorized,
        "mauvais jeton -> 401");
    Check(hub.Store.HeadSeq() == 0, "aucune de ces tentatives n'a ecrit au journal");

    // --- pairing
    var badCode = await anon.PostAsJsonAsync("/auth/pair", new PairRequest("00000000", "phone", "Telephone"), SimDevice.Wire);
    Check(badCode.StatusCode == HttpStatusCode.Forbidden, "appairage sans code valide -> 403");

    var code = hub.Store.CreatePairCode(10);
    var phone = await SimDevice.PairAsync(hub.Address, code, "phone");
    Check(phone.Token.Length >= 40, "code valide -> un jeton pour l'appareil");
    var reuse = await anon.PostAsJsonAsync("/auth/pair", new PairRequest(code, "voleur", "x"), SimDevice.Wire);
    Check(reuse.StatusCode == HttpStatusCode.Forbidden, "le meme code une 2e fois -> 403 (usage unique)");

    var id = phone.Create(Ramen());
    await phone.SyncAsync();
    var row = (await phone.PullRawAsync(0, 10)).Rows.Single();
    Check(row.DeviceId == "phone", "avec le jeton, l'ecriture passe et l'auteur inscrit est l'appareil du jeton");

    var spoof = await phone.Http.PostAsJsonAsync("/sync/push", new PushRequest("pc", push.Mutations), SimDevice.Wire);
    Check(spoof.StatusCode == HttpStatusCode.Forbidden, "se declarer comme un autre appareil -> 403");

    // --- brute force: a valid code is burnt after 5 wrong guesses
    var victim = hub.Store.CreatePairCode(10);
    for (var i = 0; i < HubStore.MaxPairFailures; i++)
        await anon.PostAsJsonAsync("/auth/pair", new PairRequest($"9999000{i}", "x", "x"), SimDevice.Wire);
    var burnt = await anon.PostAsJsonAsync("/auth/pair", new PairRequest(victim, "x", "x"), SimDevice.Wire);
    Check(burnt.StatusCode == HttpStatusCode.Forbidden, "apres 5 codes faux, le code en attente est grille (pas de devinette possible)");

    // --- one token per device, revocation, re-pairing
    var pc = await hub.Device("pc");
    Check(pc.Token != phone.Token, "chaque appareil a son propre jeton");
    Check(hub.Store.Revoke("phone"), "revocation du telephone cote hub");
    Check((await phone.Http.GetAsync("/sync/pull")).StatusCode == HttpStatusCode.Unauthorized, "jeton revoque -> 401");
    Check((await pc.Http.GetAsync("/sync/pull")).StatusCode == HttpStatusCode.OK, "le jeton du PC n'est pas touche");

    var phone2 = await hub.Device("phone");
    Check((await phone2.Http.GetAsync("/sync/pull")).StatusCode == HttpStatusCode.OK
          && (await phone.Http.GetAsync("/sync/pull")).StatusCode == HttpStatusCode.Unauthorized,
        "re-appairage du meme appareil : nouveau jeton valable, l'ancien reste mort");

    // --- nothing usable at rest
    // Database, its write-ahead log and the request log, read while the hub still has them open.
    var onDisk = Directory.EnumerateFiles(hub.DataDir, "*", SearchOption.AllDirectories).Select(f =>
    {
        using var s = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var m = new MemoryStream();
        s.CopyTo(m);
        return Encoding.Latin1.GetString(m.ToArray());
    }).ToList();
    Check(onDisk.Any(t => t.Contains("\"Title\":\"Ramen shoyu\"")), "(controle : la lecture du dossier voit bien les donnees ecrites)");
    Check(!onDisk.Any(t => t.Contains(pc.Token) || t.Contains(phone2.Token) || t.Contains(code)),
        "ni jeton ni code en clair dans le dossier de donnees (base et journal de requetes)");
});

await Scenario("10. Snapshot : etat complet pour un appareil trop ancien", async hub =>
{
    var pc = await hub.Device("pc");
    var phone = await hub.Device("phone");
    var ids = Enumerable.Range(1, 7).Select(i => pc.Create(Ramen($"Recette {i}"))).ToList();
    await pc.SyncAsync();
    await phone.SyncAsync(); // the phone is up to date... then goes away for a long time

    pc.Delete(ids[0]);
    pc.Save(ids[1], Ramen("Recette 2", notes: "modifiee pendant l'absence"));
    var added = pc.Create(Ramen("Ajoutee pendant l'absence"));
    await pc.SyncAsync();

    var first = (await phone.Http.GetFromJsonAsync<SnapshotResponse>("/snapshot?limit=3", SimDevice.Wire))!;
    Check(first.Entities.Count == 3 && first.HasMore && first.NextAfterId == first.Entities[^1].EntityId && first.HeadSeq == hub.Store.HeadSeq(),
        "/snapshot pagine (3 par page) et annonce la position du journal");

    var pages = await phone.ResyncFromSnapshotAsync(pageSize: 3);
    Check(pages == 3, "8 entites lues en 3 pages, sans doublon ni oubli");
    Check(phone.State[ids[0]].Deleted, "la suppression faite pendant l'absence arrive par le snapshot (tombale)");
    Check(phone.Get(ids[1])?.Notes == "modifiee pendant l'absence" && phone.Get(added) is not null,
        "la modification et l'ajout aussi");
    Check(phone.Snapshot() == pc.Snapshot() && phone.Cursor == pc.Cursor,
        "apres resynchro, le telephone est identique au PC et son curseur est a jour");

    // Then life goes on through the normal journal.
    phone.Save(ids[2], Ramen("Recette 3", notes: "editee apres resynchro"));
    await phone.SyncAsync();
    await pc.SyncAsync();
    Check(pc.Get(ids[2])?.Notes == "editee apres resynchro" && phone.Snapshot() == pc.Snapshot(),
        "l'appareil resynchronise reprend le cycle normal push/pull");
});

await Scenario("11. Blobs : depot et lecture par empreinte", async hub =>
{
    var pc = await hub.Device("pc");
    var phone = await hub.Device("phone");

    var bytes = new byte[300_000];
    new Random(7).NextBytes(bytes);
    var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    var up = await pc.Http.PostAsync($"/media?hash={hash}", new ByteArrayContent(bytes));
    var saved = await up.Content.ReadFromJsonAsync<MediaResponse>(SimDevice.Wire);
    Check(up.StatusCode == HttpStatusCode.Created && saved is { Created: true } && saved.Hash == hash && saved.Size == bytes.Length,
        "depot depuis le PC : 201, empreinte calculee par le hub = celle du client");

    var down = await phone.Http.GetAsync($"/media/{hash}");
    var got = await down.Content.ReadAsByteArrayAsync();
    Check(down.StatusCode == HttpStatusCode.OK && got.AsSpan().SequenceEqual(bytes),
        "lecture depuis le telephone : memes octets, au bit pres");
    Check(down.Headers.CacheControl?.ToString().Contains("immutable") == true && down.Headers.ETag?.Tag == $"\"{hash}\"",
        "reponse marquee immuable (le telephone peut la garder en cache)");

    var range = new HttpRequestMessage(HttpMethod.Get, $"/media/{hash}");
    range.Headers.Range = new RangeHeaderValue(1000, 1999);
    var part = await phone.Http.SendAsync(range);
    var partBytes = await part.Content.ReadAsByteArrayAsync();
    Check(part.StatusCode == HttpStatusCode.PartialContent && partBytes.AsSpan().SequenceEqual(bytes.AsSpan(1000, 1000)),
        "lecture partielle (Range) : reprise d'un telechargement et lecture audio possibles");

    var again = await phone.Http.PostAsync("/media", new ByteArrayContent(bytes));
    var againBody = await again.Content.ReadFromJsonAsync<MediaResponse>(SimDevice.Wire);
    var files = Directory.EnumerateFiles(Path.Combine(hub.DataDir, "blobs"), "*", SearchOption.AllDirectories).ToList();
    Check(again.StatusCode == HttpStatusCode.OK && againBody is { Created: false } && files.Count == 1,
        "meme contenu depose une 2e fois : reconnu, un seul fichier sur le disque");

    var lie = await pc.Http.PostAsync($"/media?hash={new string('0', 64)}", new ByteArrayContent(new byte[] { 1, 2, 3 }));
    Check(lie.StatusCode == HttpStatusCode.BadRequest && !File.Exists(Path.Combine(hub.DataDir, "blobs", "00", new string('0', 64)))
          && Directory.EnumerateFiles(Path.Combine(hub.DataDir, "blobs"), "*", SearchOption.AllDirectories).Count() == 1,
        "contenu qui ne correspond pas a l'empreinte annoncee : refuse, rien n'est stocke");

    Check((await pc.Http.GetAsync("/media/" + new string('b', 64))).StatusCode == HttpStatusCode.NotFound, "empreinte inconnue -> 404");
    Check((await pc.Http.GetAsync("/media/..%2Fhub.db")).StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound,
        "empreinte mal formee (tentative de sortir du dossier) -> refusee");

    var big = await pc.Http.PostAsync("/media", new ByteArrayContent(new byte[3 * 1024 * 1024]));
    Check(big.StatusCode == HttpStatusCode.RequestEntityTooLarge, "blob au-dela de la limite configuree (2 Mo ici) -> 413");

    await hub.RestartAsync();
    var afterRestart = await phone.Http.GetByteArrayAsync($"/media/{hash}");
    Check(afterRestart.AsSpan().SequenceEqual(bytes), "le blob est toujours la apres redemarrage du hub");
});

await Scenario("12. Deuxieme type d'entite : le hub ne connait que des noms et du JSON", async hub =>
{
    var pc = await hub.Device("pc");
    var phone = await hub.Device("phone");

    var recipe = pc.Create(Ramen());
    var poem = Guid.NewGuid().ToString();
    pc.SaveAs("Poem", poem, new { Title = "Brume", Text = "Le matin se leve\nsur la riviere", RecueilId = (string?)null });
    await pc.SyncAsync();
    await phone.SyncAsync();

    Check(phone.State[poem].Type == "Poem" && phone.State[poem].PayloadJson!.Contains("riviere") && phone.Get(recipe) is not null,
        "un poeme et une recette voyagent par le meme journal");

    phone.SaveAs("Poem", poem, new { Title = "Brume", Text = "Le matin se leve\nsur la riviere\net s'en va", RecueilId = (string?)null });
    await phone.SyncAsync();
    await pc.SyncAsync();
    Check(pc.State[poem].Rev == 2 && pc.Snapshot() == phone.Snapshot(), "edition du poeme sur le telephone : revision 2, repliques identiques");

    var onlyPoems = (await pc.Http.GetFromJsonAsync<SnapshotResponse>("/snapshot?entity_type=Poem", SimDevice.Wire))!;
    Check(onlyPoems.Entities.Count == 1 && onlyPoems.Entities[0].EntityType == "Poem", "/snapshot?entity_type=Poem ne renvoie que les poemes");

    // Same entity id under two types = two distinct entities.
    var twin = new Mutation(Guid.NewGuid().ToString(), "Recipe", poem, 0, JsonSerializer.SerializeToElement(Ramen("homonyme")), false);
    var res = (await pc.PushRawAsync(new[] { twin })).Results.Single();
    Check(res.Status == MutationStatus.Accepted && res.Rev == 1, "les revisions sont comptees par (type, id), pas par id seul");

    // Accepted types come from configuration, not from the code.
    hub.EntityTypes.Add("JournalEntry");
    await hub.RestartAsync();
    var entry = new Mutation(Guid.NewGuid().ToString(), "JournalEntry", Guid.NewGuid().ToString(), 0, JsonSerializer.SerializeToElement(new { Text = "Aujourd'hui" }), false);
    var film = new Mutation(Guid.NewGuid().ToString(), "Film", Guid.NewGuid().ToString(), 0, JsonSerializer.SerializeToElement(new { Title = "x" }), false);
    var okEntry = (await pc.PushRawAsync(new[] { entry })).Results.Single().Status == MutationStatus.Accepted;
    var koFilm = (await pc.Http.PostAsJsonAsync("/sync/push", new PushRequest(null, new List<Mutation> { film }), SimDevice.Wire)).StatusCode == HttpStatusCode.BadRequest;
    Check(okEntry && koFilm, "un type ajoute a la configuration est accepte apres redemarrage ; un type non liste reste refuse");
});

await Scenario("13. Journal de requetes", async hub =>
{
    var pc = await hub.Device("pc");
    pc.Create(Ramen());
    await pc.SyncAsync();
    await hub.Anonymous().GetAsync("/sync/pull?since=0");

    var file = Directory.EnumerateFiles(Path.Combine(hub.DataDir, "logs"), "hub-*.log").SingleOrDefault();
    var lines = file is null ? Array.Empty<string>() : File.ReadAllLines(file);
    Check(lines.Any(l => l.Contains("\tpc\tPOST\t/sync/push\t200\t")), "le push du PC est journalise avec l'appareil et le statut");
    Check(lines.Any(l => l.Contains("\t-\tGET\t/sync/pull?since=0\t401\t")), "l'appel anonyme refuse est journalise (401)");
    Check(lines.Any(l => l.Contains("paired device 'pc'")), "l'appairage est journalise");
});

await Scenario("14. Calcul partage : le hub garde les modeles et la liste, les appareils font le travail", async hub =>
{
    var pc = await hub.Device("pc");
    var phone = await hub.Device("phone");

    // What an install puts in DataDir/compute: the model files (fake bytes here; the engine is in the apps).
    var compute = Path.Combine(hub.DataDir, "compute");
    Directory.CreateDirectory(Path.Combine(compute, "models"));
    foreach (var m in new[] { "nomos-webphoto-4x", "autre-4x" })
        File.WriteAllText(Path.Combine(compute, "models", m + ".onnx"), "onnx " + m);
    File.WriteAllText(Path.Combine(compute, "models", "old.param"), "left over from an older engine: not a model");

    JsonElement Json(HttpResponseMessage r) => JsonDocument.Parse(r.Content.ReadAsStringAsync().Result).RootElement;
    async Task<HttpResponseMessage> Post(SimDevice d, string url, object body) => await d.Http.PostAsJsonAsync(url, body, SimDevice.Wire);

    var board = Json(await pc.Http.GetAsync("/compute"));
    var names = board.GetProperty("models").EnumerateArray().Select(m => m.GetProperty("name").GetString()).ToList();
    Check(names.SequenceEqual(new[] { "nomos-webphoto-4x", "autre-4x", "flux-kontext" }) && board.GetProperty("models")[0].GetProperty("label").GetString() == "NomosWebPhoto",
        "les modeles du hub sont listes (fichiers .onnx, le connu d'abord, avec son nom lisible), puis Kontext (fait par un PC)");

    var manifest = Json(await pc.Http.GetAsync("/compute/manifest"));
    var file = manifest.GetProperty("models")[0].GetProperty("files")[0];
    var served = await pc.Http.GetByteArrayAsync("/compute/files/" + file.GetProperty("hash").GetString());
    Check(file.GetProperty("path").GetString() == "nomos-webphoto-4x.onnx" && Encoding.UTF8.GetString(served) == "onnx nomos-webphoto-4x",
        "manifeste : le fichier du modele, servi par son empreinte, identique");
    Check((await pc.Http.GetAsync("/compute/files/" + new string('c', 64))).StatusCode == HttpStatusCode.NotFound,
        "fichier hors catalogue -> 404 (le hub ne sert que ses modeles)");

    // The phone sends a photo and asks for it to be upscaled.
    var photo = Encoding.UTF8.GetBytes("pretend this is a webp");
    var sent = (await (await phone.Http.PostAsync("/media", new ByteArrayContent(photo))).Content.ReadFromJsonAsync<MediaResponse>(SimDevice.Wire))!;
    Check((await Post(phone, "/compute/jobs", new { model = "nope", input_hash = sent.Hash, input_name = "photo.webp" })).StatusCode == HttpStatusCode.BadRequest
          && (await Post(phone, "/compute/jobs", new { model = "nomos-webphoto-4x", input_hash = sent.Hash, input_name = "photo.gif" })).StatusCode == HttpStatusCode.BadRequest
          && (await Post(phone, "/compute/jobs", new { model = "nomos-webphoto-4x", input_hash = new string('d', 64), input_name = "photo.webp" })).StatusCode == HttpStatusCode.BadRequest,
        "demande refusee : modele inconnu, format non pris en charge, image jamais envoyee");
    var added = await Post(phone, "/compute/jobs", new { input_hash = sent.Hash, input_name = "C:\\Photos\\photo.webp" });
    var job = Json(added);
    var jobId = job.GetProperty("id").GetString()!;
    Check(added.StatusCode == HttpStatusCode.Created && job.GetProperty("status").GetString() == "queued" && job.GetProperty("input_name").GetString() == "photo.webp"
          && job.GetProperty("model").GetString() == "nomos-webphoto-4x",
        "demande sans modele precise : le modele unique est pris ; en attente, seul le nom du fichier est garde");

    Check((await Post(phone, "/compute/claim", new { name = "Telephone", rid = "android", models = Array.Empty<string>() })).StatusCode == HttpStatusCode.NoContent,
        "le telephone ne sait rien faire tourner : rien pour lui");
    Check((await Post(pc, "/compute/claim", new { name = "LOHAN", rid = "win-x64", models = new[] { "autre-4x" } })).StatusCode == HttpStatusCode.NoContent,
        "un appareil sans ce modele ne recoit pas le travail");
    var claimed = await Post(pc, "/compute/claim", new { name = "LOHAN", rid = "win-x64", models = new[] { "nomos-webphoto-4x", "autre-4x" } });
    var held = Json(claimed);
    Check(claimed.StatusCode == HttpStatusCode.OK && held.GetProperty("id").GetString() == jobId && held.GetProperty("status").GetString() == "running"
          && held.GetProperty("worker_name").GetString() == "LOHAN",
        "le PC qui sait le faire le recoit, marque en cours chez lui");
    Check((await Post(pc, "/compute/claim", new { name = "LOHAN", rid = "win-x64", models = new[] { "nomos-webphoto-4x" } })).StatusCode == HttpStatusCode.NoContent,
        "un travail en cours n'est pas donne deux fois");

    var result = Encoding.UTF8.GetBytes("pretend this is a bigger png");
    var up = (await (await pc.Http.PostAsync("/media", new ByteArrayContent(result))).Content.ReadFromJsonAsync<MediaResponse>(SimDevice.Wire))!;
    Check((await Post(phone, $"/compute/jobs/{jobId}/complete", new { result_hash = up.Hash, seconds = 1.0 })).StatusCode == HttpStatusCode.Conflict,
        "un autre appareil ne peut pas rendre le travail du PC");
    var done = Json(await Post(pc, $"/compute/jobs/{jobId}/complete", new { result_hash = up.Hash, seconds = 4.7 }));
    var fetched = await phone.Http.GetByteArrayAsync("/media/" + done.GetProperty("result_hash").GetString());
    Check(done.GetProperty("status").GetString() == "done" && done.GetProperty("result_name").GetString() == "photo-x4.png"
          && fetched.AsSpan().SequenceEqual(result),
        "resultat rendu par le PC : garde sur le hub, le telephone le recupere quand il veut");

    // A failure, a retry, a removal.
    var second = Json(await Post(phone, "/compute/jobs", new { model = "autre-4x", input_hash = sent.Hash, input_name = "photo.webp" })).GetProperty("id").GetString()!;
    await Post(pc, "/compute/claim", new { name = "LOHAN", rid = "win-x64", models = new[] { "autre-4x" } });
    var failed = Json(await Post(pc, $"/compute/jobs/{second}/fail", new { error = "vkCreateDevice failed" }));
    var retried = (await phone.Http.PostAsync($"/compute/jobs/{second}/retry", null)).StatusCode;
    var removed = (await phone.Http.DeleteAsync($"/compute/jobs/{second}")).StatusCode;
    var left = Json(await pc.Http.GetAsync("/compute")).GetProperty("jobs").EnumerateArray().Select(j => j.GetProperty("id").GetString()).ToList();
    Check(failed.GetProperty("error").GetString() == "vkCreateDevice failed" && retried == HttpStatusCode.OK && removed == HttpStatusCode.OK
          && left.SequenceEqual(new[] { jobId }),
        "echec garde avec son message, relance possible, retrait de la liste");

    var workersNow = Json(await phone.Http.GetAsync("/compute")).GetProperty("workers").EnumerateArray().Select(w => w.GetProperty("name").GetString()).ToList();
    Check(workersNow.SequenceEqual(new[] { "LOHAN", "Telephone" }), "la liste montre les appareils presents qui demandent du travail");

    // A job held when the hub stops goes back to the queue.
    var third = Json(await Post(phone, "/compute/jobs", new { model = "nomos-webphoto-4x", input_hash = sent.Hash, input_name = "photo.webp" })).GetProperty("id").GetString()!;
    await Post(pc, "/compute/claim", new { name = "LOHAN", rid = "win-x64", models = new[] { "nomos-webphoto-4x" } });
    await hub.RestartAsync();
    var after = Json(await pc.Http.GetAsync("/compute")).GetProperty("jobs").EnumerateArray().First(j => j.GetProperty("id").GetString() == third);
    Check(after.GetProperty("status").GetString() == "queued", "redemarrage du hub : un travail en cours repart en attente");

    // The hub defers to a stronger device that is around.
    var seen = new ComputeWorkers();
    seen.Saw("pc", "LOHAN", "win-x64", new[] { "nomos-webphoto-4x" });
    Check(seen.OtherCanRun("nomos-webphoto-4x", HubStore.HubDevice) && !seen.OtherCanRun("autre-4x", HubStore.HubDevice)
          && !seen.OtherCanRun("nomos-webphoto-4x", "pc"),
        "le hub laisse un modele au PC present qui sait le faire tourner, et le fait lui-meme sinon");
});

await Scenario("15. Apercus des photos : le hub fait une petite image a la demande", async hub =>
{
    var phone = await hub.Device("phone");
    async Task<string> Upload(byte[] data)
    {
        var r = await phone.Http.PostAsync("/media", new ByteArrayContent(data));
        return (await r.Content.ReadFromJsonAsync<MediaResponse>(SimDevice.Wire))!.Hash;
    }

    var notPicture = await Upload(Encoding.UTF8.GetBytes("pas une image"));
    var refused = await phone.Http.GetAsync($"/media/{notPicture}/thumb");
    Check(refused.StatusCode == HttpStatusCode.UnsupportedMediaType, "fichier qui n'est pas une image -> 415");
    Check((await phone.Http.GetAsync($"/media/{new string('c', 64)}/thumb")).StatusCode == HttpStatusCode.NotFound, "apercu d'un fichier inconnu -> 404");

    // A real picture, when ffmpeg is around to draw one (and to shrink it, if vipsthumbnail is absent).
    var ffmpeg = Environment.GetEnvironmentVariable("AKASHIC_HUB_FFMPEG");
    if (string.IsNullOrEmpty(ffmpeg)) { Console.WriteLine("    (AKASHIC_HUB_FFMPEG absent : apercu d'une vraie photo non verifie)"); return; }
    var jpg = Path.Combine(hub.DataDir, "grande.jpg");
    var draw = Process.Start(new ProcessStartInfo(ffmpeg, $"-v error -y -f lavfi -i testsrc=size=2400x1600 -frames:v 1 \"{jpg}\"") { UseShellExecute = false })!;
    await draw.WaitForExitAsync();
    var original = await File.ReadAllBytesAsync(jpg);
    var hash = await Upload(original);
    var t = await phone.Http.GetAsync($"/media/{hash}/thumb");
    var thumb = await t.Content.ReadAsByteArrayAsync();
    Check(t.StatusCode == HttpStatusCode.OK && t.Content.Headers.ContentType?.MediaType == "image/jpeg" && thumb.Length > 1000 && thumb.Length < original.Length / 3,
        $"apercu JPEG bien plus leger que l'original ({thumb.Length / 1024} Ko contre {original.Length / 1024} Ko)");
    var again = await phone.Http.GetByteArrayAsync($"/media/{hash}/thumb");
    Check(again.AsSpan().SequenceEqual(thumb) && File.Exists(Path.Combine(hub.DataDir, "thumbs", hash[..2], hash + ".jpg")),
        "garde sur le disque du hub : la 2e demande renvoie le meme apercu sans le refaire");
});

await Scenario("16. Agrandir une image de l'app : le resultat se range a cote de l'original", async hub =>
{
    hub.EntityTypes.AddRange(["MediaFile", "Photo", "PhotoAlbum", "CanvasElement"]);
    await hub.RestartAsync();
    var pc = await hub.Device("pc");
    var phone = await hub.Device("phone");
    var models = Path.Combine(hub.DataDir, "compute", "models");
    Directory.CreateDirectory(models);
    File.WriteAllText(Path.Combine(models, "nomos-webphoto-4x.onnx"), "fake model");

    byte[] Picture(SkiaSharp.SKEncodedImageFormat format, int side)
    {
        using var bmp = new SkiaSharp.SKBitmap(side, side);
        bmp.Erase(SkiaSharp.SKColors.Coral);
        using var img = SkiaSharp.SKImage.FromBitmap(bmp);
        return img.Encode(format, 90).ToArray();
    }
    JsonElement Json(HttpResponseMessage r) => JsonDocument.Parse(r.Content.ReadAsStringAsync().Result).RootElement;
    async Task<string> Upload(SimDevice d, byte[] bytes) =>
        (await (await d.Http.PostAsync("/media", new ByteArrayContent(bytes))).Content.ReadFromJsonAsync<MediaResponse>(SimDevice.Wire))!.Hash;
    Mutation Row(string type, string id, object payload) =>
        new(Guid.NewGuid().ToString(), type, id, 0, JsonSerializer.SerializeToElement(payload), false);

    // The PC has an Archives photo (in an album) and a picture on a canvas, both synchronised.
    var original = Picture(SkiaSharp.SKEncodedImageFormat.Jpeg, 16);
    var originalHash = await Upload(pc, original);
    var album = Guid.NewGuid().ToString();
    var photo = Guid.NewGuid().ToString();
    var element = Guid.NewGuid().ToString();
    await pc.PushRawAsync([
        Row("PhotoAlbum", album, new { Name = "Plage", CreatedAt = "2026-11-20T10:00:00.0000000" }),
        Row("MediaFile", Guid.NewGuid().ToString(), new { Path = "media/archive/plage.jpg", Hash = originalHash, Size = original.Length }),
        Row("Photo", photo, new { ImagePath = @"media\archive\plage.jpg", Title = "Plage", Description = "", TakenDate = (string?)null, Tags = "", IsFavorite = 0, AlbumId = album, ImportedAt = "2026-11-20T10:00:00.0000000" }),
        Row("CanvasElement", element, new { ProjectId = "proj", Type = 1, X = 100.0, Y = 50.0, TextContent = (string?)null, ImagePath = @"media\archive\plage.jpg", ObservationId = (string?)null, Width = 300.0, Height = 200.0 }),
    ]);

    // The phone asks for that photo to be upscaled: the hub takes the original from its own copy.
    var bad = await phone.Http.PostAsJsonAsync("/compute/jobs/from", new { entity_type = "Photo", entity_id = Guid.NewGuid().ToString() }, SimDevice.Wire);
    var wrongKind = await phone.Http.PostAsJsonAsync("/compute/jobs/from", new { entity_type = "Recipe", entity_id = photo }, SimDevice.Wire);
    Check(bad.StatusCode == HttpStatusCode.UnprocessableEntity && Json(bad).GetProperty("error").GetString()!.Contains("synchronise")
          && wrongKind.StatusCode == HttpStatusCode.BadRequest,
        "image inconnue du hub -> 422 avec un message clair ; type sans image -> 400");
    var asked = await phone.Http.PostAsJsonAsync("/compute/jobs/from", new { entity_type = "Photo", entity_id = photo }, SimDevice.Wire);
    var job = Json(asked);
    Check(asked.StatusCode == HttpStatusCode.Created && job.GetProperty("input_hash").GetString() == originalHash
          && job.GetProperty("input_name").GetString() == "plage.jpg" && job.GetProperty("attach_type").GetString() == "Photo",
        "demande depuis l'app : l'original est pris sur le hub, sans nouvel envoi");

    // The PC does it and sends a (pretend) result back.
    await pc.Http.PostAsJsonAsync("/compute/claim", new { name = "LOHAN", rid = "win-x64", models = new[] { "nomos-webphoto-4x" } }, SimDevice.Wire);
    var resultHash = await Upload(pc, Picture(SkiaSharp.SKEncodedImageFormat.Png, 64));
    var done = Json(await pc.Http.PostAsJsonAsync($"/compute/jobs/{job.GetProperty("id").GetString()}/complete", new { result_hash = resultHash, seconds = 9.5 }, SimDevice.Wire));
    Check(done.GetProperty("attached_path").GetString() == @"media\archive\plage-x4.jpg" && done.GetProperty("attach_error").ValueKind == JsonValueKind.Null,
        "resultat range : meme dossier, meme style de chemin, nom en -x4.jpg");

    // What every device then receives.
    var photos = hub.Store.Snapshot("Photo", null, null, 50).Entities.Where(e => !e.Deleted).ToList();
    var added = photos.Single(e => e.EntityId != photo).Payload!.Value;
    Check(photos.Count == 2 && added.GetProperty("AlbumId").GetString() == album && added.GetProperty("Title").GetString() == "Plage"
          && added.GetProperty("ImagePath").GetString() == @"media\archive\plage-x4.jpg" && added.GetProperty("ImportedAt").GetString() != "2026-11-20T10:00:00.0000000",
        "une 2e photo dans le meme album, l'original intact");
    var file = hub.Store.Snapshot("MediaFile", null, null, 50).Entities.Select(e => e.Payload!.Value)
        .Single(p => p.GetProperty("Path").GetString() == "media/archive/plage-x4.jpg");
    var jpeg = await phone.Http.GetByteArrayAsync("/media/" + file.GetProperty("Hash").GetString());
    Check(jpeg.Length > 0 && jpeg[0] == 0xFF && jpeg[1] == 0xD8 && file.GetProperty("Size").GetInt64() == jpeg.Length,
        "son fichier est liste et sur le hub, en JPEG");

    // Upscaling the copy again is refused: it would add next to nothing.
    var copyId = hub.Store.Snapshot("Photo", null, null, 50).Entities.Single(e => !e.Deleted && e.EntityId != photo).EntityId;
    var twice = await phone.Http.PostAsJsonAsync("/compute/jobs/from", new { entity_type = "Photo", entity_id = copyId }, SimDevice.Wire);
    Check(twice.StatusCode == HttpStatusCode.UnprocessableEntity && Json(twice).GetProperty("error").GetString()!.Contains("déjà agrandie"),
        "agrandir une image deja agrandie : refuse avec un message clair");

    // An edited picture (apps' editor: "<root>.edit-<hex>.jpg"): its copy is named after the first name, never as an edit.
    var edited = Picture(SkiaSharp.SKEncodedImageFormat.Jpeg, 16);
    var editedHash = await Upload(pc, edited);
    var editedPhoto = Guid.NewGuid().ToString();
    await pc.PushRawAsync([
        Row("MediaFile", Guid.NewGuid().ToString(), new { Path = "media/archive/mer.heic.edit-1a2b3c4d.jpg", Hash = editedHash, Size = edited.Length }),
        Row("Photo", editedPhoto, new { ImagePath = @"media\archive\mer.heic.edit-1a2b3c4d.jpg", Title = "Mer", Description = "", TakenDate = (string?)null, Tags = "", IsFavorite = 0, AlbumId = album, ImportedAt = "2026-11-21T10:00:00.0000000" }),
    ]);
    var fromEdit = Json(await phone.Http.PostAsJsonAsync("/compute/jobs/from", new { entity_type = "Photo", entity_id = editedPhoto }, SimDevice.Wire));
    await pc.Http.PostAsJsonAsync("/compute/claim", new { name = "LOHAN", rid = "win-x64", models = new[] { "nomos-webphoto-4x" } }, SimDevice.Wire);
    var editDone = Json(await pc.Http.PostAsJsonAsync($"/compute/jobs/{fromEdit.GetProperty("id").GetString()}/complete", new { result_hash = resultHash, seconds = 9.5 }, SimDevice.Wire));
    Check(editDone.GetProperty("attached_path").GetString() == @"media\archive\mer-x4.jpg",
        "copie agrandie d'une image modifiee : nommee d'apres son premier nom (mer-x4.jpg), sans marque de modification");
    // A picture on a canvas: the copy goes beside it.
    var again = Json(await phone.Http.PostAsJsonAsync("/compute/jobs/from", new { entity_type = "CanvasElement", entity_id = element }, SimDevice.Wire));
    await pc.Http.PostAsJsonAsync("/compute/claim", new { name = "LOHAN", rid = "win-x64", models = new[] { "nomos-webphoto-4x" } }, SimDevice.Wire);
    var second = Json(await pc.Http.PostAsJsonAsync($"/compute/jobs/{again.GetProperty("id").GetString()}/complete", new { result_hash = resultHash, seconds = 9.5 }, SimDevice.Wire));
    var copy = hub.Store.Snapshot("CanvasElement", null, null, 50).Entities.Where(e => e.EntityId != element).Single().Payload!.Value;
    Check(second.GetProperty("attached_path").GetString() == @"media\archive\plage-x4-2.jpg" && copy.GetProperty("X").GetDouble() == 424
          && copy.GetProperty("ProjectId").GetString() == "proj" && copy.GetProperty("Width").GetDouble() == 300,
        "image d'un canevas : la copie se pose a cote (meme projet, meme taille), sans ecraser le -x4 deja la");
});

await Scenario("17. Images agrandies depuis l'outil : rangees dans l'album Agrandies des Archives", async hub =>
{
    hub.EntityTypes.AddRange(["MediaFile", "Photo", "PhotoAlbum"]);
    await hub.RestartAsync();
    var pc = await hub.Device("pc");
    var models = Path.Combine(hub.DataDir, "compute", "models");
    Directory.CreateDirectory(models);
    File.WriteAllText(Path.Combine(models, "nomos-webphoto-4x.onnx"), "fake model");

    byte[] Picture(SkiaSharp.SKEncodedImageFormat format, int side, SkiaSharp.SKColor color)
    {
        using var bmp = new SkiaSharp.SKBitmap(side, side);
        bmp.Erase(color);
        using var img = SkiaSharp.SKImage.FromBitmap(bmp);
        return img.Encode(format, 90).ToArray();
    }
    JsonElement Json(HttpResponseMessage r) => JsonDocument.Parse(r.Content.ReadAsStringAsync().Result).RootElement;
    async Task<string> Upload(byte[] bytes) =>
        (await (await pc.Http.PostAsync("/media", new ByteArrayContent(bytes))).Content.ReadFromJsonAsync<MediaResponse>(SimDevice.Wire))!.Hash;
    async Task<JsonElement> Run(string name, SkiaSharp.SKColor color)
    {
        var input = await Upload(Picture(SkiaSharp.SKEncodedImageFormat.Jpeg, 8, color));
        var job = Json(await pc.Http.PostAsJsonAsync("/compute/jobs", new { input_hash = input, input_name = name }, SimDevice.Wire));
        await pc.Http.PostAsJsonAsync("/compute/claim", new { name = "LOHAN", rid = "win-x64", models = new[] { "nomos-webphoto-4x" } }, SimDevice.Wire);
        var result = await Upload(Picture(SkiaSharp.SKEncodedImageFormat.Png, 32, color));
        return Json(await pc.Http.PostAsJsonAsync($"/compute/jobs/{job.GetProperty("id").GetString()}/complete", new { result_hash = result, seconds = 5.0 }, SimDevice.Wire));
    }
    List<JsonElement> Live(string type) => hub.Store.Snapshot(type, null, null, 100).Entities.Where(e => !e.Deleted).Select(e => e.Payload!.Value).ToList();

    var first = await Run("vacances.webp", SkiaSharp.SKColors.Teal);
    var second = await Run("chat.png", SkiaSharp.SKColors.Gold);
    var albums = hub.Store.Snapshot("PhotoAlbum", null, null, 10).Entities.Where(e => !e.Deleted).ToList();
    var photos = Live("Photo");
    Check(albums.Count == 1 && albums[0].Payload!.Value.GetProperty("Name").GetString() == "Agrandies"
          && photos.Count == 2 && photos.All(p => p.GetProperty("AlbumId").GetString() == albums[0].EntityId)
          && photos.Select(p => p.GetProperty("Title").GetString()).OrderBy(t => t).SequenceEqual(new[] { "chat", "vacances" }),
        "deux images de l'outil : un seul album Agrandies, une photo chacune, avec leur nom");
    var path = first.GetProperty("attached_path").GetString()!;
    var listed = Live("MediaFile").Single(f => f.GetProperty("Path").GetString() == path.Replace('\\', '/'));
    var bytes = await pc.Http.GetByteArrayAsync("/media/" + listed.GetProperty("Hash").GetString());
    Check(path.StartsWith(@"media\archive\") && path.EndsWith(".jpg") && bytes[0] == 0xFF && bytes[1] == 0xD8,
        "photo rangee avec les autres photos d'Archives, en JPEG, fichier liste et present");

    // Results made before the album existed: placed in it once, at the hub's start (one per image).
    var older = await Run("ancienne.webp", SkiaSharp.SKColors.Plum);
    var oldId = older.GetProperty("id").GetString()!;
    var photoOfOld = Live("Photo").Single(p => p.GetProperty("Title").GetString() == "ancienne");
    await pc.PushRawAsync([new Mutation(Guid.NewGuid().ToString(), "Photo",
        hub.Store.Snapshot("Photo", null, null, 100).Entities.Single(e => e.Payload?.GetProperty("Title").GetString() == "ancienne").EntityId,
        1, null, true)]);
    using (var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(hub.DataDir, "compute.db")}"))
    {
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE cp_job SET attached_path = NULL, attach_error = NULL WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", oldId);
        cmd.ExecuteNonQuery();
    }
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    await hub.RestartAsync();
    string? placed = null;
    for (var i = 0; i < 50 && placed is null; i++)
    {
        await Task.Delay(100);
        placed = Json(await pc.Http.GetAsync("/compute")).GetProperty("jobs").EnumerateArray()
            .First(j => j.GetProperty("id").GetString() == oldId).GetProperty("attached_path").GetString();
    }
    Check(placed is not null && Live("Photo").Count(p => p.GetProperty("Title").GetString() == "ancienne") == 1 && Live("PhotoAlbum").Count == 1,
        "resultat d'avant l'album : range une fois au demarrage du hub, dans le meme album");
});

await Scenario("18. Modifier par IA : le resultat devient une modification de la meme image", async hub =>
{
    hub.EntityTypes.AddRange(["MediaFile", "Photo", "PhotoAlbum", "RefImage"]);
    await hub.RestartAsync();
    var pc = await hub.Device("pc");
    var phone = await hub.Device("phone");
    var models = Path.Combine(hub.DataDir, "compute", "models");
    Directory.CreateDirectory(models);
    File.WriteAllText(Path.Combine(models, "nomos-webphoto-4x.onnx"), "fake model");

    byte[] Picture(SkiaSharp.SKEncodedImageFormat format, int w, int h)
    {
        using var bmp = new SkiaSharp.SKBitmap(w, h);
        bmp.Erase(SkiaSharp.SKColors.SkyBlue);
        using var img = SkiaSharp.SKImage.FromBitmap(bmp);
        return img.Encode(format, 90).ToArray();
    }
    JsonElement Json(HttpResponseMessage r) => JsonDocument.Parse(r.Content.ReadAsStringAsync().Result).RootElement;
    async Task<string> Upload(SimDevice d, byte[] bytes) =>
        (await (await d.Http.PostAsync("/media", new ByteArrayContent(bytes))).Content.ReadFromJsonAsync<MediaResponse>(SimDevice.Wire))!.Hash;
    Mutation Row(string type, string id, object payload) =>
        new(Guid.NewGuid().ToString(), type, id, 0, JsonSerializer.SerializeToElement(payload), false);

    var original = Picture(SkiaSharp.SKEncodedImageFormat.Jpeg, 20, 30);
    var originalHash = await Upload(pc, original);
    var photo = Guid.NewGuid().ToString();
    var refImage = Guid.NewGuid().ToString();
    await pc.PushRawAsync([
        Row("MediaFile", Guid.NewGuid().ToString(), new { Path = "media/archive/piscine-x4.jpg", Hash = originalHash, Size = original.Length }),
        Row("Photo", photo, new { ImagePath = @"media\archive\piscine-x4.jpg", Title = "Piscine", Description = "", TakenDate = (string?)null, Tags = "", IsFavorite = 0, AlbumId = (string?)null, ImportedAt = "2026-11-27T10:00:00.0000000" }),
        Row("RefImage", refImage, new { ImagePath = "media/references/pose.jpg.edit-0badf00d.jpg", Title = "Pose", Width = 20, Height = 30, CreatedAt = "2026-11-27T10:00:00.0000000" }),
        Row("MediaFile", Guid.NewGuid().ToString(), new { Path = "media/references/pose.jpg.edit-0badf00d.jpg", Hash = originalHash, Size = original.Length }),
    ]);

    // Bad requests: no sentence, unknown mask, unknown kind.
    var noPrompt = await phone.Http.PostAsJsonAsync("/compute/jobs/from", new { entity_type = "Photo", entity_id = photo, kind = "edit", prompt = "  " }, SimDevice.Wire);
    var badMask = await phone.Http.PostAsJsonAsync("/compute/jobs/from", new { entity_type = "Photo", entity_id = photo, kind = "edit", prompt = "add a banana", mask_hash = new string('e', 64) }, SimDevice.Wire);
    var badKind = await phone.Http.PostAsJsonAsync("/compute/jobs/from", new { entity_type = "Photo", entity_id = photo, kind = "paint" }, SimDevice.Wire);
    Check(noPrompt.StatusCode == HttpStatusCode.BadRequest && badMask.StatusCode == HttpStatusCode.BadRequest && badKind.StatusCode == HttpStatusCode.BadRequest,
        "demande IA sans phrase, avec un masque inconnu ou d'un type inconnu : 400");

    // An edit with a mask, asked from the phone. An upscaled picture can be edited (only upscaling it again is refused).
    var maskHash = await Upload(phone, Picture(SkiaSharp.SKEncodedImageFormat.Png, 10, 15));
    var asked = await phone.Http.PostAsJsonAsync("/compute/jobs/from", new
    {
        entity_type = "Photo", entity_id = photo, kind = "edit", prompt = "A ripe banana held firmly in her hand", mask_hash = maskHash, mask_text = "hand", erase = false,
    }, SimDevice.Wire);
    var job = Json(asked);
    var p = JsonDocument.Parse(job.GetProperty("params").GetString()!).RootElement;
    Check(asked.StatusCode == HttpStatusCode.Created && job.GetProperty("model").GetString() == "flux-kontext" && job.GetProperty("kind").GetString() == "edit"
          && p.GetProperty("prompt").GetString() == "A ripe banana held firmly in her hand" && p.GetProperty("mask_hash").GetString() == maskHash
          && p.GetProperty("mask_text").GetString() == "hand" && !p.GetProperty("erase").GetBoolean(),
        "demande IA acceptee : modele flux-kontext, phrase, masque, mots et choix d'effacement gardes");

    // Only a device that runs Kontext gets it; the hub never takes it.
    Check((await pc.Http.PostAsJsonAsync("/compute/claim", new { name = "LOHAN", rid = "win-x64", models = new[] { "nomos-webphoto-4x" } }, SimDevice.Wire)).StatusCode == HttpStatusCode.NoContent,
        "un appareil sans Kontext ne recoit pas la demande IA");
    var claimed = await pc.Http.PostAsJsonAsync("/compute/claim", new { name = "LOHAN", rid = "win-x64", models = new[] { "nomos-webphoto-4x", "flux-kontext" } }, SimDevice.Wire);
    var board = Json(await phone.Http.GetAsync("/compute"));
    var kontext = board.GetProperty("models").EnumerateArray().Single(m => m.GetProperty("name").GetString() == "flux-kontext");
    Check(claimed.StatusCode == HttpStatusCode.OK && kontext.GetProperty("runners").EnumerateArray().Any(r => r.GetString() == "LOHAN"),
        "le PC avec Kontext la prend, et le tableau dit qui sait la faire");

    var resultHash = await Upload(pc, Picture(SkiaSharp.SKEncodedImageFormat.Png, 40, 60));
    var done = Json(await pc.Http.PostAsJsonAsync($"/compute/jobs/{job.GetProperty("id").GetString()}/complete", new { result_hash = resultHash, seconds = 80.0 }, SimDevice.Wire));
    var path = done.GetProperty("attached_path").GetString()!;
    Check(System.Text.RegularExpressions.Regex.IsMatch(path, @"^media\\archive\\piscine-x4\.jpg\.edit-[0-9a-f]{8}\.jpg$") && done.GetProperty("attach_error").ValueKind == JsonValueKind.Null,
        "resultat pose comme une modification : <nom>.edit-<8 hex>.jpg a cote de l'original");
    var photos = hub.Store.Snapshot("Photo", null, null, 50).Entities.Where(e => !e.Deleted).ToList();
    var row = photos.Single();
    Check(row.EntityId == photo && row.Payload!.Value.GetProperty("ImagePath").GetString() == path && row.Rev == 2
          && row.Payload.Value.GetProperty("Title").GetString() == "Piscine",
        "la meme photo pointe vers la version modifiee (pas de nouvelle photo, le reste de la ligne intact)");
    var file = hub.Store.Snapshot("MediaFile", null, null, 50).Entities.Select(e => e.Payload!.Value)
        .Single(f => f.GetProperty("Path").GetString() == path.Replace('\\', '/'));
    var jpeg = await phone.Http.GetByteArrayAsync("/media/" + file.GetProperty("Hash").GetString());
    Check(jpeg[0] == 0xFF && jpeg[1] == 0xD8 && hub.Store.FileHash("media/archive/piscine-x4.jpg") == originalHash,
        "le fichier modifie est sur le hub en JPEG, l'original reste liste (« Revenir a l'original » possible)");

    // A restore of an already edited reference picture: same root name, new size written in the row.
    var restore = Json(await phone.Http.PostAsJsonAsync("/compute/jobs/from", new { entity_type = "RefImage", entity_id = refImage, kind = "restore" }, SimDevice.Wire));
    await pc.Http.PostAsJsonAsync("/compute/claim", new { name = "LOHAN", rid = "win-x64", models = new[] { "flux-kontext" } }, SimDevice.Wire);
    var restored = Json(await pc.Http.PostAsJsonAsync($"/compute/jobs/{restore.GetProperty("id").GetString()}/complete", new { result_hash = resultHash, seconds = 90.0 }, SimDevice.Wire));
    var refRow = hub.Store.Snapshot("RefImage", null, null, 50).Entities.Single(e => e.EntityId == refImage).Payload!.Value;
    Check(System.Text.RegularExpressions.Regex.IsMatch(restored.GetProperty("attached_path").GetString()!, @"^media/references/pose\.jpg\.edit-[0-9a-f]{8}\.from-0badf00d\.jpg$")
          && restore.GetProperty("params").ValueKind == JsonValueKind.Null
          && refRow.GetProperty("Width").GetInt32() == 40 && refRow.GetProperty("Height").GetInt32() == 60,
        "restauration d'une image deja modifiee : nommee d'apres son premier nom, nouvelle taille dans la ligne");

    // Several tries: the versions wait for a choice; the chosen one is posed as an edit OF THE EDIT (".from-<its hex>").
    var firstHex = System.Text.RegularExpressions.Regex.Match(path, @"\.edit-([0-9a-f]{8})\.jpg$").Groups[1].Value;
    var tries = Json(await phone.Http.PostAsJsonAsync("/compute/jobs/from", new
    {
        entity_type = "Photo", entity_id = photo, kind = "edit", prompt = "Make the water darker", tries = 3,
    }, SimDevice.Wire));
    Check(JsonDocument.Parse(tries.GetProperty("params").GetString()!).RootElement.GetProperty("tries").GetInt32() == 3
          && tries.GetProperty("input_hash").GetString() == file.GetProperty("Hash").GetString(),
        "demande IA en 3 essais : le nombre est garde, et c'est la version modifiee qui sert d'entree");
    await pc.Http.PostAsJsonAsync("/compute/claim", new { name = "LOHAN", rid = "win-x64", models = new[] { "flux-kontext" } }, SimDevice.Wire);
    var versions = new List<string>();
    foreach (var side in new[] { 50, 52, 54 }) versions.Add(await Upload(pc, Picture(SkiaSharp.SKEncodedImageFormat.Png, side, side + 10)));
    var three = Json(await pc.Http.PostAsJsonAsync($"/compute/jobs/{tries.GetProperty("id").GetString()}/complete",
        new { result_hash = versions[0], result_hashes = versions, seconds = 240.0 }, SimDevice.Wire));
    var still = hub.Store.Snapshot("Photo", null, null, 50).Entities.Single(e => e.EntityId == photo).Payload!.Value.GetProperty("ImagePath").GetString();
    Check(three.GetProperty("attached_path").ValueKind == JsonValueKind.Null && still == path
          && JsonSerializer.Deserialize<List<string>>(three.GetProperty("results").GetString()!)!.SequenceEqual(versions),
        "3 versions rendues : gardees dans la demande, l'image n'est pas encore remplacee");
    var wrong = await phone.Http.PostAsJsonAsync($"/compute/jobs/{tries.GetProperty("id").GetString()}/choose", new { result_hash = resultHash }, SimDevice.Wire);
    var chose = await phone.Http.PostAsJsonAsync($"/compute/jobs/{tries.GetProperty("id").GetString()}/choose", new { result_hash = versions[1] }, SimDevice.Wire);
    var chosen = Json(chose);
    var again2 = await phone.Http.PostAsJsonAsync($"/compute/jobs/{tries.GetProperty("id").GetString()}/choose", new { result_hash = versions[2] }, SimDevice.Wire);
    var newPath = chosen.GetProperty("attached_path").GetString()!;
    Check(wrong.StatusCode == HttpStatusCode.BadRequest && chose.StatusCode == HttpStatusCode.OK && again2.StatusCode == HttpStatusCode.Conflict
          && chosen.GetProperty("result_hash").GetString() == versions[1],
        "choix : une version de la liste seulement, une seule fois ; la version choisie devient le resultat");
    Check(System.Text.RegularExpressions.Regex.IsMatch(newPath, @"^media\\archive\\piscine-x4\.jpg\.edit-[0-9a-f]{8}\.from-" + firstHex + @"\.jpg$")
          && hub.Store.Snapshot("Photo", null, null, 50).Entities.Single(e => e.EntityId == photo).Payload!.Value.GetProperty("ImagePath").GetString() == newPath,
        "la version choisie est posee comme modification de la modification (.from-<8 hex precedents>) : retour d'une etape possible");
});

await Scenario("16. Ecran Serveur : place, sauvegarde, appareils, travaux", async hub =>
{
    JsonElement Json(HttpResponseMessage r) => JsonDocument.Parse(r.Content.ReadAsStringAsync().Result).RootElement;
    hub.EntityTypes.Add("MediaFile");
    await hub.RestartAsync();
    var pc = await hub.Device("pc");
    var phone = await hub.Device("phone");
    JsonElement File(string path, long size) => JsonSerializer.SerializeToElement(new { Path = path, Hash = new string('a', 64), Size = size });
    var push = new PushRequest(null, new List<Mutation>
    {
        new(Guid.NewGuid().ToString(), "MediaFile", "1", 0, File("music\\Album\\a.mp3", 5_000_000), false),
        new(Guid.NewGuid().ToString(), "MediaFile", "2", 0, File("music/Album/b.mp3", 3_000_000), false),
        new(Guid.NewGuid().ToString(), "MediaFile", "3", 0, File("media\\archive\\x.heic", 2_000_000), false),
        new(Guid.NewGuid().ToString(), "MediaFile", "4", 0, File("books\\livre.epub", 700_000), false),
    });
    await pc.Http.PostAsJsonAsync("/sync/push", push, SimDevice.Wire);

    // No backup has ever run: the screen says so rather than inventing a state.
    var before = Json(await phone.Http.GetAsync("/server"));
    Check(before.GetProperty("backup").GetProperty("state").GetString() == "unknown", "aucune sauvegarde connue -> etat 'unknown'");

    var storage = before.GetProperty("storage").EnumerateArray().ToDictionary(k => k.GetProperty("kind").GetString()!, k => k.GetProperty("bytes").GetInt64());
    Check(storage.GetValueOrDefault("music") == 8_000_000 && storage.GetValueOrDefault("photos") == 2_000_000 && storage.GetValueOrDefault("books") == 700_000,
        "place par categorie, d'apres le catalogue (musique, photos, livres ; chemins PC ou telephone)");
    var disk = before.GetProperty("disk");
    Check(disk.GetProperty("total").GetInt64() > 0 && disk.GetProperty("free").GetInt64() > 0, "disque du hub : taille et place libre");
    var devices = before.GetProperty("devices").EnumerateArray().ToList();
    var pcRow = devices.First(d => d.GetProperty("name").GetString() == "simule pc");
    Check(devices.Count == 2 && pcRow.GetProperty("changes").GetInt64() == 4 && pcRow.GetProperty("last_change_at").ValueKind == JsonValueKind.String,
        "appareils relies, avec leur derniere modification envoyee");

    // What backup.sh writes after a run.
    var at = DateTime.UtcNow.AddHours(-2).ToString("yyyy-MM-ddTHH:mm:ssZ");
    await System.IO.File.WriteAllTextAsync(Path.Combine(hub.DataDir, "backup-status.json"),
        $$"""{"state": "ok", "at": "{{at}}", "last_ok_at": "{{at}}", "message": "", "bytes": 26000000000, "files": 3165, "disk_total": 1000000000000, "disk_free": 600000000000, "seconds": 793}""");
    var ok = Json(await phone.Http.GetAsync("/server")).GetProperty("backup");
    Check(ok.GetProperty("state").GetString() == "ok" && ok.GetProperty("files").GetInt64() == 3165 && ok.GetProperty("disk_free").GetInt64() == 600000000000,
        "derniere sauvegarde lue depuis backup-status.json");

    var old = DateTime.UtcNow.AddDays(-3).ToString("yyyy-MM-ddTHH:mm:ssZ");
    await System.IO.File.WriteAllTextAsync(Path.Combine(hub.DataDir, "backup-status.json"), $$"""{"state": "ok", "at": "{{old}}", "last_ok_at": "{{old}}"}""");
    Check(Json(await phone.Http.GetAsync("/server")).GetProperty("backup").GetProperty("state").GetString() == "late",
        "sauvegarde de plus d'un jour et demi -> 'late' (une nuit a ete manquee)");
    Check((await hub.Anonymous().GetAsync("/server")).StatusCode == HttpStatusCode.Unauthorized, "sans jeton -> 401");
});

await Scenario("18. Ecran Serveur : parcourir le disque dossier par dossier", async hub =>
{
    JsonElement Json(HttpResponseMessage r) => JsonDocument.Parse(r.Content.ReadAsStringAsync().Result).RootElement;
    var phone = await hub.Device("phone");
    var none = Json(await phone.Http.GetAsync("/server/disk"));
    Check(none.GetProperty("state").GetString() == "none", "pas encore d'inventaire -> 'none'");

    // A small disk: big folder with a sub-folder, a tiny folder (folded), loose files.
    var disk = Path.Combine(hub.DataDir, "disque");
    Directory.CreateDirectory(Path.Combine(disk, "films", "2024"));
    Directory.CreateDirectory(Path.Combine(disk, "petit"));
    void Make(string rel, int mb) { using var f = System.IO.File.Create(Path.Combine(disk, rel)); f.SetLength(mb * 1024L * 1024); }
    Make("films/a.mkv", 5); Make("films/2024/b.mkv", 3); Make("gros.iso", 2);
    System.IO.File.WriteAllText(Path.Combine(disk, "petit", "note.txt"), "x");
    DiskIndex.Run(disk, Path.Combine(hub.DataDir, DiskIndex.FileName));

    var top = Json(await phone.Http.GetAsync("/server/disk"));
    var folders = top.GetProperty("folders").EnumerateArray().ToList();
    Check(top.GetProperty("state").GetString() == "ready" && top.GetProperty("size").GetInt64() == 10L * 1024 * 1024 + 1,
        "racine : taille totale de tout ce qui est dessous");
    Check(folders.Count == 1 && folders[0].GetProperty("name").GetString() == "films" && folders[0].GetProperty("size").GetInt64() == 8L * 1024 * 1024
          && top.GetProperty("small_folders").GetInt64() == 1,
        "dossiers du plus gros au plus petit ; les tout petits regroupes");
    Check(top.GetProperty("largest").EnumerateArray().First().GetProperty("name").GetString() == "gros.iso", "plus gros fichiers du dossier");

    var films = Json(await phone.Http.GetAsync("/server/disk?path=/films"));
    Check(films.GetProperty("path").GetString() == "/films" && films.GetProperty("folders")[0].GetProperty("name").GetString() == "2024",
        "on entre dans un dossier, puis dans ses sous-dossiers");
    var lost = Json(await phone.Http.GetAsync("/server/disk?path=/films/disparu"));
    Check(lost.GetProperty("path").GetString() == "/films", "dossier disparu depuis : on reste au plus profond connu");

    var r = Json(await phone.Http.PostAsync("/server/disk/refresh", null));
    Check(r.GetProperty("refreshing").GetBoolean() && System.IO.File.Exists(Path.Combine(hub.DataDir, DiskIndex.RequestName)),
        "Actualiser : le hub depose la demande pour la tache systeme");
});

Console.WriteLine($"\n{passed} verifications reussies, {failures} en echec.");
return failures == 0 ? 0 : 1;
