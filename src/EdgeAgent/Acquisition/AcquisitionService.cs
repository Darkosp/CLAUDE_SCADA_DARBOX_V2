using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ScadaDarbox.Core.Drivers;
using ScadaDarbox.Core.Model;
using ScadaDarbox.EdgeAgent.Buffer;
using ScadaDarbox.EdgeAgent.Configuration;
using ScadaDarbox.Modules.Drivers.Mqtt;

namespace ScadaDarbox.EdgeAgent.Acquisition;

/// <summary>
/// Reads every device in the configuration in force on its scan interval and appends what it read to
/// the buffer — the edge half of the read path (ADR-0017). It does not evaluate alarms and does not
/// care whether the link is up: measuring carries on through an outage, which is the point of it.
/// </summary>
/// <remarks>
/// <para>
/// The same driver modules as the Gateway, with the same rule: an unreachable device reads Bad,
/// never a made-up value (ADR-0003). A Bad reading is buffered and sent like any other — the
/// cloud should know the device was unreachable, and when.
/// </para>
/// <para>
/// The devices come from <see cref="EdgeConfigurationSource"/>, not from a file, and a newer
/// configuration is applied by restarting acquisition (ADR-0019 §6): the loops in force are stopped
/// and the new ones started. A device that is no longer assigned simply stops being read, and an
/// edge that has accepted no configuration yet reads nothing rather than something typed by hand.
/// </para>
/// </remarks>
public sealed class AcquisitionService : BackgroundService
{
    private readonly EdgeConfigurationSource _configuration;
    private readonly SampleBuffer _buffer;
    private readonly IReadOnlyDictionary<string, IDeviceDriverFactory> _factories;
    private readonly ILogger<AcquisitionService> _logger;

    public AcquisitionService(
        EdgeConfigurationSource configuration,
        SampleBuffer buffer,
        IEnumerable<IDeviceDriverFactory> factories,
        ILogger<AcquisitionService> logger)
    {
        _configuration = configuration;
        _buffer = buffer;
        _factories = factories.ToDictionary(factory => factory.DriverKey, StringComparer.OrdinalIgnoreCase);
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            // Captured before the devices are read, so a configuration that arrives while these
            // loops are being started completes this task rather than being missed.
            var changed = _configuration.Changed();
            var devices = _configuration.Devices;

            _logger.LogInformation(
                "Reading {Devices} device(s) from configuration {Revision}.",
                devices.Count,
                _configuration.Revision ?? "(none accepted yet)");

            using var generation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            var loops = new List<Task>();

            foreach (var device in devices)
            {
                if (!_factories.TryGetValue(device.Driver, out var factory))
                {
                    _logger.LogError(
                        "Device {Device} needs driver '{Driver}', which this edge agent does not have.",
                        device.Name,
                        device.Driver);
                    continue;
                }

                loops.Add(Task.Run(() => ScanAsync(device, factory, generation.Token), CancellationToken.None));
            }

            try
            {
                await changed.WaitAsync(stoppingToken).ConfigureAwait(false);
                _logger.LogInformation("A newer configuration arrived; restarting acquisition.");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Stopping.
            }

            generation.Cancel();

            // The loops stop at their own next step; each one leaves the device as it found it, and
            // nothing half-read reaches the buffer (a scan is appended whole or not at all).
            await Task.WhenAll(loops).ConfigureAwait(false);
        }
    }

    private async Task ScanAsync(EdgeConfigurationDevice options, IDeviceDriverFactory factory, CancellationToken cancellationToken)
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

        var tags = options.Tags.Select(tag => new DriverTag(tag.TagId, tag.Address, tag.Kind)).ToList();

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
