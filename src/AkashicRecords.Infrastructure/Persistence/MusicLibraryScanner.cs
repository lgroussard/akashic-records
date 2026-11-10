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

            // Known tracks read once, then looked up in memory: one query per file (a fresh connection each time)
            // made every player opening cost seconds of CPU on a library of a few thousand files.
            var known = new Dictionary<string, MusicTrack>(StringComparer.OrdinalIgnoreCase);
            foreach (var track in _trackRepository.GetAll()) known.TryAdd(track.FilePath.Replace('\\', '/'), track);

            foreach (var fullPath in Directory.EnumerateFiles(MusicFolder, "*.mp3", SearchOption.AllDirectories))
            {
                try
                {
                    // Store a path relative to the exe folder so the app folder stays portable.
                    var relativePath = Path.Combine("music", UnderMusic(fullPath));
                    var album = AlbumFromPath(fullPath);

                    var existing = known.GetValueOrDefault(relativePath.Replace('\\', '/'));
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

        // Prune rows whose files were deleted from disk, so the library never shows
        // tracks pointing at missing files (the "Sans album" ghost tracks).
        PruneMissingTracks();

        return _trackRepository.GetAll();
    }

    // Removes DB rows whose FilePath no longer exists under music/. The scanner only ever
    // *adds* rows, so a file deleted from disk would otherwise linger forever.
    private void PruneMissingTracks()
    {
        var present = new HashSet<string>(
            Directory.EnumerateFiles(MusicFolder, "*.mp3", SearchOption.AllDirectories)
                .Select(f => Path.Combine("music", UnderMusic(f)))
                .Select(p => p.Replace('\\', '/')),
            StringComparer.OrdinalIgnoreCase);

        foreach (var track in _trackRepository.GetAll())
        {
            var rel = track.FilePath.Replace('\\', '/');
            if (!present.Contains(rel))
            {
                _trackRepository.Delete(track.Id);
            }
        }
    }

    // Path of a file/folder found under MusicFolder, relative to it. Directory.EnumerateFiles returns paths that start
    // with the folder string it was given, so the prefix is simply cut off: Path.GetRelativePath resolved both paths on
    // disk on every call (thousands of file-system queries per scan when the path holds a short "XXXX~1" name).
    private static string UnderMusic(string path)
    {
        var root = MusicFolder;
        if (path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            var rest = path[root.Length..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return rest.Length == 0 ? "." : rest;
        }
        return Path.GetRelativePath(root, path);
    }

    // The immediate parent folder under music/ is the album; files loose in music/ have none.
    private static string AlbumFromPath(string fullPath)
    {
        var dir = Path.GetDirectoryName(fullPath);
        if (dir is null) return string.Empty;
        var relativeDir = UnderMusic(dir);
        if (relativeDir is "." or "") return string.Empty;
        // Use the top-level subfolder name (music/<album>/...).
        return relativeDir.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
    }
}
