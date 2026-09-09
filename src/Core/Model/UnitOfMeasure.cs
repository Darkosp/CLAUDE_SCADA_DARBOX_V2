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
