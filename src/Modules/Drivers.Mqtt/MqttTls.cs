using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using MQTTnet;

namespace ScadaDarbox.Modules.Drivers.Mqtt;

/// <summary>
/// The files for mutual TLS to the broker (ADR-0017): the certificate authority that signed the
/// broker's certificate, and this client's own certificate and private key, all PEM.
/// </summary>
/// <remarks>
/// A certificate per client, not a shared password: each edge is its own identity, and the
/// broker's ACL confines it to its own topic by the name in its certificate.
/// </remarks>
public sealed record MqttTlsFiles(string CaFile, string CertFile, string KeyFile)
{
    /// <summary>Why these files cannot be used, or nothing.</summary>
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();
        foreach (var (name, path) in new[] { ("CA certificate", CaFile), ("client certificate", CertFile), ("client key", KeyFile) })
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                problems.Add($"the {name} file is not set");
            }
            else if (!File.Exists(path))
            {
                problems.Add($"the {name} file '{path}' does not exist");
            }
        }

        return problems;
    }
}

/// <summary>Mutual TLS for an MQTT client, trusting one certificate authority and nothing else.</summary>
public static class MqttTls
{
    /// <summary>
    /// Connects over TLS, presents this client's certificate, and accepts the broker only if its
    /// certificate was issued by the given CA and names the host being connected to.
    /// </summary>
    public static MqttClientOptionsBuilder WithMutualTls(this MqttClientOptionsBuilder builder, MqttTlsFiles files)
    {
        var authority = X509CertificateLoader.LoadCertificateFromFile(files.CaFile);
        var client = LoadClientCertificate(files);

        return builder.WithTlsOptions(tls => tls
            .UseTls()
            .WithClientCertificates(new X509Certificate2Collection(client))
            .WithCertificateValidationHandler(context => IsIssuedBy(authority, context.Certificate, context.SslPolicyErrors)));
    }

    /// <summary>
    /// The broker's certificate is accepted when the only fault the platform found is that it does
    /// not know our CA, and a chain built on that CA alone succeeds. A name mismatch or anything
    /// else is refused: trusting our own CA is not the same as trusting whatever it is shown.
    /// </summary>
    internal static bool IsIssuedBy(X509Certificate2 authority, X509Certificate? presented, SslPolicyErrors errors)
    {
        if (presented is null || (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) != SslPolicyErrors.None)
        {
            return false;
        }

        using var certificate = X509CertificateLoader.LoadCertificate(presented.GetRawCertData());
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(authority);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        return chain.Build(certificate);
    }

    private static X509Certificate2 LoadClientCertificate(MqttTlsFiles files)
    {
        // Loaded from PEM the key is ephemeral, and Windows' TLS stack will not present an
        // ephemeral key as a client certificate. A round trip through PKCS#12 gives it a key it
        // will use; on Linux it changes nothing.
        using var pem = X509Certificate2.CreateFromPemFile(files.CertFile, files.KeyFile);
        return X509CertificateLoader.LoadPkcs12(pem.Export(X509ContentType.Pkcs12), password: null);
    }
}
