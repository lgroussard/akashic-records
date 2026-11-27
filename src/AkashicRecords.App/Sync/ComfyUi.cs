using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using AkashicRecords.App.Views;
using AkashicRecords.Sync;
using SkiaSharp;

namespace AkashicRecords.App.Sync;

/// <summary>
/// The AI edits of shared computing ("edit" and "restore" jobs of the hub, model FLUX Kontext), run on this computer
/// through a local ComfyUI (portable install, chosen in the computing window). ComfyUI is started hidden when a job
/// comes and stopped after a while without work, so its memory is given back. Nothing stays in its folders: the files
/// a job puts there are deleted once the result is on the hub, where every device sees it.
///
/// The three recipes (validated by hand on real photos before being written here):
///  - edit, no mask: Kontext redraws the whole picture following the sentence (about 1 megapixel), then a large
///    original gets its size back with Nomos ×4;
///  - edit with a mask (painted and/or found from a text by SAM 3.1): the zone, zoomed to 1024 px, is redrawn there
///    only and pasted back, so the rest of the picture stays identical; with "erase", LaMa empties the zone first;
///  - restore: Kontext cleans the picture up (blur, noise, compression), then Nomos ×4.
/// </summary>
public sealed class ComfyUi
{
    public static ComfyUi Instance { get; } = new();

    /// <summary>The model name the hub gives these jobs (its ComputeCatalog.Kontext).</summary>
    public const string Model = "flux-kontext";

    private const string Url = "http://127.0.0.1:8188";
    private static readonly TimeSpan StopAfter = TimeSpan.FromMinutes(10);

    public const string RestorePrompt = "Restore this photo in high definition: sharp, highly detailed, remove blur, noise and compression artifacts. " +
                                        "Keep the content, the people, the framing and the colors exactly the same.";

    // What the recipes need inside the ComfyUI folder.
    private static readonly string[] Required =
    [
        @"ComfyUI\models\diffusion_models\flux1-dev-kontext_fp8_scaled.safetensors",
        @"ComfyUI\models\text_encoders\clip_l.safetensors",
        @"ComfyUI\models\text_encoders\t5xxl_fp8_e4m3fn_scaled.safetensors",
        @"ComfyUI\models\vae\ae.safetensors",
        @"ComfyUI\models\upscale_models\4xNomosWebPhoto_RealPLKSR.safetensors",
        @"ComfyUI\models\inpaint\big-lama.pt",
        @"ComfyUI\models\checkpoints\sam3.1_multiplex_fp16.safetensors",
        @"ComfyUI\custom_nodes\ComfyUI-Inpaint-CropAndStitch",
        @"ComfyUI\custom_nodes\comfyui-inpaint-nodes",
    ];

    private readonly HttpClient _http = new() { BaseAddress = new Uri(Url), Timeout = TimeSpan.FromMinutes(2) };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _process; // the ComfyUI this app started (one already running is used, never stopped)
    private DateTime _lastUse = DateTime.MinValue;
    private Timer? _idleTimer;

    /// <summary>The portable ComfyUI folder (the one holding python_embeded and ComfyUI), or null when none is set.</summary>
    public string? Dir => ComputeService.Instance.ComfyUiDir;

    /// <summary>Why AI edits cannot run on this computer, or null when they can.</summary>
    public string? Missing()
    {
        var dir = Dir;
        if (string.IsNullOrWhiteSpace(dir)) return "Dossier ComfyUI non choisi.";
        if (!File.Exists(Path.Combine(dir, @"python_embeded\python.exe")) || !File.Exists(Path.Combine(dir, @"ComfyUI\main.py")))
            return "Ce dossier n'est pas un ComfyUI portable (python_embeded et ComfyUI attendus).";
        var absent = Required.FirstOrDefault(r => !File.Exists(Path.Combine(dir, r)) && !Directory.Exists(Path.Combine(dir, r)));
        return absent is null ? null : "Il manque dans ComfyUI : " + absent;
    }

    public bool Available => Missing() is null;

    /// <summary>Does one job: returns the paths of its versions (PNG, one per try) in `work`.</summary>
    public async Task<List<string>> RunAsync(HubClient hub, HubComputeJob job, string work)
    {
        await _gate.WaitAsync();
        var uploaded = new List<string>();
        var produced = new List<string>();
        try
        {
            var (prompt, maskHash, maskText, erase, tries, engine) = ReadParams(job);
            if (engine == "sdxl" && !File.Exists(Path.Combine(Dir!, RealVisXlFile)))
                throw new InvalidOperationException("RealVisXL n'est pas dans ce ComfyUI (" + RealVisXlFile + ").");

            // The picture upright (orientation applied), with the painted zone as transparency: ComfyUI's LoadImage
            // turns transparency into its mask.
            var source = Path.Combine(work, "input" + Path.GetExtension(job.InputName).ToLowerInvariant());
            if (!await hub.DownloadFileAsync(job.InputHash, source)) throw new InvalidOperationException("L'image n'est plus sur le serveur.");
            string? maskFile = null;
            if (maskHash is not null)
            {
                maskFile = Path.Combine(work, "mask.png");
                if (!await hub.DownloadFileAsync(maskHash, maskFile)) throw new InvalidOperationException("Le masque n'est plus sur le serveur.");
            }
            var name = $"akashic-{job.Id}.png";
            var prepared = Path.Combine(work, name);
            var (width, height) = Prepare(source, maskFile, prepared);

            await EnsureRunningAsync();
            uploaded.Add(await UploadAsync(prepared, name));

            // One run per try, each with its own seed: the versions differ, the user keeps one.
            var results = new List<string>();
            for (var t = 0; t < tries; t++)
            {
                var graph = job.Kind == "restore" ? Restore(name, job.Id)
                    : engine == "sdxl" ? EditSdxl(name, job.Id, prompt!, maskFile is not null, maskText, erase)
                    : Edit(name, job.Id, prompt!, maskFile is not null, maskText, erase, width, height);
                var (file, subfolder) = await QueueAndWaitAsync(graph);
                produced.Add(Path.Combine(subfolder, file));

                var result = Path.Combine(work, $"result-{t + 1}.png");
                var url = $"/view?filename={Uri.EscapeDataString(file)}&subfolder={Uri.EscapeDataString(subfolder)}&type=output";
                await using (var stream = await _http.GetStreamAsync(url))
                await using (var target = File.Create(result))
                    await stream.CopyToAsync(target);
                results.Add(result);
            }
            return results;
        }
        finally
        {
            _lastUse = DateTime.UtcNow;
            Cleanup(uploaded, produced);
            _gate.Release();
        }
    }

    private const string RealVisXlFile = @"ComfyUI\models\checkpoints\realvisxl_v5_fp16.safetensors";

    private static (string? Prompt, string? MaskHash, string? MaskText, bool Erase, int Tries, string Engine) ReadParams(HubComputeJob job)
    {
        using var doc = JsonDocument.Parse(job.Params ?? "{}");
        var r = doc.RootElement;
        var tries = r.TryGetProperty("tries", out var n) && n.ValueKind == JsonValueKind.Number ? Math.Clamp(n.GetInt32(), 1, 4) : 1;
        if (job.Kind == "restore") return (RestorePrompt, null, null, false, tries, "kontext");
        string? Text(string key) => r.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        var prompt = Text("prompt");
        if (string.IsNullOrWhiteSpace(prompt)) throw new InvalidOperationException("Demande de modification sans phrase.");
        return (prompt, Text("mask_hash"), Text("mask_text"), r.TryGetProperty("erase", out var e) && e.ValueKind == JsonValueKind.True,
            tries, Text("engine") ?? "kontext");
    }

    /// <summary>Writes the upright picture as a PNG, the mask (white = zone, any size) as transparency. Returns its size.</summary>
    private static (int Width, int Height) Prepare(string source, string? maskFile, string target)
    {
        using var picture = ImageEditing.DecodeOriented(source) ?? throw new InvalidOperationException("Format d'image illisible sur cet ordinateur.");
        if (maskFile is not null)
        {
            using var mask = SKBitmap.Decode(maskFile) ?? throw new InvalidOperationException("Masque illisible.");
            using var scaled = mask.Resize(new SKImageInfo(picture.Width, picture.Height), new SKSamplingOptions(SKFilterMode.Linear));
            using var withAlpha = new SKBitmap(new SKImageInfo(picture.Width, picture.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul));
            for (var y = 0; y < picture.Height; y++)
                for (var x = 0; x < picture.Width; x++)
                {
                    var c = picture.GetPixel(x, y);
                    var m = scaled.GetPixel(x, y);
                    var zone = Math.Max(m.Red, Math.Max(m.Green, m.Blue)) * m.Alpha / 255;
                    withAlpha.SetPixel(x, y, c.WithAlpha((byte)(255 - zone)));
                }
            Save(withAlpha, target);
        }
        else Save(picture, target);
        return (picture.Width, picture.Height);
    }

    private static void Save(SKBitmap bitmap, string path)
    {
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100) ?? throw new IOException("Encodage PNG impossible.");
        using var file = File.Create(path);
        data.SaveTo(file);
    }

    // ------------------------------------------------------------------ the recipes (ComfyUI API graphs)

    private static JsonObject Node(string type, object inputs) =>
        new() { ["class_type"] = type, ["inputs"] = JsonSerializer.SerializeToNode(inputs) };

    private static JsonArray Out(string node, int index) => new(node, index);

    private static long Seed() => Random.Shared.NextInt64(1, 1L << 48);

    // The Kontext part, shared: loaders, the sentence, and a sampler fed with `latent` (the reference is `reference`).
    private static void Kontext(JsonObject g, string prompt, JsonArray reference, JsonArray latent)
    {
        g["30"] = Node("UNETLoader", new { unet_name = "flux1-dev-kontext_fp8_scaled.safetensors", weight_dtype = "default" });
        g["31"] = Node("DualCLIPLoader", new { clip_name1 = "clip_l.safetensors", clip_name2 = "t5xxl_fp8_e4m3fn_scaled.safetensors", type = "flux" });
        g["32"] = Node("VAELoader", new { vae_name = "ae.safetensors" });
        g["36"] = Node("CLIPTextEncode", new { text = prompt, clip = Out("31", 0) });
        g["37"] = Node("ReferenceLatent", new { conditioning = Out("36", 0), latent = reference });
        g["38"] = Node("FluxGuidance", new { conditioning = Out("37", 0), guidance = 2.5 });
        g["39"] = Node("ConditioningZeroOut", new { conditioning = Out("36", 0) });
        g["40"] = Node("KSampler", new
        {
            model = Out("30", 0), positive = Out("38", 0), negative = Out("39", 0), latent_image = latent, seed = Seed(),
            steps = 20, cfg = 1.0, sampler_name = "euler", scheduler = "simple", denoise = 1.0,
        });
        g["41"] = Node("VAEDecode", new { samples = Out("40", 0), vae = Out("32", 0) });
    }

    // Whole picture through Kontext (scaled to its ~1 megapixel), decoded in "41".
    private static void WholePicture(JsonObject g, string prompt)
    {
        g["34"] = Node("FluxKontextImageScale", new { image = Out("1", 0) });
        g["35"] = Node("VAEEncode", new { pixels = Out("34", 0), vae = Out("32", 0) });
        Kontext(g, prompt, Out("35", 0), Out("35", 0));
    }

    private static void Nomos(JsonObject g, JsonArray image)
    {
        g["60"] = Node("UpscaleModelLoader", new { model_name = "4xNomosWebPhoto_RealPLKSR.safetensors" });
        g["61"] = Node("ImageUpscaleWithModel", new { upscale_model = Out("60", 0), image });
    }

    private static JsonObject Save(JsonObject g, JsonArray image, string jobId)
    {
        g["99"] = Node("SaveImage", new { images = image, filename_prefix = "akashic-" + jobId });
        return g;
    }

    private static JsonObject Restore(string name, string jobId)
    {
        var g = new JsonObject { ["1"] = Node("LoadImage", new { image = name }) };
        WholePicture(g, RestorePrompt);
        Nomos(g, Out("41", 0));
        return Save(g, Out("61", 0), jobId);
    }

    private static object Crop(JsonArray image, JsonArray mask, int target, int expand, int blend, double context, bool fillHoles) => new
    {
        image, mask, downscale_algorithm = "bilinear", upscale_algorithm = "bicubic",
        preresize = false, preresize_mode = "ensure minimum resolution", preresize_min_width = 1024, preresize_min_height = 1024,
        preresize_max_width = 16384, preresize_max_height = 16384, mask_fill_holes = fillHoles, mask_expand_pixels = expand, mask_invert = false,
        mask_blend_pixels = blend, mask_hipass_filter = 0.1, extend_for_outpainting = false, extend_up_factor = 1.0, extend_down_factor = 1.0,
        extend_left_factor = 1.0, extend_right_factor = 1.0, context_from_mask_extend_factor = context, output_resize_to_target_size = true,
        output_target_width = target, output_target_height = target, output_padding = "32", device_mode = "gpu (much faster)",
    };

    // The zone: painted (transparency of the upload), found from a text by SAM 3.1, or both added together; null if none.
    private static JsonArray? Zone(JsonObject g, bool painted, string? maskText)
    {
        JsonArray? mask = painted ? Out("1", 1) : null;
        if (string.IsNullOrWhiteSpace(maskText)) return mask;
        g["50"] = Node("CheckpointLoaderSimple", new { ckpt_name = "sam3.1_multiplex_fp16.safetensors" });
        g["51"] = Node("CLIPTextEncode", new { text = maskText, clip = Out("50", 1) });
        g["52"] = Node("SAM3_Detect", new { model = Out("50", 0), image = Out("1", 0), conditioning = Out("51", 0), threshold = 0.4, refine_iterations = 2, individual_masks = false });
        if (mask is null) return Out("52", 0);
        g["53"] = Node("MaskComposite", new { destination = Out("1", 1), source = Out("52", 0), x = 0, y = 0, operation = "add" });
        return Out("53", 0);
    }

    // LaMa empties the zone first (zoomed in at its own scale), when asked; returns the picture to draw on.
    private static JsonArray Erased(JsonObject g, JsonArray mask, bool erase)
    {
        if (!erase) return Out("1", 0);
        g["20"] = Node("INPAINT_LoadInpaintModel", new { model_name = "big-lama.pt" });
        g["21"] = Node("InpaintCropImproved", Crop(Out("1", 0), mask, 512, 10, 16, 2.5, true));
        g["22"] = Node("INPAINT_InpaintWithModel", new { inpaint_model = Out("20", 0), image = Out("21", 1), mask = Out("21", 2), seed = 0 });
        g["23"] = Node("InpaintStitchImproved", new { stitcher = Out("21", 0), inpainted_image = Out("22", 0) });
        return Out("23", 0);
    }

    /// <summary>
    /// The other engine, RealVisXL (SDXL), kept "just in case" next to Kontext: it only redraws a zone (inpainting,
    /// zoomed to 1024 px, soft edge, the rest identical), from the sentence as a description of what must be there.
    /// </summary>
    private static JsonObject EditSdxl(string name, string jobId, string prompt, bool painted, string? maskText, bool erase)
    {
        var g = new JsonObject { ["1"] = Node("LoadImage", new { image = name }) };
        var mask = Zone(g, painted, maskText) ?? throw new InvalidOperationException("RealVisXL a besoin d'une zone.");
        var basePicture = Erased(g, mask, erase);
        g["2"] = Node("CheckpointLoaderSimple", new { ckpt_name = "realvisxl_v5_fp16.safetensors" });
        g["3"] = Node("CLIPTextEncode", new { text = prompt + ", photo, natural light, realistic, detailed", clip = Out("2", 1) });
        g["4"] = Node("CLIPTextEncode", new { text = "blurry, lowres, deformed, watermark, text, signature, jpeg artifacts, cartoon", clip = Out("2", 1) });
        g["10"] = Node("InpaintCropImproved", Crop(basePicture, mask, 1024, 8, 32, 3.0, false));
        g["11"] = Node("DifferentialDiffusion", new { model = Out("2", 0) });
        g["12"] = Node("InpaintModelConditioning", new { positive = Out("3", 0), negative = Out("4", 0), vae = Out("2", 2), pixels = Out("10", 1), mask = Out("10", 2), noise_mask = true });
        g["5"] = Node("KSampler", new
        {
            model = Out("11", 0), positive = Out("12", 0), negative = Out("12", 1), latent_image = Out("12", 2), seed = Seed(),
            steps = 30, cfg = 6.0, sampler_name = "dpmpp_2m", scheduler = "karras", denoise = 1.0,
        });
        g["6"] = Node("VAEDecode", new { samples = Out("5", 0), vae = Out("2", 2) });
        g["13"] = Node("InpaintStitchImproved", new { stitcher = Out("10", 0), inpainted_image = Out("6", 0) });
        return Save(g, Out("13", 0), jobId);
    }

    private static JsonObject Edit(string name, string jobId, string prompt, bool painted, string? maskText, bool erase, int width, int height)
    {
        var g = new JsonObject { ["1"] = Node("LoadImage", new { image = name }) };

        // The zone: painted (transparency of the upload), found from a text by SAM 3.1, or both added together.
        JsonArray? mask = Zone(g, painted, maskText);

        if (mask is null)
        {
            // No zone: the whole picture follows the sentence; a large original gets its size back.
            WholePicture(g, prompt);
            if ((long)width * height <= 1_300_000) return Save(g, Out("41", 0), jobId);
            Nomos(g, Out("41", 0));
            g["62"] = Node("ImageScale", new { image = Out("61", 0), upscale_method = "lanczos", width, height, crop = "disabled" });
            return Save(g, Out("62", 0), jobId);
        }

        // With a zone: LaMa empties it first if asked (zoomed in at its own scale), then Kontext draws the sentence in
        // the zone only, zoomed to 1024 px, pasted back with a soft edge: the rest stays identical, at full size.
        var basePicture = Erased(g, mask, erase);
        g["10"] = Node("InpaintCropImproved", Crop(basePicture, (JsonArray)mask.DeepClone(), 1024, 8, 32, 3.0, false));
        g["35"] = Node("VAEEncode", new { pixels = Out("10", 1), vae = Out("32", 0) });
        g["43"] = Node("SetLatentNoiseMask", new { samples = Out("35", 0), mask = Out("10", 2) });
        Kontext(g, prompt, Out("35", 0), Out("43", 0));
        g["13"] = Node("InpaintStitchImproved", new { stitcher = Out("10", 0), inpainted_image = Out("41", 0) });
        return Save(g, Out("13", 0), jobId);
    }

    // ------------------------------------------------------------------ talking to ComfyUI

    private async Task<bool> UpAsync()
    {
        try
        {
            using var r = await _http.GetAsync("/system_stats");
            return r.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { return false; }
    }

    private async Task EnsureRunningAsync()
    {
        if (await UpAsync()) return;
        var dir = Dir ?? throw new InvalidOperationException("Dossier ComfyUI non choisi.");
        if (_process is null || _process.HasExited)
        {
            var log = Path.Combine(AppContext.BaseDirectory, "compute", "comfyui.log");
            Directory.CreateDirectory(Path.GetDirectoryName(log)!);
            var psi = new ProcessStartInfo(Path.Combine(dir, @"python_embeded\python.exe"))
            {
                WorkingDirectory = dir, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            foreach (var a in new[] { "-s", @"ComfyUI\main.py", "--windows-standalone-build", "--listen", "127.0.0.1", "--port", "8188", "--disable-auto-launch" })
                psi.ArgumentList.Add(a);
            _process = Process.Start(psi) ?? throw new InvalidOperationException("ComfyUI n'a pas pu démarrer.");
            var writer = new StreamWriter(log, append: false) { AutoFlush = true };
            _process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (writer) writer.WriteLine(e.Data); };
            _process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (writer) writer.WriteLine(e.Data); };
            _process.Exited += (_, _) => { lock (writer) writer.Dispose(); };
            _process.EnableRaisingEvents = true;
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
            if (_idleTimer is null)
            {
                _idleTimer = new Timer(_ => StopIfIdle(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
                AppDomain.CurrentDomain.ProcessExit += (_, _) => Stop(); // never left running once Akashic is closed
            }
        }
        for (var i = 0; i < 180; i++)
        {
            if (_process.HasExited) throw new InvalidOperationException("ComfyUI s'est arrêté au démarrage (voir compute\\comfyui.log).");
            await Task.Delay(1000);
            if (await UpAsync()) return;
        }
        throw new InvalidOperationException("ComfyUI ne répond pas après 3 minutes.");
    }

    // The ComfyUI this app started is stopped after a while without work (its memory goes back to the computer).
    private void StopIfIdle()
    {
        if (_process is null || _process.HasExited || DateTime.UtcNow - _lastUse < StopAfter || _gate.CurrentCount == 0) return;
        try { _process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        _process = null;
    }

    /// <summary>Stops the ComfyUI this app started (when the app closes).</summary>
    public void Stop()
    {
        try { if (_process is { HasExited: false }) _process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
    }

    private async Task<string> UploadAsync(string path, string name)
    {
        using var form = new MultipartFormDataContent();
        var content = new ByteArrayContent(await File.ReadAllBytesAsync(path));
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
        form.Add(content, "image", name);
        form.Add(new StringContent("true"), "overwrite");
        using var r = await _http.PostAsync("/upload/image", form);
        r.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("name").GetString()!;
    }

    private async Task<(string File, string Subfolder)> QueueAndWaitAsync(JsonObject graph)
    {
        var body = new JsonObject { ["prompt"] = graph }.ToJsonString();
        using var queued = await _http.PostAsync("/prompt", new StringContent(body, System.Text.Encoding.UTF8, "application/json"));
        var answer = await queued.Content.ReadAsStringAsync();
        if (!queued.IsSuccessStatusCode) throw new InvalidOperationException("ComfyUI a refusé le travail : " + Shorten(answer));
        string id;
        using (var doc = JsonDocument.Parse(answer)) id = doc.RootElement.GetProperty("prompt_id").GetString()!;

        var started = DateTime.UtcNow;
        while (DateTime.UtcNow - started < TimeSpan.FromMinutes(20))
        {
            await Task.Delay(1500);
            using var doc = JsonDocument.Parse(await _http.GetStringAsync("/history/" + id));
            if (!doc.RootElement.TryGetProperty(id, out var entry)) continue;
            var status = entry.TryGetProperty("status", out var s) ? s : default;
            if (status.ValueKind == JsonValueKind.Object && status.TryGetProperty("status_str", out var st) && st.GetString() == "error")
                throw new InvalidOperationException("ComfyUI a échoué : " + Shorten(status.GetRawText()));
            if (entry.TryGetProperty("outputs", out var outputs) && outputs.TryGetProperty("99", out var save)
                && save.TryGetProperty("images", out var images) && images.GetArrayLength() > 0)
            {
                var image = images[0];
                return (image.GetProperty("filename").GetString()!, image.TryGetProperty("subfolder", out var sub) ? sub.GetString() ?? "" : "");
            }
        }
        throw new InvalidOperationException("ComfyUI n'a pas fini en 20 minutes.");
    }

    private static string Shorten(string s) => s.Length > 300 ? s[..300] + "…" : s;

    // The files a job left in ComfyUI's folders: the upload and the result are deleted (they live on the hub).
    private void Cleanup(List<string> uploaded, List<string> produced)
    {
        var dir = Dir;
        if (dir is null) return;
        foreach (var name in uploaded) TryDelete(Path.Combine(dir, @"ComfyUI\input", name));
        foreach (var name in produced) TryDelete(Path.Combine(dir, @"ComfyUI\output", name));
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
