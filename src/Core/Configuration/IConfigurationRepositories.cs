using ScadaDarbox.Core.Model;

namespace ScadaDarbox.Core.Configuration;

/// <summary>
/// Reads and writes the folder tree of one site (ADR-0001 §4).
/// </summary>
/// <remarks>
/// Every read here returns live configuration only. Deletion is soft (ADR-0009): the row
/// survives so a historian sample can still resolve a name, but it is gone from every
/// browsing and lookup path.
/// </remarks>
public interface IFolderRepository
{
    Task<IReadOnlyList<Folder>> GetBySiteAsync(Guid siteId, CancellationToken cancellationToken);

    Task AddAsync(Folder folder, CancellationToken cancellationToken);

    /// <summary>
    /// Renames a folder and/or moves it under a different parent.
    /// </summary>
    /// <exception cref="ConfigurationConflictException">
    /// The move would make the folder its own ancestor. The composite foreign keys
    /// cannot express that, so it is checked here.
    /// </exception>
    Task UpdateAsync(Folder folder, CancellationToken cancellationToken);

    /// <summary>
    /// Soft-deletes an empty folder.
    /// </summary>
    /// <exception cref="ConfigurationConflictException">
    /// The folder still holds a live child folder or device. There is deliberately no
    /// cascade and no implicit reparenting: a position in the browse tree must never
    /// change as a side effect of something else (ADR-0001 §6), so the operator moves
    /// the contents out explicitly first.
    /// </exception>
    Task DeleteAsync(Guid folderId, CancellationToken cancellationToken);
}

/// <summary>Reads and writes device configuration.</summary>
public interface IDeviceRepository
{
    Task<IReadOnlyList<Device>> GetBySiteAsync(Guid siteId, CancellationToken cancellationToken);

    Task<Device?> FindAsync(Guid deviceId, CancellationToken cancellationToken);

    Task AddAsync(Device device, CancellationToken cancellationToken);

    Task UpdateAsync(Device device, CancellationToken cancellationToken);

    /// <summary>
    /// Soft-deletes a device and, in the same transaction, the tags it owns.
    /// </summary>
    /// <remarks>
    /// Unlike a folder, a device does cascade. A tag has no placement of its own to
    /// preserve — its device owns it (ADR-0001 §3) — so there is nothing an operator
    /// could usefully do with the tags first, and no choice worth forcing them to make.
    /// </remarks>
    Task DeleteAsync(Guid deviceId, CancellationToken cancellationToken);
}

/// <summary>Reads and writes the alarm conditions watching a tag.</summary>
public interface IAlarmDefinitionRepository
{
    Task<IReadOnlyList<AlarmDefinition>> GetByTagAsync(Guid tagId, CancellationToken cancellationToken);

    Task AddAsync(AlarmDefinition definition, CancellationToken cancellationToken);

    Task UpdateAsync(AlarmDefinition definition, CancellationToken cancellationToken);

    /// <summary>Soft-deletes one definition (ADR-0009).</summary>
    Task DeleteAsync(Guid definitionId, CancellationToken cancellationToken);
}

/// <summary>
/// A tag's names as recorded, whether or not it is still live.
/// </summary>
/// <remarks>
/// Exists so history outlives configuration in a usable form (ADR-0001, ADR-0009): a
/// trend for a device retired last year should read as its name, not as a UUID.
/// </remarks>
public sealed record TagIdentity(Guid TagId, string TagName, string DeviceName, bool IsDeleted);

/// <summary>Reads and writes the tags of a device.</summary>
public interface ITagRepository
{
    Task<IReadOnlyList<Tag>> GetByDeviceAsync(Guid deviceId, CancellationToken cancellationToken);

    Task AddAsync(Tag tag, CancellationToken cancellationToken);

    Task UpdateAsync(Tag tag, CancellationToken cancellationToken);

    /// <summary>Soft-deletes one tag.</summary>
    Task DeleteAsync(Guid tagId, CancellationToken cancellationToken);

    /// <summary>
    /// Resolves a tag's name and its device's name even after either has been deleted.
    /// The one read path that deliberately looks past the active-row views.
    /// </summary>
    Task<TagIdentity?> FindIdentityIncludingDeletedAsync(Guid tagId, CancellationToken cancellationToken);
}
