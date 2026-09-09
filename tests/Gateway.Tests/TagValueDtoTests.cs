using System.Text.Json;
using ScadaDarbox.Core.Model;
using ScadaDarbox.Gateway.Contracts;

namespace ScadaDarbox.Gateway.Tests;

public class TagValueDtoTests
{
    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void A_non_finite_numeric_is_sent_as_no_value_and_stays_serialisable(double value)
    {
        // JSON cannot express NaN or infinity. Without this guard, serialisation throws
        // part-way through a response whose headers have already been sent, so a client
        // sees HTTP 200 with a truncated body — which is how this was found.
        var dto = TagValueDto.From(new TagValue.Numeric(value));

        Assert.Equal("none", dto.Kind);
        Assert.Null(dto.Numeric);

        var json = JsonSerializer.Serialize(dto);
        Assert.Contains("\"none\"", json, StringComparison.Ordinal);
    }
}
