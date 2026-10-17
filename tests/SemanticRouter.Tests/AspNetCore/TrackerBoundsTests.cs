using SemanticRouter.AspNetCore;
using SemanticRouter.Tests.Support;

namespace SemanticRouter.Tests.AspNetCore;

/// <summary>
/// The conversation and retry trackers stay bounded and cheap under traffic of many unrelated requests (#229): they
/// used to walk every entry on every request once they held more than a few thousand.
/// </summary>
public sealed class TrackerBoundsTests
{
    private const int Keys = 100_000;

    [Fact]
    public void A_hundred_thousand_conversations_stay_within_the_cap_at_a_constant_cost_per_turn()
    {
        var time = new ManualTimeProvider();
        var tracker = new ConversationTracker(time);
        var options = new ConversationRoutingOptions();

        for (int i = 0; i < Keys; i++)
        {
            time.Advance(TimeSpan.FromMilliseconds(1));
            tracker.Record("conversation " + i, 2, raisedByFloor: false, followUp: false, "local", options);
        }

        tracker.Count.Should().BeLessThanOrEqualTo(ConversationTracker.MaxEntries + 64, "none is idle yet, so the least recently active go");
        tracker.Pruning.Visited.Should().BeLessThan(10L * Keys, "pruning looks at a few entries per turn, not at all of them");
        tracker.Lookup("conversation " + (Keys - 1), "next", options).Floor.Should().Be(2, "the newest conversations are kept");
        tracker.Lookup("conversation 0", "next", options).Floor.Should().BeNull("the oldest went first");
    }

    [Fact]
    public void Idle_conversations_go_once_the_tracker_is_large()
    {
        var time = new ManualTimeProvider();
        var tracker = new ConversationTracker(time);
        var options = new ConversationRoutingOptions();
        for (int i = 0; i < 5_000; i++)
        {
            tracker.Record("old " + i, 2, raisedByFloor: false, followUp: false, "local", options);
        }

        time.Advance(options.IdleTimeout + TimeSpan.FromMinutes(1));
        for (int i = 0; i < 128; i++)
        {
            tracker.Record("new " + i, 2, raisedByFloor: false, followUp: false, "local", options);
        }

        tracker.Count.Should().Be(128);
    }

    [Fact]
    public void A_hundred_thousand_answers_stay_within_the_cap_at_a_constant_cost_per_request()
    {
        var time = new ManualTimeProvider();
        var tracker = new RetryTracker(time);
        TimeSpan window = TimeSpan.FromMinutes(2);

        for (int i = 0; i < Keys; i++)
        {
            time.Advance(TimeSpan.FromMilliseconds(1));
            tracker.Answered("laptop", "prompt " + i, new DecisionRecord(i, 1, null, "local", Automatic: true), window);
        }

        tracker.Count.Should().BeLessThanOrEqualTo(RetryTracker.MaxEntries + 64);
        tracker.Pruning.Visited.Should().BeLessThan(10L * Keys);
        tracker.Previous("laptop", "prompt " + (Keys - 1), window).Should().NotBeNull();
        tracker.Previous("laptop", "prompt 0", window).Should().BeNull();
    }
}
