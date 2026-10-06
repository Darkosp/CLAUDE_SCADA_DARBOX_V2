using ScadaDarbox.Core.Model;
using ScadaDarbox.Core.Tags;

namespace ScadaDarbox.Core.Screens;

/// <summary>
/// What a symbol should be drawn as, and why (ADR-0027).
/// </summary>
/// <param name="State">One of <see cref="Symbols.AllStates"/>. Never null: there is always something to draw.</param>
/// <param name="FromQuality">
/// Whether the state came from the reading's quality rather than from the mapping (ADR-0027 §4).
/// True means the mapping was not consulted at all.
/// </param>
/// <param name="MatchedRule">
/// The index of the rule that decided it, or null when the state came from quality or from no match.
/// Kept so that an author looking at a screen that draws the wrong thing can be told which rule did it.
/// </param>
public sealed record SymbolStateResult(string State, bool FromQuality, int? MatchedRule);

/// <summary>
/// Turns a tag's reading into the state a symbol is drawn in (ADR-0027).
/// </summary>
/// <remarks>
/// <para>
/// Pure, and in Core rather than in the client, because it is a decision about what a plant is doing
/// rather than a way of drawing it: given the same reading and the same mapping, every renderer must
/// agree, and a rule evaluated in two places is a rule that will eventually be evaluated two ways.
/// </para>
/// <para>
/// <b>The order of the two steps is the whole of ADR-0027 §4.</b> Quality is consulted first and the
/// mapping is not consulted at all when it says anything other than Good. A boolean tag that has gone
/// Bad still carries its last value, and a pump drawn as running because of a reading nothing
/// measured is the failure ADR-0003 exists to prevent — in the medium where it is hardest to notice,
/// because a turning pump looks like news rather than like a missing reading.
/// </para>
/// </remarks>
public static class SymbolStates
{
    /// <summary>
    /// The state to draw, from a mapping and a reading.
    /// </summary>
    /// <remarks>
    /// A null snapshot means nothing has ever arrived for the tag. That is not the same as Bad — a Bad
    /// reading HAS a reading and it is not good — but it is the same answer here: there is no state to
    /// derive from nothing, and <see cref="Symbols.Unknown"/> is what this project draws instead of
    /// inventing one.
    /// </remarks>
    public static SymbolStateResult Derive(IReadOnlyList<SymbolState> states, TagSnapshot? snapshot)
    {
        ArgumentNullException.ThrowIfNull(states);

        if (snapshot is null)
        {
            return new SymbolStateResult(Symbols.Unknown, FromQuality: false, MatchedRule: null);
        }

        // §4, before anything is compared: a reading that is not Good has no state, whatever it says.
        var fromQuality = QualityStateOf(snapshot.Quality);

        if (fromQuality is not null)
        {
            return new SymbolStateResult(fromQuality, FromQuality: true, MatchedRule: null);
        }

        SymbolState? fallback = null;
        var fallbackAt = (int?)null;

        for (var index = 0; index < states.Count; index++)
        {
            var rule = states[index];

            if (rule.Otherwise)
            {
                // Remembered, not taken: a fallback matches anything, so honouring it where it stands
                // would make every rule after it dead. It is used only if nothing else matched.
                fallback ??= rule;
                fallbackAt ??= index;
                continue;
            }

            if (Matches(rule, snapshot))
            {
                return new SymbolStateResult(rule.State, FromQuality: false, MatchedRule: index);
            }
        }

        return fallback is null
            ? new SymbolStateResult(Symbols.Unknown, FromQuality: false, MatchedRule: null)
            : new SymbolStateResult(fallback.State, FromQuality: false, MatchedRule: fallbackAt);
    }

    /// <summary>
    /// The state a quality forces, or null when the quality is Good and the mapping should decide.
    /// </summary>
    /// <remarks>
    /// Uncertain and Stale share one state because they are the same message to an operator: the
    /// number is there and you should not lean on it. Bad is the one with nothing behind it, and it is
    /// kept separate for exactly that reason.
    /// </remarks>
    private static string? QualityStateOf(Quality quality) => quality switch
    {
        Quality.Good => null,
        Quality.Bad => Symbols.Bad,
        _ => Symbols.Stale,
    };

    /// <summary>
    /// Whether one rule matches a reading.
    /// </summary>
    /// <remarks>
    /// A comparison this build does not know, or one the reading cannot answer, is **not a match**
    /// rather than an error: the mapping is data, a screen is read by everyone, and a rule that cannot
    /// be evaluated should cost its own state rather than the screen. The API refuses an unknown
    /// comparison at save time (ADR-0027's consequences), so this is the second line.
    /// </remarks>
    private static bool Matches(SymbolState rule, TagSnapshot snapshot)
    {
        if (rule.When == SymbolComparisons.Is)
        {
            // A snapshot with no value at all — a Good reading always has one, so this is a tag that
            // has never received anything, and there is nothing for `is` to compare.
            return snapshot.Value is { } value && Equals(value, rule.Value);
        }

        // Above and below are numeric only. A boolean has no ordering, and a text tag's is not one an
        // operator would expect a symbol to draw from.
        if (snapshot.Value is not TagValue.Numeric numeric)
        {
            return false;
        }

        if (!double.TryParse(rule.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var threshold))
        {
            return false;
        }

        return rule.When switch
        {
            SymbolComparisons.Above => numeric.Value > threshold,
            SymbolComparisons.Below => numeric.Value < threshold,
            _ => false,
        };
    }

    /// <summary>
    /// Whether a reading's value equals what an author typed.
    /// </summary>
    /// <remarks>
    /// Compared as text, and that is a decision rather than laziness: the author types into a field
    /// and what they typed is what the API stores and what a refusal quotes back, so comparing
    /// anything else would make `true` and `True` and `1` different rules for one boolean. Numbers are
    /// parsed so that `4.50` and `4.5` are the same threshold — an author typing a trailing zero to
    /// line a form up should not thereby create a state nothing reaches.
    /// </remarks>
    private static bool Equals(TagValue value, string? typed)
    {
        if (typed is null)
        {
            return false;
        }

        return value switch
        {
            TagValue.Numeric numeric => double.TryParse(
                typed,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var wanted) && numeric.Value == wanted,
            TagValue.Boolean boolean => bool.TryParse(typed, out var wanted) && boolean.Value == wanted,
            TagValue.Text text => string.Equals(text.Value, typed, StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }
}
