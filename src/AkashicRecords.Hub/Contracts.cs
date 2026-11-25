using System.Text.Json;

namespace AkashicRecords.Hub;

// Wire contract of the hub. Serialized in snake_case (see HubApp.JsonOptions).
// `Payload` is opaque JSON for the hub: it stores and returns it, never interprets it.

/// <summary>One intention emitted by a device. `MutationId` is a client-generated GUID that makes
/// a retry of the same mutation harmless (idempotence).</summary>
public sealed record Mutation(
    string MutationId,
    string EntityType,
    string EntityId,
    long BaseRev,
    JsonElement? Payload,
    bool Deleted);

/// <summary>The author is the device the token belongs to. `DeviceId` is optional; if sent it must
/// be that same device.</summary>
public sealed record PushRequest(string? DeviceId, List<Mutation> Mutations);

/// <summary>`Code` is the one-time code shown on the hub side. `DeviceId` is chosen by the device
/// and kept for life (a GUID); omitted, the hub issues one.</summary>
public sealed record PairRequest(string Code, string? DeviceId, string? DeviceName);

/// <summary>`Token` goes in `Authorization: Bearer ...` on every later call. Shown once.</summary>
public sealed record PairResponse(string DeviceId, string Token);

public sealed record SnapshotEntity(
    string EntityType,
    string EntityId,
    long Rev,
    JsonElement? Payload,
    bool Deleted,
    string UpdatedAt,
    string LastDevice);

/// <summary>One page of the current state. Next page: `after_type` / `after_id` = the `Next*` values.
/// `HeadSeq` of the first page is the cursor to pull from once every page is loaded.</summary>
public sealed record SnapshotResponse(
    long HeadSeq,
    List<SnapshotEntity> Entities,
    bool HasMore,
    string? NextAfterType,
    string? NextAfterId);

/// <summary>`Created` is false when the hub already had this exact content.</summary>
public sealed record MediaResponse(string Hash, long Size, bool Created);

/// <summary>Current state of an entity at the hub, returned with a conflict.</summary>
public sealed record EntityState(
    long Rev,
    JsonElement? Payload,
    bool Deleted,
    string LastDevice,
    string UpdatedAt);

/// <summary>Status is "accepted", "duplicate" (already committed earlier, same outcome returned)
/// or "conflict" (base_rev is stale; `Current` carries the hub's state, null if the entity
/// does not exist).</summary>
public sealed record MutationResult(
    string MutationId,
    string Status,
    long Rev,
    long? Seq,
    EntityState? Current);

public sealed record PushResponse(List<MutationResult> Results, long HeadSeq);

public sealed record LogRow(
    long Seq,
    string EntityType,
    string EntityId,
    long Rev,
    string DeviceId,
    JsonElement? Payload,
    bool Deleted,
    string CommittedAt);

/// <summary>`NextCursor` is the value to send as `since` on the next pull.</summary>
public sealed record PullResponse(List<LogRow> Rows, long NextCursor, bool HasMore, long HeadSeq);

public static class MutationStatus
{
    public const string Accepted = "accepted";
    public const string Duplicate = "duplicate";
    public const string Conflict = "conflict";
}
