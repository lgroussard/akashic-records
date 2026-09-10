using Microsoft.Data.Sqlite;

namespace AkashicRecords.Infrastructure.Persistence;

// Central place to open connections to the app's SQLite db, stored next to the exe
// (<exe folder>\data\akashic.db) so the whole app folder stays portable.
public sealed class SqliteConnectionFactory
{
    private readonly string _connectionString;

    public SqliteConnectionFactory()
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "data");
        Directory.CreateDirectory(folder);
        var dbPath = Path.Combine(folder, "akashic.db");
        _connectionString = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
    }

    public SqliteConnection CreateOpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }
}
