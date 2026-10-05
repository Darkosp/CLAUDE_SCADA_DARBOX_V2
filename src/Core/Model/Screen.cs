namespace ScadaDarbox.Core.Model;

/// <summary>
/// An operator screen: what a person looks at (ADR-0024).
/// </summary>
/// <remarks>
/// <para>
/// A screen is configuration and not code. It is a row, its components are rows, and the API is the
/// only way one is created — there is no document, no file, and no scripting. A deployment changes
/// what an operator sees by writing rows, which means no deploy and no build.
/// </para>
/// <para>
/// The screen belongs to one Site (ADR-0004), and is read through the same Site-scoped rules as
/// every other entity (ADR-0011): reading needs <c>CanView</c>, editing needs <c>CanOperate</c>, and
/// a screen on a Site the caller cannot see is indistinguishable from one that does not exist.
/// </para>
/// </remarks>
public sealed class Screen
{
    public required Guid Id { get; init; }

    /// <summary>
    /// Owning tenant (ADR-0004). Every Site-scoped entity carries this transitively, even though a
    /// deployment has exactly one Tenant row today.
    /// </summary>
    public required Guid TenantId { get; init; }

    /// <summary>Owning Site. Immutable: a screen cannot be moved to another Site.</summary>
    public required Guid SiteId { get; init; }

    /// <summary>Display name. Unique within its Site among live rows, ignoring case (ADR-0015).</summary>
    public required string Name { get; set; }

    /// <summary>
    /// Where this screen sits among its Site's screens, so an operator's list has a stable order.
    /// </summary>
    public int Position { get; set; }

    /// <summary>Everything on the screen, in the order the renderer should consider it.</summary>
    public IReadOnlyList<ScreenComponent> Components { get; set; } = [];
}

/// <summary>
/// One thing on a screen (ADR-0024).
/// </summary>
/// <remarks>
/// <para>
/// <b>The kind is a closed set fixed at compile time</b> (ADR-0002's no-reflection rule). An unknown
/// kind is refused when the screen is <i>saved</i>, by name, rather than stored and skipped when it
/// is drawn: a screen saved with a component nobody can render is a screen that is silently missing
/// something, and an operator has no way to know.
/// </para>
/// <para>
/// <b>Layout is a row and a span, and nothing else.</b> A component occupies one row of a
/// twelve-column grid and spans some of it. Absolute positioning is deliberately not modelled
/// (ADR-0024 §2): a canvas is a promise about pixels and window sizes that this project would then
/// have to keep.
/// </para>
/// </remarks>
public sealed class ScreenComponent
{
    public required Guid Id { get; init; }

    public required Guid ScreenId { get; init; }

    /// <summary>Which row of the grid this component sits in. Ascending.</summary>
    public int RowIndex { get; set; }

    /// <summary>
    /// Where within its row and how wide, out of <see cref="GridColumns"/>.
    /// </summary>
    public int ColumnSpan { get; set; } = GridColumns;

    /// <summary>
    /// Order within its row when several components share one. Ascending, and the only thing that
    /// decides left-to-right.
    /// </summary>
    public int Position { get; set; }

    /// <summary>One of <see cref="ScreenComponentKinds"/>.</summary>
    public required string Kind { get; set; }

    /// <summary>
    /// What the author wrote for this component, when its kind shows text. Null for kinds that show
    /// a reading instead — and refusing a title on those is deliberate: a label beside a live value
    /// that nobody can see the source of is the beginning of every screen that lies.
    /// </summary>
    public string? Title { get; set; }

    /// <summary>
    /// The tag to read, for the kinds that read one (ADR-0001: the stable id, never the path).
    /// Null for the kinds that do not, and a kind that needs one without it is refused.
    /// </summary>
    public Guid? TagId { get; set; }

    /// <summary>
    /// The device <see cref="TagId"/> is on, set beside it and never separately.
    /// </summary>
    /// <remarks>
    /// A tag does not carry its Site — its device does (migration 0001) — so a component that names
    /// a tag on another Site is only preventable if the component also names the device that tag is
    /// on. Migration 0017 pins the device to the component's Site and the tag to that device, and
    /// the pair is what makes a cross-Site binding unrepresentable rather than merely refused.
    ///
    /// It is derived from the tag rather than chosen, so it is written by whatever resolves the tag
    /// and is not something a caller supplies.
    /// </remarks>
    public Guid? DeviceId { get; set; }

    /// <summary>The grid's width. Spans and rows are read against it.</summary>
    public const int GridColumns = 12;
}

/// <summary>
/// The component kinds this build can draw — the whole of them (ADR-0024 §3).
/// </summary>
/// <remarks>
/// A deployment cannot add a sixth (ADR-0002). The first real screen will want something that is
/// not here, and the answer is a new ADR adding a kind — not a generic component that can be made
/// to show anything, and not a scripting escape hatch. A generic component would let a screen be
/// built that cannot say "this is not a current reading", which is the one thing every component
/// here is shaped to guarantee.
/// </remarks>
public static class ScreenComponentKinds
{
    /// <summary>Text an author wrote: a heading, a unit, an instruction. Needs no tag.</summary>
    public const string Label = "label";

    /// <summary>One tag's value, its quality and its source time. Needs a tag.</summary>
    public const string Value = "value";

    /// <summary>One numeric tag's recent history. Needs a tag.</summary>
    public const string Trend = "trend";

    /// <summary>The standing alarms of this screen's Site. Needs no tag.</summary>
    public const string Alarms = "alarms";

    /// <summary>One tag's quality alone, as a state read at a glance. Needs a tag.</summary>
    public const string Status = "status";

    /// <summary>Every kind this build draws.</summary>
    public static IReadOnlyList<string> All { get; } = [Label, Value, Trend, Alarms, Status];

    /// <summary>Whether this build draws <paramref name="kind"/>.</summary>
    public static bool IsKnown(string? kind) => kind is not null && All.Contains(kind);

    /// <summary>Whether a component of this kind reads a tag.</summary>
    public static bool NeedsTag(string kind) => kind is Value or Trend or Status;

    /// <summary>
    /// Whether a component of this kind shows text an author wrote. The complement of
    /// <see cref="NeedsTag"/>, and said separately because that is what it is about.
    /// </summary>
    /// <remarks>
    /// <b>This is not the same question as whether the text is required</b>, and conflating the two
    /// produced a screen the API would not save. See <see cref="NeedsTitle"/> and
    /// <see cref="WantsTitle"/>.
    /// </remarks>
    public static bool TakesTitle(string kind) => kind is Label or Alarms;

    /// <summary>
    /// Whether a component of this kind is <i>nothing but</i> its text, and so cannot be saved without
    /// it.
    /// </summary>
    /// <remarks>
    /// A `label` is the text — a heading, a unit, an instruction (ADR-0024's kinds table) — and a
    /// `label` with no text is an empty box an author cannot explain. That is the whole of this rule.
    /// </remarks>
    public static bool NeedsTitle(string kind) => kind is Label;

    /// <summary>
    /// Whether a component of this kind may carry text that is optional.
    /// </summary>
    /// <remarks>
    /// <para>
    /// First: an `alarms` component is the Site's standing alarms, and its title is a heading over
    /// them — worth having, not worth requiring. **ADR-0024's kinds table gives text as what a `label`
    /// shows and says nothing about it for `alarms`**, so requiring one put the code ahead of the ADR
    /// and made an author invent a heading to save a screen.
    /// </para>
    /// <para>
    /// Second, and this is the defect that was found by walking a screen rather than by reading one:
    /// the seeder's screen — the one every new Site is born with, a `label` naming
    /// the Site and an `alarms` component — was written with no title on the `alarms`, because the
    /// seeder writes rows directly and nothing asked it. **The result was a screen that could not be
    /// saved**: an author opened it, changed anything, and the API answered `A 'alarms' component
    /// shows text and needs some`. The seeder now checks its own screen against these same rules, so
    /// that particular shape cannot come back; this method is what makes the check pass with the
    /// screen keeping its meaning.
    /// </para>
    /// </remarks>
    public static bool WantsTitle(string kind) => kind is Label or Alarms;
}
