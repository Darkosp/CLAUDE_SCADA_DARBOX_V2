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

    /// <summary>
    /// Which drawing to use, for a component of kind <see cref="ScreenComponentKinds.Symbol"/>
    /// (ADR-0027). Null for every other kind, and a symbol without one is refused.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="Kind"/> because they are separate questions: the kind says *this
    /// component is a picture of equipment*, and this says *which picture*. Keeping them apart is what
    /// lets a second symbol be added without a second kind, and it is also what makes "a mapping that
    /// names a state this drawing does not have" a question that can be asked at all — a mapping is only
    /// valid against a particular drawing.
    /// </remarks>
    public string? Symbol { get; set; }

    /// <summary>
    /// How to turn this component's tag into a named state, for the kinds that draw a symbol
    /// (ADR-0027). Empty for every other kind, and a symbol without it is refused.
    /// </summary>
    /// <remarks>
    /// **A list of declarative comparisons rather than an expression**, which is the decision
    /// ADR-0027 exists to make. An expression would be shorter to type and would be a language: it
    /// needs parsing, error reporting, a story about what an operator may write, and eventually a
    /// debugger. A list of comparisons needs a form with a dropdown in it and can be tested without a
    /// browser — the same trade ADR-0024 made when it closed the component set.
    ///
    /// Order is meaning: the first rule that matches wins, and a rule marked
    /// <see cref="SymbolState.Otherwise"/> is used only when nothing else matched.
    /// </remarks>
    public IReadOnlyList<SymbolState> States { get; set; } = [];

    /// <summary>The grid's width. Spans and rows are read against it.</summary>
    public const int GridColumns = 12;
}

/// <summary>
/// One rule in a symbol's state mapping: <i>draw this state when the tag's value is this</i>
/// (ADR-0027).
/// </summary>
/// <param name="State">
/// Which of the symbol's states to draw. The set a symbol can be drawn in is fixed per symbol —
/// <see cref="Symbols"/> — so a mapping that names one the drawing does not have is refused when the
/// screen is saved, by name.
/// </param>
/// <param name="When">
/// How the reading is compared: one of <see cref="SymbolComparisons"/>. Null on the fallback rule,
/// which matches anything.
/// </param>
/// <param name="Value">
/// What to compare against, as the author typed it. A string because a boolean tag, a numeric tag and
/// a text tag all have to be expressible in one column, and because what an author typed is worth
/// keeping exactly so a refusal can quote it back.
/// </param>
/// <param name="Otherwise">
/// Whether this is the fallback — used only when no other rule matched. At most one rule may say so.
/// </param>
public sealed record SymbolState(string State, string? When, string? Value, bool Otherwise = false);

/// <summary>
/// How a symbol's rule compares a reading (ADR-0027).
/// </summary>
/// <remarks>
/// Three, and the shortness is the point. `equals` covers a boolean tag and an exact number; `above`
/// and `below` cover the two directions a threshold can go. Anything richer — a range, a band, a
/// combination — is a request for an expression language, and ADR-0027 refuses it for the reason it
/// gives: a language needs a parser, a debugger and a story about what an operator may type.
/// </remarks>
public static class SymbolComparisons
{
    /// <summary>
    /// The reading is this value.
    /// </summary>
    /// <remarks>
    /// Spelled <c>Is</c> rather than <c>Equals</c> because a constant with that name hides
    /// <see cref="object.Equals(object)"/> and the compiler says so — and the wire value below is
    /// `equals` in any case, which is what a request carries and what the client compares against.
    /// </remarks>
    public const string Is = "equals";

    public const string Above = "above";
    public const string Below = "below";

    public static IReadOnlyList<string> All { get; } = [Is, Above, Below];

    public static bool IsKnown(string? when) => when is not null && All.Contains(when);
}

/// <summary>
/// The symbols this build can draw (ADR-0027), and the states each can be drawn in.
/// </summary>
/// <remarks>
/// <para>
/// A closed set fixed at compile time, for ADR-0024 §3's reason: a component nobody can render is a
/// screen that is silently missing something, and the fix is a new ADR adding a symbol rather than a
/// generic drawing surface.
/// </para>
/// <para>
/// <b>One symbol in this slice, deliberately.</b> A symbol is a drawing plus a set of states plus a
/// mapping, and the questions ADR-0027 answers — what a mapping may contain, what quality does, what
/// animates — are answered by one of them. A tank or a valve is then a drawing and a state list.
/// </para>
/// </remarks>
public static class Symbols
{
    /// <summary>A pump: turning, stopped, or unable to be read.</summary>
    public const string Pump = "pump";

    public static IReadOnlyList<string> All { get; } = [Pump];

    public static bool IsKnown(string? symbol) => symbol is not null && All.Contains(symbol);

    /// <summary>The states <paramref name="symbol"/> can be drawn in, or empty when unknown.</summary>
    /// <remarks>
    /// `unknown` is in every set because it is what a mapping with no match resolves to, and a symbol
    /// that could not draw it would have nothing to show an author who has a gap in their rules.
    /// `bad` and `stale` are in every set because quality overrides the state (ADR-0027 §4) and those
    /// are the two ways a reading stops being usable.
    /// </remarks>
    public static IReadOnlyList<string> StatesOf(string? symbol) => symbol switch
    {
        Pump => [Running, Stopped, Fault, Unknown, Bad, Stale],
        _ => [],
    };

    /// <summary>Whether <paramref name="symbol"/> can be drawn in <paramref name="state"/>.</summary>
    public static bool CanDraw(string? symbol, string? state) =>
        state is not null && StatesOf(symbol).Contains(state);

    /// <summary>Whether a state is one the symbol animates. Only a running machine turns.</summary>
    /// <remarks>
    /// Whether a state *animates* is a property of the symbol and the state rather than of the
    /// mapping, which is why it is here: an author choosing "running" is choosing the picture, and the
    /// picture is this build's to draw.
    /// </remarks>
    public static bool Animates(string? symbol, string? state) => symbol == Pump && state == Running;

    // ---- the states themselves. Named once, used by every symbol that has them. ----

    /// <summary>A machine that is turning.</summary>
    public const string Running = "running";

    /// <summary>A machine that is not.</summary>
    public const string Stopped = "stopped";

    /// <summary>A machine the plant says is in fault. A state the plant reports, not one this infers.</summary>
    public const string Fault = "fault";

    /// <summary>Nothing matched, or nothing is known. Drawn, never silent.</summary>
    public const string Unknown = "unknown";

    /// <summary>The reading's quality is Bad: there is no reading (ADR-0003).</summary>
    public const string Bad = "bad";

    /// <summary>The reading's quality is Uncertain or Stale: there is a reading and it is not current.</summary>
    public const string Stale = "stale";

    /// <summary>Every state name any symbol uses, for a refusal to offer as a choice.</summary>
    public static IReadOnlyList<string> AllStates { get; } =
        [Running, Stopped, Fault, Unknown, Bad, Stale];
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

    /// <summary>
    /// One tag drawn as a picture of equipment, in a state its mapping derives from the reading
    /// (ADR-0027). Needs a tag and a mapping.
    /// </summary>
    /// <remarks>
    /// The kind that stops a screen being a table. Everything about it that could have been a rule —
    /// what a mapping may contain, what quality does to it, what moves — is decided in ADR-0027 rather
    /// than here.
    /// </remarks>
    public const string Symbol = "symbol";

    /// <summary>Every kind this build draws.</summary>
    public static IReadOnlyList<string> All { get; } = [Label, Value, Trend, Alarms, Status, Symbol];

    /// <summary>Whether this build draws <paramref name="kind"/>.</summary>
    public static bool IsKnown(string? kind) => kind is not null && All.Contains(kind);

    /// <summary>Whether a component of this kind reads a tag.</summary>
    public static bool NeedsTag(string kind) => kind is Value or Trend or Status or Symbol;

    /// <summary>Whether a component of this kind draws a symbol, and so needs a state mapping.</summary>
    public static bool NeedsStates(string kind) => kind is Symbol;

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
