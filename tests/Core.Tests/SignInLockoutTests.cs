using ScadaDarbox.Core.Security;
using Xunit;

namespace ScadaDarbox.Core.Tests;

/// <summary>
/// The sign-in lockout state machine (ADR-0031).
/// </summary>
/// <remarks>
/// The rule is three lines and every one of them is a decision a later reader would happily "simplify":
/// that a deadline exactly at the named moment is over, that an expired lock starts the count again, and
/// that the threshold is reached *on* the fifth failure rather than after it. Each has its own test.
/// </remarks>
public sealed class SignInLockoutTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 23, 0, 0, TimeSpan.Zero);
    private const int LockAfter = 5;
    private static readonly TimeSpan LockFor = TimeSpan.FromMinutes(15);

    [Fact]
    public void An_account_that_has_failed_less_than_the_threshold_is_not_locked()
    {
        var state = SignInLockout.None;

        for (var attempt = 1; attempt < LockAfter; attempt++)
        {
            state = state.AfterFailure(Now, LockAfter, LockFor);
            Assert.False(state.IsLockedAt(Now), $"attempt {attempt} must not lock an account");
        }

        Assert.Equal(LockAfter - 1, state.FailedSignIns);
        Assert.Null(state.LockedUntilUtc);
    }

    [Fact]
    public void The_failure_that_reaches_the_threshold_locks_it_until_the_window_has_passed()
    {
        var state = SignInLockout.None;
        for (var attempt = 0; attempt < LockAfter; attempt++)
        {
            state = state.AfterFailure(Now, LockAfter, LockFor);
        }

        Assert.True(state.IsLockedAt(Now));
        Assert.Equal(Now + LockFor, state.LockedUntilUtc);

        // And the deadline itself is over: the moment the message names is the first one that works.
        Assert.False(state.IsLockedAt(Now + LockFor));
        Assert.True(state.IsLockedAt(Now + LockFor - TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void An_expired_lock_starts_the_next_series_rather_than_re_locking_at_once()
    {
        // The trap this test exists for: a count left at the threshold makes the *next* failure lock the
        // account again, so a person who was locked out last week is locked out again by one typo.
        var locked = new SignInLockout(LockAfter, Now - TimeSpan.FromMinutes(1));

        var afterOneMore = locked.AfterFailure(Now, LockAfter, LockFor);

        Assert.Equal(1, afterOneMore.FailedSignIns);
        Assert.False(afterOneMore.IsLockedAt(Now));
    }

    [Fact]
    public void A_success_clears_both_the_count_and_the_lock()
    {
        var cleared = SignInLockout.Cleared;

        Assert.Equal(0, cleared.FailedSignIns);
        Assert.Null(cleared.LockedUntilUtc);
        Assert.False(cleared.IsLockedAt(Now));
    }

    [Fact]
    public void Four_failures_then_a_success_then_four_more_does_not_lock()
    {
        // Decision 3, as a person experiences it: mistyping repeatedly around a successful sign-in is not
        // the same as five failures in a row.
        var state = SignInLockout.None;

        for (var attempt = 0; attempt < 4; attempt++)
        {
            state = state.AfterFailure(Now, LockAfter, LockFor);
        }

        state = SignInLockout.Cleared;

        for (var attempt = 0; attempt < 4; attempt++)
        {
            state = state.AfterFailure(Now, LockAfter, LockFor);
        }

        Assert.False(state.IsLockedAt(Now));
        Assert.Equal(4, state.FailedSignIns);
    }
}
