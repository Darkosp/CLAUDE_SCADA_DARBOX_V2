using ScadaDarbox.Gateway.Tests.Hosting;

namespace ScadaDarbox.Gateway.Tests;

/// <summary>
/// The difference between a tool that is not there and one that did not answer.
/// </summary>
/// <remarks>
/// These tests exist because that difference was measured to be worth one: a whole-solution run
/// reported nine broker tests as skipped while Docker was answering, and the skip said "Docker is
/// not available". Each test here needs a process started with its output redirected, so on a
/// machine where that is refused they report as skipped, with that reason, rather than failing.
/// The guard asks <c>dotnet</c>, which is present wherever these tests can run at all, rather than
/// a shell that only one platform has — a guard that is absent on Linux would skip every test
/// below and report the machine as unable to start a process, which it can.
/// </remarks>
public sealed class ProbeTests
{
    /// <summary>A command that will not finish on its own within any bound these tests use.</summary>
    private static (string File, string Arguments) Slow() =>
        OperatingSystem.IsWindows()
            ? ("cmd", "/c ping -n 30 127.0.0.1 > NUL")
            : ("sleep", "30");

    [RequiresProcessSpawningFact]
    public void A_tool_that_is_not_installed_is_absent()
    {
        var outcome = ToolProbe.Run(
            "scada-darbox-no-such-tool", string.Empty, TimeSpan.FromSeconds(5));

        Assert.Equal(ProbeOutcome.Absent, outcome);
    }

    [RequiresProcessSpawningFact]
    public void A_tool_that_answers_is_available()
    {
        var outcome = ToolProbe.Run("dotnet", "--version", TimeSpan.FromSeconds(20));

        Assert.Equal(ProbeOutcome.Available, outcome);
    }

    [RequiresProcessSpawningFact]
    public void A_tool_that_does_not_answer_in_time_is_a_timeout_and_not_an_absence()
    {
        var (file, arguments) = Slow();

        var outcome = ToolProbe.Run(file, arguments, TimeSpan.FromSeconds(2));

        Assert.Equal(ProbeOutcome.TimedOut, outcome);
        Assert.NotEqual(ProbeOutcome.Absent, outcome);
    }

    [RequiresProcessSpawningFact]
    public void A_timeout_is_asked_again_rather_than_remembered()
    {
        // The lie this type exists to prevent: one slow answer becoming a verdict for the run.
        // The first ask outlives its bound; the second answers at once.
        var (file, arguments) = Slow();
        var asks = 0;
        var availability = new ToolAvailability("A tool that answers on the second ask", TimeSpan.FromSeconds(2), () =>
        {
            asks++;
            return asks == 1
                ? ToolProbe.Run(file, arguments, TimeSpan.FromSeconds(2))
                : ProbeOutcome.Available;
        });

        Assert.Equal(ProbeOutcome.TimedOut, availability.Outcome);
        Assert.True(availability.IsAvailable);
        Assert.Equal(2, asks);
    }

    [RequiresProcessSpawningFact]
    public void A_settled_answer_is_asked_once()
    {
        var asks = 0;
        var availability = new ToolAvailability("A tool that is there", TimeSpan.FromSeconds(5), () =>
        {
            asks++;
            return ProbeOutcome.Available;
        });

        Assert.True(availability.IsAvailable);
        Assert.True(availability.IsAvailable);
        Assert.Equal(1, asks);
    }

    [Fact]
    public void A_skip_names_which_of_the_four_happened()
    {
        var timedOut = ToolProbe.Describe(ProbeOutcome.TimedOut, "Docker", TimeSpan.FromSeconds(60));
        var absent = ToolProbe.Describe(ProbeOutcome.Absent, "Docker", TimeSpan.FromSeconds(60));

        Assert.Contains("did not answer within 60 seconds", timedOut, StringComparison.Ordinal);
        Assert.Contains("not the same as it being absent", timedOut, StringComparison.Ordinal);
        Assert.Contains("is not installed", absent, StringComparison.Ordinal);
        Assert.NotEqual(timedOut, absent);
    }
}
