using ScadaDarbox.Core.Security;

namespace ScadaDarbox.Gateway.Contracts;

/// <summary>
/// One audit entry on the wire (ADR-0032).
/// </summary>
/// <param name="Detail">
/// The writer's opaque document as text, **not** a parsed object: the trail's shape belongs to the writers,
/// and a reader that understood the fields would drift from them (ADR-0032 §6). A screen prints it.
/// </param>
public sealed record AuditEntryDto(
    long Id,
    DateTimeOffset OccurredAtUtc,
    Guid? ActorUserId,
    string? Actor,
    string Action,
    string? EntityType,
    Guid? EntityId,
    string Detail)
{
    public static AuditEntryDto From(StoredAuditEntry entry) => new(
        entry.Id,
        entry.OccurredAtUtc,
        entry.ActorUserId,
        entry.ActorUsername,
        entry.Action,
        entry.EntityType,
        entry.EntityId,
        entry.DetailJson);
}

/// <summary>
/// A page of the trail, with how many rows the same filters match in total (ADR-0032 §3).
/// </summary>
public sealed record AuditPageDto(IReadOnlyList<AuditEntryDto> Entries, long Total);
