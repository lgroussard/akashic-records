using AkashicRecords.Domain;
using AkashicRecords.Infrastructure.Persistence;
using AkashicRecords.Sync;

namespace AkashicRecords.App.Sync;

/// <summary>
/// Paired with the hub, the ⤓ of the music player still lands in this computer's download queue first (that is
/// what keeps its ✓); from there it goes to the hub's shared board, in the zone of its album folder, and the hub
/// downloads it for every device. A request the hub cannot get stays queued and goes after the next sync.
/// </summary>
public static class HubDownloadQueue
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static async Task<int> SendQueuedAsync()
    {
        var settings = SyncService.Instance.Settings;
        if (!settings.IsPaired) return 0;
        if (!await Gate.WaitAsync(0)) return 0;
        try
        {
            return await Task.Run(async () =>
            {
                var items = new DownloadItemRepository(new SqliteConnectionFactory());
                var queued = items.GetAll().Where(i => i.Status == DownloadStatus.Queued).ToList();
                if (queued.Count == 0) return 0;
                var folders = new DownloadFolderRepository(new SqliteConnectionFactory()).GetAll().ToDictionary(f => f.Id);
                using var hub = new HubClient(settings.HubUrl, settings.Token);
                var zones = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                var sent = 0;
                foreach (var item in queued)
                {
                    var folder = item.FolderId is int id && folders.TryGetValue(id, out var f) ? (f.Subfolder ?? "").Trim() : "";
                    if (!zones.TryGetValue(folder, out var zoneId))
                        zones[folder] = zoneId = folder.Length == 0 ? null : (await hub.AddZoneAsync(folder, folder)).Id;
                    await hub.AddDownloadAsync(item.Url, item.Title, zoneId);
                    items.UpdateStatus(item.Id, DownloadStatus.Done);
                    sent++;
                }
                return sent;
            });
        }
        catch (HubException)
        {
            return 0; // the hub is not reachable now: they stay queued
        }
        finally
        {
            Gate.Release();
        }
    }
}
