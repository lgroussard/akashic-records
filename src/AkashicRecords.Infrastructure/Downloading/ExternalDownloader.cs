using System.Diagnostics;
using System.Text;

namespace AkashicRecords.Infrastructure.Downloading;

// Launches a user-installed command-line downloader (e.g. yt-dlp) as a child process.
// Akashic does not bundle the tool: the user installs/configures it, and is responsible
// for what they download and for respecting each source's terms of use.
public sealed class ExternalDownloader
{
    // Output carries the trimmed stdout (e.g. the direct stream URL in yt-dlp "print" mode).
    public sealed record Result(bool Success, string? Error, string? Output = null);

    // A tool that hangs (network, a prompt, ffmpeg) would otherwise never give the hand back: the caller
    // stayed "busy" until the app was restarted. Generous, because a long video on a slow line is legitimate.
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(20);

    // The child processes still running, so that quitting the app does not leave them behind holding
    // their output file.
    private static readonly HashSet<Process> Running = new();

    public async Task<Result> DownloadAsync(
        string toolPath,
        string argumentsTemplate,
        string url,
        string workingDirectory,
        string? ffmpegDirectory,
        CancellationToken cancellationToken = default,
        TimeSpan? timeout = null)
    {
        try
        {
            Directory.CreateDirectory(workingDirectory);

            // The template is the user's own setting and is passed as written. What is added to it is
            // quoted for real: [url] can be a search built from a track title, and a title holding a
            // quote (« "Heroes" ») broke the command line, or added options to the tool.
            var arguments = argumentsTemplate;
            if (!string.IsNullOrWhiteSpace(ffmpegDirectory))
            {
                arguments += " --ffmpeg-location " + Quote(ffmpegDirectory);
            }
            arguments += " " + Quote(url);

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
            lock (Running) Running.Add(process);

            try
            {
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                limit.CancelAfter(timeout ?? DefaultTimeout);

                // Read both streams concurrently to avoid a full-buffer deadlock, then wait.
                // ConfigureAwait(false) everywhere: the caller may block on GetResult() from the WPF
                // UI thread, and continuations must not need that captured context.
                var stdOutTask = process.StandardOutput.ReadToEndAsync();
                var stdErrTask = process.StandardError.ReadToEndAsync();
                try
                {
                    await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    Kill(process);
                    return new Result(false, cancellationToken.IsCancellationRequested
                        ? "Téléchargement annulé."
                        : "L'outil de téléchargement ne répondait plus : il a été arrêté.");
                }
                var stdErr = await stdErrTask.ConfigureAwait(false);
                var stdOut = await stdOutTask.ConfigureAwait(false);

                return process.ExitCode == 0
                    ? new Result(true, null, stdOut?.Trim())
                    : new Result(false, string.IsNullOrWhiteSpace(stdErr) ? $"Code de sortie {process.ExitCode}." : stdErr.Trim(), stdOut?.Trim());
            }
            finally
            {
                lock (Running) Running.Remove(process);
            }
        }
        catch (Exception ex)
        {
            return new Result(false, ex.Message);
        }
    }

    // Called when the app really exits.
    public static void KillRunning()
    {
        Process[] processes;
        lock (Running) processes = Running.ToArray();
        foreach (var process in processes) Kill(process);
    }

    // The whole tree: yt-dlp starts ffmpeg, which would survive its parent.
    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Already gone, or not ours to stop: nothing more to do.
        }
    }

    // One argument as the Windows command line expects it: quotes escaped, and the backslashes that
    // precede a quote doubled (a path ending in "\" would otherwise swallow the closing quote).
    internal static string Quote(string argument)
    {
        var quoted = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var c in argument)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }
            if (c == '"')
            {
                quoted.Append('\\', backslashes * 2 + 1);
            }
            else
            {
                quoted.Append('\\', backslashes);
            }
            backslashes = 0;
            quoted.Append(c);
        }
        quoted.Append('\\', backslashes * 2).Append('"');
        return quoted.ToString();
    }
}
