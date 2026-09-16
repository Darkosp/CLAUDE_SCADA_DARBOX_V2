namespace ScadaDarbox.Core.Security;

/// <summary>
/// One entry in the append-only audit trail (ADR-0011).
/// </summary>
/// <param name="ActorUserId">Who did it, or null when nobody was authenticated — a failed login.</param>
/// <param name="Action">A stable dotted name, e.g. <c>alarm.acknowledge</c>.</param>
/// <param name="Detail">
/// Anything else worth keeping, stored as an opaque document. Never a password or a token.
/// </param>
public sealed record AuditEntry(
    Guid? ActorUserId,
    string Action,
    string? EntityType = null,
    Guid? EntityId = null,
    IReadOnlyDictionary<string, object?>? Detail = null);

/// <summary>Appends to the audit trail. There is deliberately no way to change or remove an entry.</summary>
public interface IAuditLog
{
    Task AppendAsync(AuditEntry entry, CancellationToken cancellationToken);
}
