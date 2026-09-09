using ScadaDarbox.Core.Model;

namespace ScadaDarbox.Core.Tests;

public class UnitOfMeasureTests
{
    private static readonly UnitOfMeasure Bar = new("bar", Dimension.Pressure, 100_000);
    private static readonly UnitOfMeasure Psi = new("psi", Dimension.Pressure, 6_894.757_293_168_36);
    private static readonly UnitOfMeasure Celsius = new("°C", Dimension.Temperature, 1.0, 273.15);
    private static readonly UnitOfMeasure Fahrenheit = new("°F", Dimension.Temperature, 5.0 / 9.0, 255.372_222_222_222);

    [Fact]
    public void Converts_between_units_of_the_same_dimension()
    {
        Assert.Equal(14.5038, Bar.ConvertTo(1.0, Psi), precision: 3);
    }

    [Fact]
    public void Converts_temperature_using_the_offset_not_only_the_factor()
    {
        // The case a factor-only unit model gets wrong: 0 °C is 32 °F, not 0 °F.
        Assert.Equal(32.0, Celsius.ConvertTo(0.0, Fahrenheit), precision: 6);
        Assert.Equal(100.0, Fahrenheit.ConvertTo(212.0, Celsius), precision: 6);
    }

    [Fact]
    public void Refuses_to_convert_across_dimensions()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => Bar.ConvertTo(1.0, Celsius));

        Assert.Contains("different dimensions", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Round_trips_through_si()
    {
        Assert.Equal(4.2, Bar.FromSi(Bar.ToSi(4.2)), precision: 9);
    }
}
