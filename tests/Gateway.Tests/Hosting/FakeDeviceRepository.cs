using ScadaDarbox.Core.Configuration;
using ScadaDarbox.Core.Model;

namespace ScadaDarbox.Gateway.Tests.Hosting;

/// <summary>
/// A device store in memory, for the tests that watch the Gateway write a device without a
/// database — the link device it derives for an edge (ADR-0022).
/// </summary>
/// <remarks>
/// Separate from <see cref="FakeCatalogue"/> rather than folded into it: the catalogue serves as the
/// configuration store and the edge repository because a declaration is written through one and read
/// back through the other, and those two have to be looking at the same rows. Nothing writes a device
/// and then reads it back through the catalogue in the same way, so there is no reason to make them
/// one object — and one object that implements four interfaces is harder to read than two that each
/// say what they are for.
/// </remarks>
public sealed class FakeDeviceRepository : IDeviceRepository
{
    public List<Device> Devices { get; } = [];

    public Task<IReadOnlyList<Device>> GetBySiteAsync(Guid siteId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Device>>(Devices.Where(device => device.SiteId == siteId).ToList());

    public Task<Device?> FindAsync(Guid deviceId, CancellationToken cancellationToken) =>
        Task.FromResult(Devices.FirstOrDefault(device => device.Id == deviceId));

    public Task AddAsync(Device device, CancellationToken cancellationToken)
    {
        Devices.Add(device);
        return Task.CompletedTask;
    }

    public Task UpdateAsync(Device device, CancellationToken cancellationToken)
    {
        var index = Devices.FindIndex(candidate => candidate.Id == device.Id);

        if (index < 0)
        {
            throw new ConfigurationConflictException($"Device {device.Id} no longer exists.");
        }

        // Replaced rather than copied field by field: Device's driver key and settings are init-only,
        // which says the same thing the API does — a device is not moved to another driver, it is
        // deleted and made again.
        Devices[index] = device;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(Guid deviceId, CancellationToken cancellationToken)
    {
        Devices.RemoveAll(device => device.Id == deviceId);
        return Task.CompletedTask;
    }
}
