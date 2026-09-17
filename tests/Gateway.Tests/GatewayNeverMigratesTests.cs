using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace ScadaDarbox.Gateway.Tests;

/// <summary>
/// ADR-0012's criterion that nothing in the Gateway calls DbUp's upgrade path.
/// </summary>
/// <remarks>
/// Read from the compiled Gateway assembly rather than from a running host. Putting a
/// migration back into startup "for convenience" would break nothing visible — the Gateway
/// would still start, and with the application role's grants it might even appear to work —
/// while quietly undoing the whole point of ADR-0012. Every call the Gateway makes into
/// another assembly is recorded in its metadata, including calls made from lambdas, so this
/// sees any such call wherever it is written.
/// </remarks>
public sealed class GatewayNeverMigratesTests
{
    private static readonly string[] MigrationEntryPoints = ["ScadaDarbox.Persistence.TimescaleDb.DatabaseMigrator"];

    [Fact]
    public void The_gateway_assembly_references_no_migration_code()
    {
        var referenced = Referenced(typeof(GatewayApp).Assembly);

        // The scan sees what the Gateway really does call — its startup check — so an empty
        // result below cannot come from a scan that saw nothing.
        Assert.Contains("ScadaDarbox.Persistence.TimescaleDb.SchemaVersion", referenced.Types);
        Assert.Contains("ScadaDarbox.Persistence.TimescaleDb.SchemaVersion.EnsureCurrentAsync", referenced.Members);

        foreach (var entryPoint in MigrationEntryPoints)
        {
            Assert.DoesNotContain(entryPoint, referenced.Types);
        }

        Assert.DoesNotContain(referenced.Types, type => type.StartsWith("DbUp", StringComparison.Ordinal));
    }

    private static (HashSet<string> Types, HashSet<string> Members) Referenced(Assembly assembly)
    {
        using var stream = File.OpenRead(assembly.Location);
        using var image = new PEReader(stream);
        var metadata = image.GetMetadataReader();

        var types = new HashSet<string>(StringComparer.Ordinal);
        foreach (var handle in metadata.TypeReferences)
        {
            types.Add(FullName(metadata, metadata.GetTypeReference(handle)));
        }

        var members = new HashSet<string>(StringComparer.Ordinal);
        foreach (var handle in metadata.MemberReferences)
        {
            var member = metadata.GetMemberReference(handle);
            if (member.Parent.Kind == HandleKind.TypeReference)
            {
                var owner = metadata.GetTypeReference((TypeReferenceHandle)member.Parent);
                members.Add($"{FullName(metadata, owner)}.{metadata.GetString(member.Name)}");
            }
        }

        return (types, members);
    }

    private static string FullName(MetadataReader metadata, TypeReference type)
    {
        var name = metadata.GetString(type.Name);
        var ns = metadata.GetString(type.Namespace);
        return ns.Length == 0 ? name : $"{ns}.{name}";
    }
}
