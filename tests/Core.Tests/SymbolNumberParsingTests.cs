using ScadaDarbox.Core.Model;
using ScadaDarbox.Core.Screens;
using ScadaDarbox.Core.Tags;

namespace ScadaDarbox.Core.Tests;

/// <summary>
/// What counts as a number in a symbol's mapping (ADR-0027), from Core's side.
/// </summary>
/// <remarks>
/// <para>
/// The twin of <c>src/Web/tests/symbol-number-parsing.test.mjs</c>, and the pair exists because
/// ADR-0027 evaluates one set of rules in two languages. The argument for tolerating that is at
/// <see cref="SymbolStates"/> and it rests on one claim — <i>both sides are held to the same
/// cases</i> — which was **false in three places** until 2026-10-07: JavaScript's <c>Number()</c>
/// reads <c>""</c> and <c>"   "</c> as zero and <c>"0x10"</c> as sixteen, and
/// <see cref="double.TryParse(string, System.Globalization.NumberStyles, IFormatProvider, out double)"/>
/// refuses all three.
/// </para>
/// <para>
/// The empty case was reachable: the editor creates a rule with an empty value and the live preview
/// evaluates the draft at once, so a tag reading <c>0.00</c> entered a state the API would then refuse
/// to store. **This file is the half that cannot drift silently** — if someone relaxes Core's parse,
/// these fail; if someone relaxes the client's, its twin fails.
/// </para>
/// </remarks>
public sealed class SymbolNumberParsingTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("0x10")]
    [InlineData("1,5")]
    [InlineData("1_0")]
    [InlineData("hot")]
    public void What_is_not_a_number_matches_no_reading(string typed)
    {
        // Each of these is refused rather than guessed at. `1,5` is the one this project will actually
        // meet -- a decimal comma is what a Macedonian keyboard produces -- and a mapping stores what
        // the author typed, so it is refused here and recorded as a question in open-work 3.
        var states = new[] { new SymbolState(Symbols.Running, SymbolComparisons.Is, typed) };

        // Zero and sixteen are the two readings the JavaScript parser would have matched.
        foreach (var reading in new double[] { 0, 1.5, 10, 16 })
        {
            Assert.Equal(Symbols.Unknown, SymbolStates.Derive(states, Snapshot(reading)).State);
        }
    }

    [Theory]
    [InlineData("4.50", 4.5)]
    [InlineData(" 12 ", 12)]
    [InlineData("1e3", 1000)]
    [InlineData("-273.15", -273.15)]
    [InlineData(".5", 0.5)]
    [InlineData("+7", 7)]
    public void What_is_a_number_matches_the_reading_it_names(string typed, double reading)
    {
        // The control. Without it, a parse that refused everything would satisfy the theory above --
        // the shape of test this project has already been caught by once.
        var states = new[] { new SymbolState(Symbols.Running, SymbolComparisons.Is, typed) };

        Assert.Equal(Symbols.Running, SymbolStates.Derive(states, Snapshot(reading)).State);
    }

    [Theory]
    [InlineData(SymbolComparisons.Below, "Infinity")]
    [InlineData(SymbolComparisons.Above, "-Infinity")]
    [InlineData(SymbolComparisons.Above, "NaN")]
    [InlineData(SymbolComparisons.Below, "NaN")]
    public void A_non_finite_threshold_matches_nothing(string when, string typed)
    {
        // `double.TryParse` ACCEPTS "Infinity" and "NaN", so without the explicit finite guard
        // `below Infinity` would match every numeric reading here while matching none in the client --
        // a rule alive in the tests and dead in the product. `ScreenRules` refuses storing one at all;
        // this is what happens to a mapping stored before that rule existed.
        var states = new[] { new SymbolState(Symbols.Running, when, typed) };

        Assert.Equal(Symbols.Unknown, SymbolStates.Derive(states, Snapshot(5)).State);
    }

    [Fact]
    public void NaN_equals_nothing_including_a_reading_that_is_itself_NaN()
    {
        var states = new[] { new SymbolState(Symbols.Running, SymbolComparisons.Is, "NaN") };

        Assert.Equal(Symbols.Unknown, SymbolStates.Derive(states, Snapshot(double.NaN)).State);
    }

    private static TagSnapshot Snapshot(double value) => new(
        new Guid("33333333-3333-4333-8333-333333333333"),
        "Site/Device/Tag",
        new TagValue.Numeric(value),
        DateTimeOffset.UnixEpoch,
        Quality.Good,
        UnitSymbol: null,
        NoDataSinceUtc: null);
}
