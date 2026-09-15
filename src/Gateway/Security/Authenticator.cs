using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using ScadaDarbox.Core.Security;

namespace ScadaDarbox.Gateway.Security;

/// <summary>
/// Checks a user name and password and issues a session (ADR-0011).
/// </summary>
internal sealed class Authenticator
{
    // PasswordHasher is used on its own, with none of the rest of ASP.NET Core Identity: it
    // needs a user type only to satisfy its signature, and never looks at the instance.
    private static readonly PasswordHasher<PasswordOwner> Hasher = new();
    private static readonly PasswordOwner Owner = new();

    private static readonly Lazy<string> DecoyHash = new(() =>
        Hasher.HashPassword(Owner, Convert.ToBase64String(RandomNumberGenerator.GetBytes(18))));

    private readonly ISecurityStore _store;
    private readonly SessionManager _sessions;
    private readonly UserDirectorySource _users;

    public Authenticator(ISecurityStore store, SessionManager sessions, UserDirectorySource users)
    {
        _store = store;
        _sessions = sessions;
        _users = users;
    }

    /// <returns>Null when the name or the password is wrong, without saying which.</returns>
    public async Task<LoginOutcome?> LoginAsync(string username, string password, CancellationToken cancellationToken)
    {
        var credential = await _store.FindCredentialAsync(username.Trim(), cancellationToken).ConfigureAwait(false);

        // Verified even when the name matches nobody, against a hash of something nobody
        // knows, so an unknown name takes as long to refuse as a wrong password. Otherwise
        // the response time alone would tell an attacker which names exist.
        var outcome = Hasher.VerifyHashedPassword(Owner, credential?.PasswordHash ?? DecoyHash.Value, password);

        if (credential is null
            || outcome == PasswordVerificationResult.Failed
            || _users.Current.Find(credential.UserId) is not { } access)
        {
            return null;
        }

        if (outcome == PasswordVerificationResult.SuccessRehashNeeded)
        {
            await _store.SetPasswordHashAsync(credential.UserId, Hash(password), cancellationToken).ConfigureAwait(false);
        }

        var session = await _sessions.IssueAsync(credential.UserId, cancellationToken).ConfigureAwait(false);
        return new LoginOutcome(session, access);
    }

    public static string Hash(string password) => Hasher.HashPassword(Owner, password);

    internal sealed class PasswordOwner
    {
    }
}

internal sealed record LoginOutcome(IssuedSession Session, UserAccess Access);

/// <summary>What a user name and a password must look like before they are stored.</summary>
internal static class Credentials
{
    public const int MinimumPasswordLength = 12;
    public const int MaximumPasswordLength = 256;
    public const int MaximumUsernameLength = 64;

    public static string? ProblemWithUsername(string? username)
    {
        var trimmed = username?.Trim() ?? string.Empty;

        if (trimmed.Length == 0)
        {
            return "A user name is required.";
        }

        if (trimmed.Length > MaximumUsernameLength)
        {
            return $"A user name can be at most {MaximumUsernameLength} characters.";
        }

        return trimmed.Any(char.IsControl) ? "A user name cannot contain control characters." : null;
    }

    public static string? ProblemWithPassword(string? password)
    {
        if (password is null || password.Length < MinimumPasswordLength)
        {
            return $"A password must be at least {MinimumPasswordLength} characters.";
        }

        return password.Length > MaximumPasswordLength
            ? $"A password can be at most {MaximumPasswordLength} characters."
            : null;
    }
}
