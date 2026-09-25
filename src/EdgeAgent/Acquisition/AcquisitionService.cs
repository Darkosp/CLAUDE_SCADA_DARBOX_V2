using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ScadaDarbox.Core.Drivers;
using ScadaDarbox.Core.Model;
using ScadaDarbox.EdgeAgent.Buffer;

namespace ScadaDarbox.EdgeAgent.Acquisition;

/// <summary>
/// Reads every configured device on its scan interval and appends what it read to the buffer —
/// the edge half of the read path (ADR-0017). It does not evaluate alarms and does not care
/// whether the link is up: measuring carries on through an outage, which is the point of it.
/// </summary>
/// <remarks>
/// The same driver modules as the Gateway, with the same rule: an unreachable device reads Bad,
/// never a made-up value (ADR-0003). A Bad reading is buffered and sent like any other — the
/// cloud should know the device was unreachable, and when.
/// </remarks>
public sealed class AcquisitionService : BackgroundService
{
    private readonly EdgeOptions _options;
    private readonly SampleBuffer _buffer;
    private readonly IReadOnlyDictionary<string, IDeviceDriverFactory> _factories;
    private readonly ILogger<AcquisitionService> _logger;

    public AcquisitionService(
        IOptions<EdgeOptions> options,
        SampleBuffer buffer,
        IEnumerable<IDeviceDriverFactory> factories,
        ILogger<AcquisitionService> logger)
    {
        _options = options.Value;
        _buffer = buffer;
        _factories = factories.ToDictionary(factory => factory.DriverKey, StringComparer.OrdinalIgnoreCase);
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var loops = new List<Task>();

        foreach (var device in _options.Devices)
        {
            if (!_factories.TryGetValue(device.Driver, out var factory))
            {
                _logger.LogError("Device {Device} needs driver '{Driver}', which this edge agent does not have.", device.Name, device.Driver);
                continue;
            }

            loops.Add(Task.Run(() => ScanAsync(device, factory, stoppingToken), CancellationToken.None));
        }

        return Task.WhenAll(loops);
    }

    private async Task ScanAsync(EdgeDeviceOptions options, IDeviceDriverFactory factory, CancellationToken cancellationToken)
    {
        var device = new Device
        {
            Id = Guid.NewGuid(),
            SiteId = Guid.Empty,
            Name = options.Name,
            DriverKey = options.Driver,
            ConnectionSettings = options.Settings,
            ScanInterval = TimeSpan.FromMilliseconds(options.ScanIntervalMs),
        };

        var tags = options.Tags.Select(tag => new DriverTag(tag.Id, tag.Address, tag.Kind)).ToList();

        await using var driver = factory.Create(device);
        var connected = false;

        while (!cancellationToken.IsCancellationRequested)
        {
            if (!connected)
            {
                try
                {
                    await driver.ConnectAsync(cancellationToken).ConfigureAwait(false);
                    connected = true;
                    _logger.LogInformation("Connected to device {Device}.", options.Name);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // The scan goes on so the driver reports Bad for the tags (ADR-0003).
                    _logger.LogWarning("Cannot reach device {Device}: {Reason}", options.Name, exception.Message);
                }
            }

            try
            {
                var readings = await driver.ReadAsync(tags, cancellationToken).ConfigureAwait(false);
                _buffer.Append(readings);

                if (connected && readings.Any(reading => reading.Quality == Quality.Bad))
                {
                    connected = false;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                connected = false;
                _logger.LogError(exception, "Reading device {Device} failed.", options.Name);
            }

            try
            {
                await Task.Delay(device.ScanInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
