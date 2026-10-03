using AkashicRecords.Domain;
using Microsoft.Data.Sqlite;

namespace AkashicRecords.Infrastructure.Persistence;

// Stores which app items (poems, projects, observations…) a calendar entry points at. The entry is
// named by OwnerType/OwnerId — an event or a birthday, whose id spaces overlap — so one table
// serves both. Links are cheap rows; deleting the owner cascades manually here (SQLite FKs are off
// in this schema, same as the other child tables).
public sealed class CalendarEventLinkRepository
{
    private readonly SqliteConnectionFactory _connectionFactory;

    public CalendarEventLinkRepository(SqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public void Add(CalendarEventLink link)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO CalendarEventLink (OwnerType, OwnerId, Kind, Section, Title, PrimaryId, SecondaryId, Category)
            VALUES ($ownerType, $ownerId, $kind, $section, $title, $primaryId, $secondaryId, $category);
            """;
        command.Parameters.AddWithValue("$ownerType", link.OwnerType.ToString());
        command.Parameters.AddWithValue("$ownerId", link.OwnerId);
        command.Parameters.AddWithValue("$kind", link.Kind);
        command.Parameters.AddWithValue("$section", link.Section);
        command.Parameters.AddWithValue("$title", link.Title);
        command.Parameters.AddWithValue("$primaryId", link.PrimaryId);
        command.Parameters.AddWithValue("$secondaryId", (object?)link.SecondaryId ?? DBNull.Value);
        command.Parameters.AddWithValue("$category", (object?)link.Category ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<CalendarEventLink> GetByOwner(CalendarLinkOwnerType ownerType, int ownerId)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT Id, OwnerType, OwnerId, Kind, Section, Title, PrimaryId, SecondaryId, Category " +
            "FROM CalendarEventLink WHERE OwnerType = $ownerType AND OwnerId = $ownerId ORDER BY Title;";
        command.Parameters.AddWithValue("$ownerType", ownerType.ToString());
        command.Parameters.AddWithValue("$ownerId", ownerId);

        var results = new List<CalendarEventLink>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new CalendarEventLink
            {
                Id = reader.GetInt32(0),
                OwnerType = Enum.TryParse<CalendarLinkOwnerType>(reader.GetString(1), out var type)
                    ? type : CalendarLinkOwnerType.Event,
                OwnerId = reader.GetInt32(2),
                Kind = reader.GetString(3),
                Section = reader.GetString(4),
                Title = reader.GetString(5),
                PrimaryId = reader.GetInt32(6),
                SecondaryId = reader.IsDBNull(7) ? null : reader.GetInt32(7),
                Category = reader.IsDBNull(8) ? null : reader.GetInt32(8),
            });
        }
        return results;
    }

    public void DeleteForOwner(CalendarLinkOwnerType ownerType, int ownerId)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM CalendarEventLink WHERE OwnerType = $ownerType AND OwnerId = $ownerId;";
        command.Parameters.AddWithValue("$ownerType", ownerType.ToString());
        command.Parameters.AddWithValue("$ownerId", ownerId);
        command.ExecuteNonQuery();
    }
}
