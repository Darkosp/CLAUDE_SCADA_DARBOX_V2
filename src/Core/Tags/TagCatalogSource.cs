namespace ScadaDarbox.Core.Tags;

/// <summary>
/// Holds the catalogue currently in force and publishes replacements.
/// </summary>
/// <remarks>
/// A <see cref="TagCatalog"/> is immutable, which is what makes it safe to read from
/// the scan loops and the API at the same time. Configuration changes therefore build a
/// whole new catalogue and swap it in here, rather than mutating one in place — readers
/// always see a complete, self-consistent hierarchy, never a half-applied edit.
/// </remarks>
public sealed class TagCatalogSource
{
    private TagCatalog _current;

    public TagCatalogSource(TagCatalog initial) => _current = initial;

    /// <summary>Raised after a new catalogue has been installed.</summary>
    public event EventHandler<TagCatalog>? Changed;

    /// <summary>
    /// The catalogue in force. Callers doing several lookups should read this once and
    /// work from that instance, so their view cannot change mid-operation.
    /// </summary>
    public TagCatalog Current => Volatile.Read(ref _current);

    /// <summary>Installs a rebuilt catalogue and notifies subscribers.</summary>
    public void Set(TagCatalog catalog)
    {
        Volatile.Write(ref _current, catalog);
        Changed?.Invoke(this, catalog);
    }
}
