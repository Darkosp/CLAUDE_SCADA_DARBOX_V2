using ScadaDarbox.Core.Model;

namespace ScadaDarbox.Core.Tests;

/// <summary>
/// The symbols this build draws, and the states each can be drawn in (ADR-0027 §6).
/// </summary>
/// <remarks>
/// <para>
/// ADR-0027 shipped one shape and said what adding another would cost: <i>"a drawing and a state list,
/// not a decision"</i>, naming a tank, a valve and a motor. These are those three, and this file is
/// what keeps the claim true — every rule that made one symbol safe has to hold for four, and most of
/// them are rules about the **set** rather than about any one drawing.
/// </para>
/// <para>
/// The two that matter most are the last: every symbol must be able to draw the three states quality
/// can force on it, or a Bad reading would have nowhere to go; and no symbol may name a state outside
/// the vocabulary a refusal can offer, or an author would be told to pick from a list that does not
/// contain the answer.
/// </para>
/// </remarks>
public sealed class SymbolVocabularyTests
{
    [Fact]
    public void This_build_draws_four_symbols()
    {
        Assert.Equal(
            new[] { Symbols.Pump, Symbols.Motor, Symbols.Valve, Symbols.Tank },
            Symbols.All);
    }

    [Theory]
    [InlineData(Symbols.Pump)]
    [InlineData(Symbols.Motor)]
    [InlineData(Symbols.Valve)]
    [InlineData(Symbols.Tank)]
    public void Every_symbol_can_be_drawn_in_the_three_states_quality_forces(string symbol)
    {
        // **The rule that makes the set safe.** ADR-0027 §4 says quality overrides the mapping: a
        // reading that is not Good produces `bad` or `stale` whatever it says, and a mapping with no
        // match produces `unknown`. A symbol that could not draw one of those would have a state the
        // engine can reach and the drawing cannot show -- so a Bad reading would land nowhere.
        Assert.True(Symbols.CanDraw(symbol, Symbols.Unknown), $"{symbol} cannot draw unknown");
        Assert.True(Symbols.CanDraw(symbol, Symbols.Bad), $"{symbol} cannot draw bad");
        Assert.True(Symbols.CanDraw(symbol, Symbols.Stale), $"{symbol} cannot draw stale");
    }

    [Theory]
    [InlineData(Symbols.Pump)]
    [InlineData(Symbols.Motor)]
    [InlineData(Symbols.Valve)]
    [InlineData(Symbols.Tank)]
    public void Every_state_a_symbol_offers_is_in_the_vocabulary_a_refusal_quotes(string symbol)
    {
        // `AllStates` is what a refusal offers when an author names a state that does not exist. A
        // symbol carrying a state outside it would have the product tell an author to choose from a
        // list that does not contain their answer.
        foreach (var state in Symbols.StatesOf(symbol))
        {
            Assert.Contains(state, Symbols.AllStates);
        }
    }

    [Fact]
    public void Each_symbol_has_its_own_plant_states_and_they_are_not_interchangeable()
    {
        // A valve is not open-or-running and a tank is not stopped. The states are the drawing's, which
        // is why `CanDraw` takes both -- and why a mapping is only valid against a particular symbol.
        Assert.True(Symbols.CanDraw(Symbols.Valve, Symbols.Open));
        Assert.False(Symbols.CanDraw(Symbols.Pump, Symbols.Open));

        Assert.True(Symbols.CanDraw(Symbols.Tank, Symbols.High));
        Assert.False(Symbols.CanDraw(Symbols.Valve, Symbols.High));

        Assert.True(Symbols.CanDraw(Symbols.Motor, Symbols.Running));
        Assert.False(Symbols.CanDraw(Symbols.Tank, Symbols.Running));
    }

    [Fact]
    public void A_motor_runs_and_a_valve_and_a_tank_never_animate()
    {
        // ADR-0027 §5: animation comes from the state, and only a machine that turns turns. A valve
        // that is open is open -- there is no continuing motion to show -- and a tank in a band is not
        // moving either. **A tank is the one to watch here**: the temptation is a fill that tracks the
        // reading, and that is the exact thing §5 refuses, because a reader cannot measure a height.
        Assert.True(Symbols.Animates(Symbols.Pump, Symbols.Running));
        Assert.True(Symbols.Animates(Symbols.Motor, Symbols.Running));

        Assert.False(Symbols.Animates(Symbols.Pump, Symbols.Stopped));
        Assert.False(Symbols.Animates(Symbols.Motor, Symbols.Fault));

        foreach (var state in Symbols.StatesOf(Symbols.Valve))
        {
            Assert.False(Symbols.Animates(Symbols.Valve, state), $"a valve must not animate in {state}");
        }

        foreach (var state in Symbols.StatesOf(Symbols.Tank))
        {
            Assert.False(Symbols.Animates(Symbols.Tank, state), $"a tank must not animate in {state}");
        }
    }

    [Fact]
    public void A_symbol_this_build_does_not_have_draws_nothing_rather_than_something()
    {
        // The control for every theory above: they would all pass against a `CanDraw` that said yes to
        // everything. An unknown shape has no states, so a mapping against one is refused rather than
        // quietly accepted.
        Assert.Empty(Symbols.StatesOf("turbine"));
        Assert.Empty(Symbols.StatesOf(null));
        Assert.False(Symbols.IsKnown("turbine"));
        Assert.False(Symbols.CanDraw("turbine", Symbols.Running));
        Assert.False(Symbols.Animates("turbine", Symbols.Running));
    }

    [Fact]
    public void No_symbol_offers_the_same_state_twice()
    {
        // A duplicate would show twice in the author's picker and read as two different things.
        foreach (var symbol in Symbols.All)
        {
            var states = Symbols.StatesOf(symbol);
            Assert.Equal(states.Count, states.Distinct().Count());
        }
    }
}
