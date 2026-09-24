using System.Reflection;
using ScadaDarbox.Core.Drivers;
using ScadaDarbox.Core.Model;

namespace ScadaDarbox.Core.Tests;

/// <summary>
/// ADR-0016's review criteria that are about the shape of the contract itself, read from the
/// compiled Core assembly rather than trusted to stay true.
/// </summary>
public sealed class DriverContractTests
{
    [Fact]
    public void A_pushing_driver_has_no_way_to_be_asked_for_a_value()
    {
        // The whole point of the distinction: a pushing source has no honest answer to "what is
        // it now", so nothing on its contract may ask. A read method added "for convenience"
        // would let a cached last value answer, and invent the value nobody measured.
        // Property accessors are not questions: StalenessLimit's getter is get_StalenessLimit.
        var methods = typeof(IPushingDeviceDriver).GetMethods().Where(method => !method.IsSpecialName).ToList();

        // Something was inspected, so "none found" below means something.
        Assert.Contains(methods, method => method.Name == nameof(IPushingDeviceDriver.RunAsync));

        var asks = methods
            .Where(method => method.Name.StartsWith("Read", StringComparison.OrdinalIgnoreCase)
                             || method.Name.StartsWith("Get", StringComparison.OrdinalIgnoreCase)
                             || ReturnsReadings(method.ReturnType))
            .Select(method => method.Name)
            .ToList();

        Assert.Empty(asks);

        // And the polled contract is a different type, not a base the pushing one inherits.
        Assert.False(typeof(IDeviceDriver).IsAssignableFrom(typeof(IPushingDeviceDriver)));
    }

    [Fact]
    public void Core_knows_nothing_of_MQTT_brokers_or_topics()
    {
        // ADR-0002, ADR-0016, ADR-0017: the transport is a module's business. Every type and
        // member name in Core, public or not.
        var forbidden = new[] { "mqtt", "mosquitto", "broker", "topic", "sparkplug" };
        var core = typeof(IPushingDeviceDriver).Assembly;

        var names = core.GetTypes()
            .SelectMany(type => new[] { type.FullName ?? type.Name }
                .Concat(type.GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                    .Select(member => $"{type.Name}.{member.Name}")))
            .ToList();

        // Something was inspected.
        Assert.Contains(names, name => name.Contains(nameof(IPushingDeviceDriver), StringComparison.Ordinal));

        var offenders = names
            .Where(name => forbidden.Any(word => name.Contains(word, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        Assert.Empty(offenders);
    }

    private static bool ReturnsReadings(Type type)
    {
        if (type == typeof(TagReading))
        {
            return true;
        }

        return type.IsGenericType && type.GetGenericArguments().Any(ReturnsReadings);
    }
}
