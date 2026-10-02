using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ScadaDarbox.Core.Drivers;
using ScadaDarbox.EdgeAgent;
using ScadaDarbox.EdgeAgent.Acquisition;
using ScadaDarbox.EdgeAgent.Buffer;
using ScadaDarbox.EdgeAgent.Configuration;
using ScadaDarbox.EdgeAgent.Uplink;
using ScadaDarbox.Modules.Drivers.Modbus;
using ScadaDarbox.Modules.Drivers.OpcUa;

// The edge agent (Phase 7, ADR-0017, ADR-0018, ADR-0019): reads the plant's devices, keeps what it
// read in a buffer on disk, and sends it to the cloud Gateway's broker whenever the link allows.
// What it reads is not written here — the cloud derives it and publishes it over that same link.

var builder = Host.CreateApplicationBuilder(args);
builder.Services.Configure<EdgeOptions>(builder.Configuration.GetSection("Edge"));

var options = builder.Configuration.GetSection("Edge").Get<EdgeOptions>() ?? new EdgeOptions();
if (options.Problems() is { Count: > 0 } problems)
{
    // Refused rather than run half-configured: an edge that starts without an id or a buffer
    // path would look healthy and send nothing anywhere.
    Console.Error.WriteLine("The edge agent's configuration cannot run:");
    foreach (var problem in problems)
    {
        Console.Error.WriteLine($"  {problem}");
    }

    return 2;
}

// The same driver modules as the Gateway, composed at compile time (ADR-0002). The edge agent
// logs the same reasons the Gateway does: a mistyped address on a plant node is diagnosable
// from the edge's own log rather than only from a screen nobody has out there.
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IDeviceDriverFactory>(provider => new ModbusTcpDriverFactory(
    provider.GetRequiredService<TimeProvider>(),
    provider.GetRequiredService<ILoggerFactory>()));
builder.Services.AddSingleton<IDeviceDriverFactory>(provider => new OpcUaDriverFactory(
    provider.GetRequiredService<TimeProvider>(),
    provider.GetRequiredService<ILoggerFactory>()));

builder.Services.AddSingleton(provider =>
{
    var edge = provider.GetRequiredService<IOptions<EdgeOptions>>().Value;
    return SampleBuffer.Open(edge.Buffer.Path, edge.Buffer.MaxPendingSamples, provider.GetRequiredService<TimeProvider>());
});

// What this edge reads: the configuration the cloud derived for it, starting from the last one it
// accepted, so a restart taken while the link is down changes nothing about what is being read
// (ADR-0019 §5). Until a configuration has arrived, this edge reads nothing.
builder.Services.AddSingleton(provider => EdgeConfigurationSource.From(
    provider.GetRequiredService<SampleBuffer>(),
    provider.GetRequiredService<ILoggerFactory>().CreateLogger<EdgeConfigurationSource>()));

builder.Services.AddSingleton<EdgeConfigurationConsumer>();

// What this edge cannot read, shared by the two services that need it: acquisition finds it out,
// the uplink says so on the declaration it already publishes (ADR-0021). Neither depends on the
// other, which is why it is a third thing rather than a method on either.
builder.Services.AddSingleton<EdgeUnreadableDevices>();

builder.Services.AddHostedService<AcquisitionService>();
builder.Services.AddHostedService<UplinkService>();

await builder.Build().RunAsync();
return 0;
