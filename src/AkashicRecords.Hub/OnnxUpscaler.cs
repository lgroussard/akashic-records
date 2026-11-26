using System.IO;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;
using SessionOptions = Microsoft.ML.OnnxRuntime.SessionOptions;

namespace AkashicRecords.Hub;

/// <summary>
/// Upscales an image ×4 with an ONNX model (NomosWebPhoto). The same file is compiled into the hub (processor) and
/// into the desktop app (graphics card through DirectML, processor if that fails). The image goes through in tiles
/// with a margin, so memory stays small whatever its size; the margins are cut off when the tiles are put together.
/// </summary>
public sealed class OnnxUpscaler : IDisposable
{
    /// <summary>Longer side above which an image is first reduced: ×4 of a big photo is huge and adds nothing.</summary>
    public const int MaxInputSide = 1600;
    public const int Scale = 4;
    public static readonly string[] InputExtensions = [".jpg", ".jpeg", ".png", ".webp"];

    private readonly InferenceSession _session;
    private readonly string _input;
    private readonly int _fixed; // > 0 when the model only takes tiles of one size

    public bool OnGpu { get; }

    private OnnxUpscaler(InferenceSession session, bool onGpu)
    {
        _session = session;
        OnGpu = onGpu;
        _input = session.InputMetadata.Keys.First();
        var dims = session.InputMetadata[_input].Dimensions;
        _fixed = dims.Length == 4 && dims[2] > 0 ? dims[2] : 0;
    }

    /// <summary>Loads a model; on the graphics card if asked and possible, else on the processor.</summary>
    public static OnnxUpscaler Load(string modelPath, bool preferGpu)
    {
        if (preferGpu)
        {
            try
            {
                var gpu = new SessionOptions();
                gpu.AppendExecutionProvider_DML(0);
                return new OnnxUpscaler(new InferenceSession(modelPath, gpu), true);
            }
            catch (Exception ex) when (ex is OnnxRuntimeException or EntryPointNotFoundException or DllNotFoundException) { }
        }
        var cpu = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
        return new OnnxUpscaler(new InferenceSession(modelPath, cpu), false);
    }

    public static string ResultName(string inputName) => $"{Path.GetFileNameWithoutExtension(inputName)}-x4.png";

    /// <summary>Reads `input` (jpg, png, webp), writes the ×4 result to `output` as PNG.</summary>
    public void Run(string input, string output, CancellationToken ct = default)
    {
        using var decoded = SKBitmap.Decode(input) ?? throw new InvalidOperationException("Image illisible.");
        var longer = Math.Max(decoded.Width, decoded.Height);
        var f = longer > MaxInputSide ? (double)MaxInputSide / longer : 1.0;
        int w = Math.Max(1, (int)Math.Round(decoded.Width * f)), h = Math.Max(1, (int)Math.Round(decoded.Height * f));
        // Drawn once onto plain RGBA (resized if too big, transparency flattened on white): one known layout to read.
        using var rgba = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(rgba))
        using (var image = SKImage.FromBitmap(decoded))
        {
            canvas.Clear(SKColors.White);
            canvas.DrawImage(image, new SKRect(0, 0, w, h), new SKSamplingOptions(SKCubicResampler.Mitchell));
        }
        var src = rgba.Bytes;
        int srcStride = rgba.RowBytes;

        int ow = w * Scale, oh = h * Scale;
        var dst = new byte[ow * oh * 4];
        int tile = _fixed > 0 ? _fixed : 192, margin = _fixed > 0 ? Math.Max(4, _fixed / 8) : 16;
        int step = tile - 2 * margin;

        for (int ty = 0; ty < h; ty += step)
        for (int tx = 0; tx < w; tx += step)
        {
            ct.ThrowIfCancellationRequested();
            // The tile with its margin, cut at the image's edges.
            int x0 = Math.Max(0, tx - margin), y0 = Math.Max(0, ty - margin);
            int x1 = Math.Min(w, tx + step + margin), y1 = Math.Min(h, ty + step + margin);
            int tw = x1 - x0, th = y1 - y0;
            // Sizes the model accepts: its fixed size, or a multiple of 16 (the edge pixels are repeated).
            int pw = _fixed > 0 ? _fixed : (tw + 15) / 16 * 16, ph = _fixed > 0 ? _fixed : (th + 15) / 16 * 16;
            var t = new DenseTensor<float>([1, 3, ph, pw]);
            for (int y = 0; y < ph; y++)
            for (int x = 0; x < pw; x++)
            {
                int i = (y0 + Math.Min(y, th - 1)) * srcStride + (x0 + Math.Min(x, tw - 1)) * 4;
                t[0, 0, y, x] = src[i] / 255f; t[0, 1, y, x] = src[i + 1] / 255f; t[0, 2, y, x] = src[i + 2] / 255f;
            }
            using var result = _session.Run([NamedOnnxValue.CreateFromTensor(_input, t)]);
            var o = result.First().AsTensor<float>();

            // Only the inside of the tile is kept (the margin is there for context, not to be shown).
            int keepX0 = tx, keepY0 = ty, keepX1 = Math.Min(w, tx + step), keepY1 = Math.Min(h, ty + step);
            for (int y = keepY0 * Scale; y < keepY1 * Scale; y++)
            for (int x = keepX0 * Scale; x < keepX1 * Scale; x++)
            {
                int lx = x - x0 * Scale, ly = y - y0 * Scale;
                int j = (y * ow + x) * 4;
                dst[j] = Byte(o[0, 0, ly, lx]); dst[j + 1] = Byte(o[0, 1, ly, lx]); dst[j + 2] = Byte(o[0, 2, ly, lx]); dst[j + 3] = 255;
            }
        }

        var info = new SKImageInfo(ow, oh, SKColorType.Rgba8888, SKAlphaType.Opaque);
        using var pixels = SKData.CreateCopy(dst);
        using var outImage = SKImage.FromPixels(info, pixels, ow * 4);
        using var png = outImage.Encode(SKEncodedImageFormat.Png, 100);
        using var file = File.Create(output);
        png.SaveTo(file);
    }

    private static byte Byte(float v) => (byte)Math.Clamp(MathF.Round(v * 255f), 0, 255);

    public void Dispose() => _session.Dispose();
}
