using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using ScadaDarbox.Core.Drivers;
using ScadaDarbox.EdgeAgent;
using ScadaDarbox.EdgeAgent.Acquisition;
using ScadaDarbox.EdgeAgent.Buffer;
using ScadaDarbox.EdgeAgent.Uplink;
using ScadaDarbox.Modules.Drivers.Modbus;
using ScadaDarbox.Modules.Drivers.OpcUa;

// The edge agent (Phase 7, ADR-0017, ADR-0018): reads the plant's devices, keeps what it read in a
// buffer on disk, and sends it to the cloud Gateway's broker whenever the link allows.

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

// The same driver modules as the Gateway, composed at compile time (ADR-0002).
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IDeviceDriverFactory>(provider => new ModbusTcpDriverFactory(provider.GetRequiredService<TimeProvider>()));
builder.Services.AddSingleton<IDeviceDriverFactory>(provider => new OpcUaDriverFactory(provider.GetRequiredService<TimeProvider>()));

builder.Services.AddSingleton(provider =>
{
    var edge = provider.GetRequiredService<IOptions<EdgeOptions>>().Value;
    return SampleBuffer.Open(edge.Buffer.Path, edge.Buffer.MaxPendingSamples, provider.GetRequiredService<TimeProvider>());
});

builder.Services.AddHostedService<AcquisitionService>();
builder.Services.AddHostedService<UplinkService>();

await builder.Build().RunAsync();
return 0;
