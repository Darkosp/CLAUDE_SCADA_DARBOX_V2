using ScadaDarbox.Core.Model;

namespace ScadaDarbox.Core.Tests;

/// <summary>
/// A unit that cannot convert is refused when it is built (ADR-0005).
/// </summary>
/// <remarks>
/// <para>
/// **Found on 2026-10-08 by reading an audit row**, not by reading the code: the trail's Detail column
/// showed <c>"Unit": {"Symbol": "%", "Dimension": "Dimensionless", "FactorToSi": 0}</c> in an entry
/// about something else, and the database then showed **three of the four tags carrying a unit had a
/// factor of zero**. The API accepted them and stored them.
/// </para>
/// <para>
/// ADR-0005 makes a unit <i>a dimension plus its conversion to SI</i>. A zero factor is not a
/// conversion: <c>ToSi</c> returns the offset for every reading, and <c>FromSi</c> divides by zero. The
/// model said "this is a percentage" while meaning "every percentage is the same number".
/// </para>
/// <para>
/// **The blast radius was small and that was luck** — nothing in the product converted yet. These tests
/// exist so the hole cannot reopen when something does.
/// </para>
/// </remarks>
public sealed class UnitRefusalTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-0.001)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void A_factor_that_is_not_a_positive_finite_number_is_refused(double factor)
    {
        // Zero is the one that was really stored. The rest are refused with it because each is a
        // different way of not being a conversion: a negative factor inverts the dimension's
        // direction, which is what OffsetToSi is for; infinity and NaN are not numbers a reading can
        // be multiplied by, and NaN in particular spreads silently through every arithmetic it meets.
        var refusal = Assert.Throws<ArgumentOutOfRangeException>(
            () => new UnitOfMeasure("%", Dimension.Dimensionless, factor));

        Assert.Equal(nameof(UnitOfMeasure.FactorToSi), refusal.ParamName);
        Assert.Contains("ADR-0005", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("%", refusal.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void An_offset_that_is_not_finite_is_refused(double offset)
    {
        var refusal = Assert.Throws<ArgumentOutOfRangeException>(
            () => new UnitOfMeasure("°C", Dimension.Temperature, 1.0, offset));

        Assert.Equal(nameof(UnitOfMeasure.OffsetToSi), refusal.ParamName);
    }

    [Fact]
    public void A_zero_offset_is_fine_and_so_is_a_negative_one()
    {
        // The control for the offset rule, and it matters: **zero is the common case** -- every unit
        // that is a pure scaling has it -- and a check that treated the offset like the factor would
        // refuse `bar`. A negative offset is legitimate too; a scale whose zero sits above SI's has one.
        Assert.Equal(0, new UnitOfMeasure("bar", Dimension.Pressure, 100_000).OffsetToSi);
        Assert.Equal(-40, new UnitOfMeasure("x", Dimension.Temperature, 1, -40).OffsetToSi);
    }

    [Fact]
    public void Every_unit_the_product_ships_survives_its_own_rule()
    {
        // The control that matters most: a refusal strict enough to reject the real catalogue would be
        // worse than the hole it closes. These are the client's UNIT_PRESETS, in the same order.
        var presets = new[]
        {
            new UnitOfMeasure("bar", Dimension.Pressure, 100_000),
            new UnitOfMeasure("kPa", Dimension.Pressure, 1_000),
            new UnitOfMeasure("psi", Dimension.Pressure, 6_894.75729316836),
            new UnitOfMeasure("°C", Dimension.Temperature, 1, 273.15),
            new UnitOfMeasure("°F", Dimension.Temperature, 5.0 / 9.0, 255.372222222222),
            new UnitOfMeasure("m³/h", Dimension.VolumeFlow, 1.0 / 3600),
            new UnitOfMeasure("l/s", Dimension.VolumeFlow, 0.001),
            new UnitOfMeasure("kW", Dimension.Power, 1_000),
            new UnitOfMeasure("A", Dimension.ElectricCurrent, 1),
            new UnitOfMeasure("V", Dimension.ElectricPotential, 1),
            new UnitOfMeasure("Hz", Dimension.Frequency, 1),
            new UnitOfMeasure("m", Dimension.Length, 1),
        };

        Assert.Equal(12, presets.Length);
    }

    [Fact]
    public void A_unit_that_was_built_converts_both_ways_without_reaching_infinity()
    {
        // What the refusal buys. Before it, `%` with a factor of zero turned every reading into the
        // offset one way and into infinity the other -- and nothing said so.
        var bar = new UnitOfMeasure("bar", Dimension.Pressure, 100_000);

        Assert.Equal(420_000, bar.ToSi(4.2), precision: 6);
        Assert.Equal(4.2, bar.FromSi(420_000), precision: 9);
        Assert.True(double.IsFinite(bar.FromSi(1)), "a built unit can never divide by zero");
    }

    [Fact]
    public void The_default_factor_is_one_so_a_unit_that_names_none_is_still_a_conversion()
    {
        // The default was already 1; what was missing was anything stopping a caller overriding it
        // with something that is not. Pinned so a later change cannot default it to zero.
        var dimensionless = new UnitOfMeasure("%", Dimension.Dimensionless);

        Assert.Equal(1.0, dimensionless.FactorToSi);
        Assert.Equal(7.5, dimensionless.ToSi(7.5));
    }
}
