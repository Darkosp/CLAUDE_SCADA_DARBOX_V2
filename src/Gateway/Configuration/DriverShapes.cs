using ScadaDarbox.Core.Drivers;

namespace ScadaDarbox.Gateway.Configuration;

/// <summary>
/// Which driver keys this build has, and which of them push rather than being polled (ADR-0016).
/// </summary>
/// <remarks>
/// Read from the factories the composition root registered — the same list the scanner uses — so
/// the configuration API can never disagree with what will actually run a device.
/// </remarks>
public sealed class DriverShapes
{
    private readonly HashSet<string> _pushing;
    private readonly HashSet<string> _polled;

    public DriverShapes(IEnumerable<IDeviceDriverFactory> polled, IEnumerable<IPushingDeviceDriverFactory> pushing)
    {
        _polled = polled.Select(factory => factory.DriverKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _pushing = pushing.Select(factory => factory.DriverKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Every driver key in this build, and whether it pushes.</summary>
    public IReadOnlyList<(string Key, bool Pushing)> All =>
        _polled.Select(key => (key, false))
            .Concat(_pushing.Select(key => (key, true)))
            .OrderBy(driver => driver.Item1, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public bool Pushes(string driverKey) => _pushing.Contains(driverKey);

    /// <summary>
    /// Why a scan interval is wrong for this driver, or null when it is right. A pushing device
    /// has none — a field that exists but does nothing is a lie told to whoever configures it
    /// (ADR-0016) — and a polled one cannot run without one.
    /// </summary>
    public string? ScanIntervalProblem(string driverKey, int? scanIntervalMs) =>
        (Pushes(driverKey), scanIntervalMs) switch
        {
            (true, not null) =>
                $"A '{driverKey}' device sends its values when they change; it is not polled, so it has no scan interval. Leave the scan interval out.",
            (false, null) =>
                $"A '{driverKey}' device is polled and needs a scan interval.",
            (false, <= 0) =>
                "The scan interval must be a positive number of milliseconds.",
            _ => null,
        };
}
