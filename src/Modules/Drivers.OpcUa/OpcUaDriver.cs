using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Configuration;
using ScadaDarbox.Core.Drivers;
using ScadaDarbox.Core.Model;

namespace ScadaDarbox.Modules.Drivers.OpcUa;

/// <summary>
/// Reads and writes tags over OPC UA. A module: it depends only on core's public
/// contracts and is composed at compile time (ADR-0002).
/// </summary>
/// <remarks>
/// Unlike Modbus, OPC UA carries a real device-side source timestamp and a status code
/// on every value. Both are used as they arrive rather than substituted — the source
/// timestamp is exactly what ADR-0003 asks for, and the status code is the model
/// ADR-0003's own quality scale was taken from. The status code, the parser's own words and
/// the connection's state are each written to the log as the reason for a Bad reading:
/// a mistyped address and a device that is not answering look identical otherwise.
/// </remarks>
public sealed class OpcUaDriver : IDeviceDriver
{
    private readonly string _endpointUrl;
    private readonly bool _acceptUntrustedCertificates;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;

    /// <summary>
    /// The stack asks every current entry point for a telemetry context. Built once and
    /// shared: it only routes the library's own logging, and nothing here subscribes to
    /// it, but the deprecated overloads that omit it are the ones being retired.
    /// </summary>
    private static readonly ITelemetryContext Telemetry = DefaultTelemetry.Create(_ => { });

    /// <summary>
    /// The one words for "there is no session to ask", used at both places that need it so
    /// that a fault repeating every scan compares equal to itself and is named once.
    /// </summary>
    private const string NotConnected = "the session is not connected";

    /// <summary>The reason each tag last read Bad, so a standing fault is named once.</summary>
    /// <remarks>
    /// A fault that repeats every scan is one condition, not an event per second: writing it
    /// again each time buries the line that says what is actually wrong — the same reasoning
    /// the alarm journal uses for a flapping value. The entry is dropped when the tag reads
    /// again, so a fault that returns is named again, and a reason that changes is a new
    /// message rather than one swallowed by the first.
    /// <para>
    /// No lock: a device's tags are read by that device's own scan loop, one at a time, and
    /// nothing else touches this. Writes do not.
    /// </para>
    /// </remarks>
    private readonly Dictionary<Guid, string> _badReasons = [];

    private ISession? _session;

    public OpcUaDriver(
        string endpointUrl,
        bool acceptUntrustedCertificates,
        TimeProvider timeProvider,
        ILogger<OpcUaDriver>? logger = null)
    {
        _endpointUrl = endpointUrl;
        _acceptUntrustedCertificates = acceptUntrustedCertificates;
        _timeProvider = timeProvider;

        // Optional so a composition that deliberately keeps no log — a unit test, a tool —
        // needs no argument, and the same way the MQTT module takes its logger.
        _logger = logger ?? NullLogger<OpcUaDriver>.Instance;
    }

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        await DisposeSessionAsync().ConfigureAwait(false);

        var configuration = await BuildConfigurationAsync().ConfigureAwait(false);

        // Security is deliberately not negotiated up in Phase 4. Certificates, policies
        // and user identity belong with the auth work in Phase 5, and choosing them here
        // would pre-empt a decision that has its own phase.
        var endpoint = await CoreClientUtils.SelectEndpointAsync(
            configuration,
            _endpointUrl,
            useSecurity: false,
            telemetry: Telemetry,
            cancellationToken).ConfigureAwait(false);

        var description = new ConfiguredEndpoint(null, endpoint, EndpointConfiguration.Create(configuration));

        _session = await new DefaultSessionFactory(Telemetry).CreateAsync(
            configuration,
            description,
            updateBeforeConnect: false,
            checkDomain: false,
            sessionName: "ScadaDarbox",
            sessionTimeout: 60_000,
            identity: new UserIdentity(new AnonymousIdentityToken()),
            preferredLocales: null,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<TagReading>> ReadAsync(
        IReadOnlyList<DriverTag> tags,
        CancellationToken cancellationToken)
    {
        var readings = new List<TagReading>(tags.Count);
        var readable = new List<(DriverTag Tag, NodeId NodeId)>(tags.Count);

        foreach (var tag in tags)
        {
            if (OpcUaAddress.TryParse(tag.SourceAddress, out var nodeId, out var error))
            {
                readable.Add((tag, nodeId));
            }
            else
            {
                // A misconfigured address is a permanent fault, but it is still reported as
                // Bad rather than thrown: one broken tag must not stop the device's scan.
                // The parser keeps its own words for the reason, which is the thing that was
                // being discarded here — a mistyped address and an unplugged device are not
                // the same fault, and there was nothing anywhere to tell them apart.
                readings.Add(Bad(tag, error));
            }
        }

        if (readable.Count == 0 || _session is not { Connected: true } session)
        {
            readings.AddRange(readable.Select(entry => Bad(entry.Tag, NotConnected)));
            return readings;
        }

        try
        {
            var nodesToRead = new ReadValueIdCollection(
                readable.Select(entry => new ReadValueId
                {
                    NodeId = entry.NodeId,
                    AttributeId = Attributes.Value,
                }));

            // The raw Read service rather than a convenience wrapper, because
            // TimestampsToReturn.Source is the whole point: it is what carries the
            // device's own capture time (ADR-0003).
            var response = await session.ReadAsync(
                requestHeader: null,
                maxAge: 0,
                timestampsToReturn: TimestampsToReturn.Both,
                nodesToRead: nodesToRead,
                cancellationToken).ConfigureAwait(false);

            for (var index = 0; index < readable.Count; index++)
            {
                readings.Add(ToReading(readable[index].Tag, response.Results[index]));
            }
        }
        catch (Exception exception) when (exception is ServiceResultException or IOException or TimeoutException)
        {
            // The session is gone or the server refused. There is no value for any of
            // them, and a fabricated one would be indistinguishable from a real reading.
            // What the stack said happened is the reason, and it is the difference between
            // this and every other way a tag can read Bad.
            readings.AddRange(readable.Select(entry => Bad(entry.Tag, exception)));
        }

        return readings;
    }

    public async Task WriteAsync(DriverTag tag, TagValue value, CancellationToken cancellationToken)
    {
        if (_session is not { Connected: true } session)
        {
            throw new InvalidOperationException("Cannot write before ConnectAsync has succeeded.");
        }

        if (!OpcUaAddress.TryParse(tag.SourceAddress, out var nodeId, out var error))
        {
            throw new FormatException(error);
        }

        var write = new WriteValue
        {
            NodeId = nodeId,
            AttributeId = Attributes.Value,
            Value = new DataValue(new Variant(ToNative(value))),
        };

        var response = await session.WriteAsync(null, [write], cancellationToken).ConfigureAwait(false);

        if (StatusCode.IsBad(response.Results[0]))
        {
            throw new InvalidOperationException(
                $"The server refused the write to {tag.SourceAddress}: {response.Results[0]}.");
        }
    }

    /// <summary>Turns one OPC UA value into a reading, keeping the server's own view of it.</summary>
    private TagReading ToReading(DriverTag tag, DataValue value)
    {
        if (StatusCode.IsBad(value.StatusCode))
        {
            // The server's own status is the reason, and it is the one worth having: it is
            // what separates a node id the server does not know from a server that is not
            // answering at all.
            return Bad(tag, $"the server answered {value.StatusCode}");
        }

        var converted = Convert(tag.ValueKind, value.Value);

        if (converted is null)
        {
            // The server answered, but with something this tag cannot hold — a text value
            // on a numeric tag, say. That is a configuration mismatch, not a reading, and
            // naming the shape that arrived is what makes it visible as one.
            return Bad(tag, value.Value is null
                ? "the server answered with no value at all"
                : $"the server answered with a {value.Value.GetType().Name}, which a {tag.ValueKind} tag cannot hold");
        }

        // SourceTimestamp is when the device captured the value, which is precisely what
        // ADR-0003 asks for and what Modbus cannot supply. It is only replaced when the
        // server omits it entirely.
        var sourceTimestamp = value.SourceTimestamp == DateTime.MinValue
            ? _timeProvider.GetUtcNow()
            : new DateTimeOffset(value.SourceTimestamp, TimeSpan.Zero);

        var quality = StatusCode.IsUncertain(value.StatusCode) ? Quality.Uncertain : Quality.Good;

        var reading = new TagReading(tag.TagId, converted, sourceTimestamp, quality);
        ReportRecovery(tag);
        return reading;
    }

    private static TagValue? Convert(TagValueKind kind, object? raw) => raw switch
    {
        null => null,
        _ when kind == TagValueKind.Boolean && raw is bool flag => new TagValue.Boolean(flag),
        _ when kind == TagValueKind.Text => new TagValue.Text(raw.ToString() ?? string.Empty),
        _ when kind == TagValueKind.Discrete && raw is IConvertible => new TagValue.Discrete(
            System.Convert.ToInt32(raw, System.Globalization.CultureInfo.InvariantCulture)),
        _ when kind == TagValueKind.Numeric && raw is IConvertible and not string and not bool =>
            new TagValue.Numeric(System.Convert.ToDouble(raw, System.Globalization.CultureInfo.InvariantCulture)),
        _ => null,
    };

    private static object ToNative(TagValue value) => value switch
    {
        TagValue.Numeric numeric => numeric.Value,
        TagValue.Boolean boolean => boolean.Value,
        TagValue.Text text => text.Value,
        TagValue.Discrete discrete => discrete.Code,
        _ => throw new NotSupportedException($"Cannot write a {value.Kind} value over OPC UA."),
    };

    /// <remarks>No value is supplied — not a zero, not a false (ADR-0003).</remarks>
    private TagReading Bad(DriverTag tag, string reason)
    {
        ReportFault(tag, reason);
        return new(tag.TagId, null, _timeProvider.GetUtcNow(), Quality.Bad);
    }

    /// <summary>The same reading, for a failure the stack described itself.</summary>
    private TagReading Bad(DriverTag tag, Exception exception)
    {
        // The exception is carried as well as its message: the message is the reason an
        // operator reads, and the stack is what is needed when it is not self-explanatory.
        ReportFault(tag, exception.Message, exception);
        return new(tag.TagId, null, _timeProvider.GetUtcNow(), Quality.Bad);
    }

    private void ReportFault(DriverTag tag, string reason, Exception? exception = null)
    {
        if (_badReasons.TryGetValue(tag.TagId, out var previous) && previous == reason)
        {
            // Already said, and nothing about it has changed.
            return;
        }

        _badReasons[tag.TagId] = reason;

        _logger.LogWarning(
            exception,
            "Tag {TagId} (node '{SourceAddress}') reads Bad at {EndpointUrl}: {Reason}",
            tag.TagId,
            tag.SourceAddress,
            _endpointUrl,
            reason);
    }

    private void ReportRecovery(DriverTag tag)
    {
        // Only for a tag that had a fault to report: an ordinary Good reading is not news.
        if (_badReasons.Remove(tag.TagId))
        {
            _logger.LogInformation(
                "Tag {TagId} (node '{SourceAddress}') at {EndpointUrl} reads Good again.",
                tag.TagId,
                tag.SourceAddress,
                _endpointUrl);
        }
    }

    private async Task<ApplicationConfiguration> BuildConfigurationAsync()
    {
        var configuration = new ApplicationConfiguration
        {
            ApplicationName = "ScadaDarbox",
            ApplicationUri = "urn:scadadarbox:gateway",
            ApplicationType = ApplicationType.Client,
            SecurityConfiguration = new SecurityConfiguration
            {
                ApplicationCertificate = new CertificateIdentifier
                {
                    StoreType = CertificateStoreType.Directory,
                    StorePath = Path.Combine(AppContext.BaseDirectory, "pki", "own"),
                    SubjectName = "CN=ScadaDarbox, O=Darbo",
                },
                TrustedPeerCertificates = new CertificateTrustList
                {
                    StoreType = CertificateStoreType.Directory,
                    StorePath = Path.Combine(AppContext.BaseDirectory, "pki", "trusted"),
                },
                TrustedIssuerCertificates = new CertificateTrustList
                {
                    StoreType = CertificateStoreType.Directory,
                    StorePath = Path.Combine(AppContext.BaseDirectory, "pki", "issuers"),
                },
                RejectedCertificateStore = new CertificateTrustList
                {
                    StoreType = CertificateStoreType.Directory,
                    StorePath = Path.Combine(AppContext.BaseDirectory, "pki", "rejected"),
                },
                // Off unless a device opts in. Accepting any server certificate means
                // trusting whatever answers on that address, which is a decision an
                // installation should make on purpose rather than inherit as a default.
                AutoAcceptUntrustedCertificates = _acceptUntrustedCertificates,
            },
            TransportConfigurations = [],
            TransportQuotas = new TransportQuotas { OperationTimeout = 15_000 },
            ClientConfiguration = new ClientConfiguration { DefaultSessionTimeout = 60_000 },
            TraceConfiguration = new TraceConfiguration(),
        };

        await configuration.ValidateAsync(ApplicationType.Client, CancellationToken.None)
            .ConfigureAwait(false);

        // Creates a self-signed client certificate on first run. The stack requires the
        // client to have one even when the channel itself is unsecured.
        var application = new ApplicationInstance(configuration, Telemetry);
        await application.CheckApplicationInstanceCertificatesAsync(
            true,
            null,
            CancellationToken.None).ConfigureAwait(false);

        return configuration;
    }

    public async ValueTask DisposeAsync() => await DisposeSessionAsync().ConfigureAwait(false);

    private async Task DisposeSessionAsync()
    {
        if (_session is null)
        {
            return;
        }

        try
        {
            await _session.CloseAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Closing a session that is already gone is not a failure worth reporting.
        }

        _session.Dispose();
        _session = null;
    }
}
