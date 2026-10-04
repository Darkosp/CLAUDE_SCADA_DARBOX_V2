using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using ScadaDarbox.Core.Drivers;
using ScadaDarbox.Core.Model;
using ScadaDarbox.Core.Security;
using ScadaDarbox.Core.Tags;
using ScadaDarbox.Gateway.Contracts;
using ScadaDarbox.Gateway.Provisioning;
using ScadaDarbox.Gateway.Security;

namespace ScadaDarbox.Gateway.Operations;

/// <summary>
/// Writing a value to a tag — the write path Phase 5 had to build before "cannot write to a
/// tag" meant anything (ADR-0011).
/// </summary>
internal static class TagWriteEndpoints
{
    internal static void MapTagWriteApi(this WebApplication app) =>
        app.MapPost("/api/tags/{tagId:guid}/value", async (
            Guid tagId,
            WriteTagValueRequest request,
            Caller caller,
            TagCatalogSource catalogSource,
            TagWriter writer,
            EdgeWriteRouter edgeWrites,
            IAuditLog audit,
            CancellationToken cancellationToken) =>
        {
            var catalog = catalogSource.Current;
            var tag = catalog.FindTag(tagId);
            var device = tag is null ? null : catalog.FindDevice(tag.DeviceId);

            // Not found, rather than forbidden, for a Site the caller cannot see: a refusal
            // would confirm the tag exists.
            if (tag is null || device is null || !caller.Access.CanView(device.SiteId))
            {
                return Results.NotFound();
            }

            if (!caller.Access.CanOperate(device.SiteId))
            {
                return ApiErrors.Forbidden("Writing a tag needs the Operator role on its Site.");
            }

            if (!tag.IsWritable)
            {
                return Results.BadRequest(new { error = "This tag is not writable." });
            }

            if (!TryReadValue(request.Value, tag.ValueKind, out var value))
            {
                return Results.BadRequest(new { error = $"This tag takes a {tag.ValueKind.ToString().ToLowerInvariant()} value." });
            }

            // A device an edge acquires is not reachable from here (ADR-0019 §3): the link is
            // outbound only, so the write below would open a connection that cannot be made and then
            // report a device error that never happened — an untrue refusal, the class ADR-0003
            // exists to prevent. It is routed to the edge that reads the device instead (ADR-0023),
            // and a deployment may refuse that outright (§8) rather than route it.
            if (catalog.EdgeOfDevice(device.Id) is { } edge)
            {
                return await WriteThroughEdgeAsync(
                    edge,
                    tag,
                    device,
                    value,
                    caller,
                    edgeWrites,
                    audit,
                    cancellationToken).ConfigureAwait(false);
            }

            var detail = Audit.Detail(
                ("siteId", device.SiteId),
                ("deviceId", device.Id),
                ("value", TagValueDto.From(value)));

            try
            {
                await writer.WriteAsync(device, tag, value, cancellationToken);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                // Recorded too: the attempt may have reached the equipment even though it
                // did not report success.
                detail["error"] = exception.Message;
                await audit.AppendAsync(new AuditEntry(caller.UserId, "tag.write_failed", "tag", tag.Id, detail), CancellationToken.None);

                return Results.Json(
                    new { error = $"The device did not accept the write: {exception.Message}" },
                    statusCode: StatusCodes.Status502BadGateway);
            }
            await audit.AppendAsync(new AuditEntry(caller.UserId, "tag.write", "tag", tag.Id, detail), CancellationToken.None);
            return Results.NoContent();
        });

    private static bool TryReadValue(JsonElement json, TagValueKind kind, [NotNullWhen(true)] out TagValue? value)
    {
        value = kind switch
        {
            TagValueKind.Numeric when json.ValueKind == JsonValueKind.Number
                                      && json.TryGetDouble(out var number)
                                      && double.IsFinite(number) => new TagValue.Numeric(number),
            TagValueKind.Boolean when json.ValueKind is JsonValueKind.True or JsonValueKind.False =>
                new TagValue.Boolean(json.GetBoolean()),
            TagValueKind.Text when json.ValueKind == JsonValueKind.String => new TagValue.Text(json.GetString()!),
            TagValueKind.Discrete when json.ValueKind == JsonValueKind.Number && json.TryGetInt32(out var code) =>
                new TagValue.Discrete(code),
            _ => null,
        };

        return value is not null;
    }

    /// <summary>
    /// Hands one write to the edge that reads the device, and turns its answer into the caller's
    /// (ADR-0023).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Nothing is published while writing over the link is off: the setting is a refusal, not a
    /// filter applied after the fact, so a deployment that has turned it off has not asked a plant
    /// to do anything (ADR-0023 §8).
    /// </para>
    /// <para>
    /// The three outcomes are kept apart because they are three different facts: the edge wrote it,
    /// the edge tried and the device refused, and the edge never answered. Collapsing the third into
    /// either of the others would be the untrue answer this path exists to avoid — so it is a
    /// gateway timeout that says the write is **not confirmed**, and no audit row claims otherwise.
    /// </para>
    /// </remarks>
    private static async Task<IResult> WriteThroughEdgeAsync(
        Edge edge,
        Tag tag,
        Device device,
        TagValue value,
        Caller caller,
        EdgeWriteRouter edgeWrites,
        IAuditLog audit,
        CancellationToken cancellationToken)
    {
        var detail = Audit.Detail(
            ("siteId", device.SiteId),
            ("deviceId", device.Id),
            ("edgeId", edge.Id),
            ("value", TagValueDto.From(value)));

        if (!edgeWrites.Enabled)
        {
            await audit.AppendAsync(
                new AuditEntry(caller.UserId, "tag.write_refused", "tag", tag.Id, detail),
                CancellationToken.None);

            return Results.Conflict(new
            {
                error = $"Writing to a device an edge reads is turned off on this deployment, so this tag cannot be written.",
            });
        }

        var outcome = await edgeWrites.WriteAsync(edge.Name, tag.Id, value, cancellationToken).ConfigureAwait(false);

        if (!outcome.Confirmed)
        {
            detail["edge"] = edge.Name;
            await audit.AppendAsync(
                new AuditEntry(caller.UserId, "tag.write_unconfirmed", "tag", tag.Id, detail),
                CancellationToken.None);

            return Results.Json(
                new
                {
                    error = $"Edge '{edge.Name}' did not answer in time, so the write is not confirmed. "
                        + "It may or may not have reached the device.",
                },
                statusCode: StatusCodes.Status504GatewayTimeout);
        }

        if (!outcome.Written)
        {
            detail["edge"] = edge.Name;
            detail["error"] = outcome.Reason;
            await audit.AppendAsync(
                new AuditEntry(caller.UserId, "tag.write_failed", "tag", tag.Id, detail),
                CancellationToken.None);

            return Results.Json(
                new { error = $"Edge '{edge.Name}' could not write it: {outcome.Reason}" },
                statusCode: StatusCodes.Status502BadGateway);
        }

        detail["edge"] = edge.Name;
        await audit.AppendAsync(new AuditEntry(caller.UserId, "tag.write", "tag", tag.Id, detail), CancellationToken.None);
        return Results.NoContent();
    }
}

/// <summary>
/// Hands a write to the driver that owns the tag's device.
/// </summary>
/// <remarks>
/// The write opens its own short-lived connection through the device's driver factory
/// rather than borrowing the scan loop's. The client libraries behind the drivers are not
/// safe for concurrent use, so sharing would mean coordinating every write with a poll in
/// progress; a connection per write costs a little latency on an action an operator takes
/// by hand, which is the cheaper side of that trade.
/// </remarks>
public sealed class TagWriter
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly IReadOnlyDictionary<string, IDeviceDriverFactory> _factoriesByKey;

    public TagWriter(IEnumerable<IDeviceDriverFactory> driverFactories) =>
        _factoriesByKey = driverFactories.ToDictionary(factory => factory.DriverKey, StringComparer.OrdinalIgnoreCase);

    public async Task WriteAsync(Device device, Tag tag, TagValue value, CancellationToken cancellationToken)
    {
        if (!_factoriesByKey.TryGetValue(device.DriverKey, out var factory))
        {
            throw new InvalidOperationException($"Driver '{device.DriverKey}' is not part of this build.");
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(Timeout);

        try
        {
            await using var driver = factory.Create(device);
            await driver.ConnectAsync(deadline.Token).ConfigureAwait(false);
            await driver.WriteAsync(new DriverTag(tag.Id, tag.SourceAddress, tag.ValueKind), value, deadline.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"The device did not answer within {Timeout.TotalSeconds:0} seconds.");
        }
    }
}
