namespace ScadaDarbox.Modules.Drivers.OpcUa;

/// <summary>
/// A server was asked for a secured channel and offers none (ADR-0033 decision 1).
/// </summary>
/// <remarks>
/// <para>
/// **Its own type because its remedy is its own.** Every other connect failure in this driver means
/// roughly *the server did not answer*, and the person who fixes one goes looking at a network. This
/// one means the server answered perfectly well and speaks no security at all, and the person who fixes
/// it either configures the server or decides, deliberately, that an unsecured link is acceptable here.
/// </para>
/// <para>
/// **The message names both values of the setting**, because a refusal that does not say what to type
/// leaves the reader to guess, and the guess they will reach for is to turn the whole thing off.
/// </para>
/// </remarks>
public sealed class OpcUaSecurityUnavailableException : Exception
{
    public OpcUaSecurityUnavailableException(string endpointUrl)
        : base($"The OPC UA server at '{endpointUrl}' offers no secured endpoint, and this device "
               + "requires one (ADR-0033). Configure security on the server, or set the device's "
               + "'security' connection setting to 'none' to accept an unsecured session deliberately.")
    {
        EndpointUrl = endpointUrl;
    }

    /// <summary>The endpoint that was asked, so a caller can name it without parsing the message.</summary>
    public string EndpointUrl { get; }
}
