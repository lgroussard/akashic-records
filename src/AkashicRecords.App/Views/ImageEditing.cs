using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AkashicRecords.App.Sync;
using AkashicRecords.Infrastructure.Persistence;
using SkiaSharp;

namespace AkashicRecords.App.Views;

/// <summary>What an edit or a revert left in the row: the new ImagePath, the picture's pixel size, and for a canvas
/// element its new display height.</summary>
public sealed record ImageEditOutcome(string ImagePath, int PixelWidth, int PixelHeight, double? CanvasHeight);

// « Modifier » / « Revenir à l'original » on one picture already in the app (a row of Photo, JournalPhoto, RefImage
// or CanvasElement). The phone follows the very same rules, so edits made on either side meet through sync:
//  - an edit is a new JPEG next to the current file, named <root>.edit-<8 hex>.jpg, where <root> is the current name
//    up to its first ".edit-" (else the whole name, extension included): abc.heic → abc.heic.edit-1a2b3c4d.jpg;
//  - the row's ImagePath is pointed at it (the path keeps its own separator style);
//  - an edit deletes nothing: the original (folder + <root>) is what « Revenir à l'original » points back to;
//  - « Rendre définitif » copies the edit to <base>-<8 hex>.jpg (<base> = <root> without its extension), points the
//    row at it, then deletes from this computer the original and every <root>.edit-* file no row references anymore.
public static class ImageEditing
{
    public const string EditLabel = "Modifier";
    public const string RevertLabel = "Revenir à l'original";
    public const string FinalLabel = "Rendre définitif";
    public const string EditMarker = ".edit-";
    public const int JpegQuality = 92;

    private static readonly string[] Tables = ["Photo", "JournalPhoto", "RefImage", "CanvasElement"];

    // ---- Path rules (identical on the phone) ----

    /// <summary>True when the stored path is an edit, i.e. « Revenir à l'original » applies.</summary>
    public static bool IsEdited(string? storedPath) => storedPath is not null && storedPath.Contains(EditMarker, StringComparison.Ordinal);

    private static (string Folder, string Name) Split(string storedPath)
    {
        var cut = storedPath.LastIndexOfAny(['\\', '/']);
        return cut < 0 ? ("", storedPath) : (storedPath[..(cut + 1)], storedPath[(cut + 1)..]);
    }

    /// <summary>The current file name up to its first ".edit-", else the whole name with its extension.</summary>
    public static string RootName(string fileName)
    {
        var at = fileName.IndexOf(EditMarker, StringComparison.Ordinal);
        return at >= 0 ? fileName[..at] : fileName;
    }

    /// <summary>Same folder (same separators) + root name.</summary>
    public static string OriginalPath(string storedPath)
    {
        var (folder, name) = Split(storedPath);
        return folder + RootName(name);
    }

    /// <summary>
    /// A fresh edit path next to the current file, not yet taken on this computer. An edit of an edit names the
    /// version it came from: &lt;root&gt;.edit-&lt;new hex&gt;.from-&lt;previous hex&gt;.jpg, so « Annuler la dernière étape » can
    /// go back one step on any device (same rule on the phone and on the hub).
    /// </summary>
    public static string NewEditPath(string storedPath)
    {
        var (folder, name) = Split(storedPath);
        var root = RootName(name);
        var from = EditId(name) is { } previous ? FromMarker + previous : "";
        while (true)
        {
            var hex = Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
            var candidate = folder + root + EditMarker + hex + from + ".jpg";
            if (!File.Exists(MediaStorage.ResolveFullPath(candidate))) return candidate;
        }
    }

    public const string FromMarker = ".from-";
    public const string UndoLabel = "Annuler la dernière étape";

    /// <summary>The 8 hex naming an edit ("abc.jpg.edit-1a2b3c4d…" → "1a2b3c4d"), or null for a picture that is not an edit.</summary>
    public static string? EditId(string fileName)
    {
        var at = fileName.IndexOf(EditMarker, StringComparison.Ordinal);
        if (at < 0 || fileName.Length < at + EditMarker.Length + 8) return null;
        var id = fileName.Substring(at + EditMarker.Length, 8);
        return id.All(Uri.IsHexDigit) ? id.ToLowerInvariant() : null;
    }

    /// <summary>The 8 hex of the version an edit came from (".from-xxxxxxxx.jpg"), or null (it came from the original).</summary>
    public static string? FromId(string fileName)
    {
        var m = System.Text.RegularExpressions.Regex.Match(fileName, @"\.from-([0-9a-fA-F]{8})\.jpg$");
        return m.Success ? m.Groups[1].Value.ToLowerInvariant() : null;
    }

    /// <summary>True when going back one step is not the same as going back to the original.</summary>
    public static bool HasPreviousStep(string? storedPath) => storedPath is not null && FromId(Split(storedPath).Name) is not null;

    /// <summary>
    /// The path of the version before the current edit: the edit named by ".from-", looked for in the folder on this
    /// computer and in the synchronised file list; the original when the edit came straight from it. Null when not
    /// an edit or when that version is unknown here.
    /// </summary>
    public static string? PreviousPath(string storedPath)
    {
        var (folder, name) = Split(storedPath);
        if (!IsEdited(storedPath)) return null;
        var from = FromId(name);
        if (from is null) return OriginalPath(storedPath);
        var prefix = RootName(name) + EditMarker + from;

        var fullFolder = Path.GetDirectoryName(MediaStorage.ResolveFullPath(storedPath));
        if (fullFolder is not null && Directory.Exists(fullFolder))
            foreach (var file in Directory.EnumerateFiles(fullFolder, prefix + "*"))
                return folder + Path.GetFileName(file);

        // Not here yet: the synchronised list knows every file of the app (forward slashes there).
        using var connection = new SqliteConnectionFactory().CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Path FROM sync_file WHERE lower(Path) LIKE $p ESCAPE '\\' LIMIT 1;";
        var key = (folder + prefix).Replace('\\', '/').ToLowerInvariant().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
        command.Parameters.AddWithValue("$p", key + "%");
        try
        {
            if (command.ExecuteScalar() is string known) return folder + known[(known.LastIndexOf('/') + 1)..];
        }
        catch (Microsoft.Data.Sqlite.SqliteException) { /* never synchronised: no sync_file */ }
        return null;
    }

    // ---- Actions (called from the views) ----

    /// <summary>Opens the editor on the row's current picture. Returns what was saved, or null (cancelled, unreadable).</summary>
    public static ImageEditOutcome? Edit(DependencyObject? from, string table, int id)
    {
        var owner = from is null ? null : from as Window ?? Window.GetWindow(from);
        try
        {
            var row = ReadRow(table, id);
            if (row is null) return null;
            var fullPath = MediaStorage.ResolveFullPath(row.Value.Path);
            SKBitmap? oriented = File.Exists(fullPath) ? DecodeOriented(fullPath) : null;
            if (oriented is null)
            {
                Say(owner, File.Exists(fullPath)
                    ? "Cette image ne peut pas être ouverte sur cet ordinateur (format non pris en charge, par exemple HEIC sans le codec Windows)."
                    : "Le fichier de cette image n'est pas sur cet ordinateur : synchronise puis réessaie.");
                return null;
            }

            var window = new ImageEditWindow(oriented, row.Value.Path, aiAvailable: UpscaleAction.Available) { Owner = owner };
            if (window.ShowDialog() != true) return null;
            if (window.AiResult is { } ai)
            {
                _ = SendToAiAsync(owner, table, id, ai);
                return null; // the picture changes later, when the result comes back through the sync
            }
            if (window.Result is not { } settings) return null;

            var newPath = NewEditPath(row.Value.Path);
            System.Windows.Input.Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
            try
            {
                var (w, h) = RenderAndSave(fullPath, MediaStorage.ResolveFullPath(newPath), settings);
                return Apply(table, id, newPath, w, h, row.Value.Width);
            }
            finally
            {
                System.Windows.Input.Mouse.OverrideCursor = null;
            }
        }
        catch (Exception ex)
        {
            // Called from click handlers: anything unexpected is said, never left to crash the app.
            Say(owner, "La modification n'a pas pu être enregistrée : " + ex.Message);
            return null;
        }
    }

    private static async Task SendToAiAsync(Window? owner, string table, int id, ImageAiRequest ai)
    {
        string message;
        try
        {
            await ComputeService.Instance.RequestEditAsync(table, id, ai.Kind, ai.Prompt, ai.MaskPng, ai.MaskText, ai.Erase, ai.Tries, ai.Engine, ai.HidePng);
            message = (ai.Kind == "restore" ? "Envoyée à la restauration" : "Envoyée à l'IA") + (ai.Tries > 1
                ? $" : {ai.Tries} versions seront prêtes d'ici quelques minutes, à choisir dans la fenêtre des calculs (« Garder celle-ci »)."
                : " : la version modifiée remplacera l'image d'ici quelques minutes (suivi dans la fenêtre des calculs).") +
                " « " + UndoLabel + " » et « " + RevertLabel + " » resteront possibles.";
        }
        catch (AkashicRecords.Sync.HubException ex)
        {
            message = ex.Message;
        }
        catch (Exception ex)
        {
            message = "La demande n'a pas pu être envoyée : " + ex.Message;
        }
        Say(owner, message);
    }

    /// <summary>Points the row back at the original file. Returns the restored state, or null.</summary>
    public static ImageEditOutcome? Revert(DependencyObject? from, string table, int id)
    {
        var owner = from is null ? null : from as Window ?? Window.GetWindow(from);
        try
        {
            var row = ReadRow(table, id);
            if (row is null || !IsEdited(row.Value.Path)) return null;
            var original = OriginalPath(row.Value.Path);
            var fullPath = MediaStorage.ResolveFullPath(original);
            if (!File.Exists(fullPath))
            {
                Say(owner, "L'original n'est pas encore sur cet ordinateur : synchronise puis réessaie.");
                return null;
            }
            var (w, h) = ReadOrientedSize(fullPath);
            return Apply(table, id, original, w, h, null, revert: true);
        }
        catch (Exception ex)
        {
            Say(owner, "Le retour à l'original n'a pas pu se faire : " + ex.Message);
            return null;
        }
    }

    /// <summary>« Annuler la dernière étape »: points the row at the version before the current edit. Returns the new state, or null.</summary>
    public static ImageEditOutcome? Undo(DependencyObject? from, string table, int id)
    {
        var owner = from is null ? null : from as Window ?? Window.GetWindow(from);
        try
        {
            var row = ReadRow(table, id);
            if (row is null || !IsEdited(row.Value.Path)) return null;
            var previous = PreviousPath(row.Value.Path);
            if (previous is null)
            {
                Say(owner, "La version précédente n'est pas connue sur cet ordinateur : synchronise puis réessaie.");
                return null;
            }
            var fullPath = MediaStorage.ResolveFullPath(previous);
            if (!File.Exists(fullPath))
            {
                Say(owner, "La version précédente n'est pas encore sur cet ordinateur : synchronise puis réessaie.");
                return null;
            }
            var (w, h) = ReadOrientedSize(fullPath);
            return Apply(table, id, previous, w, h, row.Value.Width);
        }
        catch (Exception ex)
        {
            Say(owner, "Le retour en arrière n'a pas pu se faire : " + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// « Rendre définitif »: after a confirmation, the current edit is copied to a clean name
    /// (<base>-<8 hex>.jpg), the row points at it, and the original and every <root>.edit-* file that no row
    /// references anymore are deleted from this computer. Returns the new state, or null (cancelled, failed).
    /// </summary>
    public static ImageEditOutcome? MakeFinal(DependencyObject? from, string table, int id)
    {
        var owner = from is null ? null : from as Window ?? Window.GetWindow(from);
        try
        {
            var row = ReadRow(table, id);
            if (row is null || !IsEdited(row.Value.Path)) return null;
            var currentFull = MediaStorage.ResolveFullPath(row.Value.Path);
            if (!File.Exists(currentFull))
            {
                Say(owner, "Le fichier de cette image n'est pas sur cet ordinateur : synchronise puis réessaie.");
                return null;
            }
            if (!ConfirmFinal(owner)) return null;

            var (finalPath, message) = MakeFinal(table, id, row.Value.Path);
            if (message is not null) Say(owner, message);
            return new ImageEditOutcome(finalPath, 0, 0, null);
        }
        catch (Exception ex)
        {
            Say(owner, "La modification n'a pas pu être rendue définitive : " + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// The work behind « Rendre définitif », without any dialog. Returns the clean path now in the row, and null or
    /// a message for the user (once the row is updated, only the deletions can still go wrong).
    /// </summary>
    public static (string FinalPath, string? Message) MakeFinal(string table, int id, string storedPath)
    {
        if (!Tables.Contains(table)) throw new ArgumentException("Table inconnue : " + table);
        var finalPath = NewFinalPath(storedPath);
        File.Copy(MediaStorage.ResolveFullPath(storedPath), MediaStorage.ResolveFullPath(finalPath), overwrite: false);

        // Path only: the content is the same picture, so RefImage / CanvasElement sizes stay as they are.
        using (var connection = new SqliteConnectionFactory().CreateOpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"UPDATE {table} SET ImagePath = $path WHERE Id = $id;";
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$path", finalPath);
            command.ExecuteNonQuery();
        }
        string? message;
        try
        {
            message = DeleteFormerFiles(storedPath, finalPath);
        }
        catch (Exception ex)
        {
            message = "La modification est définitive, mais les anciens fichiers n'ont pas pu être supprimés : " + ex.Message;
        }
        _ = SyncService.Instance.RunAsync();
        return (finalPath, message);
    }

    private static string? DeleteFormerFiles(string storedPath, string finalPath)
    {
        // The original and every edit of it, unless some row still uses it (another photo pointing at the same file).
        var (folder, name) = Split(storedPath);
        var root = RootName(name);
        var candidates = new List<string> { folder + root };
        var fullFolder = Path.GetDirectoryName(MediaStorage.ResolveFullPath(folder + root));
        if (fullFolder is not null && Directory.Exists(fullFolder))
        {
            foreach (var file in Directory.EnumerateFiles(fullFolder))
            {
                var fileName = Path.GetFileName(file);
                if (fileName.StartsWith(root + EditMarker, StringComparison.OrdinalIgnoreCase)) candidates.Add(folder + fileName);
            }
        }

        var referenced = ReferencedPaths();
        var failed = 0;
        foreach (var candidate in candidates)
        {
            if (SamePath(candidate, finalPath) || referenced.Contains(NormalisePath(candidate))) continue;
            var full = MediaStorage.ResolveFullPath(candidate);
            if (referenced.Contains(NormaliseFull(full)) || !File.Exists(full)) continue;
            try { File.Delete(full); }
            catch (IOException) { failed++; }
            catch (UnauthorizedAccessException) { failed++; }
        }

        return failed == 0 ? null
            : $"La modification est définitive, mais {failed} ancien(s) fichier(s) n'ont pas pu être supprimés (peut-être ouverts ailleurs).";
    }

    /// <summary>A clean path next to the current file: <root> without its extension + "-" + 8 hex + ".jpg".</summary>
    public static string NewFinalPath(string storedPath)
    {
        var (folder, name) = Split(storedPath);
        var root = RootName(name);
        var dot = root.LastIndexOf('.');
        var stem = dot > 0 ? root[..dot] : root;
        while (true)
        {
            var hex = Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
            var candidate = folder + stem + "-" + hex + ".jpg";
            if (!File.Exists(MediaStorage.ResolveFullPath(candidate))) return candidate;
        }
    }

    // Every picture path some row of the app points at, normalised (both as stored and as resolved on this computer).
    private static readonly (string Table, string Column)[] ImageColumns =
    [
        ("Photo", "ImagePath"), ("JournalPhoto", "ImagePath"), ("RefImage", "ImagePath"), ("CanvasElement", "ImagePath"),
        ("Recipe", "CoverImagePath"), ("Artwork", "CoverImagePath"), ("Poem", "ImagePath"),
        ("MusicTrack", "CoverImagePath"), ("WatchlistItem", "CoverImagePath"), ("Book", "CoverPath"),
    ];

    private static HashSet<string> ReferencedPaths()
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        using var connection = new SqliteConnectionFactory().CreateOpenConnection();
        foreach (var (table, column) in ImageColumns)
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT {column} FROM {table} WHERE {column} IS NOT NULL AND {column} <> '';";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var path = reader.GetString(0);
                set.Add(NormalisePath(path));
                try { set.Add(NormaliseFull(MediaStorage.ResolveFullPath(path))); }
                catch (ArgumentException) { /* not a usable path: the stored form is enough */ }
            }
        }
        return set;
    }

    private static string NormalisePath(string path) => path.Replace('\\', '/').ToLowerInvariant();

    private static string NormaliseFull(string fullPath) => NormalisePath(Path.GetFullPath(fullPath));

    private static bool SamePath(string a, string b) => NormalisePath(a) == NormalisePath(b);

    // « Oui » / « Annuler », « Annuler » being the default (Entrée and Échap both cancel).
    private static bool ConfirmFinal(Window? owner)
    {
        Brush? Find(string key) => Application.Current.TryFindResource(key) as Brush;
        var dialog = new Window
        {
            Title = FinalLabel,
            Owner = owner,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Background = Find("AppBgBrush") ?? Brushes.White,
            Foreground = Find("TextBrush") ?? Brushes.Black,
        };
        var text = new System.Windows.Controls.TextBlock
        {
            Text = "Rendre cette modification définitive ? L'original et les versions précédentes seront supprimés de cet ordinateur, et « "
                + RevertLabel + " » ne sera plus possible.",
            TextWrapping = TextWrapping.Wrap, MaxWidth = 420, Margin = new Thickness(0, 0, 0, 16),
        };
        var yes = new System.Windows.Controls.Button
        {
            Content = "Oui", Padding = new Thickness(14, 6, 14, 6), Cursor = System.Windows.Input.Cursors.Hand,
            Style = Application.Current.TryFindResource("PrimaryButtonStyle") as Style,
        };
        yes.Click += (_, _) => dialog.DialogResult = true;
        var cancel = new System.Windows.Controls.Button
        {
            Content = "Annuler", Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(8, 0, 0, 0), Cursor = System.Windows.Input.Cursors.Hand,
            IsCancel = true, IsDefault = true,
            Style = Application.Current.TryFindResource("SecondaryButtonStyle") as Style,
        };
        var buttons = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(yes);
        buttons.Children.Add(cancel);
        var stack = new System.Windows.Controls.StackPanel { Margin = new Thickness(20) };
        stack.Children.Add(text);
        stack.Children.Add(buttons);
        dialog.Content = stack;
        dialog.Loaded += (_, _) => cancel.Focus();
        return dialog.ShowDialog() == true;
    }

    private static void Say(Window? owner, string message)
    {
        if (owner is not null) MessageBox.Show(owner, message, EditLabel);
        else MessageBox.Show(message, EditLabel);
    }

    // ---- Rows ----

    private static (string Path, double? Width)? ReadRow(string table, int id)
    {
        if (!Tables.Contains(table)) throw new ArgumentException("Table inconnue : " + table);
        using var connection = new SqliteConnectionFactory().CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = table == "CanvasElement"
            ? "SELECT ImagePath, Width FROM CanvasElement WHERE Id = $id;"
            : $"SELECT ImagePath, NULL FROM {table} WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        if (!reader.Read() || reader.IsDBNull(0)) return null;
        var path = reader.GetString(0);
        if (path.Length == 0) return null;
        return (path, reader.IsDBNull(1) ? null : reader.GetDouble(1));
    }

    // Writes the new path (and the size columns the table has), then lets the sync carry it: rows are compared by
    // content, and a newly referenced file is sent by itself.
    private static ImageEditOutcome Apply(string table, int id, string path, int width, int height, double? canvasWidth, bool revert = false)
    {
        double? canvasHeight = null;
        using (var connection = new SqliteConnectionFactory().CreateOpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$path", path);
            if (table == "RefImage" && width > 0 && height > 0)
            {
                command.CommandText = "UPDATE RefImage SET ImagePath = $path, Width = $w, Height = $h WHERE Id = $id;";
                command.Parameters.AddWithValue("$w", width);
                command.Parameters.AddWithValue("$h", height);
            }
            else if (table == "CanvasElement" && !revert && canvasWidth is > 0 && width > 0)
            {
                // The on-canvas box keeps its width and takes the new picture's shape.
                canvasHeight = canvasWidth.Value * height / width;
                command.CommandText = "UPDATE CanvasElement SET ImagePath = $path, Height = $h WHERE Id = $id;";
                command.Parameters.AddWithValue("$h", canvasHeight.Value);
            }
            else
            {
                command.CommandText = $"UPDATE {table} SET ImagePath = $path WHERE Id = $id;";
            }
            command.ExecuteNonQuery();
        }
        _ = SyncService.Instance.RunAsync();
        return new ImageEditOutcome(path, width, height, canvasHeight);
    }

    // ---- Decoding ----

    /// <summary>The file decoded upright (EXIF orientation applied), opaque BGRA. Null when it cannot be read.</summary>
    public static SKBitmap? DecodeOriented(string fullPath)
    {
        var (bitmap, origin) = DecodeRaw(fullPath);
        if (bitmap is null) return null;
        if (origin == SKEncodedOrigin.TopLeft && bitmap.AlphaType == SKAlphaType.Opaque) return bitmap;
        using (bitmap)
        {
            var matrix = OriginMatrix(origin, bitmap.Width, bitmap.Height, out var w, out var h);
            return Draw(bitmap, matrix, w, h);
        }
    }

    // Skia first; Windows' own codecs (WIC: HEIC when its extension is installed, among others) when Skia cannot.
    private static (SKBitmap? Bitmap, SKEncodedOrigin Origin) DecodeRaw(string fullPath)
    {
        using (var codec = SKCodec.Create(fullPath))
        {
            if (codec is not null)
            {
                var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
                var bitmap = new SKBitmap(info);
                var result = codec.GetPixels(info, bitmap.GetPixels());
                if (result is SKCodecResult.Success or SKCodecResult.IncompleteInput) return (bitmap, codec.EncodedOrigin);
                bitmap.Dispose();
            }
        }
        try
        {
            using var stream = File.OpenRead(fullPath);
            var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
            var origin = SKEncodedOrigin.TopLeft;
            try
            {
                if ((frame.Metadata as BitmapMetadata)?.GetQuery("System.Photo.Orientation") is ushort o && o is >= 1 and <= 8)
                    origin = (SKEncodedOrigin)o;
            }
            catch { /* no metadata: upright */ }
            var converted = new FormatConvertedBitmap(frame, PixelFormats.Pbgra32, null, 0);
            var bitmap = new SKBitmap(new SKImageInfo(converted.PixelWidth, converted.PixelHeight, SKColorType.Bgra8888, SKAlphaType.Premul));
            var bytes = new byte[bitmap.ByteCount];
            converted.CopyPixels(bytes, bitmap.RowBytes, 0);
            Marshal.Copy(bytes, 0, bitmap.GetPixels(), bytes.Length);
            return (bitmap, origin);
        }
        catch
        {
            return (null, SKEncodedOrigin.TopLeft);
        }
    }

    /// <summary>Upright pixel size of a file (orientation applied), or (0, 0).</summary>
    private static (int Width, int Height) ReadOrientedSize(string fullPath)
    {
        using (var codec = SKCodec.Create(fullPath))
        {
            if (codec is not null)
            {
                var swap = (int)codec.EncodedOrigin >= 5;
                return swap ? (codec.Info.Height, codec.Info.Width) : (codec.Info.Width, codec.Info.Height);
            }
        }
        using var bitmap = DecodeOriented(fullPath);
        return bitmap is null ? (0, 0) : (bitmap.Width, bitmap.Height);
    }

    // ---- Geometry ----

    // Maps a w×h source onto its upright form for each EXIF orientation (1..8).
    private static SKMatrix OriginMatrix(SKEncodedOrigin origin, int w, int h, out int outW, out int outH)
    {
        var swap = (int)origin >= 5;
        outW = swap ? h : w;
        outH = swap ? w : h;
        return origin switch
        {
            SKEncodedOrigin.TopRight => new SKMatrix(-1, 0, w, 0, 1, 0, 0, 0, 1),     // mirror
            SKEncodedOrigin.BottomRight => new SKMatrix(-1, 0, w, 0, -1, h, 0, 0, 1), // 180°
            SKEncodedOrigin.BottomLeft => new SKMatrix(1, 0, 0, 0, -1, h, 0, 0, 1),   // flip
            SKEncodedOrigin.LeftTop => new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1),       // transpose
            SKEncodedOrigin.RightTop => new SKMatrix(0, -1, h, 1, 0, 0, 0, 0, 1),     // 90° clockwise
            SKEncodedOrigin.RightBottom => new SKMatrix(0, -1, h, -1, 0, w, 0, 0, 1), // transverse
            SKEncodedOrigin.LeftBottom => new SKMatrix(0, 1, 0, -1, 0, w, 0, 0, 1),   // 90° anticlockwise
            _ => SKMatrix.Identity,
        };
    }

    /// <summary>Quarter turns clockwise (0..3) as an orientation.</summary>
    private static SKEncodedOrigin TurnOrigin(int quarterTurns) => (((quarterTurns % 4) + 4) % 4) switch
    {
        1 => SKEncodedOrigin.RightTop,
        2 => SKEncodedOrigin.BottomRight,
        3 => SKEncodedOrigin.LeftBottom,
        _ => SKEncodedOrigin.TopLeft,
    };

    /// <summary>The bitmap turned by quarter turns clockwise (a new bitmap).</summary>
    public static SKBitmap Rotate(SKBitmap source, int quarterTurns)
    {
        var matrix = OriginMatrix(TurnOrigin(quarterTurns), source.Width, source.Height, out var w, out var h);
        return Draw(source, matrix, w, h);
    }

    /// <summary>Scaled down so its longer side is at most `maxSide` (a new bitmap; a copy when already small).</summary>
    public static SKBitmap Shrink(SKBitmap source, int maxSide)
    {
        var scale = Math.Min(1.0, maxSide / (double)Math.Max(source.Width, source.Height));
        var w = Math.Max(1, (int)Math.Round(source.Width * scale));
        var h = Math.Max(1, (int)Math.Round(source.Height * scale));
        return Draw(source, SKMatrix.CreateScale((float)scale, (float)scale), w, h);
    }

    // Draws `source` through `matrix` onto a new opaque w×h bitmap (white under any transparency: the result is a JPEG).
    private static SKBitmap Draw(SKBitmap source, SKMatrix matrix, int w, int h)
    {
        var target = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(target);
        canvas.Clear(SKColors.White);
        canvas.SetMatrix(matrix);
        using var image = SKImage.FromBitmap(source);
        canvas.DrawImage(image, 0, 0, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        canvas.Flush();
        return target;
    }

    // ---- Colour ----

    /// <summary>
    /// Contrast then saturation, in place, on an opaque BGRA bitmap. Contrast scales around mid-grey by 1 + c/100
    /// (0 at -100, 2× at +100); saturation mixes with the luminance (the weights of Android's ColorMatrix, like the phone)
    /// by s = 1 + v/100 (greyscale at -100, 2× at +100).
    /// </summary>
    public static void AdjustColours(SKBitmap bitmap, int contrast, int saturation)
    {
        if (contrast == 0 && saturation == 0) return;
        var k = (float)Math.Clamp(1 + contrast / 100.0, 0, 2);
        var s = (float)Math.Clamp(1 + saturation / 100.0, 0, 2);
        const float lr = 0.213f, lg = 0.715f, lb = 0.072f; // Android ColorMatrix.setSaturation, as on the phone
        // Saturation matrix rows (R, G, B) applied to the contrast result.
        float rr = lr * (1 - s) + s, rg = lg * (1 - s), rb = lb * (1 - s);
        float gr = lr * (1 - s), gg = lg * (1 - s) + s, gb = lb * (1 - s);
        float br = lr * (1 - s), bg = lg * (1 - s), bb = lb * (1 - s) + s;
        var offset = 128f * (1 - k);

        var pixels = new byte[bitmap.ByteCount];
        var address = bitmap.GetPixels();
        Marshal.Copy(address, pixels, 0, pixels.Length);
        var rowBytes = bitmap.RowBytes;
        var width = bitmap.Width;
        Parallel.For(0, bitmap.Height, y =>
        {
            var i = y * rowBytes;
            for (var x = 0; x < width; x++, i += 4)
            {
                var b = pixels[i] * k + offset;
                var g = pixels[i + 1] * k + offset;
                var r = pixels[i + 2] * k + offset;
                pixels[i] = Clamp(br * r + bg * g + bb * b);
                pixels[i + 1] = Clamp(gr * r + gg * g + gb * b);
                pixels[i + 2] = Clamp(rr * r + rg * g + rb * b);
            }
        });
        Marshal.Copy(pixels, 0, address, pixels.Length);
    }

    private static byte Clamp(float v) => v <= 0 ? (byte)0 : v >= 255 ? (byte)255 : (byte)(v + 0.5f);

    // ---- Saving ----

    /// <summary>
    /// Full resolution, in the agreed order: orientation → rotation → crop (normalised rectangle on the rotated
    /// picture) → contrast/saturation. Written as JPEG quality 92. Returns the new pixel size.
    /// </summary>
    private static (int Width, int Height) RenderAndSave(string sourceFull, string targetFull, ImageEditSettings settings)
    {
        using var oriented = DecodeOriented(sourceFull) ?? throw new IOException("l'image ne peut plus être lue.");
        using var turned = settings.QuarterTurns % 4 == 0 ? null : Rotate(oriented, settings.QuarterTurns);
        var rotated = turned ?? oriented;
        SKBitmap result = rotated;
        if (settings.Crop is { } crop)
        {
            var x = (int)Math.Round(crop.X * rotated.Width);
            var y = (int)Math.Round(crop.Y * rotated.Height);
            var w = (int)Math.Round(crop.Width * rotated.Width);
            var h = (int)Math.Round(crop.Height * rotated.Height);
            x = Math.Clamp(x, 0, rotated.Width - 1);
            y = Math.Clamp(y, 0, rotated.Height - 1);
            w = Math.Clamp(w, 1, rotated.Width - x);
            h = Math.Clamp(h, 1, rotated.Height - y);
            result = Draw(rotated, SKMatrix.CreateTranslation(-x, -y), w, h);
        }
        try
        {
            AdjustColours(result, settings.Contrast, settings.Saturation);
            Directory.CreateDirectory(Path.GetDirectoryName(targetFull)!);
            using (var data = result.Encode(SKEncodedImageFormat.Jpeg, JpegQuality))
            {
                if (data is null) throw new IOException("l'encodage JPEG a échoué.");
                using var file = File.Create(targetFull);
                data.SaveTo(file);
            }
            return (result.Width, result.Height);
        }
        finally
        {
            if (!ReferenceEquals(result, rotated)) result.Dispose();
        }
    }

    /// <summary>An opaque BGRA Skia bitmap as a frozen WPF image.</summary>
    public static BitmapSource ToBitmapSource(SKBitmap bitmap)
    {
        var source = BitmapSource.Create(bitmap.Width, bitmap.Height, 96, 96, PixelFormats.Pbgra32, null,
            bitmap.GetPixels(), bitmap.ByteCount, bitmap.RowBytes);
        source.Freeze();
        return source;
    }
}

/// <summary>What the editor window asks for. `Crop` is normalised (0..1) on the rotated picture; null = whole picture.</summary>
public sealed record ImageEditSettings(int QuarterTurns, Rect? Crop, int Contrast, int Saturation);
