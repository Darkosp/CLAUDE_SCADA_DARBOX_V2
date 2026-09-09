namespace ScadaDarbox.Core.Model;

/// <summary>
/// The value of a tag: a closed discriminated union over the kinds defined in
/// ADR-0003. The set is deliberately closed — a new kind is added only when a
/// concrete need appears, never speculatively, and never by widening this to a
/// free-form blob.
/// </summary>
/// <remarks>
/// The private constructor plus nested subtypes make the union closed: no type
/// outside this file can extend it.
/// </remarks>
public abstract record TagValue
{
    private TagValue() { }

    /// <summary>Discriminator, for persistence and transport where a C# type match is unavailable.</summary>
    public abstract TagValueKind Kind { get; }

    /// <summary>An analog measurement.</summary>
    public sealed record Numeric(double Value) : TagValue
    {
        public override TagValueKind Kind => TagValueKind.Numeric;
    }

    /// <summary>A digital/discrete on-off state.</summary>
    public sealed record Boolean(bool Value) : TagValue
    {
        public override TagValueKind Kind => TagValueKind.Boolean;
    }

    /// <summary>Free text — batch ID, recipe name, operator ID.</summary>
    public sealed record Text(string Value) : TagValue
    {
        public override TagValueKind Kind => TagValueKind.Text;
    }

    /// <summary>
    /// An enumerated state: an integer code with an optional human-readable label
    /// resolved from a lookup, rather than text re-encoded as a number.
    /// </summary>
    public sealed record Discrete(int Code, string? Label = null) : TagValue
    {
        public override TagValueKind Kind => TagValueKind.Discrete;
    }
}

/// <summary>Discriminator for <see cref="TagValue"/>, mirrored in the historian schema.</summary>
public enum TagValueKind
{
    Numeric = 0,
    Boolean = 1,
    Text = 2,
    Discrete = 3,
}
