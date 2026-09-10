namespace AkashicRecords.Domain;

// Status of a queued link. The app does NOT download content itself (see DownloaderWindow):
// the queue only organizes links the user processes with their own configured tool.
public enum DownloadStatus
{
    Queued,
    Done,
    Failed
}

public sealed class DownloadItem
{
    public int Id { get; set; }
    public required string Url { get; set; }
    public string Title { get; set; } = string.Empty;
    public DownloadStatus Status { get; set; }
    public DateTime AddedAt { get; set; }
}
