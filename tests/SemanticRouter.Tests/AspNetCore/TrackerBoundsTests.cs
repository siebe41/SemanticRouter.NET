using System.Net;
using Microsoft.AspNetCore.Http;
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

    [Fact]
    public void A_hundred_thousand_addresses_failing_to_sign_in_stay_within_the_cap_at_a_constant_cost_each()
    {
        var time = new ManualTimeProvider();
        var throttle = new SignInThrottle(time);

        for (int i = 0; i < Keys; i++)
        {
            time.Advance(TimeSpan.FromMilliseconds(1));
            throttle.Failed(From(IPAddress.Parse($"10.{i / 65536}.{(i / 256) % 256}.{i % 256}")));
        }

        throttle.Count.Should().BeLessThanOrEqualTo(SignInThrottle.MaxAddresses + 64, "none has expired yet, so the least recently failed go");
        throttle.Pruning.Visited.Should().BeLessThan(10L * Keys, "pruning looks at a few entries per failure, not at all of them");

        // The newest address is still counted.
        HttpContext newest = From(IPAddress.Parse($"10.{(Keys - 1) / 65536}.{((Keys - 1) / 256) % 256}.{(Keys - 1) % 256}"));
        for (int i = 0; i < SignInThrottle.MaxFailures - 1; i++)
        {
            throttle.Failed(newest);
        }

        throttle.RetryAfter(newest).Should().NotBeNull();
    }

    [Fact]
    public void Sign_in_failures_from_one_ipv6_network_are_counted_together()
    {
        var throttle = new SignInThrottle(new ManualTimeProvider());
        for (int i = 1; i <= SignInThrottle.MaxFailures; i++)
        {
            throttle.Failed(From(IPAddress.Parse($"2001:db8:1:2::{i:x}")));
        }

        throttle.RetryAfter(From(IPAddress.Parse("2001:db8:1:2:ffff:ffff:ffff:ffff"))).Should().NotBeNull("a fresh address in the same /64 is the same client");
        throttle.RetryAfter(From(IPAddress.Parse("2001:db8:1:3::1"))).Should().BeNull("another /64 is another client");
        throttle.Count.Should().Be(1);
    }

    [Fact]
    public void A_hundred_thousand_clients_without_a_key_stay_within_the_rate_limiters_cap_at_a_constant_cost_each()
    {
        var time = new ManualTimeProvider();
        var limiter = new ClientRateLimiter(time);
        var limit = new RateLimitOptions { RequestsPerMinute = 10 };

        for (int i = 0; i < Keys; i++)
        {
            time.Advance(TimeSpan.FromMilliseconds(1));
            limiter.TryAdmit($"10.{i / 65536}.{(i / 256) % 256}.{i % 256}", limit, 1).Admitted.Should().BeTrue();
        }

        limiter.Count.Should().BeLessThanOrEqualTo(ClientRateLimiter.MaxClients + 64);
        limiter.Pruning.Visited.Should().BeLessThan(10L * Keys);
    }

    [Theory]
    [InlineData("203.0.113.7", "203.0.113.7")]
    [InlineData("::ffff:203.0.113.7", "203.0.113.7")]
    [InlineData("2001:db8:1:2:3:4:5:6", "2001:db8:1:2::/64")]
    [InlineData("2001:db8:1:2::", "2001:db8:1:2::/64")]
    [InlineData("::1", "::1")]
    [InlineData(null, "local")]
    public void Clients_are_counted_by_ipv4_address_and_ipv6_network(string? address, string key) =>
        ClientAddressKey.KeyOf(address is null ? null : IPAddress.Parse(address)).Should().Be(key);

    private static DefaultHttpContext From(IPAddress address)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = address;
        return context;
    }
}
