using ScadaDarbox.Core.Model;

namespace ScadaDarbox.Core.Configuration;

/// <summary>
/// Reads and writes the folder tree of one site (ADR-0001 §4).
/// </summary>
/// <remarks>
/// Deleting folders is deliberately absent: what should happen to the devices inside a
/// deleted folder is a real decision, not an implementation detail, and Phase 2's gate
/// does not need it.
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
}

/// <summary>Reads and writes device configuration.</summary>
public interface IDeviceRepository
{
    Task<IReadOnlyList<Device>> GetBySiteAsync(Guid siteId, CancellationToken cancellationToken);

    Task<Device?> FindAsync(Guid deviceId, CancellationToken cancellationToken);

    Task AddAsync(Device device, CancellationToken cancellationToken);

    Task UpdateAsync(Device device, CancellationToken cancellationToken);
}

/// <summary>Reads and writes the tags of a device.</summary>
public interface ITagRepository
{
    Task<IReadOnlyList<Tag>> GetByDeviceAsync(Guid deviceId, CancellationToken cancellationToken);

    Task AddAsync(Tag tag, CancellationToken cancellationToken);

    Task UpdateAsync(Tag tag, CancellationToken cancellationToken);
}
