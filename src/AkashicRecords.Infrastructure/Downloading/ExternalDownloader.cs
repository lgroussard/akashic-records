using System.Diagnostics;

namespace AkashicRecords.Infrastructure.Downloading;

// Launches a user-installed command-line downloader (e.g. yt-dlp) as a child process.
// Akashic does not bundle the tool: the user installs/configures it, and is responsible
// for what they download and for respecting each source's terms of use.
public sealed class ExternalDownloader
{
    // Output carries the trimmed stdout (e.g. the direct stream URL in yt-dlp "print" mode).
    public sealed record Result(bool Success, string? Error, string? Output = null);

    public async Task<Result> DownloadAsync(
        string toolPath,
        string argumentsTemplate,
        string url,
        string workingDirectory,
        string? ffmpegDirectory)
    {
        try
        {
            Directory.CreateDirectory(workingDirectory);

            var arguments = argumentsTemplate;
            if (!string.IsNullOrWhiteSpace(ffmpegDirectory))
            {
                arguments += $" --ffmpeg-location \"{ffmpegDirectory}\"";
            }
            arguments += $" \"{url}\"";

            var psi = new ProcessStartInfo
            {
                FileName = toolPath,
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = new Process { StartInfo = psi };
            if (!process.Start())
            {
                return new Result(false, "Impossible de démarrer l'outil de téléchargement.");
            }

            // Read both streams concurrently to avoid a full-buffer deadlock, then wait.
            // ConfigureAwait(false) everywhere: the caller may block on GetResult() from the WPF
            // UI thread, and continuations must not need that captured context.
            var stdOutTask = process.StandardOutput.ReadToEndAsync();
            var stdErrTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().ConfigureAwait(false);
            var stdErr = await stdErrTask.ConfigureAwait(false);
            var stdOut = await stdOutTask.ConfigureAwait(false);

            return process.ExitCode == 0
                ? new Result(true, null, stdOut?.Trim())
                : new Result(false, string.IsNullOrWhiteSpace(stdErr) ? $"Code de sortie {process.ExitCode}." : stdErr.Trim(), stdOut?.Trim());
        }
        catch (Exception ex)
        {
            return new Result(false, ex.Message);
        }
    }
}
