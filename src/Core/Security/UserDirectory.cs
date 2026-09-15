namespace ScadaDarbox.Core.Security;

/// <summary>
/// An immutable snapshot of every active user and their roles.
/// </summary>
/// <remarks>
/// The same shape as the tag catalogue, and for the same reason: rebuilt whole after a
/// change and swapped in, so a reader never sees a half-applied role edit. The user table
/// is small enough that reading all of it costs less than reasoning about partial updates.
/// A deactivated user is simply absent, which is what makes deactivation take effect on
/// their next request.
/// </remarks>
public sealed class UserDirectory
{
    public static readonly UserDirectory Empty = new([]);

    private readonly Dictionary<Guid, UserAccess> _usersById;

    public UserDirectory(IEnumerable<UserAccess> users) =>
        _usersById = users.ToDictionary(user => user.UserId);

    public IReadOnlyCollection<UserAccess> Users => _usersById.Values;

    /// <summary>The user's current access, or null if they are not an active user.</summary>
    public UserAccess? Find(Guid userId) => _usersById.GetValueOrDefault(userId);
}

/// <summary>Holds the user directory in force and installs replacements.</summary>
public sealed class UserDirectorySource
{
    private UserDirectory _current;

    public UserDirectorySource(UserDirectory initial) => _current = initial;

    public UserDirectory Current => Volatile.Read(ref _current);

    public void Set(UserDirectory directory) => Volatile.Write(ref _current, directory);
}
