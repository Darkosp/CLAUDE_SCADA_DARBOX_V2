namespace ScadaDarbox.Gateway.Scanning;

/// <summary>What the Gateway tolerates from a pushing source before the journal says so (ADR-0017).</summary>
/// <param name="ClockSkewTolerance">
/// How far the source's clock may disagree with the Gateway's, measured when a message arrives.
/// </param>
public sealed record PushedSourceSettings(TimeSpan ClockSkewTolerance)
{
    public static readonly TimeSpan DefaultClockSkewTolerance = TimeSpan.FromSeconds(30);
}
