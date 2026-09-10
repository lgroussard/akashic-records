namespace AkashicRecords.Infrastructure.Persistence;

// Copies user-picked media files into <exe folder>\media\... so they travel with the portable app folder.
public sealed class MediaStorage
{
    // Returns a path relative to the exe folder, suitable for storing in the db.
    public string ImportCover(string sourceFilePath) => Import(sourceFilePath, "covers");

    // Stores in-memory bytes (e.g. a web-fetched cover) into the media folder.
    public string ImportCoverFromBytes(byte[] data, string extension)
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "media", "covers");
        Directory.CreateDirectory(folder);
        var fileName = $"{Guid.NewGuid():N}{extension}";
        File.WriteAllBytes(Path.Combine(folder, fileName), data);
        return Path.Combine("media", "covers", fileName);
    }

    // Canvas images live in their own media subfolder per the spec's proposed folder layout.
    public string ImportArtwork(string sourceFilePath) => Import(sourceFilePath, "artwork");

    // Personal journal photos, per the spec's proposed media/photos folder layout.
    public string ImportJournalPhoto(string sourceFilePath) => Import(sourceFilePath, "photos");

    public string ImportRecipePhoto(string sourceFilePath) => Import(sourceFilePath, "recipes");

    public string ImportPoemImage(string sourceFilePath) => Import(sourceFilePath, "poetry");

    // Photo archive images, per the spec's proposed media/archive folder layout.
    public string ImportArchivePhoto(string sourceFilePath) => Import(sourceFilePath, "archive");

    // Per-track cover images for the music library (mp3 metadata isn't read, so covers are user-set).
    public string ImportMusicCover(string sourceFilePath) => Import(sourceFilePath, "musiccovers");

    private static string Import(string sourceFilePath, string subfolder)
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "media", subfolder);
        Directory.CreateDirectory(folder);

        var fileName = $"{Guid.NewGuid():N}{Path.GetExtension(sourceFilePath)}";
        File.Copy(sourceFilePath, Path.Combine(folder, fileName));
        return Path.Combine("media", subfolder, fileName);
    }

    public static string ResolveFullPath(string relativePath) => Path.Combine(AppContext.BaseDirectory, relativePath);

    public static void DeleteFile(string relativePath)
    {
        var fullPath = ResolveFullPath(relativePath);
        if (File.Exists(fullPath)) File.Delete(fullPath);
    }
}

