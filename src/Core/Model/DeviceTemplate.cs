namespace ScadaDarbox.Core.Model;

/// <summary>
/// A device type defined once and instantiated many times (ADR-0010).
/// </summary>
/// <remarks>
/// Templates hang off the tenant rather than a site: being reusable across sites is the
/// point of having them. Instances hold a live reference, so editing a template changes
/// every device made from it.
/// </remarks>
public sealed class DeviceTemplate
{
    public required Guid Id { get; init; }

    /// <summary>Owning tenant (ADR-0004).</summary>
    public required Guid TenantId { get; init; }

    public required string Name { get; set; }
}

/// <summary>
/// The shape of one tag on a template.
/// </summary>
/// <remarks>
/// Everything an instantiated tag needs except its address, which is a template of its
/// own resolved per instance.
/// </remarks>
public sealed class DeviceTemplateTag
{
    public required Guid Id { get; init; }

    public required Guid TemplateId { get; init; }

    public required string Name { get; set; }

    public required TagValueKind ValueKind { get; init; }

    /// <summary>Dimensioned unit (ADR-0005), shared by every instance of this tag.</summary>
    public UnitOfMeasure? Unit { get; set; }

    /// <summary>
    /// The driver-specific address with named placeholders, e.g.
    /// <c>holding:{offset}?scale=0.01</c>.
    /// </summary>
    /// <remarks>
    /// What a resolved address means is a driver's business (ADR-0002). Core substitutes
    /// the named parameters and hands the result on without interpreting it.
    /// </remarks>
    public required string AddressTemplate { get; set; }

    public bool IsWritable { get; set; }
}
