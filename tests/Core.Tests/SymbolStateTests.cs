using ScadaDarbox.Core.Model;
using ScadaDarbox.Core.Screens;
using ScadaDarbox.Core.Tags;

namespace ScadaDarbox.Core.Tests;

/// <summary>
/// Deriving a symbol's state from a reading (ADR-0027).
/// </summary>
/// <remarks>
/// The order of the checks is the whole of this behaviour and every test here is about that order:
/// quality before mapping, first match before fallback, fallback before nothing. A rule evaluated in
/// the wrong order still produces a state — just not the one the plant is in — which is why these are
/// written as assertions about WHICH state rather than about whether one came out.
/// </remarks>
public class SymbolStateTests
{
    private static readonly IReadOnlyList<SymbolState> PumpMapping =
    [
        new("running", SymbolComparisons.Is, "true"),
        new("fault", SymbolComparisons.Is, "FAULT"),
        new("stopped", SymbolComparisons.Is, "false", Otherwise: true),
    ];

    private static TagSnapshot Reading(TagValue value, Quality quality = Quality.Good) => new(
        Guid.NewGuid(),
        "Skopje/Pump House/Pump Running",
        value,
        DateTimeOffset.UnixEpoch,
        quality,
        null);

    private static TagValue Bool(bool value) => new TagValue.Boolean(value);

    private static TagValue Number(double value) => new TagValue.Numeric(value);

    [Fact]
    public void A_reading_matching_a_rule_draws_that_rule_state()
    {
        var result = SymbolStates.Derive(PumpMapping, Reading(Bool(true)));

        Assert.Equal(Symbols.Running, result.State);
        Assert.False(result.FromQuality);
        Assert.Equal(0, result.MatchedRule);
    }

    [Fact]
    public void The_first_matching_rule_wins_even_when_a_later_one_also_matches()
    {
        // Two rules that both match, deliberately: the second is dead and must stay dead. Order is
        // meaning in this list (ADR-0027 §2) and a list evaluated any other way would make an
        // author's arrangement of it decorative.
        IReadOnlyList<SymbolState> mapping =
        [
            new("running", SymbolComparisons.Is, "true"),
            new("fault", SymbolComparisons.Is, "true"),
        ];

        var result = SymbolStates.Derive(mapping, Reading(Bool(true)));

        Assert.Equal(Symbols.Running, result.State);
        Assert.Equal(0, result.MatchedRule);
    }

    [Fact]
    public void A_fallback_matches_anything_and_is_used_only_when_nothing_else_did()
    {
        // "Anything" is the point: the fallback is written with no comparison at all, so it would
        // match the fault reading too — and must not, because it is listed after it.
        var fault = SymbolStates.Derive(PumpMapping, Reading(new TagValue.Text("FAULT")));

        Assert.Equal(Symbols.Fault, fault.State);
        Assert.Equal(1, fault.MatchedRule);

        var neither = SymbolStates.Derive(PumpMapping, Reading(new TagValue.Text("SOMETHING ELSE")));

        Assert.Equal(Symbols.Stopped, neither.State);
        Assert.Equal(2, neither.MatchedRule);
    }

    [Fact]
    public void A_reading_no_rule_matches_and_no_fallback_resolves_to_unknown()
    {
        // Not to any of the mapped states. Falling through to one of them would be inventing a fact
        // about a plant, and `unknown` is drawn rather than silent so the author can see the gap.
        IReadOnlyList<SymbolState> noFallback = [new("running", SymbolComparisons.Is, "true")];

        var result = SymbolStates.Derive(noFallback, Reading(Bool(false)));

        Assert.Equal(Symbols.Unknown, result.State);
        Assert.Null(result.MatchedRule);
    }

    [Theory]
    [InlineData(Quality.Bad, Symbols.Bad)]
    [InlineData(Quality.Stale, Symbols.Stale)]
    [InlineData(Quality.Uncertain, Symbols.Stale)]
    public void A_reading_that_is_not_Good_takes_its_state_from_the_quality_and_not_from_the_mapping(
        Quality quality,
        string expected)
    {
        // **The most important test here, and the reason it uses `true`.** The tag is reading true —
        // which the mapping turns into a running pump — and the quality says that reading cannot be
        // trusted. A pump that is running is a claim about the world, and this refuses to make it on
        // data nothing measured (ADR-0003, ADR-0027 §4).
        var result = SymbolStates.Derive(PumpMapping, Reading(Bool(true), quality));

        Assert.Equal(expected, result.State);
        Assert.True(result.FromQuality);
        Assert.Null(result.MatchedRule);
    }

    [Fact]
    public void A_tag_nothing_has_ever_arrived_for_is_unknown_rather_than_a_state()
    {
        var result = SymbolStates.Derive(PumpMapping, snapshot: null);

        Assert.Equal(Symbols.Unknown, result.State);
    }

    [Fact]
    public void An_empty_mapping_resolves_to_unknown_which_is_what_a_new_symbol_looks_like()
    {
        var result = SymbolStates.Derive([], Reading(Bool(true)));

        Assert.Equal(Symbols.Unknown, result.State);
    }

    [Theory]
    [InlineData(SymbolComparisons.Above, "10", 10.5, "high")]
    [InlineData(SymbolComparisons.Below, "10", 9.5, "low")]
    public void A_threshold_compares_a_number(string when, string threshold, double reading, string expected)
    {
        IReadOnlyList<SymbolState> mapping = [new(expected, when, threshold)];

        var result = SymbolStates.Derive(mapping, Reading(Number(reading)));

        Assert.Equal(expected, result.State);
    }

    [Fact]
    public void A_threshold_is_exclusive_at_the_boundary()
    {
        // `above 10` does not match 10. Stated as a test because either choice is defensible and only
        // one of them can be true, and an author setting a limit on a plant needs to know which.
        IReadOnlyList<SymbolState> above = [new("high", SymbolComparisons.Above, "10")];

        Assert.Equal(Symbols.Unknown, SymbolStates.Derive(above, Reading(Number(10))).State);
        Assert.Equal("high", SymbolStates.Derive(above, Reading(Number(10.0001))).State);
    }

    [Fact]
    public void A_threshold_does_not_match_a_boolean_because_a_boolean_has_no_ordering()
    {
        IReadOnlyList<SymbolState> mapping = [new("running", SymbolComparisons.Above, "0")];

        Assert.Equal(Symbols.Unknown, SymbolStates.Derive(mapping, Reading(Bool(true))).State);
    }

    [Fact]
    public void Equality_parses_a_number_so_a_trailing_zero_is_the_same_threshold()
    {
        // An author typing `4.50` to line a form up must not thereby create a state nothing reaches.
        IReadOnlyList<SymbolState> mapping = [new("at", SymbolComparisons.Is, "4.50")];

        Assert.Equal("at", SymbolStates.Derive(mapping, Reading(Number(4.5))).State);
    }

    [Fact]
    public void Equality_on_a_boolean_is_case_insensitive_because_it_is_typed_by_a_person()
    {
        IReadOnlyList<SymbolState> mapping = [new("running", SymbolComparisons.Is, "True")];

        Assert.Equal("running", SymbolStates.Derive(mapping, Reading(Bool(true))).State);
    }

    [Fact]
    public void A_comparison_this_build_does_not_know_matches_nothing_rather_than_throwing()
    {
        // The API refuses an unknown comparison at save time. This is the second line: the mapping is
        // data, a screen is read by everyone, and a rule that cannot be evaluated should cost its own
        // state rather than the screen.
        IReadOnlyList<SymbolState> mapping =
        [
            new("running", "approximately", "true"),
            new("stopped", SymbolComparisons.Is, "false", Otherwise: true),
        ];

        Assert.Equal(Symbols.Stopped, SymbolStates.Derive(mapping, Reading(Bool(true))).State);
    }

    [Fact]
    public void The_pump_can_be_drawn_in_every_state_its_mapping_could_name()
    {
        // The two lists have to agree or a mapping the API accepted could name a picture that does not
        // exist — which is the refusal ADR-0027's consequences describe, checked from the other side.
        foreach (var state in Symbols.StatesOf(Symbols.Pump))
        {
            Assert.True(Symbols.CanDraw(Symbols.Pump, state), $"the pump cannot draw {state}");
        }

        Assert.False(Symbols.CanDraw(Symbols.Pump, "sideways"));
        Assert.False(Symbols.CanDraw("tank", Symbols.Running));
    }

    [Fact]
    public void Only_a_running_pump_animates()
    {
        // Whether a state moves is the build's decision rather than the author's (ADR-0027 §5), and
        // only one state moves: a stopped pump that turned, or a faulted one, would be telling the
        // operator something the plant is not doing.
        Assert.True(Symbols.Animates(Symbols.Pump, Symbols.Running));

        foreach (var state in Symbols.StatesOf(Symbols.Pump).Where(s => s != Symbols.Running))
        {
            Assert.False(Symbols.Animates(Symbols.Pump, state), $"{state} should not animate");
        }
    }
}
