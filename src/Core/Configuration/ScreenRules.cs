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

        return null;
    }
}
