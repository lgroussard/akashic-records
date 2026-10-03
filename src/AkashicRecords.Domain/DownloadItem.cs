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

    // The placement zone this item sits in (DownloadFolder.Id). null = the implicit
    // "Unfiled" bucket (straight downloads to the music/ root).
    public int? FolderId { get; set; }
}

// A "placement zone": a named bucket in the downloader board. Its meaning is the
// music/<Subfolder> the items inside it download into (null/empty = music/ root). The
// music scanner treats that first-level sub-folder name as the album.
public sealed class DownloadFolder
{
    public int Id { get; set; }
    public required string Name { get; set; }
    // Single path segment, sanitized on write; null/empty means the music/ root.
    public string? Subfolder { get; set; }
    public int SortOrder { get; set; }
}
