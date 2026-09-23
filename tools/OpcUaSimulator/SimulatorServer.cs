using Opc.Ua;
using Opc.Ua.Configuration;
using Opc.Ua.Server;

namespace ScadaDarbox.Tools.OpcUaSimulator;

/// <summary>
/// A minimal OPC UA server exposing one simulated pump, so the driver can be exercised
/// end to end without hardware.
/// </summary>
/// <remarks>
/// Not part of the product: it lives under tools/ and nothing in src/ references it.
/// The same shape as the Modbus simulator — the point of both is that a driver is
/// verified against something that actually speaks the protocol.
/// </remarks>
public sealed class SimulatorServer : StandardServer
{
    private PumpNodeManager? _pumps;

    /// <summary>The address space, once the server has started.</summary>
    public PumpNodeManager Pumps =>
        _pumps ?? throw new InvalidOperationException("The server has not started yet.");

    protected override MasterNodeManager CreateMasterNodeManager(
        IServerInternal server,
        ApplicationConfiguration configuration)
    {
        _pumps = new PumpNodeManager(server, configuration);
        return new MasterNodeManager(server, configuration, null, [_pumps]);
    }

    /// <summary>
    /// Starts a server on the given port and returns it, already accepting sessions.
    /// <paramref name="host"/> is the name in the endpoint URL clients are told to use.
    /// </summary>
    /// <remarks>
    /// Security is deliberately minimal: an unsecured endpoint and anonymous users. This
    /// is a test fixture, and matching the driver's Phase 4 posture keeps the exercise
    /// honest rather than testing a configuration nobody runs.
    /// </remarks>
    public static async Task<SimulatorServer> StartAsync(string host, int port, CancellationToken cancellationToken)
    {
        var telemetry = DefaultTelemetry.Create(_ => { });

        var configuration = new ApplicationConfiguration
        {
            ApplicationName = "ScadaDarbox Simulator",
            // Deliberately free of the port. The certificate is cached on disk and its
            // subject has to keep matching this URI, so folding a per-run port into it
            // invalidates the certificate on the next run with a different port.
            ApplicationUri = "urn:localhost:scadadarbox:simulator",
            ApplicationType = ApplicationType.Server,
            ServerConfiguration = new ServerConfiguration
            {
                BaseAddresses = [$"opc.tcp://{host}:{port}/ScadaDarboxSimulator"],
                SecurityPolicies =
                [
                    new ServerSecurityPolicy
                    {
                        SecurityMode = MessageSecurityMode.None,
                        SecurityPolicyUri = SecurityPolicies.None,
                    },
                ],
                UserTokenPolicies = [new UserTokenPolicy(UserTokenType.Anonymous)],
                MaxSessionCount = 10,
                MinSessionTimeout = 10_000,
                MaxSessionTimeout = 3_600_000,
                DiagnosticsEnabled = false,
            },
            SecurityConfiguration = new SecurityConfiguration
            {
                ApplicationCertificate = new CertificateIdentifier
                {
                    StoreType = CertificateStoreType.Directory,
                    // One per host name: the certificate lists the name clients reach the server
                    // by, and the stack refuses one issued for another name — so a certificate
                    // made for "localhost" cannot serve a container reached by its service name.
                    StorePath = Path.Combine(AppContext.BaseDirectory, "simulator-pki", "own", host),
                    SubjectName = "CN=ScadaDarbox Simulator, O=Darbo",
                },
                TrustedPeerCertificates = new CertificateTrustList
                {
                    StoreType = CertificateStoreType.Directory,
                    StorePath = Path.Combine(AppContext.BaseDirectory, "simulator-pki", "trusted"),
                },
                TrustedIssuerCertificates = new CertificateTrustList
                {
                    StoreType = CertificateStoreType.Directory,
                    StorePath = Path.Combine(AppContext.BaseDirectory, "simulator-pki", "issuers"),
                },
                RejectedCertificateStore = new CertificateTrustList
                {
                    StoreType = CertificateStoreType.Directory,
                    StorePath = Path.Combine(AppContext.BaseDirectory, "simulator-pki", "rejected"),
                },
                AutoAcceptUntrustedCertificates = true,
            },
            TransportConfigurations = [],
            TransportQuotas = new TransportQuotas { OperationTimeout = 15_000 },
            TraceConfiguration = new TraceConfiguration(),
        };

        await configuration.ValidateAsync(ApplicationType.Server, cancellationToken).ConfigureAwait(false);

        var application = new ApplicationInstance(configuration, telemetry);
        await application.CheckApplicationInstanceCertificatesAsync(true, null, cancellationToken)
            .ConfigureAwait(false);

        var server = new SimulatorServer();
        await application.StartAsync(server).ConfigureAwait(false);

        return server;
    }
}
