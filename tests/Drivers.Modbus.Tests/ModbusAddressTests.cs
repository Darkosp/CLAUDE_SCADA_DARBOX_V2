using ScadaDarbox.Modules.Drivers.Modbus;

namespace ScadaDarbox.Drivers.Modbus.Tests;

public class ModbusAddressTests
{
    [Theory]
    [InlineData("holding:0", ModbusArea.Holding, 0)]
    [InlineData("input:12", ModbusArea.Input, 12)]
    [InlineData("COIL:3", ModbusArea.Coil, 3)]
    [InlineData("discrete:65535", ModbusArea.Discrete, 65535)]
    public void Parses_area_and_offset(string source, ModbusArea expectedArea, int expectedOffset)
    {
        var address = ModbusAddress.Parse(source);

        Assert.Equal(expectedArea, address.Area);
        Assert.Equal(expectedOffset, address.Offset);
        Assert.Equal(1.0, address.Scale);
    }

    [Fact]
    public void Parses_the_scale_option()
    {
        var address = ModbusAddress.Parse("holding:0?scale=0.01");

        Assert.Equal(0.01, address.Scale);
    }

    [Fact]
    public void Parses_the_scale_option_independently_of_the_current_culture()
    {
        // A decimal point must stay a decimal point on a machine whose locale uses a comma.
        var original = Thread.CurrentThread.CurrentCulture;
        Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("mk-MK");
        try
        {
            Assert.Equal(0.01, ModbusAddress.Parse("holding:0?scale=0.01").Scale);
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = original;
        }
    }

    [Theory]
    [InlineData("holding")]
    [InlineData("register:0")]
    [InlineData("holding:-1")]
    [InlineData("holding:70000")]
    [InlineData("holding:0?scale=abc")]
    [InlineData("holding:0?unknown=1")]
    public void Rejects_malformed_addresses(string source)
    {
        Assert.False(ModbusAddress.TryParse(source, out _, out var error));
        Assert.NotEmpty(error);
    }

    [Theory]
    [InlineData("coil:0", true)]
    [InlineData("discrete:0", true)]
    [InlineData("holding:0", false)]
    [InlineData("input:0", false)]
    public void Knows_which_areas_are_bits(string source, bool expected)
    {
        Assert.Equal(expected, ModbusAddress.Parse(source).IsBitArea);
    }
}
