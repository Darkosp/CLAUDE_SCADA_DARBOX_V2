using System.Security.Cryptography.X509Certificates;

namespace ScadaDarbox.Gateway.Security;

/// <summary>
/// How this Gateway is reached: over TLS it terminates itself, or over HTTP because something in
/// front of it terminates TLS instead (ADR-0028).
/// </summary>
/// <remarks>
/// <para>
/// There is no third state. A deployment that has said neither is refused at startup, because the
/// dangerous configuration is the one nobody chose: a Gateway that quietly served HTTP would be
/// secure only for as long as whoever deployed it happened to remember, and the first person to find
/// out otherwise would be whoever read a password off the wire.
/// </para>
/// <para>
/// Bound from the <c>Server</c> section, so every name below is <c>Server__…</c> in an environment.
/// </para>
/// </remarks>
public sealed class ServerTlsOptions
{
    public const string Section = "Server";

    /// <summary>The PEM certificate chain this Gateway serves, or null when something else terminates TLS.</summary>
    public string? CertificatePath { get; set; }

    /// <summary>The PEM private key for <see cref="CertificatePath"/>.</summary>
    public string? KeyPath { get; set; }

    /// <summary>
    /// Declares that TLS is terminated in front of this Gateway, so serving HTTP here is deliberate.
    /// </summary>
    /// <remarks>
    /// The same setting a development run uses, on purpose: a development-only code path is a path
    /// that exists to be forgotten. What makes development safe is that the shipped image does not run
    /// in the Development environment, not that it takes a different route through this file.
    /// </remarks>
    public bool TlsTerminatedUpstream { get; set; }

    /// <summary>
    /// Whether to send HSTS. **Off unless asked for**, which is the opposite of the usual advice.
    /// </summary>
    /// <remarks>
    /// HSTS is remembered by the browser and cannot be overridden by the person using it. A plant
    /// whose certificate is self-signed or signed by an internal CA — which is most of them — would
    /// hand its operators a browser that refuses the screen with no way past, during an incident, on
    /// the one machine that matters. A deployment with a publicly trusted certificate should turn this
    /// on; one that cannot should not be given it silently (ADR-0028 §5).
    /// </remarks>
    public bool Hsts { get; set; }

    /// <summary>Whether this Gateway is to terminate TLS itself.</summary>
    public bool ServesTls => !string.IsNullOrWhiteSpace(CertificatePath);

    /// <summary>
    /// Why this configuration cannot be served, or null when it can.
    /// </summary>
    /// <remarks>
    /// A message rather than an exception type per fault: the caller turns whichever of these it gets
    /// into one refusal, and what a reader needs is the sentence, not the class.
    /// </remarks>
    public string? Problem()
    {
        if (ServesTls)
        {
            if (string.IsNullOrWhiteSpace(KeyPath))
            {
                return $"{Section}:CertificatePath is set and {Section}:KeyPath is not. "
                    + "A certificate without its key cannot be served.";
            }

            if (!File.Exists(CertificatePath))
            {
                return $"{Section}:CertificatePath is '{CertificatePath}', and there is no file there.";
            }

            return File.Exists(KeyPath)
                ? null
                : $"{Section}:KeyPath is '{KeyPath}', and there is no file there.";
        }

        if (!string.IsNullOrWhiteSpace(KeyPath))
        {
            return $"{Section}:KeyPath is set and {Section}:CertificatePath is not.";
        }

        return TlsTerminatedUpstream
            ? null
            : "This Gateway has no TLS certificate and has not been told that anything in front of it "
                + "terminates TLS, so it would serve sign-ins and session tokens in the clear "
                + "(ADR-0028).\n"
                + $"  Give it a certificate:  {Section}__CertificatePath and {Section}__KeyPath (PEM).\n"
                + $"  Or say what is in front: {Section}__TlsTerminatedUpstream=true.";
    }

    /// <summary>The certificate to serve, read from the PEM pair.</summary>
    /// <remarks>
    /// PEM rather than PFX because that is what every certificate authority hands over and what the
    /// broker's own files already are (ADR-0017), so a deployment holds one kind of file. Read once at
    /// startup: a renewed certificate takes a restart, which the guide says.
    /// </remarks>
    public X509Certificate2 Certificate() =>
        X509Certificate2.CreateFromPemFile(CertificatePath!, KeyPath!);
}

/// <summary>
/// The Gateway was started without saying how it is reached (ADR-0028).
/// </summary>
public sealed class InsecureTransportException(string message) : Exception(message);
