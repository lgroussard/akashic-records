using System.Security.Cryptography;

namespace AkashicRecords.Hub;

/// <summary>
/// Media blobs as plain files, addressed by the SHA-256 of their content: blobs/ab/abcdef...
/// A blob is immutable - the same hash is always the same bytes - so it is written once and
/// never rewritten. No garbage collection yet.
/// </summary>
public sealed class BlobStore
{
    private readonly string _root;
    private readonly string _tmp;

    public BlobStore(string root)
    {
        _root = root;
        _tmp = Path.Combine(root, "tmp");
        Directory.CreateDirectory(_tmp);
        // Leftovers of uploads cut short by a crash.
        foreach (var f in Directory.EnumerateFiles(_tmp))
            try { File.Delete(f); } catch (IOException) { }
    }

    public static bool IsHash(string? s) =>
        s is { Length: 64 } && s.All(ch => ch is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    public string PathOf(string hash) => Path.Combine(_root, hash[..2], hash);

    public bool Exists(string hash) => File.Exists(PathOf(hash));

    public sealed class TooLargeException : Exception { }
    public sealed class HashMismatchException(string actual) : Exception { public string Actual { get; } = actual; }

    /// <summary>Streams the body to disk while hashing it. `expectedHash` (optional) is what the
    /// client claims to send: a mismatch stores nothing.</summary>
    public async Task<MediaResponse> SaveAsync(Stream body, string? expectedHash, long maxBytes, CancellationToken ct)
    {
        var tmp = Path.Combine(_tmp, Guid.NewGuid().ToString("N"));
        try
        {
            long size = 0;
            string hash;
            using (var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                await using var file = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                var buffer = new byte[81920];
                int n;
                while ((n = await body.ReadAsync(buffer, ct)) > 0)
                {
                    size += n;
                    if (size > maxBytes) throw new TooLargeException();
                    sha.AppendData(buffer, 0, n);
                    await file.WriteAsync(buffer.AsMemory(0, n), ct);
                }
                await file.FlushAsync(ct);
                hash = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
            }

            if (expectedHash is not null && expectedHash != hash) throw new HashMismatchException(hash);

            var final = PathOf(hash);
            if (File.Exists(final)) return new MediaResponse(hash, size, false);
            Directory.CreateDirectory(Path.GetDirectoryName(final)!);
            try
            {
                File.Move(tmp, final); // atomic on the same volume: a reader never sees half a blob
            }
            catch (IOException) when (File.Exists(final))
            {
                return new MediaResponse(hash, size, false); // another upload of the same content won the race
            }
            return new MediaResponse(hash, size, true);
        }
        finally
        {
            try { File.Delete(tmp); } catch (IOException) { }
        }
    }
}
