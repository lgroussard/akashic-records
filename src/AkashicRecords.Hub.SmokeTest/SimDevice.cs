using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AkashicRecords.Domain;
using AkashicRecords.Hub;

namespace AkashicRecords.Hub.SmokeTest;

/// <summary>
/// A simulated replica (PC or phone): a local state, an outbox, a pull cursor - the write path of
/// MOBILITE-SYNC.md section 3, in memory. Conflict policy: last-writer-wins per entity - on a
/// conflict the device re-pushes its whole version on top of the hub's current revision.
/// </summary>
public sealed class SimDevice
{
    public static readonly JsonSerializerOptions Wire = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    public sealed record Local(long Rev, string? PayloadJson, bool Deleted, string Type = "Recipe");

    private readonly HttpClient _http;
    private readonly List<Mutation> _outbox = new();

    /// <summary>Pairs a new device with the hub (one-time code -> token) and returns it, authenticated.</summary>
    public static async Task<SimDevice> PairAsync(Uri hub, string code, string id, int pullLimit = 500)
    {
        var http = new HttpClient { BaseAddress = hub };
        var resp = await http.PostAsJsonAsync("/auth/pair", new PairRequest(code, id, "simule " + id), Wire);
        resp.EnsureSuccessStatusCode();
        var paired = (await resp.Content.ReadFromJsonAsync<PairResponse>(Wire))!;
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", paired.Token);
        return new SimDevice(paired.DeviceId, http, pullLimit) { Token = paired.Token };
    }

    public SimDevice(string id, HttpClient http, int pullLimit = 500)
    {
        Id = id;
        _http = http;
        PullLimit = pullLimit;
    }

    public string Id { get; }
    public string Token { get; private init; } = "";
    public HttpClient Http => _http;
    public int PullLimit { get; }
    public long Cursor { get; private set; }
    public Dictionary<string, Local> State { get; } = new();
    public int ConflictsSeen { get; private set; }
    public IReadOnlyList<Mutation> Outbox => _outbox;

    // ---- local edits: state + outbox, as one step (the "same transaction" of the design) ----

    public string Create(Recipe r)
    {
        var id = Guid.NewGuid().ToString();
        Save(id, r);
        return id;
    }

    public void Save(string entityId, Recipe r) => SaveAs("Recipe", entityId, r);

    /// <summary>Any entity type: the hub only sees a type name and a JSON object.</summary>
    public void SaveAs(string entityType, string entityId, object payload)
    {
        var json = JsonSerializer.Serialize(payload);
        var baseRev = State.TryGetValue(entityId, out var cur) ? cur.Rev : 0;
        Enqueue(new Mutation(Guid.NewGuid().ToString(), entityType, entityId, baseRev, Parse(json), false));
        State[entityId] = new Local(baseRev, json, false, entityType);
    }

    public void Delete(string entityId)
    {
        var type = State.TryGetValue(entityId, out var cur) ? cur.Type : "Recipe";
        var baseRev = cur?.Rev ?? 0;
        Enqueue(new Mutation(Guid.NewGuid().ToString(), type, entityId, baseRev, null, true));
        State[entityId] = new Local(baseRev, null, true, type);
    }

    public Recipe? Get(string entityId) =>
        State.TryGetValue(entityId, out var l) && !l.Deleted && l.PayloadJson is not null
            ? JsonSerializer.Deserialize<Recipe>(l.PayloadJson)
            : null;

    // Coalesce: one pending mutation per entity (the latest intention), keeping the oldest base_rev.
    private void Enqueue(Mutation m)
    {
        var i = _outbox.FindIndex(x => x.EntityType == m.EntityType && x.EntityId == m.EntityId);
        if (i < 0) { _outbox.Add(m); return; }
        _outbox[i] = m with { BaseRev = _outbox[i].BaseRev };
    }

    // ---- sync ----

    public async Task<PushResponse> PushRawAsync(IReadOnlyList<Mutation> mutations)
    {
        var resp = await _http.PostAsJsonAsync("/sync/push", new PushRequest(null, mutations.ToList()), Wire);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<PushResponse>(Wire))!;
    }

    public async Task<PullResponse> PullRawAsync(long since, int limit)
    {
        return (await _http.GetFromJsonAsync<PullResponse>($"/sync/pull?since={since}&limit={limit}", Wire))!;
    }

    /// <summary>Pushes the outbox once, without resolving conflicts. Returns the hub's answer.</summary>
    public async Task<PushResponse> PushOnceAsync()
    {
        var sent = _outbox.ToList();
        var resp = await PushRawAsync(sent);
        foreach (var (m, res) in sent.Zip(resp.Results))
        {
            if (res.Status is MutationStatus.Accepted or MutationStatus.Duplicate)
            {
                _outbox.Remove(m);
                // Our version is now the hub's version at this revision.
                State[m.EntityId] = State[m.EntityId] with { Rev = res.Rev };
            }
            else
            {
                ConflictsSeen++;
                // Last-writer-wins per entity: keep our whole version, rebase it on the hub's revision.
                var i = _outbox.IndexOf(m);
                _outbox[i] = m with { MutationId = Guid.NewGuid().ToString(), BaseRev = res.Rev };
            }
        }
        return resp;
    }

    public async Task PullAsync()
    {
        while (true)
        {
            var page = await PullRawAsync(Cursor, PullLimit);
            foreach (var row in page.Rows)
            {
                // A pending local edit stays on top until it is pushed (it will win or be rebased).
                var pending = _outbox.Any(x => x.EntityType == row.EntityType && x.EntityId == row.EntityId);
                var localRev = State.TryGetValue(row.EntityId, out var l) ? l.Rev : 0;
                if (!pending && row.Rev > localRev)
                    State[row.EntityId] = new Local(row.Rev, row.Payload?.GetRawText(), row.Deleted, row.EntityType);
            }
            Cursor = page.NextCursor;
            if (!page.HasMore) break;
        }
    }

    /// <summary>Full cycle: push until the outbox is empty (conflicts rebased and re-pushed), then pull.</summary>
    public async Task SyncAsync()
    {
        for (var attempt = 0; _outbox.Count > 0; attempt++)
        {
            if (attempt > 20) throw new InvalidOperationException($"{Id}: outbox does not drain");
            await PushOnceAsync();
        }
        await PullAsync();
    }

    /// <summary>
    /// What a device does when its cursor is too old: take the full state from /snapshot (page by
    /// page), restart the cursor at the snapshot's head, then pull whatever was committed meanwhile.
    /// Local entities the hub has never heard of and that are not pending are dropped.
    /// Returns the number of pages read.
    /// </summary>
    public async Task<int> ResyncFromSnapshotAsync(int pageSize = 500)
    {
        long? head = null;
        var seen = new HashSet<string>();
        var pages = 0;
        var query = $"/snapshot?limit={pageSize}";
        while (true)
        {
            var page = (await _http.GetFromJsonAsync<SnapshotResponse>(query, Wire))!;
            pages++;
            head ??= page.HeadSeq; // the FIRST page's head: later commits are replayed by the pull below
            foreach (var e in page.Entities)
            {
                seen.Add(e.EntityId);
                var pending = _outbox.Any(x => x.EntityId == e.EntityId);
                var localRev = State.TryGetValue(e.EntityId, out var l) ? l.Rev : 0;
                if (!pending && e.Rev >= localRev)
                    State[e.EntityId] = new Local(e.Rev, e.Payload?.GetRawText(), e.Deleted, e.EntityType);
            }
            if (!page.HasMore) break;
            query = $"/snapshot?limit={pageSize}&after_type={Uri.EscapeDataString(page.NextAfterType!)}&after_id={Uri.EscapeDataString(page.NextAfterId!)}";
        }
        foreach (var id in State.Keys.Where(k => !seen.Contains(k) && _outbox.All(x => x.EntityId != k)).ToList())
            State.Remove(id);
        Cursor = head ?? 0;
        await PullAsync();
        return pages;
    }

    /// <summary>Canonical view of the replica, comparable across devices.</summary>
    public string Snapshot() => string.Join("\n", State
        .OrderBy(kv => kv.Key, StringComparer.Ordinal)
        .Select(kv => $"{kv.Value.Type}|{kv.Key}|rev={kv.Value.Rev}|del={kv.Value.Deleted}|{Canonical(kv.Value.PayloadJson)}"));

    private static string Canonical(string? json) =>
        json is null ? "null" : JsonSerializer.Serialize(Parse(json));

    private static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}
