using System.Globalization;
using ScadaDarbox.Core.Drivers;
using ScadaDarbox.Core.Model;
using ScadaDarbox.Core.Tags;

namespace ScadaDarbox.Gateway.Scanning;

/// <summary>
/// Polls every configured device on its own schedule and feeds the readings into the tag
/// engine — the driver end of the read path described in the Phase 0 architecture.
/// </summary>
/// <remarks>
/// Each device gets its own scan loop. The set of loops is reconciled against the
/// catalogue whenever configuration changes, so a device added through the UI starts
/// reporting without a gateway restart, which is what Phase 2's "no code required to add
/// a device" actually demands.
/// </remarks>
public sealed class DeviceScannerService : BackgroundService
{
    private readonly TagCatalogSource _catalogSource;
    private readonly ITagEngine _tagEngine;
    private readonly IReadOnlyDictionary<string, IDeviceDriverFactory> _factoriesByKey;
    private readonly ILogger<DeviceScannerService> _logger;

    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, RunningScan> _running = [];

    private CancellationToken _stoppingToken = CancellationToken.None;

    public DeviceScannerService(
        TagCatalogSource catalogSource,
        ITagEngine tagEngine,
        IEnumerable<IDeviceDriverFactory> driverFactories,
        ILogger<DeviceScannerService> logger)
    {
        _catalogSource = catalogSource;
        _tagEngine = tagEngine;
        _logger = logger;

        // Compile-time composition: the factories are whatever the composition root
        // registered, matched to a device only by its opaque driver key (ADR-0002).
        _factoriesByKey = driverFactories.ToDictionary(f => f.DriverKey, StringComparer.OrdinalIgnoreCase);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stoppingToken = stoppingToken;
        _catalogSource.Changed += OnCatalogChanged;

        try
        {
            Reconcile(_catalogSource.Current);

            // The scan loops do the work; this task only waits for shutdown.
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }
        finally
        {
            _catalogSource.Changed -= OnCatalogChanged;
            await StopAllAsync().ConfigureAwait(false);
        }
    }

    private void OnCatalogChanged(object? sender, TagCatalog catalog) => Reconcile(catalog);

    /// <summary>
    /// Brings the running scan loops in line with <paramref name="catalog"/>: starts loops
    /// for new devices, stops them for removed ones, and restarts a device whose
    /// configuration changed.
    /// </summary>
    private void Reconcile(TagCatalog catalog)
    {
        lock (_gate)
        {
            if (_stoppingToken.IsCancellationRequested)
            {
                return;
            }

            var desired = catalog.Devices.ToDictionary(device => device.Id);

            foreach (var (deviceId, scan) in _running.ToList())
            {
                var stillWanted = desired.TryGetValue(deviceId, out var device)
                                  && SignatureOf(device, catalog) == scan.Signature;

                if (stillWanted)
                {
                    continue;
                }

                // Restarting on any change is deliberately blunt. A driver holds a live
                // connection built from the device's settings, so reusing it after an edit
                // would mean reasoning about which fields can be changed underneath an
                // open socket.
                scan.Cancellation.Cancel();
                _running.Remove(deviceId);
            }

            foreach (var device in desired.Values)
            {
                if (_running.ContainsKey(device.Id))
                {
                    continue;
                }

                var tags = catalog.TagsOfDevice(device.Id)
                    .Select(tag => new DriverTag(tag.Id, tag.SourceAddress, tag.ValueKind))
                    .ToList();

                if (tags.Count == 0)
                {
                    // A device with no tags has nothing to poll. It reappears here as soon
                    // as its first tag is configured, because that changes the catalogue.
                    continue;
                }

                if (!_factoriesByKey.TryGetValue(device.DriverKey, out var factory))
                {
                    _logger.LogError(
                        "Device {DeviceName} needs driver '{DriverKey}', which is not part of this build.",
                        device.Name,
                        device.DriverKey);
                    continue;
                }

                var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_stoppingToken);
                var task = Task.Run(
                    () => ScanDeviceAsync(device, factory, tags, cancellation.Token),
                    CancellationToken.None);

                _running[device.Id] = new RunningScan(cancellation, task, SignatureOf(device, catalog));
            }
        }
    }

    /// <summary>
    /// A value that changes whenever anything the scan loop depends on changes.
    /// </summary>
    private static string SignatureOf(Device device, TagCatalog catalog)
    {
        var settings = string.Join(
            ';',
            device.ConnectionSettings.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => $"{pair.Key}={pair.Value}"));

        var tags = string.Join(
            ';',
            catalog.TagsOfDevice(device.Id)
                .OrderBy(tag => tag.Id)
                .Select(tag => $"{tag.Id}:{tag.SourceAddress}:{tag.ValueKind}"));

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{device.DriverKey}|{settings}|{device.ScanInterval.TotalMilliseconds}|{tags}");
    }

    private async Task ScanDeviceAsync(
        Device device,
        IDeviceDriverFactory factory,
        IReadOnlyList<DriverTag> driverTags,
        CancellationToken cancellationToken)
    {
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
                var readings = await driver.ReadAsync(driverTags, cancellationToken).ConfigureAwait(false);
                await _tagEngine.IngestAsync(readings, cancellationToken).ConfigureAwait(false);

                // Any Bad reading means the link is suspect: drop it so the next cycle
                // reconnects rather than polling a dead socket forever.
                if (connected && readings.Any(r => r.Quality == Quality.Bad))
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
                _logger.LogError(exception, "Scan of device {DeviceName} failed.", device.Name);
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

    private async Task StopAllAsync()
    {
        List<RunningScan> scans;

        lock (_gate)
        {
            scans = _running.Values.ToList();
            _running.Clear();
        }

        foreach (var scan in scans)
        {
            await scan.Cancellation.CancelAsync().ConfigureAwait(false);
        }

        // Let the loops unwind before the host tears the process down, so a driver gets
        // to close its connection rather than having it dropped.
        await Task.WhenAll(scans.Select(scan => scan.Task)).ConfigureAwait(false);

        foreach (var scan in scans)
        {
            scan.Cancellation.Dispose();
        }
    }

    private sealed record RunningScan(CancellationTokenSource Cancellation, Task Task, string Signature);
}
