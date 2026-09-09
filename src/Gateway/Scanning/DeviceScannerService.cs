using ScadaDarbox.Core.Drivers;
using ScadaDarbox.Core.Model;
using ScadaDarbox.Core.Tags;

namespace ScadaDarbox.Gateway.Scanning;

/// <summary>
/// Polls every configured device on its own schedule and feeds the readings into the tag
/// engine — the driver end of the read path described in the Phase 0 architecture.
/// </summary>
public sealed class DeviceScannerService : BackgroundService
{
    private readonly TagCatalog _catalog;
    private readonly ITagEngine _tagEngine;
    private readonly IReadOnlyDictionary<string, IDeviceDriverFactory> _factoriesByKey;
    private readonly ILogger<DeviceScannerService> _logger;

    public DeviceScannerService(
        TagCatalog catalog,
        ITagEngine tagEngine,
        IEnumerable<IDeviceDriverFactory> driverFactories,
        ILogger<DeviceScannerService> logger)
    {
        _catalog = catalog;
        _tagEngine = tagEngine;
        _logger = logger;

        // Compile-time composition: the factories are whatever the composition root
        // registered, matched to a device only by its opaque driver key (ADR-0002).
        _factoriesByKey = driverFactories.ToDictionary(f => f.DriverKey, StringComparer.OrdinalIgnoreCase);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var scans = _catalog.Devices
            .Select(device => ScanDeviceAsync(device, stoppingToken))
            .ToList();

        await Task.WhenAll(scans).ConfigureAwait(false);
    }

    private async Task ScanDeviceAsync(Device device, CancellationToken stoppingToken)
    {
        if (!_factoriesByKey.TryGetValue(device.DriverKey, out var factory))
        {
            _logger.LogError(
                "Device {DeviceName} needs driver '{DriverKey}', which is not part of this build.",
                device.Name,
                device.DriverKey);
            return;
        }

        var driverTags = _catalog.TagsOfDevice(device.Id)
            .Select(tag => new DriverTag(tag.Id, tag.SourceAddress, tag.ValueKind))
            .ToList();

        if (driverTags.Count == 0)
        {
            return;
        }

        await using var driver = factory.Create(device);
        var connected = false;

        while (!stoppingToken.IsCancellationRequested)
        {
            if (!connected)
            {
                try
                {
                    await driver.ConnectAsync(stoppingToken).ConfigureAwait(false);
                    connected = true;
                    _logger.LogInformation("Connected to device {DeviceName}.", device.Name);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // Deliberately not rethrown, and deliberately not skipped either: the scan
                    // continues so the driver reports Bad quality for its tags. A device that is
                    // unreachable must show as unreadable, not as stale-but-fine (ADR-0003).
                    _logger.LogWarning(
                        exception,
                        "Cannot reach device {DeviceName}; its tags will report Bad quality.",
                        device.Name);
                }
            }

            try
            {
                var readings = await driver.ReadAsync(driverTags, stoppingToken).ConfigureAwait(false);
                await _tagEngine.IngestAsync(readings, stoppingToken).ConfigureAwait(false);

                // Any Bad reading means the link is suspect: drop it so the next cycle
                // reconnects rather than polling a dead socket forever.
                if (connected && readings.Any(r => r.Quality == Quality.Bad))
                {
                    connected = false;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                connected = false;
                _logger.LogError(exception, "Scan of device {DeviceName} failed.", device.Name);
            }

            try
            {
                await Task.Delay(device.ScanInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
