namespace ScadaDarbox.Core.Security;

/// <summary>
/// How many sign-ins an account has failed in a row, and when a lock put on it lifts (ADR-0031).
/// </summary>
/// <remarks>
/// **The window is a comparison, not a job.** A lock that depends on a scheduled task running is a lock
/// that is open whenever that task is not, so nothing here schedules anything: <see cref="IsLockedAt"/>
/// answers, and a deadline that has passed is simply not a lock any more.
///
/// **An expired lock starts the count again**, which is why <see cref="AfterFailure"/> reads the clock and
/// not only the number: the first failure after a lock has elapsed is the first of a new series, not the
/// sixth of an old one. A person locked out last week who mistypes once today has not earned a second lock.
/// </remarks>
public readonly record struct SignInLockout(int FailedSignIns, DateTimeOffset? LockedUntilUtc)
{
    /// <summary>Nobody has failed and nothing is locked — the state every account starts in.</summary>
    public static readonly SignInLockout None = new(0, null);

    /// <summary>What a successful sign-in leaves behind: nothing counted, nothing locked.</summary>
    public static SignInLockout Cleared => None;

    /// <summary>
    /// Whether this account is shut at <paramref name="now"/>. A deadline exactly at <paramref name="now"/>
    /// is over: the message a locked operator reads names the moment it reopens, and that moment has to be
    /// the first one that works.
    /// </summary>
    public bool IsLockedAt(DateTimeOffset now) => LockedUntilUtc is { } until && until > now;

    /// <summary>This failure, and the state it leaves behind.</summary>
    /// <remarks>
    /// Called only when the account is not already locked — the sign-in path refuses before it counts — so
    /// a locked account cannot have its deadline pushed further out by someone who keeps trying.
    /// </remarks>
    public SignInLockout AfterFailure(DateTimeOffset now, int lockAfter, TimeSpan lockFor)
    {
        var prior = LockedUntilUtc is { } until && until <= now ? 0 : FailedSignIns;
        var failures = prior + 1;

        return failures < lockAfter
            ? new SignInLockout(failures, null)
            : new SignInLockout(failures, now + lockFor);
    }
}
