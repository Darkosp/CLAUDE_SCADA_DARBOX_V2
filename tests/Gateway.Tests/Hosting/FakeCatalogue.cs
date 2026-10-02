using ScadaDarbox.Core.Configuration;
using ScadaDarbox.Core.Model;
using ScadaDarbox.Core.Security;
using ScadaDarbox.Core.Tags;

namespace ScadaDarbox.Gateway.Tests;

/// <summary>
/// One catalogue in memory, serving as both the configuration store and the edge repository, for
/// the tests that exercise the provisioning conversation without a database.
/// </summary>
/// <remarks>
/// The two are one object on purpose: a declaration is written through the repository and then read
/// back through a reload of the store, and a test that wants to see the reload see the write has to
/// have them looking at the same rows.
/// </remarks>
public sealed class FakeCatalogue : IConfigurationStore, IEdgeRepository
{
    public Tenant Tenant { get; init; } = new() { Id = Guid.NewGuid(), Name = "Darbo" };

    public List<Site> Sites { get; } = [];

    public List<Device> Devices { get; } = [];

    public List<Tag> Tags { get; } = [];

    public List<Edge> Edges { get; } = [];

    /// <summary>Every declaration written, in the order it arrived.</summary>
    public List<(Guid EdgeId, IReadOnlyList<string> Drivers, DateTimeOffset DeclaredAt)> Declarations { get; } = [];

    public Task<Tenant> GetTenantAsync(CancellationToken cancellationToken) => Task.FromResult(Tenant);

    public Task<IReadOnlyList<Site>> GetSitesAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Site>>(Sites);

    public Task<IReadOnlyList<Folder>> GetFoldersAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Folder>>([]);

    public Task<IReadOnlyList<Device>> GetDevicesAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Device>>(Devices);

    public Task<IReadOnlyList<Edge>> GetEdgesAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Edge>>(Edges);

    public Task<IReadOnlyList<Tag>> GetTagsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Tag>>(Tags);

    public Task<IReadOnlyList<AlarmDefinition>> GetAlarmDefinitionsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<AlarmDefinition>>([]);

    // The edge repository's own read of the same rows, under the name that interface gives it.
    public Task<IReadOnlyList<Edge>> GetAllAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Edge>>(Edges);

    public Task AddAsync(Edge edge, CancellationToken cancellationToken)
    {
        Edges.Add(edge);
        return Task.CompletedTask;
    }

    public Task UpdateAsync(Edge edge, CancellationToken cancellationToken)
    {
        var existing = Edges.FirstOrDefault(candidate => candidate.Id == edge.Id)
            ?? throw new ConfigurationConflictException($"Edge {edge.Id} no longer exists.");

        existing.Name = edge.Name;
        existing.LinkDeviceId = edge.LinkDeviceId;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(Guid edgeId, CancellationToken cancellationToken)
    {
        Edges.RemoveAll(edge => edge.Id == edgeId);
        return Task.CompletedTask;
    }

    public Task<bool> RecordDriversAsync(
        Guid edgeId,
        IReadOnlyList<string> driverKeys,
        DateTimeOffset declaredAtUtc,
        CancellationToken cancellationToken)
    {
        if (Edges.FirstOrDefault(edge => edge.Id == edgeId) is not { } edge)
        {
            return Task.FromResult(false);
        }

        edge.DeclaredDriverKeys = driverKeys;
        edge.DriversDeclaredAt = declaredAtUtc;
        Declarations.Add((edgeId, driverKeys, declaredAtUtc));
        return Task.FromResult(true);
    }
}

/// <summary>An audit trail that keeps what it was given, for the tests that assert it was told.</summary>
public sealed class RecordingAuditLog : IAuditLog
{
    public List<AuditEntry> Entries { get; } = [];

    public Task AppendAsync(AuditEntry entry, CancellationToken cancellationToken)
    {
        Entries.Add(entry);
        return Task.CompletedTask;
    }
}
