namespace ScadaDarbox.Core.Security;

/// <summary>
/// One entry of the audit trail as it was written, read back for review (ADR-0032).
/// </summary>
/// <param name="DetailJson">
/// The writer's opaque document, as JSON. **Never parsed into columns**: the trail's shape belongs to the
/// writers, and a reader that understood the fields would drift from them — and the entries that stop
/// meaning what they meant are always the old ones.
/// </param>
/// <param name="ActorUsername">
/// The actor's name, resolved when the row is read rather than stored with it, and resolved from
/// <c>app_user</c> rather than its active view — so the actions of a deactivated account still read as the
/// person they were, instead of as nobody (ADR-0009, ADR-0032 §5).
/// </param>
public sealed record StoredAuditEntry(
    long Id,
    DateTimeOffset OccurredAtUtc,
    Guid? ActorUserId,
    string? ActorUsername,
    string Action,
    string? EntityType,
    Guid? EntityId,
    string DetailJson);

/// <summary>
/// What to read from the trail. Every field narrows; an absent one narrows nothing.
/// </summary>
/// <param name="ActionPrefix">
/// A prefix rather than an exact name, because actions are stable dotted names: <c>auth.</c> is the family,
/// <c>auth.login</c> is one action in it.
/// </param>
/// <param name="BeforeId">
/// The cursor: return rows older than this one. Reading is by id and not by time, because `occurred_at` is
/// not unique and a page boundary on a non-unique column either repeats a row or drops one (ADR-0032 §2).
/// </param>
public sealed record AuditTrailQuery(
    DateTimeOffset? FromUtc = null,
    DateTimeOffset? ToUtc = null,
    string? ActionPrefix = null,
    Guid? ActorUserId = null,
    string? EntityType = null,
    Guid? EntityId = null,
    long? BeforeId = null,
    int Limit = 200);

/// <summary>
/// A page of the trail, and how many rows the same filters match in total.
/// </summary>
/// <param name="Total">
/// **Beside the entries on purpose** (ADR-0032 §3): a limit nobody mentions looks like a quiet night, so a
/// reader is always told whether what they are looking at is all of it — *"the newest 200 of 4,312"*.
/// </param>
public sealed record AuditTrailPage(IReadOnlyList<StoredAuditEntry> Entries, long Total);

/// <summary>
/// Reads the audit trail (ADR-0032).
/// </summary>
/// <remarks>
/// **Deliberately not on <see cref="IAuditLog"/>**, which stays the write-only contract it says it is: a
/// component handed the log can append and cannot read, and the reader has a different shape (a query, a
/// cap, a count) and a different consumer — one Admin screen. Nothing here can change or remove a row; that
/// is enforced where it belongs, by the application role's revoked privileges on the table.
/// </remarks>
public interface IAuditTrail
{
    Task<AuditTrailPage> ReadAsync(AuditTrailQuery query, CancellationToken cancellationToken);
}
