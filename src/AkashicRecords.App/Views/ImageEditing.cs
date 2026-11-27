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
//  - no file is ever deleted: the original (folder + <root>) is what « Revenir à l'original » points back to.
public static class ImageEditing
{
    public const string EditLabel = "Modifier";
    public const string RevertLabel = "Revenir à l'original";
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

    /// <summary>A fresh edit path next to the current file, not yet taken on this computer.</summary>
    public static string NewEditPath(string storedPath)
    {
        var (folder, name) = Split(storedPath);
        var root = RootName(name);
        while (true)
        {
            var hex = Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
            var candidate = folder + root + EditMarker + hex + ".jpg";
            if (!File.Exists(MediaStorage.ResolveFullPath(candidate))) return candidate;
        }
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

            var window = new ImageEditWindow(oriented, row.Value.Path) { Owner = owner };
            if (window.ShowDialog() != true || window.Result is not { } settings) return null;

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
