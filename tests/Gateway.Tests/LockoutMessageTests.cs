using ScadaDarbox.Gateway.Security;

namespace ScadaDarbox.Gateway.Tests;

/// <summary>
/// What a locked-out operator is told, and in what form (ADR-0031 §4).
/// </summary>
/// <remarks>
/// <para>
/// **Seen on the sign-in screen on 2026-10-08 and changed.** The message read:
/// <i>"This account is locked until 2026-10-08T06:31:12.8382210+00:00 (UTC)"</i> — an ISO instant with
/// seven decimal places of a second, in UTC, on a screen whose reader ADR-0031 §4 names as *"the person
/// at three o'clock in the morning"*.
/// </para>
/// <para>
/// The code's own reasoning for the instant was that *"the server does not know what the reader's clock
/// says"*, which is **true and led to the wrong answer**: a duration needs no clock at all. The ADR says
/// the message "names the moment it reopens" — it did, and nobody asked what the moment would look like.
/// A test that asserts the moment is present passes either way, which is why these assert the shape.
/// </para>
/// </remarks>
public sealed class LockoutMessageTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 8, 20, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(11, "for another 11 minutes")]
    [InlineData(15, "for another 15 minutes")]
    public void A_lock_with_minutes_left_says_how_many(int minutes, string expected)
    {
        Assert.Equal(expected, AuthEndpoints.LockedFor(Now.AddMinutes(minutes), Now));
    }

    [Fact]
    public void A_part_minute_rounds_up_rather_than_down()
    {
        // **Down would be a false statement.** A reader told "another 10 minutes" who finds it still
        // shut at ten has been misled; told eleven, they wait once and it works. The product may be
        // pessimistic about its own lock; it may not be optimistic.
        Assert.Equal("for another 11 minutes", AuthEndpoints.LockedFor(Now.AddSeconds(10 * 60 + 50), Now));

        // Sixty-one seconds is two minutes under this rule, and that is not a rounding quirk -- it is
        // the rule holding where it is least comfortable. "Another minute" would be optimistic by a
        // second, and optimistic is the direction that leaves a reader standing at a locked door.
        // (My own first expectation here was "another minute", and the test was wrong, not the code.)
        Assert.Equal("for another 2 minutes", AuthEndpoints.LockedFor(Now.AddSeconds(61), Now));
        Assert.Equal("for another minute", AuthEndpoints.LockedFor(Now.AddSeconds(60), Now));
    }

    [Fact]
    public void One_minute_is_singular_because_a_message_a_person_reads_is_a_sentence()
    {
        Assert.Equal("for another minute", AuthEndpoints.LockedFor(Now.AddMinutes(1), Now));
        Assert.DoesNotContain("1 minutes", AuthEndpoints.LockedFor(Now.AddMinutes(1), Now), StringComparison.Ordinal);
    }

    [Fact]
    public void Under_a_minute_says_so_in_words_rather_than_counting_seconds()
    {
        // A number that small invites refreshing rather than waiting, and the seconds are noise.
        Assert.Equal("for less than a minute", AuthEndpoints.LockedFor(Now.AddSeconds(40), Now));
        Assert.Equal("for less than a minute", AuthEndpoints.LockedFor(Now.AddSeconds(1), Now));
    }

    [Fact]
    public void A_lock_that_has_already_elapsed_reopens_now_rather_than_counting_backwards()
    {
        // The row can be a tick stale, and "locked for -1 minutes" is the kind of sentence that makes a
        // reader distrust everything else on the screen.
        Assert.Equal("and reopens now", AuthEndpoints.LockedFor(Now.AddMinutes(-3), Now));
        Assert.Equal("and reopens now", AuthEndpoints.LockedFor(Now, Now));
    }

    [Fact]
    public void Nothing_in_the_message_is_a_machine_timestamp()
    {
        // The defect itself, pinned as a shape rather than as a string: no ISO separator, no sub-second
        // fraction, no zone designator. If any of those come back, so has the thing a person could not
        // read.
        foreach (var minutes in new[] { 1, 5, 11, 15 })
        {
            var message = AuthEndpoints.LockedFor(Now.AddMinutes(minutes), Now);

            Assert.DoesNotContain("T", message, StringComparison.Ordinal);
            Assert.DoesNotContain("UTC", message, StringComparison.Ordinal);
            Assert.DoesNotContain(".", message, StringComparison.Ordinal);
            Assert.DoesNotContain("+00:00", message, StringComparison.Ordinal);
        }
    }
}
