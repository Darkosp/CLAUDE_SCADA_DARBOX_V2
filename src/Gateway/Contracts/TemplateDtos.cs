using ScadaDarbox.Core.Model;

namespace ScadaDarbox.Gateway.Contracts;

/// <summary>Wire form of a device template.</summary>
public sealed record DeviceTemplateDto(Guid Id, string Name)
{
    public static DeviceTemplateDto From(DeviceTemplate template) => new(template.Id, template.Name);
}

/// <summary>
/// Wire form of a template tag.
/// </summary>
/// <param name="AddressTemplate">
/// The address with its named placeholders intact, e.g. <c>holding:{offset}</c>.
/// </param>
/// <param name="ParameterNames">
/// The parameters this address needs, so a client can prompt for exactly those when
/// instantiating rather than guessing.
/// </param>
public sealed record TemplateTagDto(
    Guid Id,
    string Name,
    string ValueKind,
    UnitDto? Unit,
    string AddressTemplate,
    bool IsWritable,
    IReadOnlyList<string> ParameterNames)
{
    public static TemplateTagDto From(DeviceTemplateTag tag) => new(
        tag.Id,
        tag.Name,
        tag.ValueKind.ToString(),
        UnitDto.From(tag.Unit),
        tag.AddressTemplate,
        tag.IsWritable,
        ScadaDarbox.Core.Templates.AddressTemplate.ParameterNames(tag.AddressTemplate));
}

public sealed record CreateTemplateRequest(string Name);

public sealed record SaveTemplateTagRequest(
    string Name,
    string ValueKind,
    UnitDto? Unit,
    string AddressTemplate,
    bool IsWritable);

/// <summary>A device being created from a template, with the parameters that make it its own.</summary>
public sealed record InstantiateDeviceRequest(
    Guid TemplateId,
    string Name,
    string DriverKey,
    IReadOnlyDictionary<string, string> ConnectionSettings,
    int? ScanIntervalMs,
    Guid? FolderId,
    IReadOnlyDictionary<string, string> Parameters);
