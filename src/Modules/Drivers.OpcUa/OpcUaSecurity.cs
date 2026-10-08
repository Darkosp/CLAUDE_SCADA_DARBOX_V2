namespace ScadaDarbox.Modules.Drivers.OpcUa;

/// <summary>
/// What a device expects of the channel to its OPC UA server (ADR-0033).
/// </summary>
/// <remarks>
/// <para>
/// **Two values, not a scale.** The driver always takes the strongest endpoint the server offers, so
/// there is nothing here to tune: the only question a deployment answers is whether an unsecured server
/// is acceptable at all. A per-device policy pin was considered and refused in ADR-0033 — "the strongest
/// the server offers" needs no knowledge, and a pin is a setting that has to be kept correct as servers
/// are upgraded.
/// </para>
/// </remarks>
public enum OpcUaSecurity
{
    /// <summary>
    /// The default. The strongest endpoint the server offers, and a **refusal** if it offers none.
    /// </summary>
    /// <remarks>
    /// A server that speaks no security is not quietly fallen back to. A downgrade nobody asked for is
    /// the defect ADR-0033 exists to remove, and one that happens automatically on a bad day is the same
    /// defect with worse timing.
    /// </remarks>
    Required,

    /// <summary>
    /// An unsecured, anonymous session — what this driver did unconditionally before ADR-0033.
    /// </summary>
    /// <remarks>
    /// An installation whose server speaks no security is a real situation and this product must not
    /// make it unservable. What it must not be is the **default**, or something that happens without
    /// anybody choosing it, which is why selecting this is logged at every connect.
    /// </remarks>
    None,
}
