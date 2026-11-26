using AkashicRecords.Hub;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AkashicRecords.Hub.SmokeTest;

/// <summary>The real hub (Kestrel + SQLite + blob files), in-process, on a free local port with a
/// throw-away data folder.</summary>
public sealed class TestHub : IAsyncDisposable
{
    private WebApplication? _app;
    private int _port; // 0 the first time; kept across a restart so paired devices keep their address

    public string DataDir { get; } = Path.Combine(Path.GetTempPath(), "akashic-hub-smoke-" + Guid.NewGuid().ToString("N"));
    public Uri Address { get; private set; } = null!;
    public HubStore Store => _app!.Services.GetRequiredService<HubStore>();
    public List<string> EntityTypes { get; } = new() { "Recipe", "Poem" };
    /// <summary>Models the hub runs itself. None by default: the tests' fake programs must never be started.</summary>
    public string ComputeHubModels { get; set; } = "";

    public async Task StartAsync()
    {
        var options = new HubOptions
        {
            DataDir = DataDir,
            Url = $"http://127.0.0.1:{_port}",
            EntityTypes = EntityTypes.ToList(),
            MaxBlobMegabytes = 2,
            ComputeHubModels = ComputeHubModels,
        };
        _app = HubApp.Build(new[] { "--Logging:LogLevel:Default=Warning" }, options);
        await _app.StartAsync();
        Address = new Uri(_app.Urls.First());
        _port = Address.Port;
    }

    /// <summary>Stops the hub and starts a new one on the same data folder and the same port.</summary>
    public async Task RestartAsync()
    {
        await StopAsync();
        await StartAsync();
    }

    /// <summary>A new device, paired through the real flow: code created on the hub side, exchanged for a token.</summary>
    public Task<SimDevice> Device(string id, int pullLimit = 500) =>
        SimDevice.PairAsync(Address, Store.CreatePairCode(10), id, pullLimit);

    public HttpClient Anonymous() => new() { BaseAddress = Address };

    private async Task StopAsync()
    {
        if (_app is null) return;
        await _app.StopAsync();
        await _app.DisposeAsync();
        _app = null;
        SqliteConnection.ClearAllPools();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        try { Directory.Delete(DataDir, true); } catch (IOException) { /* temp dir, best effort */ }
    }
}
