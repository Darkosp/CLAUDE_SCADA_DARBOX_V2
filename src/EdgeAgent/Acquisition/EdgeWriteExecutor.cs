using Microsoft.Extensions.Logging;
using ScadaDarbox.Core.Drivers;
using ScadaDarbox.Core.Model;
using ScadaDarbox.EdgeAgent.Configuration;
using ScadaDarbox.Modules.Drivers.Mqtt;

namespace ScadaDarbox.EdgeAgent.Acquisition;

/// <summary>
/// Carries out a write the cloud has asked for, through the same driver module that reads the
/// device (ADR-0023).
/// </summary>
/// <remarks>
/// <para>
/// The device is found in the configuration this edge last accepted, so a write can only reach a
/// device the cloud has actually assigned to this edge — the address it is written at is the
/// address in that configuration, not anything the write message carries. A message naming a tag
/// this edge does not hold is refused rather than guessed at, which is the same rule the sample
/// reader applies in the other direction.
/// </para>
/// <para>
/// The connection is opened for the write and closed after it, deliberately not borrowed from the
/// scan loop: the client libraries behind the drivers are not safe for concurrent use, and a scan
/// loop that held a write's connection would be coordinating every poll with an operator's action.
/// This is the same trade the Gateway's own <c>TagWriter</c> makes for a device it polls.
/// </para>
/// <para>
/// <b>Nothing here decides whether a write is allowed to happen.</b> The cloud has already checked
/// the operator's permission and the tag's writability before publishing (ADR-0023 §6), and what
/// arrives here is a request that was permitted. The edge's own job is to carry it out and say
/// what happened.
/// </para>
/// </remarks>
public sealed class EdgeWriteExecutor
{
    /// <summary>
    /// How long a device is given to answer one write. The same bound the Gateway's write path
    /// uses for a device it polls, because it is the same kind of operation.
    /// </summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly EdgeConfigurationSource _configuration;
    private readonly IReadOnlyDictionary<string, IDeviceDriverFactory> _factories;
    private readonly ILogger<EdgeWriteExecutor> _logger;

    public EdgeWriteExecutor(
        EdgeConfigurationSource configuration,
        IEnumerable<IDeviceDriverFactory> factories,
        ILogger<EdgeWriteExecutor> logger)
    {
        _configuration = configuration;
        _factories = factories.ToDictionary(factory => factory.DriverKey, StringComparer.OrdinalIgnoreCase);
        _logger = logger;
    }

    /// <summary>
    /// Carries out one write request. The result is the answer to send back, and it always says
    /// something: a write that could not be attempted is reported as failed with the reason, never
    /// left unanswered.
    /// </summary>
    public async Task<WriteResultResult> ExecuteAsync(WriteRequestResult request, CancellationToken cancellationToken)
    {
        var (device, tag) = Find(request.TagId);

        if (device is null || tag is null)
        {
            _logger.LogWarning(
                "A write arrived for tag {Tag}, which is not in the configuration this edge reads; refusing it.",
                request.TagId);

            return WriteResultResult.Refused($"this edge does not read tag {request.TagId}");
        }

        if (!_factories.TryGetValue(device.Driver, out var factory))
        {
            _logger.LogWarning(
                "A write arrived for tag {Tag} on device {Device}, which needs driver '{Driver}' and this edge agent does not have it.",
                request.TagId,
                device.Name,
                device.Driver);

            return WriteResultResult.Refused($"this edge has no '{device.Driver}' driver");
        }

        var target = ToDevice(device);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(Timeout);

        try
        {
            await using var driver = factory.Create(target);
            await driver.ConnectAsync(deadline.Token).ConfigureAwait(false);

            // The same call the Gateway's write path makes for a device it polls. ADR-0002's
            // boundary is what makes that possible: the module is the same code on both sides.
            await driver.WriteAsync(new DriverTag(tag.TagId, tag.Address, tag.Kind), request.Value!, deadline.Token)
                .ConfigureAwait(false);

            _logger.LogInformation(
                "Wrote tag {Tag} on device {Device} as the cloud asked.",
                request.TagId,
                device.Name);

            return new WriteResultResult(request.WriteId, request.TagId, Written: true, Reason: null, Refusal: null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The deadline is the edge's own, and saying so is more use than "it failed": a device
            // that accepts a connection and then stops answering is a plant fault, not a bad request.
            var reason = $"the device did not answer within {Timeout.TotalSeconds:0} seconds";

            _logger.LogWarning(
                "A write to tag {Tag} on device {Device} timed out: {Reason}.",
                request.TagId,
                device.Name,
                reason);

            return new WriteResultResult(request.WriteId, request.TagId, Written: false, reason, Refusal: null);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(
                exception,
                "A write to tag {Tag} on device {Device} failed.",
                request.TagId,
                device.Name);

            return new WriteResultResult(
                request.WriteId,
                request.TagId,
                Written: false,
                exception.Message,
                Refusal: null);
        }
    }

    /// <summary>The device and the tag it holds, from the configuration in force, or nothing.</summary>
    private (EdgeConfigurationDevice? Device, EdgeConfigurationTag? Tag) Find(Guid tagId)
    {
        foreach (var device in _configuration.Devices)
        {
            foreach (var tag in device.Tags)
            {
                if (tag.TagId == tagId)
                {
                    return (device, tag);
                }
            }
        }

        return (null, null);
    }

    /// <summary>
    /// The configuration's device as the drivers want one. The id is the edge's own — nothing about
    /// this device is stored anywhere but the configuration, which is the point of ADR-0019 — and
    /// the connection settings are the cloud's, unchanged.
    /// </summary>
    private static Device ToDevice(EdgeConfigurationDevice device) => new()
    {
        Id = Guid.NewGuid(),
        SiteId = Guid.Empty,
        Name = device.Name,
        DriverKey = device.Driver,
        ConnectionSettings = device.Settings,
        ScanInterval = TimeSpan.FromMilliseconds(device.ScanIntervalMs),
    };
}
