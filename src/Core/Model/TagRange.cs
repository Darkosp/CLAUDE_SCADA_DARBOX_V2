namespace ScadaDarbox.Core.Model;

/// <summary>
/// Where a tag's readings are expected to fall (ADR-0030) — the tag's own statement about
/// **plausibility**, which is a different statement from its unit (ADR-0005 says what a number means
/// and how to convert it) and a different one from an alarm limit (ADR-0025 says a value somebody chose
/// to watch, with a wait and a deadband and a journal row).
/// </summary>
/// <remarks>
/// **Both ends or neither.** A range with one end has no verdict to give, so the pair is refused rather
/// than half-stored — in the schema as well as in the API, because the API is not the only thing that
/// writes that table.
///
/// This type deliberately has no opinion about **quality**: whether a value can be trusted and whether it
/// is plausible are two facts, and every protocol this project sits beside keeps them apart — OPC UA puts
/// the limit information in status-code bits that "do not affect the meaning of the StatusCode", and
/// IEC 61850 carries out-of-range as a *detail* bit inside an otherwise good quality. So an out-of-range
/// reading keeps the quality its driver reported.
/// </remarks>
public sealed record TagRange(double Low, double High)
{
    /// <summary>
    /// What this range says about one reading. Total by construction: a value is below, inside or above,
    /// and the ends are inclusive, because a range declared as 0–100 that calls 100 out of range would be
    /// a range nobody could write down.
    /// </summary>
    public RangeVerdict VerdictFor(double value) =>
        value > High ? RangeVerdict.AboveRange
        : value < Low ? RangeVerdict.BelowRange
        : RangeVerdict.InRange;
}

/// <summary>Where a reading sits against the range its tag declares (ADR-0030).</summary>
public enum RangeVerdict
{
    InRange = 0,
    AboveRange = 1,
    BelowRange = 2,
}
