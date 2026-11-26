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

    // What an install puts in DataDir/compute: a program per platform and models (fake bytes here).
    var compute = Path.Combine(hub.DataDir, "compute");
    Directory.CreateDirectory(Path.Combine(compute, "models"));
    Directory.CreateDirectory(Path.Combine(compute, "tools", "realesrgan", "win-x64"));
    foreach (var m in new[] { "high-fidelity-4x", "ultrasharp-4x" })
    {
        File.WriteAllText(Path.Combine(compute, "models", m + ".param"), "param " + m);
        File.WriteAllText(Path.Combine(compute, "models", m + ".bin"), "bin " + m);
    }
    File.WriteAllText(Path.Combine(compute, "models", "orphan.param"), "no .bin: not a model");
    var exeBytes = Encoding.UTF8.GetBytes("fake exe");
    File.WriteAllBytes(Path.Combine(compute, "tools", "realesrgan", "win-x64", "realesrgan-ncnn-vulkan.exe"), exeBytes);

    JsonElement Json(HttpResponseMessage r) => JsonDocument.Parse(r.Content.ReadAsStringAsync().Result).RootElement;
    async Task<HttpResponseMessage> Post(SimDevice d, string url, object body) => await d.Http.PostAsJsonAsync(url, body, SimDevice.Wire);

    var board = Json(await pc.Http.GetAsync("/compute"));
    var names = board.GetProperty("models").EnumerateArray().Select(m => m.GetProperty("name").GetString()).ToList();
    Check(names.SequenceEqual(new[] { "high-fidelity-4x", "ultrasharp-4x" }) && board.GetProperty("models")[0].GetProperty("label").GetString() == "High Fidelity",
        "les modeles du hub sont listes (paire .param + .bin), dans l'ordre et avec leur nom lisible");

    var win = Json(await pc.Http.GetAsync("/compute/manifest?rid=win-x64"));
    var exe = win.GetProperty("tool_files")[0];
    var served = await pc.Http.GetByteArrayAsync("/compute/files/" + exe.GetProperty("hash").GetString());
    Check(exe.GetProperty("path").GetString() == "realesrgan-ncnn-vulkan.exe" && served.AsSpan().SequenceEqual(exeBytes),
        "manifeste Windows : le programme, servi par son empreinte, identique");
    var linux = Json(await pc.Http.GetAsync("/compute/manifest?rid=linux-x64"));
    Check(linux.GetProperty("tool_files").GetArrayLength() == 0 && linux.GetProperty("models").GetArrayLength() == 2,
        "manifeste Linux : pas de programme pour cette plateforme, les modeles quand meme");
    Check((await pc.Http.GetAsync("/compute/files/" + new string('c', 64))).StatusCode == HttpStatusCode.NotFound,
        "fichier hors catalogue -> 404 (le hub ne sert que ses outils et modeles)");

    // The phone sends a photo and asks for it to be upscaled.
    var photo = Encoding.UTF8.GetBytes("pretend this is a webp");
    var sent = (await (await phone.Http.PostAsync("/media", new ByteArrayContent(photo))).Content.ReadFromJsonAsync<MediaResponse>(SimDevice.Wire))!;
    Check((await Post(phone, "/compute/jobs", new { model = "nope", input_hash = sent.Hash, input_name = "photo.webp" })).StatusCode == HttpStatusCode.BadRequest
          && (await Post(phone, "/compute/jobs", new { model = "high-fidelity-4x", input_hash = sent.Hash, input_name = "photo.gif" })).StatusCode == HttpStatusCode.BadRequest
          && (await Post(phone, "/compute/jobs", new { model = "high-fidelity-4x", input_hash = new string('d', 64), input_name = "photo.webp" })).StatusCode == HttpStatusCode.BadRequest,
        "demande refusee : modele inconnu, format non pris en charge, image jamais envoyee");
    var added = await Post(phone, "/compute/jobs", new { model = "high-fidelity-4x", input_hash = sent.Hash, input_name = "C:\\Photos\\photo.webp" });
    var job = Json(added);
    var jobId = job.GetProperty("id").GetString()!;
    Check(added.StatusCode == HttpStatusCode.Created && job.GetProperty("status").GetString() == "queued" && job.GetProperty("input_name").GetString() == "photo.webp",
        "demande acceptee, en attente ; seul le nom du fichier est garde");

    Check((await Post(phone, "/compute/claim", new { name = "Telephone", rid = "android", models = Array.Empty<string>() })).StatusCode == HttpStatusCode.NoContent,
        "le telephone ne sait rien faire tourner : rien pour lui");
    Check((await Post(pc, "/compute/claim", new { name = "LOHAN", rid = "win-x64", models = new[] { "ultrasharp-4x" } })).StatusCode == HttpStatusCode.NoContent,
        "un appareil sans ce modele ne recoit pas le travail");
    var claimed = await Post(pc, "/compute/claim", new { name = "LOHAN", rid = "win-x64", models = new[] { "high-fidelity-4x", "ultrasharp-4x" } });
    var held = Json(claimed);
    Check(claimed.StatusCode == HttpStatusCode.OK && held.GetProperty("id").GetString() == jobId && held.GetProperty("status").GetString() == "running"
          && held.GetProperty("worker_name").GetString() == "LOHAN",
        "le PC qui sait le faire le recoit, marque en cours chez lui");
    Check((await Post(pc, "/compute/claim", new { name = "LOHAN", rid = "win-x64", models = new[] { "high-fidelity-4x" } })).StatusCode == HttpStatusCode.NoContent,
        "un travail en cours n'est pas donne deux fois");

    var result = Encoding.UTF8.GetBytes("pretend this is a bigger png");
    var up = (await (await pc.Http.PostAsync("/media", new ByteArrayContent(result))).Content.ReadFromJsonAsync<MediaResponse>(SimDevice.Wire))!;
    Check((await Post(phone, $"/compute/jobs/{jobId}/complete", new { result_hash = up.Hash, seconds = 1.0 })).StatusCode == HttpStatusCode.Conflict,
        "un autre appareil ne peut pas rendre le travail du PC");
    var done = Json(await Post(pc, $"/compute/jobs/{jobId}/complete", new { result_hash = up.Hash, seconds = 4.7 }));
    var fetched = await phone.Http.GetByteArrayAsync("/media/" + done.GetProperty("result_hash").GetString());
    Check(done.GetProperty("status").GetString() == "done" && done.GetProperty("result_name").GetString() == "photo-high-fidelity-4x.png"
          && fetched.AsSpan().SequenceEqual(result),
        "resultat rendu par le PC : garde sur le hub, le telephone le recupere quand il veut");

    // A failure, a retry, a removal.
    var second = Json(await Post(phone, "/compute/jobs", new { model = "ultrasharp-4x", input_hash = sent.Hash, input_name = "photo.webp" })).GetProperty("id").GetString()!;
    await Post(pc, "/compute/claim", new { name = "LOHAN", rid = "win-x64", models = new[] { "ultrasharp-4x" } });
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
    var third = Json(await Post(phone, "/compute/jobs", new { model = "high-fidelity-4x", input_hash = sent.Hash, input_name = "photo.webp" })).GetProperty("id").GetString()!;
    await Post(pc, "/compute/claim", new { name = "LOHAN", rid = "win-x64", models = new[] { "high-fidelity-4x" } });
    await hub.RestartAsync();
    var after = Json(await pc.Http.GetAsync("/compute")).GetProperty("jobs").EnumerateArray().First(j => j.GetProperty("id").GetString() == third);
    Check(after.GetProperty("status").GetString() == "queued", "redemarrage du hub : un travail en cours repart en attente");

    // The hub defers to a stronger device that is around.
    var seen = new ComputeWorkers();
    seen.Saw("pc", "LOHAN", "win-x64", new[] { "high-fidelity-4x" });
    Check(seen.OtherCanRun("high-fidelity-4x", HubStore.HubDevice) && !seen.OtherCanRun("ultrasharp-4x", HubStore.HubDevice)
          && !seen.OtherCanRun("high-fidelity-4x", "pc"),
        "le hub laisse un modele au PC present qui sait le faire tourner, et le fait lui-meme sinon");
});

Console.WriteLine($"\n{passed} verifications reussies, {failures} en echec.");
return failures == 0 ? 0 : 1;
