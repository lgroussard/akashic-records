using AkashicRecords.Domain;
using Microsoft.Data.Sqlite;

namespace AkashicRecords.Infrastructure.Persistence;

public sealed class ObservationRepository
{
    // Flattened view of an observation with its owner's title, whatever kind of item the owner is.
    // Used to pick an observation (canvas / Fil picker), by global search and by the Fil page.
    // ArtworkCategory is set for artwork owners only (Collections needs it to pick the right tab).
    public sealed record ObservationSummary(
        int ObservationId, string OwnerKind, int OwnerId, int? ArtworkCategory,
        string OwnerTitle, string Subject, string Content, DateTime CreatedAt)
    {
        public string Label => Subject.Length > 0 ? $"{OwnerTitle} — {Subject}" : OwnerTitle;

        // The nav section the owner lives in — the same strings a global-search hit carries.
        public string Section => OwnerKind switch
        {
            Observation.ArtworkOwner => "Collections",
            Observation.PhotoOwner => "Archives",
            Observation.MusicTrackOwner => "Musique",
            _ => "Journaux"
        };

        // A hit that opens the owner. An artwork observation keeps its historical shape (Kind Observation,
        // PrimaryId = artwork, SecondaryId = observation) so the fiche can select the row; any other
        // owner is opened as the item itself.
        public SearchResult ToSearchResult(string snippet)
        {
            if (OwnerKind == Observation.ArtworkOwner)
            {
                return new SearchResult(SearchResultKind.Observation, Section, Label, snippet, OwnerId, ObservationId, ArtworkCategory);
            }
            var kind = Enum.TryParse<SearchResultKind>(OwnerKind, out var parsed) ? parsed : SearchResultKind.Observation;
            return new SearchResult(kind, Section, Label, snippet, OwnerId, null, null);
        }
    }

    private const string Columns = "Id, ArtworkId, Subject, Content, CreatedAt, OwnerKind, OwnerId";

    // Owner title per kind. A missing owner yields NULL, and such rows are skipped by the readers below.
    private const string SummarySelect =
        """
        SELECT o.Id, o.OwnerKind, o.OwnerId, a.Category,
               CASE o.OwnerKind
                   WHEN 'Artwork' THEN a.Title
                   WHEN 'JournalEntry' THEN CASE WHEN j.Title <> '' THEN j.Title ELSE substr(j.EntryDate, 1, 10) END
                   WHEN 'Poem' THEN p.Title
                   WHEN 'Recipe' THEN r.Title
                   WHEN 'Photo' THEN CASE WHEN ph.Id IS NULL THEN NULL WHEN ph.Title <> '' THEN ph.Title ELSE '(photo sans titre)' END
                   WHEN 'RefImage' THEN CASE WHEN ri.Id IS NULL THEN NULL WHEN ri.Title <> '' THEN ri.Title ELSE '(image sans titre)' END
                   WHEN 'MusicTrack' THEN CASE WHEN m.Artist <> '' THEN m.Title || ' — ' || m.Artist ELSE m.Title END
               END,
               o.Subject, o.Content, o.CreatedAt
        FROM Observation o
        LEFT JOIN Artwork a ON o.OwnerKind = 'Artwork' AND a.Id = o.OwnerId
        LEFT JOIN JournalEntry j ON o.OwnerKind = 'JournalEntry' AND j.Id = o.OwnerId
        LEFT JOIN Poem p ON o.OwnerKind = 'Poem' AND p.Id = o.OwnerId
        LEFT JOIN Recipe r ON o.OwnerKind = 'Recipe' AND r.Id = o.OwnerId
        LEFT JOIN Photo ph ON o.OwnerKind = 'Photo' AND ph.Id = o.OwnerId
        LEFT JOIN RefImage ri ON o.OwnerKind = 'RefImage' AND ri.Id = o.OwnerId
        LEFT JOIN MusicTrack m ON o.OwnerKind = 'MusicTrack' AND m.Id = o.OwnerId
        """;

    private readonly SqliteConnectionFactory _connectionFactory;

    public ObservationRepository(SqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    // Every observation whose owner still exists, with the owner's title.
    public IReadOnlyList<ObservationSummary> GetAllWithOwner()
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = SummarySelect + " ORDER BY 5, o.Subject;";
        return ReadSummaries(command);
    }

    // The observations a project references (its Fil), newest first.
    public IReadOnlyList<ObservationSummary> GetByProject(int projectId)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = SummarySelect +
            " WHERE o.Id IN (SELECT ObservationId FROM Reference WHERE ProjectId = $projectId) ORDER BY o.CreatedAt DESC;";
        command.Parameters.AddWithValue("$projectId", projectId);
        return ReadSummaries(command);
    }

    public ObservationSummary? GetSummary(int observationId)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = SummarySelect + " WHERE o.Id = $id;";
        command.Parameters.AddWithValue("$id", observationId);
        return ReadSummaries(command).FirstOrDefault();
    }

    public int Add(Observation observation)
    {
        // The two owner columns and ArtworkId must agree: an artwork observation carries its artwork
        // id in both, any other owner leaves ArtworkId at 0.
        var isArtwork = observation.OwnerKind == Observation.ArtworkOwner;
        var ownerId = isArtwork && observation.OwnerId == 0 ? observation.ArtworkId : observation.OwnerId;
        var artworkId = isArtwork ? ownerId : 0;

        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO Observation (ArtworkId, Subject, Content, CreatedAt, OwnerKind, OwnerId)
            VALUES ($artworkId, $subject, $content, $createdAt, $ownerKind, $ownerId);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$artworkId", artworkId);
        command.Parameters.AddWithValue("$subject", observation.Subject);
        command.Parameters.AddWithValue("$content", observation.Content);
        command.Parameters.AddWithValue("$createdAt", observation.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$ownerKind", observation.OwnerKind);
        command.Parameters.AddWithValue("$ownerId", ownerId);
        return Convert.ToInt32((long)command.ExecuteScalar()!);
    }

    public void Update(int id, string subject, string content)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Observation SET Subject = $subject, Content = $content WHERE Id = $id;";
        command.Parameters.AddWithValue("$subject", subject);
        command.Parameters.AddWithValue("$content", content);
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    // Observation count per artwork for a whole category, used for the tier list card badges (avoids one query per artwork).
    public IReadOnlyDictionary<int, int> GetCountsByCategory(ArtworkCategory category)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT o.OwnerId, COUNT(*)
            FROM Observation o
            JOIN Artwork a ON a.Id = o.OwnerId
            WHERE o.OwnerKind = 'Artwork' AND a.Category = $category
            GROUP BY o.OwnerId;
            """;
        command.Parameters.AddWithValue("$category", (int)category);

        var results = new Dictionary<int, int>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results[reader.GetInt32(0)] = reader.GetInt32(1);
        }
        return results;
    }

    public IReadOnlyList<Observation> GetByArtwork(int artworkId) => GetByOwner(Observation.ArtworkOwner, artworkId);

    public IReadOnlyList<Observation> GetByOwner(string ownerKind, int ownerId)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM Observation WHERE OwnerKind = $kind AND OwnerId = $ownerId ORDER BY CreatedAt;";
        command.Parameters.AddWithValue("$kind", ownerKind);
        command.Parameters.AddWithValue("$ownerId", ownerId);

        var results = new List<Observation>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(ReadObservation(reader));
        }
        return results;
    }

    public Observation? GetById(int id)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM Observation WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id);

        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadObservation(reader) : null;
    }

    // Its project links (Reference rows) and its boxes on canvases (CanvasElement, with their arrows) go
    // with it: those tables reference Observation ON DELETE CASCADE and foreign keys are enforced. The
    // Reference delete is spelled out so the Fil never depends on that.
    public void Delete(int id)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            DELETE FROM Reference WHERE ObservationId = $id;
            DELETE FROM Observation WHERE Id = $id;
            """;
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    // Called when an item is deleted, so its observations (and their project links) go with it. The owner
    // is not a foreign key (it can be any table), so nothing cascades from the owner's own delete.
    public void DeleteByOwner(string ownerKind, int ownerId)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            DELETE FROM Reference WHERE ObservationId IN
                (SELECT Id FROM Observation WHERE OwnerKind = $kind AND OwnerId = $ownerId);
            DELETE FROM Observation WHERE OwnerKind = $kind AND OwnerId = $ownerId;
            """;
        command.Parameters.AddWithValue("$kind", ownerKind);
        command.Parameters.AddWithValue("$ownerId", ownerId);
        command.ExecuteNonQuery();
    }

    private static List<ObservationSummary> ReadSummaries(SqliteCommand command)
    {
        var results = new List<ObservationSummary>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (reader.IsDBNull(4)) continue; // owner no longer exists
            var title = reader.GetString(4);
            var ownerKind = reader.GetString(1);
            // An untitled journal day is named by its date; shown the way the journal page shows it.
            if (ownerKind == Observation.JournalEntryOwner &&
                DateTime.TryParseExact(title, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var day))
            {
                title = day.ToString("d MMMM yyyy", System.Globalization.CultureInfo.CurrentCulture);
            }
            results.Add(new ObservationSummary(
                reader.GetInt32(0), ownerKind, reader.GetInt32(2),
                reader.IsDBNull(3) ? null : reader.GetInt32(3),
                title.Length > 0 ? title : "(sans titre)",
                reader.GetString(5), reader.GetString(6), DateTime.Parse(reader.GetString(7))));
        }
        return results;
    }

    private static Observation ReadObservation(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt32(0),
        ArtworkId = reader.GetInt32(1),
        Subject = reader.GetString(2),
        Content = reader.GetString(3),
        CreatedAt = DateTime.Parse(reader.GetString(4)),
        OwnerKind = reader.GetString(5),
        OwnerId = reader.GetInt32(6)
    };
}
