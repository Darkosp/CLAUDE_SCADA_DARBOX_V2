using Microsoft.Extensions.Logging;
using ScadaDarbox.EdgeAgent.Buffer;
using ScadaDarbox.Modules.Drivers.Mqtt;

namespace ScadaDarbox.EdgeAgent.Configuration;

/// <summary>
/// Takes one configuration message off the link and, if it can be read, puts it in force
/// (ADR-0019 §4, §5). It is the only thing that ever changes what an edge reads.
/// </summary>
/// <remarks>
/// A message that cannot be read changes nothing: every reason is logged and the edge carries on
/// with the configuration it has. A refusal is whole — half an applied configuration would leave an
/// edge reading devices nobody asked it to read — and the revision is what says whether anything
/// needs to change at all.
/// </remarks>
public sealed class EdgeConfigurationConsumer
{
    private readonly EdgeConfigurationSource _configuration;
    private readonly SampleBuffer _buffer;
    private readonly ILogger<EdgeConfigurationConsumer> _logger;

    public EdgeConfigurationConsumer(
        EdgeConfigurationSource configuration,
        SampleBuffer buffer,
        ILogger<EdgeConfigurationConsumer> logger)
    {
        _configuration = configuration;
        _buffer = buffer;
        _logger = logger;
    }

    /// <summary>
    /// Reads one configuration message and applies it; false when nothing was applied, either
    /// because the message was refused or because it is the configuration already in force.
    /// </summary>
    public bool Accept(string message)
    {
        var read = EdgeConfigurationPayload.Read(message);

        if (read.Refusal is { } refusal)
        {
            // Every problem, not just the first: three mistyped addresses are one edit away from
            // being three correct ones, and whoever reads this log should see all three.
            _logger.LogError("A configuration from the cloud was refused: {Refusal}", refusal);
            foreach (var problem in read.Problems)
            {
                _logger.LogError("  {Problem}", problem);
            }

            return false;
        }

        var revision = read.Revision!;

        if (_configuration.Revision == revision)
        {
            _logger.LogDebug(
                "The cloud published configuration {Revision}, which is the one in force; nothing restarts.",
                revision);
            return false;
        }

        // Recorded before it is applied. A restart in between must come back reading the
        // configuration it accepted, not the one before it (ADR-0019 §5).
        _buffer.AcceptConfiguration(revision, read.GeneratedAtUtc!.Value, message);

        _configuration.Replace(read.Devices, revision);

        _logger.LogInformation(
            "Configuration {Revision} accepted, derived by the cloud at {GeneratedAtUtc:O}: {Devices} device(s), {Tags} tag(s) to read.",
            revision,
            read.GeneratedAtUtc,
            read.Devices.Count,
            read.Devices.Sum(device => device.Tags.Count));

        return true;
    }
}
