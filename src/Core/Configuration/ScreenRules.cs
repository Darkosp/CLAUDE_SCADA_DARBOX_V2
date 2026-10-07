using System.Globalization;
using ScadaDarbox.Core.Model;

namespace ScadaDarbox.Core.Configuration;

/// <summary>
/// What makes a screen saveable, as opposed to merely well-typed (ADR-0024).
/// </summary>
/// <remarks>
/// <para>
/// This is a refusal and not a repair. A screen that names a component kind this build cannot draw,
/// or a component of a kind that reads a tag with no tag to read, is **refused when it is saved** —
/// with the reason named — rather than stored and skipped when it is drawn. The alternative is a
/// screen that is silently missing something, which an operator looking at it has no way to notice.
/// </para>
/// <para>
/// It lives in Core because it is a rule about the model and not about HTTP, so the API, a future
/// builder and the tests all ask the same question of the same code (ADR-0002: no module, no host,
/// no protocol — just the rule).
/// </para>
/// </remarks>
public static class ScreenRules
{
    /// <summary>
    /// Why this screen cannot be saved, or nothing when it can.
    /// </summary>
    /// <remarks>
    /// The first problem is returned rather than all of them, because a caller fixing a screen fixes
    /// one thing at a time and a list of fifteen refusals for one mistake is worse than the one that
    /// matters. Which one comes first is the order of the checks below, and that order is deliberate:
    /// a kind that cannot be drawn is a bigger problem than a title in the wrong place.
    /// </remarks>
    public static string? ProblemWith(Screen screen)
    {
        if (string.IsNullOrWhiteSpace(screen.Name))
        {
            return "A screen needs a name.";
        }

        foreach (var component in screen.Components)
        {
            if (ProblemWith(component) is { } problem)
            {
                return problem;
            }
        }

        return null;
    }

    /// <summary>Why this component cannot be saved, or nothing when it can.</summary>
    public static string? ProblemWith(ScreenComponent component)
    {
        if (!ScreenComponentKinds.IsKnown(component.Kind))
        {
            // Named, because the author has to be able to see which one it was — and named as
            // "unknown" rather than "invalid", because a kind this build does not have is a
            // version question and not a mistake (ADR-0024 §3).
            return $"Unknown component kind '{component.Kind}'. This build draws: "
                + string.Join(", ", ScreenComponentKinds.All) + ".";
        }

        if (component.RowIndex < 0)
        {
            return $"Component of kind '{component.Kind}' has a negative row.";
        }

        if (component.Position < 0)
        {
            return $"Component of kind '{component.Kind}' has a negative position.";
        }

        if (component.ColumnSpan < 1 || component.ColumnSpan > ScreenComponent.GridColumns)
        {
            return $"Component of kind '{component.Kind}' spans {component.ColumnSpan} columns, "
                + $"and a screen is {ScreenComponent.GridColumns} wide.";
        }

        if (ScreenComponentKinds.NeedsTag(component.Kind) && component.TagId is null)
        {
            // The one refusal that matters most: this is the difference between a component that
            // shows a reading's quality and one that would have to invent one.
            return $"A '{component.Kind}' component reads a tag and needs one.";
        }

        if (!ScreenComponentKinds.NeedsTag(component.Kind) && component.TagId is not null)
        {
            return $"A '{component.Kind}' component does not read a tag, and one was given.";
        }

        if (ScreenComponentKinds.NeedsTitle(component.Kind) && string.IsNullOrWhiteSpace(component.Title))
        {
            // A `label` with no text is an empty box an author cannot explain -- it is nothing but
            // its text (ADR-0024's kinds table).
            //
            // This used to read `TakesTitle`, which is the wider question "may this kind carry text".
            // That made it fire for an `alarms` component as well, where the title is a heading over
            // the alarms rather than the component itself -- and where it is what every Site's seeded
            // screen fails on, because the seeder writes rows and had never been asked to supply one.
            return $"A '{component.Kind}' component shows text and needs some.";
        }

        if (ScreenComponentKinds.NeedsStates(component.Kind))
        {
            return ProblemWithStates(component);
        }

        return null;
    }

    /// <summary>
    /// Why a symbol's state mapping cannot be saved, or nothing when it can (ADR-0027).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Five ways a mapping is wrong, and each is refused **by name** for the reason every other
    /// refusal here is: a mapping naming a state the drawing does not have, a comparison this build
    /// does not evaluate, or a numeric comparison against something that is not a number, produces a
    /// symbol that sits on one state forever while looking configured. The author is the only person
    /// who can fix that, and they can only fix it if they are told which rule is at fault.
    /// </para>
    /// <para>
    /// What is deliberately **not** checked is whether a well-formed mapping is the right one for a
    /// plant. `running when true` is valid on a pump driven by a boolean and wrong on one driven by a
    /// pressure, and no rule can tell the difference. That is a walk's job.
    /// </para>
    /// <para>
    /// <b>Between those two there is a third case, and it is not refused here either:</b> a rule that
    /// is well formed and can never match <i>the tag it is bound to</i> — `is true` on a numeric tag,
    /// which is the defect the 2026-10-06 walk found. Deciding that needs the tag's kind, which this
    /// rule does not have and should not fetch, and refusing it at save would make a stored screen
    /// unsaveable the moment somebody changed a tag's kind underneath it. The editor warns instead:
    /// see `unmatchableRules` in the client's `screen.ts`.
    /// </para>
    /// </remarks>
    private static string? ProblemWithStates(ScreenComponent component)
    {
        var symbol = component.Symbol;

        if (!Symbols.IsKnown(symbol))
        {
            return "A 'symbol' component needs a symbol this build draws. This build draws: "
                + string.Join(", ", Symbols.All) + ".";
        }

        if (component.States.Count == 0)
        {
            // A symbol with no rules can only ever draw `unknown`: a component that says nothing while
            // taking up room on a screen.
            return "A 'symbol' component needs at least one state, or it can only ever show 'unknown'.";
        }

        var fallbacks = component.States.Count(state => state.Otherwise);

        if (fallbacks > 1)
        {
            // Two fallbacks make the second unreachable, and the author could not tell that from the
            // screen: both would look like rules doing something.
            return $"A 'symbol' component has {fallbacks} fallback states, and only one rule can be the fallback.";
        }

        for (var index = 0; index < component.States.Count; index++)
        {
            var rule = component.States[index];
            var at = $"rule {index + 1}";

            if (!Symbols.CanDraw(symbol, rule.State))
            {
                return $"A '{symbol}' symbol cannot be drawn as '{rule.State}' ({at}). It draws: "
                    + string.Join(", ", Symbols.StatesOf(symbol)) + ".";
            }

            if (rule.Otherwise)
            {
                // A fallback matches anything, so a comparison on it is dead text the author believed
                // was doing something.
                if (rule.When is not null || rule.Value is not null)
                {
                    return $"The fallback state of a 'symbol' component ({at}) cannot also have a comparison.";
                }

                continue;
            }

            if (!SymbolComparisons.IsKnown(rule.When))
            {
                return $"A 'symbol' component's {at} compares with '{rule.When}', which this build does not "
                    + "evaluate. It evaluates: " + string.Join(", ", SymbolComparisons.All) + ".";
            }

            if (string.IsNullOrWhiteSpace(rule.Value))
            {
                return $"A 'symbol' component's {at} compares with '{rule.When}' and has nothing to compare against.";
            }

            if (rule.When is SymbolComparisons.Above or SymbolComparisons.Below
                && !IsAThreshold(rule.Value))
            {
                // `above` and `below` are numeric comparisons, so a threshold that is not a number is
                // a rule that can never match ANY reading — the same "configured and dead" shape as a
                // state the symbol cannot draw, and refused here for the same reason.
                //
                // This needs nothing about the bound tag, which is why it belongs on the server at
                // all: it is a property of the rule alone. Whether a *well-formed* rule can match the
                // tag it is bound to is a different question, it needs the tag's kind, and the editor
                // answers it — see `unmatchableRules` in the client's `screen.ts` and the note there
                // about why it is not refused here.
                return $"A 'symbol' component's {at} compares '{rule.When}' against '{rule.Value}', "
                    + "which is not a number to compare against."
                    + DecimalPointHint(rule.Value);
            }
        }

        return null;
    }

    /// <summary>
    /// Whether what the author typed is a number a reading could be above or below.
    /// </summary>
    /// <remarks>
    /// Invariant culture, because this is the text the API stores and the client parses the same way —
    /// a threshold that meant one thing to a Macedonian browser and another to the server would be the
    /// worst kind of drift here. Infinity and NaN parse and are refused anyway: a reading is never
    /// above infinity and every comparison with NaN is false, so both are dead rules that look live.
    /// </remarks>
    /// <summary>
    /// The one extra sentence worth adding when what was typed is a number with a decimal comma.
    /// </summary>
    /// <remarks>
    /// <b>This project's own keyboard produces `1,5`.</b> A refusal that only says "not a number"
    /// about a string the author can see is a number is the kind of message people stop reading, and
    /// the client is not even consistent about it: <c>parseNumberField</c> converts a comma for the
    /// alarm threshold form, while a symbol's mapping stores the text exactly as typed (ADR-0027) and
    /// parses it invariantly. **Whether that difference should exist at all is a question, recorded in
    /// `open-work.md` §3** — until it is answered, the refusal at least says what to type.
    /// </remarks>
    private static string DecimalPointHint(string? typed) =>
        typed is not null && typed.Contains(',') && IsAThreshold(typed.Replace(',', '.'))
            ? $" A decimal point is what this field reads, so type '{typed.Replace(',', '.')}'."
            : string.Empty;

    private static bool IsAThreshold(string? typed) =>
        double.TryParse(
            typed,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var threshold) && double.IsFinite(threshold);
}
