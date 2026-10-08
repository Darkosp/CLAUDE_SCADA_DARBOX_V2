using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using ScadaDarbox.Core.Drivers;
using ScadaDarbox.Core.Model;
using ScadaDarbox.Modules.Drivers.OpcUa;
using ScadaDarbox.Tools.OpcUaSimulator;
using Xunit;

namespace ScadaDarbox.Drivers.OpcUa.Tests;

/// <summary>
/// What the driver does about the channel to its server (ADR-0033).
/// </summary>
/// <remarks>
/// <para>
/// **Against a real server, deliberately.** Every claim here is about what two OPC UA stacks agree on
/// during a handshake, which is exactly the kind of thing a mock would let us be wrong about for two
/// phases — and that is not hypothetical: `useSecurity: false` shipped through Phase 4, Phase 5 and an
/// ADR of its own while the surrounding `SecurityConfiguration` made it look secured.
/// </para>
/// <para>
/// **The reason a tag reads Bad is a log line, not a field on the reading.** That is how ADR-0003's
/// "say why" is carried for drivers in this codebase — the mechanism both drivers were taught on
/// 2026-10-02 — so the assertions below read the log, as `OpcUaDriverLoggingTests` does.
/// </para>
/// <para>
/// Each test starts its own server, because the question under test *is* what that server offers.
/// </para>
/// </remarks>
public sealed class OpcUaSecurityTests
{
    private static readonly DriverTag PressureTag = new(
        new Guid("44444444-4444-4444-8444-444444444444"),
        "ns=2;s=Pump1.Pressure",
        TagValueKind.Numeric);

    [Fact]
    public async Task A_server_that_offers_security_is_connected_to_securely_without_being_asked()
    {
        // **The default path, and the one the demo runs on** (ADR-0033 decision 5). No `security`
        // setting anywhere: the driver negotiates up because that is what it does now.
        await using var fixture = await Server.StartAsync(offerSecurity: true);

        var log = new RecordingLogger<OpcUaDriver>();
        await using var driver = fixture.Driver(OpcUaSecurity.Required, acceptUntrusted: true, log);

        await driver.ConnectAsync(CancellationToken.None);

        var reading = Assert.Single(await driver.ReadAsync([PressureTag], CancellationToken.None));
        Assert.Equal(Quality.Good, reading.Quality);

        // **The negotiated policy is named** (decision 3). A deprecated policy is accepted rather than
        // refused, so the log is the only thing that can tell a weak channel from a strong one.
        var said = Assert.Single(log.At(LogLevel.Information)).Message;
        Assert.Contains("SignAndEncrypt", said, StringComparison.Ordinal);
        Assert.Contains("Basic256Sha256", said, StringComparison.Ordinal);

        // Nothing warns about the channel. (The fixture's certificate is self-signed, so the
        // untrusted-certificate warning of the next test is expected and is not about security being
        // absent -- which is the thing this assertion is pinning.)
        Assert.DoesNotContain(
            "NO security",
            ReasonsFrom(log),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Accepting_an_untrusted_certificate_is_logged_because_it_is_a_deliberate_weakening()
    {
        // **The setting was never implemented.** It has existed since Phase 4 with a comment arguing
        // for its default, and nothing honoured it: with an unsecured channel no server certificate is
        // presented, so the code path could not run. Turning security on made every existing test in
        // this assembly fail with `BadCertificateUntrusted`, which is how it was found.
        await using var fixture = await Server.StartAsync(offerSecurity: true);

        var log = new RecordingLogger<OpcUaDriver>();
        await using var driver = fixture.Driver(OpcUaSecurity.Required, acceptUntrusted: true, log);

        await driver.ConnectAsync(CancellationToken.None);

        var warned = ReasonsFrom(log);

        Assert.Contains("UNTRUSTED", warned, StringComparison.Ordinal);
        Assert.Contains("ScadaDarbox Simulator", warned, StringComparison.Ordinal);
        Assert.Contains("thumbprint", warned, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_server_that_offers_no_security_is_REFUSED_rather_than_silently_downgraded()
    {
        // The defect ADR-0033 exists to remove. `SelectEndpoint` hands back the `None` endpoint when it
        // is the best on offer rather than failing, so without the check this connects happily and
        // every reading crosses the plant network in the clear with nobody told.
        await using var fixture = await Server.StartAsync(offerSecurity: false);
        await using var driver = fixture.Driver(OpcUaSecurity.Required, acceptUntrusted: true);

        var refusal = await Assert.ThrowsAsync<OpcUaSecurityUnavailableException>(
            () => driver.ConnectAsync(CancellationToken.None));

        // **The message names both values of the setting.** A refusal that does not say what to type
        // leaves the reader guessing, and the guess they reach for is to turn the whole thing off.
        Assert.Contains("'none'", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("ADR-0033", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_same_server_connects_when_the_device_asks_for_no_security_and_is_told_it_did()
    {
        // The opt-out (decision 2). An installation whose server speaks no security is a real
        // situation and must stay serviceable; what it must not be is the default or silent.
        await using var fixture = await Server.StartAsync(offerSecurity: false);

        var log = new RecordingLogger<OpcUaDriver>();
        await using var driver = fixture.Driver(OpcUaSecurity.None, acceptUntrusted: true, log);

        await driver.ConnectAsync(CancellationToken.None);

        var reading = Assert.Single(await driver.ReadAsync([PressureTag], CancellationToken.None));
        Assert.Equal(Quality.Good, reading.Quality);

        // **Every connect, not once at startup.** ADR-0028 logs its own opt-out the same way, for the
        // same reason: a deployment running in the clear should learn it from its log, not an incident.
        var warned = Assert.Single(log.At(LogLevel.Warning)).Message;
        Assert.Contains("NO security", warned, StringComparison.Ordinal);
        Assert.Contains("security=none", warned, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_untrusted_server_certificate_is_refused_and_the_reason_carries_its_thumbprint()
    {
        // Decision 6. `acceptUntrustedCertificates` has existed since Phase 4 and decided **nothing**,
        // because an unsecured channel presents no server certificate to validate. This is the test
        // that it now decides something.
        await using var fixture = await Server.StartAsync(offerSecurity: true);

        var log = new RecordingLogger<OpcUaDriver>();
        await using var driver = fixture.Driver(OpcUaSecurity.Required, acceptUntrusted: false, log);

        await Assert.ThrowsAnyAsync<Exception>(() => driver.ConnectAsync(CancellationToken.None));

        var reading = Assert.Single(await driver.ReadAsync([PressureTag], CancellationToken.None));

        Assert.Equal(Quality.Bad, reading.Quality);
        Assert.Null(reading.Value);

        // The subject and thumbprint, because those are what an operator compares against the server
        // before deciding to trust it. The certificate also lands in `pki/rejected`, and nobody looks
        // in a directory.
        var reason = ReasonsFrom(log);

        Assert.Contains("certificate", reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ScadaDarbox Simulator", reason, StringComparison.Ordinal);
        Assert.Contains("thumbprint", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_refused_certificate_and_an_unreachable_machine_do_not_read_the_same()
    {
        // **The test this whole mechanism exists for.** The two remedies have nothing in common: one
        // is *go and trust a certificate*, the other is *go and find out why the machine is not
        // answering*. Before ADR-0033 both read "the session is not connected", so a reader tried the
        // wrong one first every time.
        await using var fixture = await Server.StartAsync(offerSecurity: true);

        var refusedLog = new RecordingLogger<OpcUaDriver>();
        await using var refused = fixture.Driver(OpcUaSecurity.Required, acceptUntrusted: false, refusedLog);

        await Assert.ThrowsAnyAsync<Exception>(() => refused.ConnectAsync(CancellationToken.None));
        var refusedReading = Assert.Single(await refused.ReadAsync([PressureTag], CancellationToken.None));

        var unpluggedLog = new RecordingLogger<OpcUaDriver>();
        await using var unplugged = new OpcUaDriver(
            $"opc.tcp://localhost:{FreePort()}/NothingIsListening",
            acceptUntrustedCertificates: true,
            OpcUaSecurity.Required,
            TimeProvider.System,
            unpluggedLog);

        await Assert.ThrowsAnyAsync<Exception>(() => unplugged.ConnectAsync(CancellationToken.None));
        var unpluggedReading = Assert.Single(await unplugged.ReadAsync([PressureTag], CancellationToken.None));

        // Both Bad, and that is right: ADR-0003 is about the value, and neither has one. What must
        // differ is the sentence beside it.
        Assert.Equal(Quality.Bad, refusedReading.Quality);
        Assert.Equal(Quality.Bad, unpluggedReading.Quality);

        var refusedReason = ReasonsFrom(refusedLog);
        var unpluggedReason = ReasonsFrom(unpluggedLog);

        Assert.NotEqual(refusedReason, unpluggedReason);
        Assert.Contains("certificate", refusedReason, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("certificate", unpluggedReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_device_whose_certificate_is_trusted_afterwards_reads_normally()
    {
        // The failure reason is remembered on the driver, so it has to stop being reported once the
        // cause is gone. Without that, a device that was fixed keeps telling every reader about a
        // certificate that is now trusted.
        await using var fixture = await Server.StartAsync(offerSecurity: true);

        var log = new RecordingLogger<OpcUaDriver>();
        await using var driver = fixture.Driver(OpcUaSecurity.Required, acceptUntrusted: false, log);

        await Assert.ThrowsAnyAsync<Exception>(() => driver.ConnectAsync(CancellationToken.None));
        _ = await driver.ReadAsync([PressureTag], CancellationToken.None);

        Assert.Contains("certificate", ReasonsFrom(log), StringComparison.OrdinalIgnoreCase);

        var trustingLog = new RecordingLogger<OpcUaDriver>();
        await using var trusting = fixture.Driver(OpcUaSecurity.Required, acceptUntrusted: true, trustingLog);

        await trusting.ConnectAsync(CancellationToken.None);

        var reading = Assert.Single(await trusting.ReadAsync([PressureTag], CancellationToken.None));

        Assert.Equal(Quality.Good, reading.Quality);
        Assert.NotNull(reading.Value);

        // **Not "no mention of a certificate"** -- accepting an untrusted one is itself logged, and
        // should be. What must be gone is the refusal: the sentence that tells an operator a reading
        // could not be had. My first version of this assertion looked for the word "certificate" and
        // failed against the acceptance warning, which is the test being wrong rather than the code.
        Assert.DoesNotContain("is not trusted", ReasonsFrom(trustingLog), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Every warning the driver wrote, joined: the mechanism that carries "say why".</summary>
    private static string ReasonsFrom(RecordingLogger<OpcUaDriver> log) =>
        string.Join(" | ", log.At(LogLevel.Warning).Select(entry => entry.Message));

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    /// <summary>A simulator on its own port, with the drivers that talk to it.</summary>
    private sealed class Server : IAsyncDisposable
    {
        private SimulatorServer _server = null!;
        private int _port;

        public static async Task<Server> StartAsync(bool offerSecurity)
        {
            var fixture = new Server { _port = FreePort() };
            fixture._server = await SimulatorServer.StartAsync(
                fixture._port,
                CancellationToken.None,
                offerSecurity);

            return fixture;
        }

        public OpcUaDriver Driver(
            OpcUaSecurity security,
            bool acceptUntrusted,
            ILogger<OpcUaDriver>? logger = null) =>
            new($"opc.tcp://localhost:{_port}/ScadaDarboxSimulator",
                acceptUntrusted,
                security,
                TimeProvider.System,
                logger);

        public async ValueTask DisposeAsync()
        {
            await _server.StopAsync(CancellationToken.None);
            _server.Dispose();
        }
    }
}
