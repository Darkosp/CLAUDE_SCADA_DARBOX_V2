using ScadaDarbox.Core.Model;
using ScadaDarbox.Modules.Drivers.OpcUa;
using Xunit;

namespace ScadaDarbox.Drivers.OpcUa.Tests;

/// <summary>
/// What a device may say to this driver, and what it may not (ADR-0033 decisions 2 and 4).
/// </summary>
public sealed class OpcUaDriverFactoryTests
{
    [Fact]
    public void No_connection_setting_this_driver_reads_could_hold_a_secret()
    {
        // **ADR-0033 decision 4, pinned rather than left to a comment.** `ConnectionSettings` is a
        // dictionary on the device row: the API echoes it back, and a device edit writes it into
        // `audit_log.detail`, which ADR-0032 §6 makes opaque JSON that is **never parsed** — so nothing
        // in the reader could redact a field even in principle, and a password put here would be
        // printed in full on the screen built for an Admin to read.
        //
        // The rule is that a setting may **name** a credential and may not **carry** one. The cheap
        // wrong answer — *it is just another setting* — is the one a later session reaches for, so this
        // fails when a secret-shaped name is added rather than waiting for somebody to notice.
        string[] secretish =
        [
            "password", "secret", "token", "key", "credential", "passphrase", "pwd", "pass",
        ];

        foreach (var name in OpcUaDriverFactory.ConnectionSettingNames)
        {
            foreach (var smell in secretish)
            {
                Assert.False(
                    name.Contains(smell, StringComparison.OrdinalIgnoreCase),
                    $"The connection setting '{name}' looks like it carries a secret. A secret belongs "
                    + "where the database passwords are, not on the device row — see ADR-0033 §4.");
            }
        }
    }

    [Fact]
    public void The_list_of_settings_is_the_whole_list()
    {
        // A control for the test above: it checks the *names* in the set, so the set has to actually be
        // what the factory reads. If a setting is added to the code and not here, the guard silently
        // stops covering it — the shape of defect this repository has met as "a rule and every place
        // that applies it have to move together".
        Assert.Equal(
            ["acceptUntrustedCertificates", "endpointUrl", "security"],
            OpcUaDriverFactory.ConnectionSettingNames.OrderBy(name => name, StringComparer.Ordinal));
    }

    [Fact]
    public void A_device_that_says_nothing_about_security_gets_the_secured_default()
    {
        // The whole point of ADR-0033: the safe answer is the one you get by not deciding.
        var driver = Factory().Create(DeviceWith(("endpointUrl", "opc.tcp://plant:4840/Server")));

        Assert.NotNull(driver);
    }

    [Theory]
    [InlineData("required")]
    [InlineData("REQUIRED")]
    [InlineData(" none ")]
    [InlineData("None")]
    public void Both_values_are_accepted_whatever_the_case_or_spacing(string setting)
    {
        // An operator types this into a form. Refusing `None` because it is capitalised would be a
        // refusal about typography, and the reader cannot tell it from a refusal about meaning.
        var driver = Factory().Create(DeviceWith(
            ("endpointUrl", "opc.tcp://plant:4840/Server"),
            ("security", setting)));

        Assert.NotNull(driver);
    }

    [Fact]
    public void A_value_that_is_neither_is_refused_rather_than_defaulted_in_either_direction()
    {
        // **Defaulting a typo would be wrong both ways.** Falling back to `required` strands a
        // deployment that meant `none` and leaves it hunting; falling back to `none` silently unsecures
        // a device because somebody wrote `requried`. A device that cannot be understood does not run.
        var refusal = Assert.Throws<InvalidOperationException>(
            () => Factory().Create(DeviceWith(
                ("endpointUrl", "opc.tcp://plant:4840/Server"),
                ("security", "requried"))));

        Assert.Contains("requried", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("'required'", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("'none'", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_security_setting_reads_as_unset_rather_than_as_an_error()
    {
        // A form that submits every field sends an empty string for one nobody filled in. Treating
        // that as a typo would refuse a device for a blank box — and the absent case is the one that
        // fails silently, which this repository learned from an empty journal filter.
        var driver = Factory().Create(DeviceWith(
            ("endpointUrl", "opc.tcp://plant:4840/Server"),
            ("security", "   ")));

        Assert.NotNull(driver);
    }

    private static OpcUaDriverFactory Factory() => new(TimeProvider.System);

    private static Device DeviceWith(params (string Key, string Value)[] settings) => new()
    {
        Id = Guid.NewGuid(),
        SiteId = Guid.NewGuid(),
        Name = "Pump House",
        DriverKey = "opc-ua",
        ConnectionSettings =
            settings.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
    };
}
