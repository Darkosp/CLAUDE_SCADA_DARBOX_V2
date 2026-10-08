namespace ScadaDarbox.Core.Model;

/// <summary>
/// Physical dimension of a measured quantity (ADR-0005). Units are only ever
/// convertible or comparable within the same dimension.
/// </summary>
public enum Dimension
{
    /// <summary>A count, ratio or index — no physical dimension.</summary>
    Dimensionless = 0,
    Pressure = 1,
    Temperature = 2,
    VolumeFlow = 3,
    Power = 4,
    Energy = 5,
    Length = 6,
    Mass = 7,
    Time = 8,
    ElectricCurrent = 9,
    ElectricPotential = 10,
    Frequency = 11,
    Speed = 12,
}

/// <summary>
/// A unit of measure as a structured record rather than a decorative string
/// (ADR-0005): a dimension plus the affine conversion to that dimension's
/// canonical SI representation. Display symbols derive from this record, never
/// the other way round.
/// </summary>
/// <param name="Symbol">Display symbol, e.g. <c>bar</c>. Presentation only — never the identity of the unit.</param>
/// <param name="Dimension">The physical dimension this unit measures.</param>
/// <param name="FactorToSi">Multiplier taking a value in this unit towards SI.</param>
/// <param name="OffsetToSi">
/// Additive term applied after <paramref name="FactorToSi"/>. Required because not every
/// unit conversion is a pure scaling — °C to K is offset by 273.15 — and a factor-only
/// model would silently corrupt temperature.
/// </param>
public sealed record UnitOfMeasure(
    string Symbol,
    Dimension Dimension,
    double FactorToSi = 1.0,
    double OffsetToSi = 0.0)
{
    /// <summary>
    /// Refused here rather than checked by each caller, because a unit that cannot convert is not a
    /// unit (ADR-0005) and there is no stage of this product at which one is useful.
    /// </summary>
    /// <remarks>
    /// <para>
    /// **Found on 2026-10-08 by reading an audit row**: three of the four tags carrying a unit in the
    /// demo had <c>FactorToSi = 0</c>, accepted by the API and stored. A zero factor makes
    /// <see cref="ToSi"/> return the offset for every reading and <see cref="FromSi"/> divide by zero
    /// — so the model said "this is a percentage" while meaning "every percentage is the same number".
    /// </para>
    /// <para>
    /// **Negative is refused with it.** A unit that scales by a negative number inverts the dimension's
    /// direction, which is not what a unit does: the one conversion that genuinely runs backwards —
    /// a temperature scale's zero — is what <see cref="OffsetToSi"/> is for. Non-finite is refused
    /// because infinity and NaN are not conversions either, and NaN would spread silently.
    /// </para>
    /// <para>
    /// A constructor check rather than a validation method, so an invalid unit cannot be built at all
    /// — not by the API, not by a repository reading an old row, not by a test reaching for a shortcut.
    /// An existing row that holds one now fails loudly when it is read, which is the right direction:
    /// it was already meaningless, and silence is how it got there.
    /// </para>
    /// </remarks>
    public double FactorToSi { get; } = double.IsFinite(FactorToSi) && FactorToSi > 0
        ? FactorToSi
        : throw new ArgumentOutOfRangeException(
            nameof(FactorToSi),
            FactorToSi,
            $"A unit's factor to SI must be a positive, finite number: '{Symbol}' was given {FactorToSi}. "
            + "A unit is a dimension plus its conversion to SI (ADR-0005), and zero, a negative or a "
            + "non-finite factor is not a conversion. A scale whose zero differs from SI's carries that "
            + "in OffsetToSi.");

    /// <summary>The additive term, which may legitimately be any finite number including zero.</summary>
    public double OffsetToSi { get; } = double.IsFinite(OffsetToSi)
        ? OffsetToSi
        : throw new ArgumentOutOfRangeException(
            nameof(OffsetToSi),
            OffsetToSi,
            $"A unit's offset to SI must be finite: '{Symbol}' was given {OffsetToSi}.");

    /// <summary>Converts a value expressed in this unit to its canonical SI value.</summary>
    public double ToSi(double value) => (value * FactorToSi) + OffsetToSi;

    /// <summary>Converts a canonical SI value of this dimension into this unit.</summary>
    public double FromSi(double siValue) => (siValue - OffsetToSi) / FactorToSi;

    /// <summary>
    /// Converts a value from this unit into <paramref name="target"/>, via SI.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The two units measure different dimensions, so no conversion exists.
    /// </exception>
    public double ConvertTo(double value, UnitOfMeasure target)
    {
        if (target.Dimension != Dimension)
        {
            throw new InvalidOperationException(
                $"Cannot convert {Symbol} ({Dimension}) to {target.Symbol} ({target.Dimension}): different dimensions.");
        }

        return target.FromSi(ToSi(value));
    }
}
