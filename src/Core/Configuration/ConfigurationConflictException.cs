namespace ScadaDarbox.Core.Configuration;

/// <summary>
/// A configuration change was rejected because it would leave the hierarchy invalid —
/// for example moving a folder underneath one of its own descendants.
/// </summary>
/// <remarks>
/// Distinct from a storage failure: the request was understood and refused, so callers
/// can map it to a client error rather than a server fault.
/// </remarks>
public sealed class ConfigurationConflictException : Exception
{
    public ConfigurationConflictException(string message) : base(message)
    {
    }
}
