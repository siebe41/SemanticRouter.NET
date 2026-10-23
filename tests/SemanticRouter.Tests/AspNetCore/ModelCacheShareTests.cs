using SemanticRouter.AspNetCore;
using SemanticRouter.Tests.Support;
using static SemanticRouter.Tests.Support.TestEmbeddings;

namespace SemanticRouter.Tests.AspNetCore;

/// <summary>
/// Cache-aware cost (#96): each model's prompt-cache share comes from its recent served turns, per route when the route
/// has enough of them and over all its turns otherwise.
/// </summary>
public sealed class ModelCacheShareTests
{
    private static RoutingActivityEntry Served(
        string model, string? route, int input, int read, int write, DateTimeOffset at, string outcome = "served") =>
        new()
        {
            Timestamp = at,
            Tier = 3,
            Route = route,
            ServedBy = model,
            Outcome = outcome,
            InputTokens = input,
            CacheReadTokens = read,
            CacheCreationTokens = write,
            OutputTokens = 10,
        };

    [Fact]
    public void The_tracker_learns_each_models_cache_share_per_route_and_over_all_its_turns()
    {
        var clock = new ManualTimeProvider();
        DateTimeOffset now = clock.GetUtcNow();
        var activity = new RoutingActivityLog();
        for (int i = 0; i < 5; i++)
        {
            activity.Add(Served("cached", "agentic", input: 100, read: 800, write: 100, now.AddMinutes(-5)));
            activity.Add(Served("cached", "stale", input: 0, read: 1000, write: 0, now.AddDays(-2)));
            activity.Add(Served("cached", "failing", input: 0, read: 1000, write: 0, now.AddMinutes(-1), outcome: "failed"));
        }

        for (int i = 0; i < 3; i++)
        {
            activity.Add(Served("cached", "chat", input: 1000, read: 0, write: 0, now.AddMinutes(-5)));
            activity.Add(new RoutingActivityEntry { Timestamp = now.AddMinutes(-5), Tier = 3, Route = "chat", ServedBy = "cached", Outcome = "served" });
            activity.Add(Served("sparse", "chat", input: 0, read: 1000, write: 0, now.AddMinutes(-5)));
        }

        var tracker = new ModelCacheTracker(activity, clock);

        tracker.Typical(Model("CACHED", 3), "agentic").Should().Be(new PromptCacheShare { Read = 0.8, Write = 0.1 });
        PromptCacheShare overall = new PromptCacheShare { Read = 4000 / 8000.0, Write = 500 / 8000.0 };
        tracker.Typical(Model("cached", 3), "chat").Should().Be(overall, "chat has 3 turns with usage, fewer than 5: the model's share over all its turns");
        tracker.Typical(Model("cached", 3), null).Should().Be(overall);
        tracker.Typical(Model("sparse", 3), "chat").Should().BeNull("3 turns are too few to tell");
        tracker.Typical().Keys.Should().BeEquivalentTo(["cached"], "stale turns are outside the day, failed ones were not served, turns without usage do not count");

        tracker.Configure(new ModelSelectionOptions { CacheShareSamples = 3, CacheShareWindow = TimeSpan.FromDays(3) });

        tracker.Typical(Model("cached", 3), "chat").Should().Be(new PromptCacheShare { Read = 0, Write = 0 });
        tracker.Typical(Model("cached", 3), "stale").Should().Be(new PromptCacheShare { Read = 1, Write = 0 });
        tracker.Typical(Model("sparse", 3), "chat").Should().Be(new PromptCacheShare { Read = 1, Write = 0 });
    }
}
