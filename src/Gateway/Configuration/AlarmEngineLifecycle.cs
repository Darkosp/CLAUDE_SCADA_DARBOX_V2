using ScadaDarbox.Core.Alarms;
using ScadaDarbox.Core.Historian;

namespace ScadaDarbox.Gateway.Configuration;

/// <summary>
/// Rebuilds the alarm list from the journal before anything is scanned, and records when
/// evaluation starts and stops (ADR-0013).
/// </summary>
/// <remarks>
/// Registered before the scanner, so it starts first and stops last: the journal's
/// EvaluationStarted precedes the first value evaluated, and EvaluationStopped follows the
/// last. A failure here stops the Gateway from starting — evaluating without the standing
/// alarms would present a quiet plant that may not be quiet.
/// </remarks>
internal sealed class AlarmEngineLifecycle : IHostedService
{
    private readonly AlarmEngine _engine;
    private readonly IHistorian _historian;

    public AlarmEngineLifecycle(AlarmEngine engine, IHistorian historian)
    {
        _engine = engine;
        _historian = historian;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // The historian writes continuously, so its newest ingestion is the last moment the
        // previous run is known to have been alive — the honest start of the outage.
        var lastAlive = await _historian.LastIngestedAtAsync(cancellationToken).ConfigureAwait(false);
        await _engine.StartAsync(lastAlive, cancellationToken).ConfigureAwait(false);
    }

    public Task StopAsync(CancellationToken cancellationToken) => _engine.StopAsync(cancellationToken);
}

internal sealed record ShelveSweepSettings(TimeSpan Interval);

/// <summary>
/// Returns shelved alarms to Active when their shelf runs out, on a clock rather than on
/// readings, so an alarm on a device that has gone silent still comes back (ADR-0013).
/// </summary>
internal sealed class ShelveExpirySweeper : BackgroundService
{
    private readonly AlarmEngine _engine;
    private readonly ShelveSweepSettings _settings;
    private readonly ILogger<ShelveExpirySweeper> _logger;

    public ShelveExpirySweeper(AlarmEngine engine, ShelveSweepSettings settings, ILogger<ShelveExpirySweeper> logger)
    {
        _engine = engine;
        _settings = settings;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_settings.Interval);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    await _engine.ExpireShelvesAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    _logger.LogError(exception, "Returning expired shelves to Active failed.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }
    }
}
