using Microsoft.Extensions.Logging;
using ScadaDarbox.Gateway;
using ScadaDarbox.Gateway.Security;

namespace ScadaDarbox.Gateway.Tests;

/// <summary>
/// The Gateway refuses to serve in the clear by accident (ADR-0028).
/// </summary>
/// <remarks>
/// <para>
/// **The thing being tested is a refusal, so the control matters as much as the refusal.** A check
/// that rejected every configuration would satisfy the first test here and make the product
/// unstartable, which is why each way of satisfying it is exercised too.
/// </para>
/// <para>
/// These run against <see cref="ServerTlsOptions"/> rather than against a started host, because what
/// is being decided is a configuration question that is answered **before** anything is registered or
/// any database is reached — that ordering is itself part of the decision, so that a deployment with
/// an unreachable database still learns about this one.
/// </para>
/// </remarks>
public sealed class InsecureTransportTests
{
    [Fact]
    public void Saying_nothing_is_refused_and_the_refusal_names_both_ways_out()
    {
        // The case the whole ADR exists for: a deployment that has not chosen. It must not be the
        // quiet one, and the message has to carry an operator from "it will not start" to a running
        // Gateway without them reading the source.
        var problem = new ServerTlsOptions().Problem();

        Assert.NotNull(problem);
        Assert.Contains("CertificatePath", problem, StringComparison.Ordinal);
        Assert.Contains("TlsTerminatedUpstream", problem, StringComparison.Ordinal);
        Assert.Contains("in the clear", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Declaring_that_something_in_front_terminates_TLS_is_accepted()
    {
        // The first control. Without it, a refusal that rejected everything would pass the test above.
        var options = new ServerTlsOptions { TlsTerminatedUpstream = true };

        Assert.Null(options.Problem());
        Assert.False(options.ServesTls);
    }

    [Fact]
    public void A_certificate_and_its_key_are_accepted_and_the_Gateway_serves_TLS_itself()
    {
        // The second control, and the one that needs real files: `Problem` checks that they exist,
        // because a path that is wrong is the failure an operator will actually make and it should
        // be caught at startup rather than on the first request.
        using var files = new PemPair();
        var options = new ServerTlsOptions { CertificatePath = files.Certificate, KeyPath = files.Key };

        Assert.Null(options.Problem());
        Assert.True(options.ServesTls);
    }

    [Fact]
    public void The_certificate_is_actually_loadable_and_not_merely_present()
    {
        // "The file is there" and "the file is a certificate" are different claims, and a deployment
        // that satisfied only the first would start and then fail every connection.
        using var files = new PemPair();
        using var certificate = new ServerTlsOptions { CertificatePath = files.Certificate, KeyPath = files.Key }
            .Certificate();

        Assert.True(certificate.HasPrivateKey, "a server certificate without its key cannot complete a handshake");
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void A_half_given_certificate_is_refused_by_name(bool certificate, bool key)
    {
        // Half a pair is the mistake a hurried edit makes, and it is worth its own refusal: with only
        // a path, `ServesTls` would be true and the handshake would fail later; with only a key,
        // nothing would serve TLS and the deployment would believe it had configured it.
        using var files = new PemPair();
        var options = new ServerTlsOptions
        {
            CertificatePath = certificate ? files.Certificate : null,
            KeyPath = key ? files.Key : null,
        };

        var problem = options.Problem();

        Assert.NotNull(problem);
        Assert.Contains(certificate ? "KeyPath" : "CertificatePath", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_certificate_path_that_points_at_nothing_is_refused_with_the_path_in_it()
    {
        var options = new ServerTlsOptions
        {
            CertificatePath = Path.Combine(Path.GetTempPath(), "no-such-certificate.pem"),
            KeyPath = Path.Combine(Path.GetTempPath(), "no-such-key.pem"),
        };

        var problem = options.Problem();

        Assert.NotNull(problem);
        Assert.Contains("no-such-certificate.pem", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Terminating_TLS_upstream_leaves_the_listeners_alone()
    {
        // **The defect this pins was found by walking a documented configuration, and it stopped the
        // Gateway from starting at all.** The deployment carried `ASPNETCORE_URLS` as the constant
        // `http://+:8080;https://+:8443`, which made the URL list and the certificate setting two
        // things that had to agree. Declare TLS terminated upstream — correctly, as the guide says —
        // and Kestrel died with *"Unable to configure HTTPS endpoint. No server certificate was
        // specified"*. A deployment doing the right thing would not run.
        //
        // The Gateway owns its listeners now, and only when it has a certificate to put on one. These
        // ports must therefore mean nothing without one: if they ever start being consulted anyway,
        // the pair is back and so is the crash.
        var options = new ServerTlsOptions { TlsTerminatedUpstream = true };

        Assert.False(options.ServesTls, "nothing may open an HTTPS listener without a certificate");
        Assert.Null(options.Problem());
    }

    [Fact]
    public void A_Gateway_that_serves_TLS_has_a_port_for_each_listener()
    {
        // The other half: with a certificate there are two listeners, and both need somewhere to be.
        // Defaults rather than required settings, so the common deployment states neither.
        using var files = new PemPair();
        var options = new ServerTlsOptions { CertificatePath = files.Certificate, KeyPath = files.Key };

        Assert.True(options.ServesTls);
        Assert.NotEqual(options.HttpPort, options.HttpsPort);
        Assert.True(options.HttpPort > 0 && options.HttpsPort > 0);
    }

    [Fact]
    public void HSTS_is_off_unless_a_deployment_asks_for_it()
    {
        // ADR-0028 §5, and the opposite of the usual advice. A plant on a self-signed or internal-CA
        // certificate would otherwise hand its operators a browser that refuses the screen with no
        // way past, during an incident. Pinned because "secure by default" is exactly the instinct
        // that would flip it back.
        Assert.False(new ServerTlsOptions().Hsts);
        Assert.False(new ServerTlsOptions { TlsTerminatedUpstream = true }.Hsts);
    }

    [Fact]
    public async Task The_refusal_reaches_a_caller_that_starts_the_real_host()
    {
        // The wiring, which the tests above deliberately do not cover: that `BuildAsync` raises this
        // as the kind of exception `Program` prints as a refusal rather than a stack trace, and that
        // it does so BEFORE the database is reached — the connection string here points nowhere.
        var exception = await Assert.ThrowsAsync<InsecureTransportException>(
            () => GatewayApp.BuildAsync([
                "--ConnectionStrings:ScadaDb=Host=127.0.0.1;Port=1;Database=none;Username=none",
            ]));

        Assert.Contains("TlsTerminatedUpstream", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>A self-signed PEM certificate and key on disk, removed when the test ends.</summary>
    private sealed class PemPair : IDisposable
    {
        public PemPair()
        {
            using var key = System.Security.Cryptography.RSA.Create(2048);
            var request = new System.Security.Cryptography.X509Certificates.CertificateRequest(
                "CN=scada-darbox-test",
                key,
                System.Security.Cryptography.HashAlgorithmName.SHA256,
                System.Security.Cryptography.RSASignaturePadding.Pkcs1);

            using var made = request.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddDays(-1),
                DateTimeOffset.UtcNow.AddDays(1));

            Certificate = Path.Combine(Path.GetTempPath(), $"scada-test-{Guid.NewGuid():N}.crt");
            Key = Path.Combine(Path.GetTempPath(), $"scada-test-{Guid.NewGuid():N}.key");

            File.WriteAllText(Certificate, made.ExportCertificatePem());
            File.WriteAllText(Key, key.ExportPkcs8PrivateKeyPem());
        }

        public string Certificate { get; }

        public string Key { get; }

        public void Dispose()
        {
            File.Delete(Certificate);
            File.Delete(Key);
        }
    }
}
