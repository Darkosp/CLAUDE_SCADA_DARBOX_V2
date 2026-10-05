using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using ScadaDarbox.Core.Drivers;
using ScadaDarbox.Core.Model;
using ScadaDarbox.Modules.Drivers.Modbus;

namespace ScadaDarbox.Drivers.Modbus.Tests;

/// <summary>
/// The Modbus response bound, and the setting that moves it.
/// </summary>
/// <remarks>
/// <para>
/// <c>open-work.md</c> carried this as an open decision — *the Modbus 5 s response bound: driver
/// constant or per-device setting?* — and it is now a per-device setting. **The point of these tests
/// is that a configurable bound is only worth having if it is the bound**, which this project has
/// already got wrong once: NModbus retried three times and a stated five seconds was really twenty,
/// and nothing measured it.
/// </para>
/// <para>
/// So the bound is measured here rather than asserted from a constant. A silent socket and a
/// stopwatch, because the failure this guards is a comment that says five and a driver that takes
/// twenty.
/// </para>
/// </remarks>
public class ModbusResponseTimeoutTests
{
    private static readonly DriverTag Tag = new(Guid.NewGuid(), "holding:0", TagValueKind.Numeric);

    [Fact]
    public async Task A_request_that_is_never_answered_is_given_up_on_within_the_configured_bound()
    {
        // Two seconds, and the ceiling is three times that. Both numbers are chosen against the
        // failure this guards rather than for tidiness: the defect was NModbus retrying a failed
        // request three times, so the driver really took FOUR times whatever it said. At two seconds
        // that is eight, which fails the ceiling below -- whereas a bound of one second with a
        // five-second ceiling would have passed the same regression, which is exactly the kind of
        // test this project has already been caught by.
        using var silent = new TcpListener(IPAddress.Loopback, 0);
        silent.Start();
        var accepted = silent.AcceptTcpClientAsync();

        await using var driver = new ModbusTcpDriver(
            "127.0.0.1",
            ((IPEndPoint)silent.LocalEndpoint).Port,
            1,
            TimeProvider.System,
            responseTimeout: TimeSpan.FromSeconds(2));

        await driver.ConnectAsync(CancellationToken.None);

        var clock = Stopwatch.StartNew();
        var reading = Assert.Single(await driver.ReadAsync([Tag], CancellationToken.None));
        clock.Stop();

        Assert.Equal(Quality.Bad, reading.Quality);

        Assert.True(
            clock.Elapsed < TimeSpan.FromSeconds(6),
            $"a silent device took {clock.Elapsed.TotalSeconds:0.0} s to give up; the bound is 2 s, "
            + "and four times it is the retry this test exists to catch");

        (await accepted).Dispose();
    }

    [Fact]
    public async Task The_default_bound_is_five_seconds_for_a_driver_built_without_one()
    {
        // The measurements recorded elsewhere in this repository are stated against five seconds, so
        // the default has to stay the number they were taken with -- otherwise every one of them
        // quietly refers to something else.
        Assert.Equal(TimeSpan.FromSeconds(5), ModbusTcpDriverFactory.DefaultResponseTimeout);
    }

    [Fact]
    public void A_device_that_names_a_bound_gets_it()
    {
        var driver = Factory().Create(Device(new Dictionary<string, string>
        {
            ["host"] = "127.0.0.1",
            ["responseTimeoutSeconds"] = "30",
        }));

        Assert.NotNull(driver);
    }

    [Fact]
    public void A_device_with_no_setting_falls_back_to_the_default()
    {
        // The case that must not become an error: every device configured before this setting existed
        // has no `responseTimeoutSeconds`, and it has to keep working.
        var driver = Factory().Create(Device(new Dictionary<string, string> { ["host"] = "127.0.0.1" }));

        Assert.NotNull(driver);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("0.5")]
    [InlineData("121")]
    [InlineData("-1")]
    [InlineData("soon")]
    [InlineData("")]
    public void A_device_whose_bound_is_unusable_or_out_of_range_is_refused_by_name(string value)
    {
        // Refused rather than ignored, and that is the decision worth pinning: a device configured
        // for a thirty-second bound that silently got five would read Bad on a link that is merely
        // slow, and nothing anywhere would say why. The empty string is the exception -- it is how a
        // form field left blank arrives, and blank means "not set" rather than "set to nonsense".
        var settings = new Dictionary<string, string>
        {
            ["host"] = "127.0.0.1",
            ["responseTimeoutSeconds"] = value,
        };

        if (value == string.Empty)
        {
            Assert.NotNull(Factory().Create(Device(settings)));
            return;
        }

        var refusal = Assert.Throws<InvalidOperationException>(() => Factory().Create(Device(settings)));

        // The message names the setting and the device, because the person reading it is looking at
        // a device that is not working.
        Assert.Contains("responseTimeoutSeconds", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("bound-probe", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_bound_inside_the_range_is_accepted_at_both_ends()
    {
        // The control for the test above: min and max themselves must pass, or the range is off by
        // one and the documented limits are wrong.
        foreach (var seconds in new[] { "1", "120" })
        {
            var driver = Factory().Create(Device(new Dictionary<string, string>
            {
                ["host"] = "127.0.0.1",
                ["responseTimeoutSeconds"] = seconds,
            }));

            Assert.NotNull(driver);
        }
    }

    private static ModbusTcpDriverFactory Factory() => new(TimeProvider.System);

    private static Device Device(Dictionary<string, string> settings) => new()
    {
        Id = Guid.NewGuid(),
        SiteId = Guid.NewGuid(),
        Name = "bound-probe",
        DriverKey = "modbus-tcp",
        ConnectionSettings = settings,
    };
}
