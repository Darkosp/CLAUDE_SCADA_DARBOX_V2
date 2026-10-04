using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ScadaDarbox.Core.Model;
using ScadaDarbox.Gateway.Provisioning;
using ScadaDarbox.Modules.Drivers.Mqtt;

namespace ScadaDarbox.Gateway.Tests;

/// <summary>
/// The cloud's half of the write conversation (ADR-0023): a request goes out with an id, and the
/// call waiting for it is completed by the result that names that id — and by nothing else.
/// </summary>
/// <remarks>
/// No broker here on purpose. What this type does is match an answer to a question, and a test of
/// that needs a way to hand it a message, not a network. The message really travelling is covered
/// where the wire format is (Drivers.Mqtt.Tests) and where the API answers (EdgeOwnedDeviceWriteTests).
/// </remarks>
public sealed class EdgeWriteRouterTests
{
    private static readonly Guid TagId = new("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb");

    [Fact]
    public async Task A_result_that_names_the_write_completes_the_call()
    {
        var plant = new Plant();

        // The edge answers the moment it is asked, which is what a healthy edge does.
        plant.AnswerWith(written: true);

        var outcome = await plant.Router.WriteAsync("plant-b", TagId, new TagValue.Numeric(4.5), CancellationToken.None);

        Assert.True(outcome.Confirmed);
        Assert.True(outcome.Written);

        // And the request that went out named the id the answer did — that is the whole mechanism.
        var sent = WritePayload.ReadRequest(Assert.Single(plant.Sent));
        Assert.Null(sent.Refusal);
        Assert.Equal(TagId, sent.TagId);
        Assert.Equal(new TagValue.Numeric(4.5), sent.Value);
    }

    [Fact]
    public async Task A_failure_comes_back_with_the_edges_own_reason()
    {
        var plant = new Plant();
        plant.AnswerWith(written: false, reason: "the device refused it");

        var outcome = await plant.Router.WriteAsync("plant-b", TagId, new TagValue.Numeric(1), CancellationToken.None);

        Assert.True(outcome.Confirmed);
        Assert.False(outcome.Written);
        Assert.Equal("the device refused it", outcome.Reason);
    }

    [Fact]
    public async Task An_edge_that_does_not_answer_is_not_confirmed_rather_than_failed()
    {
        // ADR-0023 §4, and the distinction the whole path turns on: the write was asked for and
        // nobody said what happened, which is neither success nor a failure with a reason.
        var plant = new Plant();

        var outcome = await plant.Router.WriteAsync("plant-b", TagId, new TagValue.Numeric(1), CancellationToken.None);

        Assert.False(outcome.Confirmed);
        Assert.False(outcome.Written);
        Assert.Null(outcome.Reason);

        // It was genuinely published, so "the edge did not answer" is the true thing to report and
        // not "the write never left".
        Assert.Single(plant.Sent);
    }

    [Fact]
    public async Task A_result_for_a_write_that_is_not_in_flight_is_ignored()
    {
        // Two writes to the same tag, and an answer that arrives for the first after it has been
        // answered: attributing it to the second would report a plant as set on the strength of an
        // answer to a different question.
        var plant = new Plant();
        var other = Guid.NewGuid();

        plant.Router.HandleResult(WritePayload.WriteResult(other, TagId, written: true));

        // Nothing was completed by it: the next write still has to be answered on its own terms.
        plant.AnswerWith(written: false, reason: "the real answer");
        var outcome = await plant.Router.WriteAsync("plant-b", TagId, new TagValue.Numeric(1), CancellationToken.None);

        Assert.True(outcome.Confirmed);
        Assert.False(outcome.Written);
        Assert.Equal("the real answer", outcome.Reason);
    }

    [Fact]
    public async Task A_result_that_cannot_be_read_is_ignored()
    {
        var plant = new Plant();

        plant.Router.HandleResult("not a write result at all");
        plant.Router.HandleResult("""{"version":9,"writeId":"aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"}""");

        // Still answers, so neither of those completed anything or took the router down.
        plant.AnswerWith(written: true);
        var outcome = await plant.Router.WriteAsync("plant-b", TagId, new TagValue.Numeric(1), CancellationToken.None);

        Assert.True(outcome.Confirmed);
        Assert.True(outcome.Written);
    }

    [Fact]
    public async Task A_write_that_cannot_be_published_is_not_confirmed()
    {
        // The broker refusing the publish is not the edge failing to answer, but both leave the
        // caller with the same true thing to say, and neither may be reported as written.
        var router = Router();
        router.UseSender((_, _, _) => throw new InvalidOperationException("the broker refused it"));

        var outcome = await router.WriteAsync("plant-b", TagId, new TagValue.Numeric(1), CancellationToken.None);

        Assert.False(outcome.Confirmed);
        Assert.False(outcome.Written);
    }

    [Fact]
    public async Task Two_writes_to_one_edge_are_each_answered_by_their_own_result()
    {
        // Several writes may be in flight to one edge and an edge may answer out of order, which is
        // the case the id exists for.
        var plant = new Plant();

        var first = plant.Router.WriteAsync("plant-b", TagId, new TagValue.Numeric(1), CancellationToken.None);
        var second = plant.Router.WriteAsync("plant-b", TagId, new TagValue.Numeric(2), CancellationToken.None);

        await WaitUntilAsync(() => plant.Ids.Count == 2, "both writes to be published");

        // Answered in the order they were asked, but matched by id rather than by arrival.
        plant.Router.HandleResult(WritePayload.WriteResult(plant.Ids[0], TagId, written: true));
        plant.Router.HandleResult(WritePayload.WriteResult(plant.Ids[1], TagId, written: false, reason: "the second one refused"));

        var firstOutcome = await first;
        var secondOutcome = await second;

        Assert.True(firstOutcome is { Confirmed: true, Written: true });
        Assert.True(secondOutcome is { Confirmed: true, Written: false });
        Assert.Equal("the second one refused", secondOutcome.Reason);
    }

    private static EdgeWriteRouter Router() =>
        new(
            Options.Create(new EdgeProvisioningOptions { Enabled = true, WritesEnabled = true, UsesTls = false }),
            NullLogger<EdgeWriteRouter>.Instance);

    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Timed out waiting for {what}.");
            }

            await Task.Delay(10);
        }
    }

    /// <summary>
    /// A router wired to a stand-in edge: it records what was published, and can answer a request
    /// the way an edge would.
    /// </summary>
    private sealed class Plant
    {
        private readonly List<string> _sent = [];

        internal EdgeWriteRouter Router { get; } = Router();

        /// <summary>The ids of the requests that went out, in order.</summary>
        internal IReadOnlyList<Guid> Ids { get; } = new IdList();

        internal IReadOnlyList<string> Sent => _sent;

        internal Plant()
        {
            Router.UseSender((_, payload, _) =>
            {
                var request = WritePayload.ReadRequest(payload);

                lock (_sent)
                {
                    _sent.Add(payload);
                    ((IdList)Ids).Add(request.WriteId);
                }

                return Task.CompletedTask;
            });
        }

        /// <summary>Answers the next request as it arrives, the way an edge does.</summary>
        internal void AnswerWith(bool written, string? reason = null)
        {
            Router.UseSender((_, payload, _) =>
            {
                var request = WritePayload.ReadRequest(payload);
                lock (_sent)
                {
                    _sent.Add(payload);
                    ((IdList)Ids).Add(request.WriteId);
                }

                Router.HandleResult(WritePayload.WriteResult(request.WriteId, request.TagId, written, reason));
                return Task.CompletedTask;
            });
        }

        private sealed class IdList : List<Guid>, IReadOnlyList<Guid>;
    }
}
