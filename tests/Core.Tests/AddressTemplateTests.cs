using ScadaDarbox.Core.Templates;

namespace ScadaDarbox.Core.Tests;

public class AddressTemplateTests
{
    [Fact]
    public void Substitutes_named_placeholders()
    {
        var resolved = AddressTemplate.Resolve(
            "holding:{offset}?scale=0.01",
            new Dictionary<string, string> { ["offset"] = "12" });

        Assert.Equal("holding:12?scale=0.01", resolved);
    }

    [Fact]
    public void Substitutes_the_same_placeholder_everywhere_it_appears()
    {
        var resolved = AddressTemplate.Resolve(
            "{unit}:{offset}/{unit}",
            new Dictionary<string, string> { ["unit"] = "1", ["offset"] = "7" });

        Assert.Equal("1:7/1", resolved);
    }

    [Fact]
    public void Two_instances_of_one_template_resolve_to_different_addresses()
    {
        // The behaviour the phase gate rests on: without it, three devices from one
        // template would be three devices reading the same register.
        const string template = "holding:{offset}";

        Assert.Equal(
            "holding:0",
            AddressTemplate.Resolve(template, new Dictionary<string, string> { ["offset"] = "0" }));
        Assert.Equal(
            "holding:10",
            AddressTemplate.Resolve(template, new Dictionary<string, string> { ["offset"] = "10" }));
    }

    [Fact]
    public void A_template_with_no_placeholders_is_returned_unchanged()
    {
        Assert.Equal("coil:0", AddressTemplate.Resolve("coil:0", new Dictionary<string, string>()));
    }

    [Fact]
    public void A_missing_parameter_fails_rather_than_resolving_to_nothing()
    {
        // Substituting an empty string would produce an address that looks well-formed
        // and reads the wrong place — a silent wrong answer rather than a loud failure.
        var exception = Assert.Throws<TemplateParameterMissingException>(
            () => AddressTemplate.Resolve("holding:{offset}", new Dictionary<string, string>()));

        Assert.Equal("offset", exception.ParameterName);
        Assert.Contains("offset", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unmatched_brace_is_treated_as_literal_text()
    {
        // A driver's address syntax may legitimately contain a brace; only a closed
        // {name} pair is a placeholder.
        Assert.Equal(
            "weird{address",
            AddressTemplate.Resolve("weird{address", new Dictionary<string, string>()));
    }

    [Fact]
    public void Lists_the_parameters_a_template_needs()
    {
        var names = AddressTemplate.ParameterNames("{unit}:{offset}+{offset}");

        Assert.Equal(["unit", "offset"], names);
    }
}
