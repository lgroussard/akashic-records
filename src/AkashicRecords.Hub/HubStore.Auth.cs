using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace AkashicRecords.Hub;

public sealed record DeviceInfo(string DeviceId, string Name, string CreatedAt, string? LastSeenAt, bool Revoked);

// Pairing and device tokens (MOBILITE-SYNC.md section 3, POST /auth/pair).
// A one-time code is created on the hub side (`AkashicRecords.Hub pair`) and typed on the device;
// the device gets a token of its own. Only hashes are stored: reading hub.db gives no usable secret.
public sealed partial class HubStore
{
    public const int MaxPairFailures = 5; // wrong codes tolerated before the pending codes are burnt

    /// <summary>Creates a one-time pairing code (8 digits) valid for `minutes`.</summary>
    public string CreatePairCode(int minutes)
    {
        var code = RandomNumberGenerator.GetInt32(0, 100_000_000).ToString("D8");
        lock (_writeLock)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                DELETE FROM pair_codes WHERE used = 1 OR expires_at < $now;
                INSERT OR REPLACE INTO pair_codes(code_hash, expires_at, failures, used) VALUES ($h, $exp, 0, 0);
                """;
            cmd.Parameters.AddWithValue("$now", Now());
            cmd.Parameters.AddWithValue("$h", Hash(code));
            cmd.Parameters.AddWithValue("$exp", Now() + minutes * 60L);
            cmd.ExecuteNonQuery();
        }
        return code;
    }

    /// <summary>
    /// Exchanges a valid one-time code for a device token. Returns null when the code is wrong,
    /// expired, already used or burnt. Pairing an existing device id again replaces its token.
    /// </summary>
    public string? Pair(string code, string deviceId, string name)
    {
        lock (_writeLock)
        {
            using var c = Open();
            using var tx = c.BeginTransaction();

            int consumed;
            using (var use = c.CreateCommand())
            {
                use.Transaction = tx;
                use.CommandText = """
                    UPDATE pair_codes SET used = 1
                    WHERE code_hash = $h AND used = 0 AND expires_at >= $now AND failures < $max
                    """;
                use.Parameters.AddWithValue("$h", Hash(code));
                use.Parameters.AddWithValue("$now", Now());
                use.Parameters.AddWithValue("$max", MaxPairFailures);
                consumed = use.ExecuteNonQuery();
            }

            if (consumed == 0)
            {
                // A wrong guess counts against every pending code: 8 digits cannot be brute-forced.
                using var fail = c.CreateCommand();
                fail.Transaction = tx;
                fail.CommandText = "UPDATE pair_codes SET failures = failures + 1 WHERE used = 0";
                fail.ExecuteNonQuery();
                tx.Commit();
                return null;
            }

            var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
                .TrimEnd('=').Replace('+', '-').Replace('/', '_');
            using (var up = c.CreateCommand())
            {
                up.Transaction = tx;
                up.CommandText = """
                    INSERT INTO devices(device_id, name, token_hash, created_at, revoked)
                    VALUES ($id, $name, $th, $at, 0)
                    ON CONFLICT(device_id) DO UPDATE SET
                        name = excluded.name, token_hash = excluded.token_hash, revoked = 0
                    """;
                up.Parameters.AddWithValue("$id", deviceId);
                up.Parameters.AddWithValue("$name", name);
                up.Parameters.AddWithValue("$th", Hash(token));
                up.Parameters.AddWithValue("$at", DateTime.UtcNow.ToString("O"));
                up.ExecuteNonQuery();
            }
            tx.Commit();
            return token;
        }
    }

    /// <summary>The device a token belongs to, or null (unknown or revoked).</summary>
    public string? Authenticate(string token)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT device_id FROM devices WHERE token_hash = $th AND revoked = 0";
        cmd.Parameters.AddWithValue("$th", Hash(token));
        return cmd.ExecuteScalar() as string;
    }

    public void Touch(string deviceId)
    {
        lock (_writeLock)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "UPDATE devices SET last_seen_at = $at WHERE device_id = $id";
            cmd.Parameters.AddWithValue("$at", DateTime.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("$id", deviceId);
            cmd.ExecuteNonQuery();
        }
    }

    public bool Revoke(string deviceId)
    {
        lock (_writeLock)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "UPDATE devices SET revoked = 1 WHERE device_id = $id AND revoked = 0";
            cmd.Parameters.AddWithValue("$id", deviceId);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    public List<DeviceInfo> Devices()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT device_id, name, created_at, last_seen_at, revoked FROM devices ORDER BY created_at";
        using var r = cmd.ExecuteReader();
        var list = new List<DeviceInfo>();
        while (r.Read())
            list.Add(new DeviceInfo(r.GetString(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.GetInt64(4) != 0));
        return list;
    }

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private static string Hash(string secret) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
}
