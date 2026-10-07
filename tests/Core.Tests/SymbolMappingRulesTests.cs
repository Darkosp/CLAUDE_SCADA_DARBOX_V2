using ScadaDarbox.Core.Configuration;
using ScadaDarbox.Core.Model;

namespace ScadaDarbox.Core.Tests;

/// <summary>
/// What makes a symbol's state mapping saveable (ADR-0027).
/// </summary>
/// <remarks>
/// <para>
/// ADR-0027's consequences say a bad mapping is <i>"refused at save, by name, in the same way ADR-0024
/// refuses a component kind that does not exist"</i>, and until this file existed **none of those
/// refusals had a test** — <c>ProblemWithStates</c> was reached only through the API. A claim about a
/// refusal with nothing exercising it is a claim that can quietly stop being true, so each branch is
/// named here.
/// </para>
/// <para>
/// The newest refusal is the numeric threshold that is not a number, and it comes from the 2026-10-06
/// walk: <c>above 'hot'</c> saves cleanly and then matches no reading for ever, which is the same
/// "configured and dead" shape as a state the drawing does not have. It is refused here, with no tag
/// needed, because it is a property of the rule alone — <b>and the neighbouring case that needs the
/// tag's kind is deliberately NOT refused</b>, which the last test in this file pins.
/// </para>
/// </remarks>
public sealed class SymbolMappingRulesTests
{
    private static readonly Guid TagId = new("22222222-2222-4222-8222-222222222222");

    /// <summary>The mapping a new pump is born with for a boolean tag: valid, and the control here.</summary>
    private static readonly SymbolState[] APumpOnABoolean =
    [
        new(Symbols.Running, SymbolComparisons.Is, "true"),
        new(Symbols.Stopped, When: null, Value: null, Otherwise: true),
    ];

    [Fact]
    public void A_symbol_with_a_well_formed_mapping_is_saveable()
    {
        Assert.Null(ScreenRules.ProblemWith(Symbol(APumpOnABoolean)));
    }

    [Fact]
    public void A_symbol_with_no_states_is_refused_because_it_could_only_ever_draw_unknown()
    {
        var problem = ScreenRules.ProblemWith(Symbol([]));

        Assert.NotNull(problem);
        Assert.Contains("unknown", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_symbol_this_build_cannot_draw_is_refused_and_says_what_it_draws()
    {
        var problem = ScreenRules.ProblemWith(Symbol(APumpOnABoolean, symbol: "turbine"));

        Assert.NotNull(problem);
        Assert.Contains(Symbols.Pump, problem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_state_the_drawing_does_not_have_is_refused_by_name_and_by_rule()
    {
        // Named AND numbered: an author with six rules cannot fix "a state that does not exist".
        var problem = ScreenRules.ProblemWith(Symbol(
        [
            new(Symbols.Running, SymbolComparisons.Is, "true"),
            new("priming", SymbolComparisons.Is, "2"),
        ]));

        Assert.NotNull(problem);
        Assert.Contains("priming", problem, StringComparison.Ordinal);
        Assert.Contains("rule 2", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_fallbacks_are_refused_because_the_second_could_never_be_reached()
    {
        var problem = ScreenRules.ProblemWith(Symbol(
        [
            new(Symbols.Stopped, When: null, Value: null, Otherwise: true),
            new(Symbols.Unknown, When: null, Value: null, Otherwise: true),
        ]));

        Assert.NotNull(problem);
        Assert.Contains("2 fallback", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_fallback_that_also_states_a_comparison_is_refused()
    {
        // This is the one the walk hit in the seed data, and the refusal was right: a rule that matches
        // anything cannot also say what it matches, and one that did would be text the author believed
        // was doing something.
        var problem = ScreenRules.ProblemWith(Symbol(
        [
            new(Symbols.Stopped, SymbolComparisons.Is, "false", Otherwise: true),
        ]));

        Assert.NotNull(problem);
        Assert.Contains("fallback", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_comparison_this_build_does_not_evaluate_is_refused_and_lists_the_three_it_does()
    {
        var problem = ScreenRules.ProblemWith(Symbol([new(Symbols.Running, "between", "1")]));

        Assert.NotNull(problem);
        Assert.Contains("between", problem, StringComparison.Ordinal);
        Assert.Contains(SymbolComparisons.Above, problem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_comparison_with_nothing_to_compare_against_is_refused(string? value)
    {
        var problem = ScreenRules.ProblemWith(Symbol([new(Symbols.Running, SymbolComparisons.Is, value)]));

        Assert.NotNull(problem);
        Assert.Contains("nothing to compare against", problem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(SymbolComparisons.Above, "hot")]
    [InlineData(SymbolComparisons.Below, "cold")]
    [InlineData(SymbolComparisons.Above, "1,5")]
    [InlineData(SymbolComparisons.Below, "Infinity")]
    [InlineData(SymbolComparisons.Above, "NaN")]
    public void A_threshold_that_is_not_a_number_is_refused_and_quotes_what_was_typed(string when, string value)
    {
        // The new refusal. `1,5` is here because a comma is a decimal point in this project's own
        // locale and is NOT one on the wire: the stored text is parsed invariantly by both sides, so
        // accepting it would store a threshold that silently means 1 to one half and 15 to the other.
        //
        // Infinity and NaN parse as doubles and are refused anyway: no reading is above infinity, and
        // every comparison with NaN is false. Both are dead rules that look live.
        var problem = ScreenRules.ProblemWith(Symbol([new(Symbols.Running, when, value)]));

        Assert.NotNull(problem);
        Assert.Contains(value, problem, StringComparison.Ordinal);
        Assert.Contains("not a number", problem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("4.5")]
    [InlineData("-273.15")]
    [InlineData(" 12 ")]
    public void A_threshold_that_is_a_number_is_accepted(string value)
    {
        // The control for the test above. Without it, a refusal that rejected every threshold would
        // pass that one -- and this project has already paid for a test that could not fail.
        Assert.Null(ScreenRules.ProblemWith(Symbol(
        [
            new(Symbols.Running, SymbolComparisons.Above, value),
            new(Symbols.Stopped, When: null, Value: null, Otherwise: true),
        ])));
    }

    [Fact]
    public void A_decimal_comma_is_refused_and_the_refusal_says_what_to_type()
    {
        // This project's own keyboard produces `1,5`, and a refusal that only says "not a number"
        // about something the author can see is a number is a message people stop reading.
        var problem = ScreenRules.ProblemWith(Symbol([new(Symbols.Running, SymbolComparisons.Above, "1,5")]));

        Assert.NotNull(problem);
        Assert.Contains("'1.5'", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_threshold_that_is_nonsense_with_a_comma_in_it_gets_no_hint()
    {
        // The control for the hint, and the reason it is a separate test: a hint offered for `1,a`
        // would be advice that does not work, which is worse than no advice.
        var problem = ScreenRules.ProblemWith(Symbol([new(Symbols.Running, SymbolComparisons.Above, "1,a")]));

        Assert.NotNull(problem);
        Assert.DoesNotContain("decimal point", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void An_equality_against_text_is_accepted_however_unnumeric_it_is()
    {
        // `is` is not a numeric comparison, so the threshold rule above must not touch it: a text tag
        // reading "FAULT" is exactly what an author should be able to write.
        Assert.Null(ScreenRules.ProblemWith(Symbol(
        [
            new(Symbols.Fault, SymbolComparisons.Is, "FAULT"),
            new(Symbols.Stopped, When: null, Value: null, Otherwise: true),
        ])));
    }

    [Fact]
    public void A_mapping_that_cannot_match_the_tag_it_is_bound_to_is_NOT_refused_here()
    {
        // **The boundary, and it is deliberate.** `is true` on a numeric tag is the defect the
        // 2026-10-06 walk found, and it is not refused at save: deciding it needs the tag's kind, which
        // this rule does not have and should not fetch, and a stored screen would stop being saveable
        // the moment somebody changed a tag's kind underneath it.
        //
        // The editor warns instead -- `unmatchableRules` in the client's `screen.ts`, with
        // `src/Web/tests/symbol-mapping-fit.test.mjs` holding it to the same line from the other side.
        //
        // If this test ever fails, the refusal has moved and that is a decision, not a fix: it needs a
        // sentence in ADR-0027 and an answer about what happens to screens already stored.
        Assert.Null(ScreenRules.ProblemWith(Symbol(APumpOnABoolean)));
    }

    private static ScreenComponent Symbol(
        IReadOnlyList<SymbolState> states,
        string? symbol = Symbols.Pump) => new()
    {
        Id = Guid.NewGuid(),
        ScreenId = Guid.NewGuid(),
        RowIndex = 0,
        ColumnSpan = ScreenComponent.GridColumns,
        Position = 0,
        Kind = ScreenComponentKinds.Symbol,
        Title = null,
        TagId = TagId,
        Symbol = symbol,
        States = states,
    };
}
