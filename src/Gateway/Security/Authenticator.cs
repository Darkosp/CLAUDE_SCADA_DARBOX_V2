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
    private readonly LockoutPolicy _lockout;
    private readonly TimeProvider _time;

    public Authenticator(
        ISecurityStore store,
        SessionManager sessions,
        UserDirectorySource users,
        LockoutPolicy lockout,
        TimeProvider time)
    {
        _store = store;
        _sessions = sessions;
        _users = users;
        _lockout = lockout;
        _time = time;
    }

    /// <summary>
    /// Checks a name and a password and issues a session, counting the failures and shutting the account
    /// when there have been enough of them (ADR-0031).
    /// </summary>
    public async Task<LoginAttempt> LoginAsync(string username, string password, CancellationToken cancellationToken)
    {
        var credential = await _store.FindCredentialAsync(username.Trim(), cancellationToken).ConfigureAwait(false);
        var now = _time.GetUtcNow();

        // A shut account is refused before the password is looked at. Nothing secret is given away by doing
        // the cheap thing first: the answer already says the account is shut, deliberately (ADR-0031 §4).
        if (credential is not null && credential.Lockout.IsLockedAt(now))
        {
            return LoginAttempt.Shut(credential.Lockout.LockedUntilUtc!.Value);
        }

        // Verified even when the name matches nobody, against a hash of something nobody
        // knows, so an unknown name takes as long to refuse as a wrong password. Otherwise
        // the response time alone would tell an attacker which names exist.
        var outcome = Hasher.VerifyHashedPassword(Owner, credential?.PasswordHash ?? DecoyHash.Value, password);

        if (credential is null
            || outcome == PasswordVerificationResult.Failed
            || _users.Current.Find(credential.UserId) is not { } access)
        {
            // A wrong password against a name that exists is counted; a name that matches nobody has no row
            // to count against, which is the same trade the decoy hash makes one line above.
            var afterFailure = credential is null
                ? SignInLockout.None
                : credential.Lockout.AfterFailure(now, _lockout.Attempts, _lockout.Window);

            if (credential is not null)
            {
                await _store.RecordSignInFailureAsync(credential.UserId, afterFailure, cancellationToken).ConfigureAwait(false);
            }

            return afterFailure.IsLockedAt(now)
                ? LoginAttempt.BecameShut(afterFailure.LockedUntilUtc!.Value)
                : LoginAttempt.Wrong;
        }

        if (outcome == PasswordVerificationResult.SuccessRehashNeeded)
        {
            // The store clears the lock and the count with the new hash (ADR-0031 §5), and doing it here as
            // well would be a second write on a path that already has one.
            await _store.SetPasswordHashAsync(credential.UserId, Hash(password), cancellationToken).ConfigureAwait(false);
        }
        else if (credential.Lockout != SignInLockout.None)
        {
            await _store.ClearSignInFailuresAsync(credential.UserId, cancellationToken).ConfigureAwait(false);
        }

        var session = await _sessions.IssueAsync(credential.UserId, cancellationToken).ConfigureAwait(false);
        return LoginAttempt.Succeeded(new LoginOutcome(session, access));
    }

    public static string Hash(string password) => Hasher.HashPassword(Owner, password);

    internal sealed class PasswordOwner
    {
    }
}

internal sealed record LoginOutcome(IssuedSession Session, UserAccess Access);

/// <summary>
/// How one sign-in ended (ADR-0031): it worked, the name or password was wrong, or the account is shut and
/// until when. <paramref name="JustShut"/> is the transition only — the failure that reached the threshold
/// — so that the journal gets one row when an account locks rather than one per attempt afterwards.
/// </summary>
internal sealed record LoginAttempt(LoginOutcome? Outcome, DateTimeOffset? ShutUntilUtc, bool JustLocked)
{
    internal static readonly LoginAttempt Wrong = new(null, null, false);

    internal static LoginAttempt Succeeded(LoginOutcome outcome) => new(outcome, null, false);

    internal static LoginAttempt Shut(DateTimeOffset untilUtc) => new(null, untilUtc, false);

    internal static LoginAttempt BecameShut(DateTimeOffset untilUtc) => new(null, untilUtc, true);
}

/// <summary>
/// How many failed sign-ins shut an account, and for how long (ADR-0031 §6).
/// </summary>
/// <remarks>
/// Read from configuration and **refused rather than clamped** when it cannot mean what it says, the pattern
/// §2.0f set for the driver's response timeout: an operator who set thirty minutes and was given three
/// believes a control is there that is not.
/// </remarks>
internal sealed record LockoutPolicy(int Attempts, TimeSpan Window)
{
    internal const string Section = "Security:Lockout";
    internal const int DefaultAttempts = 5;
    internal const int DefaultMinutes = 15;
    internal const int MinimumAttempts = 2;
    internal const int MaximumAttempts = 100;
    internal const int MinimumMinutes = 1;
    internal const int MaximumMinutes = 24 * 60;

    internal static LockoutPolicy From(IConfiguration configuration) => new(
        configuration.GetValue($"{Section}:Attempts", DefaultAttempts),
        TimeSpan.FromMinutes(configuration.GetValue($"{Section}:Minutes", DefaultMinutes)));

    /// <summary>Why this policy cannot be used, or null when it can.</summary>
    internal string? Problem()
    {
        if (Attempts is < MinimumAttempts or > MaximumAttempts)
        {
            return $"{Section}:Attempts is {Attempts}; it has to be between {MinimumAttempts} and {MaximumAttempts}.";
        }

        var minutes = (int)Window.TotalMinutes;

        return minutes is < MinimumMinutes or > MaximumMinutes
            ? $"{Section}:Minutes is {minutes}; it has to be between {MinimumMinutes} and {MaximumMinutes}."
            : null;
    }
}

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
