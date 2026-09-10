using AkashicRecords.Domain;

namespace AkashicRecords.Infrastructure.Persistence;

// Scans the portable <exe>/music/ folder for .mp3 files and syncs them into the db.
// The user fills the folder themselves; no ID3 metadata is read (no external lib), so
// Title defaults to the file name and Artist stays empty until the user edits it.
public sealed class MusicLibraryScanner
{
    private readonly MusicTrackRepository _trackRepository;

    public MusicLibraryScanner(MusicTrackRepository trackRepository)
    {
        _trackRepository = trackRepository;
    }

    public static string MusicFolder => Path.Combine(AppContext.BaseDirectory, "music");

    public IReadOnlyList<MusicTrack> ScanAndSync()
    {
        try
        {
            Directory.CreateDirectory(MusicFolder);

            foreach (var fullPath in Directory.EnumerateFiles(MusicFolder, "*.mp3", SearchOption.AllDirectories))
            {
                try
                {
                    // Store a path relative to the exe folder so the app folder stays portable.
                    var relativePath = Path.Combine("music", Path.GetRelativePath(MusicFolder, fullPath));
                    var album = AlbumFromPath(fullPath);

                    var existing = _trackRepository.GetByPath(relativePath);
                    if (existing is not null)
                    {
                        // Backfill / keep the album in sync with the folder the file now lives in.
                        if (existing.Album != album) _trackRepository.UpdateAlbum(existing.Id, album);
                        continue;
                    }

                    _trackRepository.Add(new MusicTrack
                    {
                        FilePath = relativePath,
                        Title = Path.GetFileNameWithoutExtension(fullPath),
                        Album = album,
                        AddedAt = DateTime.Now
                    });
                }
                catch
                {
                    // A single locked/odd file must not abort the whole scan.
                }
            }
        }
        catch
        {
            // Folder access issues shouldn't crash the app; just return whatever is already known.
        }

        return _trackRepository.GetAll();
    }

    // The immediate parent folder under music/ is the album; files loose in music/ have none.
    private static string AlbumFromPath(string fullPath)
    {
        var dir = Path.GetDirectoryName(fullPath);
        if (dir is null) return string.Empty;
        var relativeDir = Path.GetRelativePath(MusicFolder, dir);
        if (relativeDir is "." or "") return string.Empty;
        // Use the top-level subfolder name (music/<album>/...).
        return relativeDir.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
    }
}
