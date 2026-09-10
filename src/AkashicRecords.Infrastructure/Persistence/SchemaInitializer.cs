namespace AkashicRecords.Infrastructure.Persistence;

// Creates all tables if they don't already exist - run once at startup. No migration
// framework yet; if the schema needs to change later, this will need a real migration step.
public sealed class SchemaInitializer
{
    private readonly SqliteConnectionFactory _connectionFactory;

    public SchemaInitializer(SqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public void EnsureCreated()
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                CREATE TABLE IF NOT EXISTS Artwork (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Title TEXT NOT NULL,
                    Category INTEGER NOT NULL,
                    Tier INTEGER NOT NULL,
                    CreatedAt TEXT NOT NULL,
                    CoverImagePath TEXT,
                    WatchPriority INTEGER NOT NULL DEFAULT 0
                );

                CREATE TABLE IF NOT EXISTS EvaluationAxis (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Name TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS ArtworkEvaluation (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    ArtworkId INTEGER NOT NULL REFERENCES Artwork(Id) ON DELETE CASCADE,
                    AxisId INTEGER NOT NULL REFERENCES EvaluationAxis(Id) ON DELETE CASCADE,
                    Score INTEGER NOT NULL
                );

                CREATE TABLE IF NOT EXISTS Observation (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    ArtworkId INTEGER NOT NULL REFERENCES Artwork(Id) ON DELETE CASCADE,
                    Subject TEXT NOT NULL,
                    Content TEXT NOT NULL,
                    CreatedAt TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS ArtisticProject (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Title TEXT NOT NULL,
                    CreatedAt TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS Reference (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    ObservationId INTEGER NOT NULL REFERENCES Observation(Id) ON DELETE CASCADE,
                    ProjectId INTEGER NOT NULL REFERENCES ArtisticProject(Id) ON DELETE CASCADE
                );

                CREATE TABLE IF NOT EXISTS CanvasElement (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    ProjectId INTEGER NOT NULL REFERENCES ArtisticProject(Id) ON DELETE CASCADE,
                    Type INTEGER NOT NULL,
                    X REAL NOT NULL,
                    Y REAL NOT NULL,
                    TextContent TEXT,
                    ImagePath TEXT,
                    ObservationId INTEGER REFERENCES Observation(Id) ON DELETE CASCADE,
                    Width REAL,
                    Height REAL
                );

                CREATE TABLE IF NOT EXISTS CanvasConnector (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    ProjectId INTEGER NOT NULL REFERENCES ArtisticProject(Id) ON DELETE CASCADE,
                    FromElementId INTEGER NOT NULL REFERENCES CanvasElement(Id) ON DELETE CASCADE,
                    ToElementId INTEGER NOT NULL REFERENCES CanvasElement(Id) ON DELETE CASCADE
                );

                CREATE TABLE IF NOT EXISTS JournalEntry (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Title TEXT NOT NULL,
                    Text TEXT NOT NULL,
                    EntryDate TEXT NOT NULL,
                    Tags TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS JournalPhoto (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    EntryId INTEGER NOT NULL REFERENCES JournalEntry(Id) ON DELETE CASCADE,
                    ImagePath TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS Recipe (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Title TEXT NOT NULL,
                    Category TEXT NOT NULL DEFAULT '',
                    Ingredients TEXT NOT NULL,
                    Instructions TEXT NOT NULL,
                    Notes TEXT NOT NULL,
                    Tags TEXT NOT NULL,
                    CoverImagePath TEXT,
                    CreatedAt TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS Recueil (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Title TEXT NOT NULL,
                    CreatedAt TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS Poem (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    RecueilId INTEGER REFERENCES Recueil(Id) ON DELETE SET NULL,
                    Title TEXT NOT NULL,
                    Text TEXT NOT NULL,
                    Tags TEXT NOT NULL,
                    ImagePath TEXT,
                    CreatedAt TEXT NOT NULL,
                    TextAlignment TEXT NOT NULL DEFAULT 'Left',
                    Margin REAL NOT NULL DEFAULT 0
                );

                CREATE TABLE IF NOT EXISTS PersonalProject (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Title TEXT NOT NULL,
                    Description TEXT NOT NULL DEFAULT '',
                    Status INTEGER NOT NULL,
                    Deadline TEXT,
                    CreatedAt TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS ProjectTask (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    ProjectId INTEGER NOT NULL REFERENCES PersonalProject(Id) ON DELETE CASCADE,
                    Text TEXT NOT NULL,
                    IsDone INTEGER NOT NULL DEFAULT 0,
                    SortOrder INTEGER NOT NULL DEFAULT 0
                );

                CREATE TABLE IF NOT EXISTS Transition (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Title TEXT NOT NULL,
                    Description TEXT NOT NULL DEFAULT '',
                    CurrentState TEXT NOT NULL DEFAULT '',
                    DesiredState TEXT NOT NULL DEFAULT '',
                    Notes TEXT NOT NULL DEFAULT '',
                    Resources TEXT NOT NULL DEFAULT '',
                    ReminderText TEXT NOT NULL DEFAULT '',
                    ShowOnDesktop INTEGER NOT NULL DEFAULT 0,
                    CreatedAt TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS TransitionStep (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    TransitionId INTEGER NOT NULL REFERENCES Transition(Id) ON DELETE CASCADE,
                    Text TEXT NOT NULL,
                    IsDone INTEGER NOT NULL DEFAULT 0,
                    SortOrder INTEGER NOT NULL DEFAULT 0
                );

                CREATE TABLE IF NOT EXISTS PhotoAlbum (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Name TEXT NOT NULL,
                    CreatedAt TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS Photo (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    ImagePath TEXT NOT NULL,
                    Title TEXT NOT NULL DEFAULT '',
                    Description TEXT NOT NULL DEFAULT '',
                    TakenDate TEXT,
                    Tags TEXT NOT NULL DEFAULT '',
                    IsFavorite INTEGER NOT NULL DEFAULT 0,
                    AlbumId INTEGER REFERENCES PhotoAlbum(Id) ON DELETE SET NULL,
                    ImportedAt TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS MusicTrack (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    FilePath TEXT NOT NULL UNIQUE,
                    Title TEXT NOT NULL DEFAULT '',
                    Artist TEXT NOT NULL DEFAULT '',
                    DurationSeconds REAL,
                    CoverImagePath TEXT,
                    AddedAt TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS Mood (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Name TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS TrackMood (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    TrackId INTEGER NOT NULL REFERENCES MusicTrack(Id) ON DELETE CASCADE,
                    MoodId INTEGER NOT NULL REFERENCES Mood(Id) ON DELETE CASCADE
                );

                CREATE TABLE IF NOT EXISTS Playlist (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Name TEXT NOT NULL,
                    CreatedAt TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS PlaylistTrack (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    PlaylistId INTEGER NOT NULL REFERENCES Playlist(Id) ON DELETE CASCADE,
                    TrackId INTEGER NOT NULL REFERENCES MusicTrack(Id) ON DELETE CASCADE,
                    SortOrder INTEGER NOT NULL DEFAULT 0
                );

                CREATE TABLE IF NOT EXISTS DownloadItem (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Url TEXT NOT NULL,
                    Title TEXT NOT NULL DEFAULT '',
                    Status INTEGER NOT NULL DEFAULT 0,
                    AddedAt TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS WatchlistItem (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Title TEXT NOT NULL,
                    Category INTEGER NOT NULL,
                    Priority INTEGER NOT NULL,
                    CoverImagePath TEXT,
                    AddedAt TEXT NOT NULL
                );
                """;
            command.ExecuteNonQuery();
        }

        AddColumnIfMissing(connection, "Artwork", "CoverImagePath", "TEXT");
        AddColumnIfMissing(connection, "Artwork", "WatchPriority", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing(connection, "MusicTrack", "Album", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing(connection, "CanvasElement", "Width", "REAL");
        AddColumnIfMissing(connection, "CanvasElement", "Height", "REAL");
        AddColumnIfMissing(connection, "Recipe", "Category", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing(connection, "Poem", "TextAlignment", "TEXT NOT NULL DEFAULT 'Left'");
        AddColumnIfMissing(connection, "Poem", "Margin", "REAL NOT NULL DEFAULT 0");
    }

    // No migration framework yet - for a db created before a column existed, add it in place.
    private static void AddColumnIfMissing(Microsoft.Data.Sqlite.SqliteConnection connection, string table, string column, string sqlType)
    {
        var hasColumn = false;
        using (var checkCommand = connection.CreateCommand())
        {
            checkCommand.CommandText = $"PRAGMA table_info({table});";
            using var reader = checkCommand.ExecuteReader();
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                {
                    hasColumn = true;
                    break;
                }
            }
        }

        if (hasColumn) return;

        using var alterCommand = connection.CreateCommand();
        alterCommand.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {sqlType};";
        alterCommand.ExecuteNonQuery();
    }
}
