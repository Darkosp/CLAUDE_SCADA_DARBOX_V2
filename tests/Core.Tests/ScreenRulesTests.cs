using ScadaDarbox.Core.Configuration;
using ScadaDarbox.Core.Model;

namespace ScadaDarbox.Core.Tests;

/// <summary>
/// What makes a screen saveable (ADR-0024 §3, §4).
/// </summary>
/// <remarks>
/// These are the refusals, and they are the point: a screen saved with a component kind nobody can
/// draw, or one that reads a tag with no tag to read, is a screen that is silently missing
/// something. An operator looking at it has no way to know, so the refusal has to happen where the
/// author can still do something about it.
/// </remarks>
public sealed class ScreenRulesTests
{
    private static readonly Guid TagId = new("11111111-1111-4111-8111-111111111111");

    [Fact]
    public void A_screen_with_a_name_and_no_components_is_saveable()
    {
        // Empty is legal: a screen somebody has made and not filled in yet is not an error, and
        // making it one would mean they could not save their work as they went.
        Assert.Null(ScreenRules.ProblemWith(ScreenOf([], name: "Overview")));
    }

    [Theory]
    [InlineData(ScreenComponentKinds.Label)]
    [InlineData(ScreenComponentKinds.Value)]
    [InlineData(ScreenComponentKinds.Trend)]
    [InlineData(ScreenComponentKinds.Alarms)]
    [InlineData(ScreenComponentKinds.Status)]
    public void Every_kind_this_build_draws_is_saveable(string kind)
    {
        var component = ScreenComponentKinds.NeedsTag(kind)
            ? Component(kind, tagId: TagId)
            : Component(kind, title: "text");

        Assert.Null(ScreenRules.ProblemWith(component));
    }

    [Fact]
    public void An_unknown_kind_is_refused_by_name_and_nothing_is_stored()
    {
        // Named, so the author can see which one it was -- and the whole screen is refused, not the
        // component skipped: a screen saved with something nobody can draw is a screen silently
        // missing something.
        var problem = ScreenRules.ProblemWith(ScreenOf([Component("gauge", tagId: TagId)]));

        Assert.NotNull(problem);
        Assert.Contains("gauge", problem, StringComparison.Ordinal);

        // And it says what this build does draw, so the author is not left guessing.
        Assert.Contains(ScreenComponentKinds.Value, problem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ScreenComponentKinds.Value)]
    [InlineData(ScreenComponentKinds.Trend)]
    [InlineData(ScreenComponentKinds.Status)]
    public void A_kind_that_reads_a_tag_is_refused_without_one(string kind)
    {
        var problem = ScreenRules.ProblemWith(Component(kind));

        Assert.NotNull(problem);
        Assert.Contains(kind, problem, StringComparison.Ordinal);
        Assert.Contains("needs one", problem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ScreenComponentKinds.Label)]
    [InlineData(ScreenComponentKinds.Alarms)]
    public void A_kind_that_reads_no_tag_is_refused_with_one(string kind)
    {
        // The mirror, and it is not pedantry: a tag on a component that cannot show a value is a
        // binding somebody believes is doing something, and a screen is where that belief is
        // expensive.
        var problem = ScreenRules.ProblemWith(Component(kind, tagId: TagId, title: "text"));

        Assert.NotNull(problem);
        Assert.Contains("does not read a tag", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_label_is_refused_without_text()
    {
        // A `label` is nothing but its text (ADR-0024's kinds table), so a label with no text is an
        // empty box an author cannot explain.
        var problem = ScreenRules.ProblemWith(Component(ScreenComponentKinds.Label, title: "  "));

        Assert.NotNull(problem);
        Assert.Contains("needs some", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void An_alarms_component_is_accepted_without_text_and_that_is_the_point()
    {
        // This used to be refused, and refusing it is what made every seeded screen unsaveable: the
        // seeder writes rows directly, so nothing had ever given its `alarms` component a title, and
        // an author who opened the screen a new Site is born with and changed anything was answered
        // "A 'alarms' component shows text and needs some".
        //
        // ADR-0024's kinds table gives text as what a `label` shows and says nothing of the sort for
        // `alarms`, whose subject is a Site's standing alarms. So the heading is optional, and this is
        // the assertion that keeps it optional.
        var screen = ScreenOf([Component(ScreenComponentKinds.Alarms, title: null)]);

        Assert.Null(ScreenRules.ProblemWith(screen));
    }

    [Fact]
    public void An_alarms_component_may_still_carry_a_heading()
    {
        // The control for the test above: optional is not the same as forbidden, and a heading is
        // worth having when an author writes one.
        var screen = ScreenOf([Component(ScreenComponentKinds.Alarms, title: "Standing alarms")]);

        Assert.Null(ScreenRules.ProblemWith(screen));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(13)]
    [InlineData(-1)]
    public void A_span_outside_the_grid_is_refused(int span)
    {
        var problem = ScreenRules.ProblemWith(Component(ScreenComponentKinds.Value, tagId: TagId, span: span));

        Assert.NotNull(problem);
        Assert.Contains($"{ScreenComponent.GridColumns} wide", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_full_width_component_is_what_a_span_of_the_whole_grid_means()
    {
        Assert.Null(ScreenRules.ProblemWith(
            Component(ScreenComponentKinds.Value, tagId: TagId, span: ScreenComponent.GridColumns)));
    }

    [Fact]
    public void A_screen_with_no_name_is_refused()
    {
        Assert.NotNull(ScreenRules.ProblemWith(ScreenOf([], name: "   ")));
    }

    [Fact]
    public void A_negative_row_or_position_is_refused()
    {
        Assert.NotNull(ScreenRules.ProblemWith(Component(ScreenComponentKinds.Value, tagId: TagId, row: -1)));
        Assert.NotNull(ScreenRules.ProblemWith(Component(ScreenComponentKinds.Value, tagId: TagId, position: -1)));
    }

    [Fact]
    public void The_first_problem_is_the_one_returned()
    {
        // A caller fixing a screen fixes one thing at a time, and a list of refusals for one mistake
        // is worse than the one that matters. The kind comes first because a component nobody can
        // draw is a bigger problem than where it sits.
        var screen = ScreenOf(
        [
            Component(ScreenComponentKinds.Value, tagId: TagId, span: 99),
            Component("gauge", tagId: TagId),
        ]);

        var problem = ScreenRules.ProblemWith(screen);

        Assert.NotNull(problem);
        Assert.Contains("99", problem, StringComparison.Ordinal);
    }

    private static Screen ScreenOf(IReadOnlyList<ScreenComponent> components, string name = "Overview") => new()
    {
        Id = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        SiteId = Guid.NewGuid(),
        Name = name,
        Components = components,
    };

    private static ScreenComponent Component(
        string kind,
        Guid? tagId = null,
        string? title = null,
        int span = ScreenComponent.GridColumns,
        int row = 0,
        int position = 0) => new()
    {
        Id = Guid.NewGuid(),
        ScreenId = Guid.NewGuid(),
        RowIndex = row,
        ColumnSpan = span,
        Position = position,
        Kind = kind,
        Title = title ?? (ScreenComponentKinds.TakesTitle(kind) ? "text" : null),
        TagId = tagId,
    };
}
