using System.Text;

namespace AkashicRecords.Hub;

/// <summary>
/// One line per request in &lt;data&gt;/logs/hub-yyyy-MM-dd.log:
///   time  remote-ip  device  METHOD  path?query  status  duration
/// Never logs a body or a header, so no code and no token ends up on disk.
/// </summary>
public sealed class RequestLog
{
    private const int KeepDays = 30;
    private readonly string _dir;
    private readonly object _lock = new();

    public RequestLog(string dir)
    {
        _dir = dir;
        Directory.CreateDirectory(dir);
        foreach (var f in Directory.EnumerateFiles(dir, "hub-*.log"))
            try { if (File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-KeepDays)) File.Delete(f); } catch (IOException) { }
    }

    public void Write(HttpContext ctx, long elapsedMs)
    {
        var now = DateTime.Now;
        var line = string.Join('\t',
            now.ToString("yyyy-MM-ddTHH:mm:ss.fff"),
            ctx.Connection.RemoteIpAddress?.ToString() ?? "-",
            ctx.Items[HubApp.DeviceItem] as string ?? "-",
            ctx.Request.Method,
            Clean(ctx.Request.Path + ctx.Request.QueryString),
            ctx.Response.StatusCode,
            elapsedMs + "ms");
        Append(now, line);
    }

    public void Info(string message) => Append(DateTime.Now, $"{DateTime.Now:yyyy-MM-ddTHH:mm:ss.fff}\t{message}");

    private void Append(DateTime day, string line)
    {
        try
        {
            lock (_lock)
                File.AppendAllText(Path.Combine(_dir, $"hub-{day:yyyy-MM-dd}.log"), line + Environment.NewLine, Encoding.UTF8);
        }
        catch (IOException) { /* a full or locked disk must not fail the request */ }
    }

    // The path is client-controlled: keep it on one line and bounded.
    private static string Clean(string s)
    {
        var sb = new StringBuilder(Math.Min(s.Length, 300));
        foreach (var ch in s)
        {
            if (sb.Length >= 300) break;
            sb.Append(char.IsControl(ch) ? '?' : ch);
        }
        return sb.ToString();
    }
}
