using System.Net;
using SemanticRouter.AspNetCore;
using SemanticRouter.Desktop;
using SemanticRouter.Host;
using SemanticRouter.Tests.Support;

namespace SemanticRouter.Tests;

/// <summary>
/// Input the router does not control (a server's reply, a file someone edited, a command line, a screen) that used to
/// throw, hang or break something unrelated, instead of being refused or skipped.
/// </summary>
public sealed class MalformedInputTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Forgetting_a_conversations_last_cache_does_not_break_later_pruning()
    {
        // #278: the conversation stayed with no caches, and the sweep's Max over them threw for every later caller.
        var caches = new WarmCacheStore();
        DateTimeOffset warm = DateTimeOffset.UtcNow.AddMinutes(5);
        caches.Remember("a", new WarmCache { Model = "m", CachedTokens = 1, ExpiresAt = warm });
        caches.Forget("a", "m");

        Action fill = () =>
        {
            for (int i = 0; i < 5000; i++)
            {
                caches.Remember($"c{i}", new WarmCache { Model = "m", CachedTokens = 1, ExpiresAt = warm });
            }
        };

        fill.Should().NotThrow();
    }

    [Fact]
    public void A_price_list_id_without_a_vendor_is_matched_without_throwing()
    {
        // #280: ranking cut the vendor off with IndexOf('/'), which is -1 here.
        ModelPriceList prices = ModelPriceList.Parse(
            """{"data":[{"id":"gpt-x","pricing":{"prompt":"0.000001","completion":"0.000002"}}]}""", ModelPriceList.DefaultSource, Now);

        prices.Find(new ConnectorOptions { Type = ConnectorTypes.Cli }, "gpt-x")!.Price.Id.Should().Be("gpt-x");
    }

    [Fact]
    public void A_connector_name_with_a_colon_gets_a_validation_error_not_an_exception()
    {
        // #281: the connector's name was read back out of "Connectors:a:b:ApiKey" by splitting on ':'.
        var candidate = new RouterConfiguration();
        candidate.Connectors["a:b"] = new ConnectorOptions { BaseAddress = new Uri("https://api.example.com"), ApiKey = "${env:HOME}" };

        candidate.ReferenceErrors(new RouterConfiguration())
            .Should().ContainSingle().Which.Should().StartWith("Connectors:a:b:ApiKey: only this machine can name an environment variable");
    }

    [Fact]
    public void A_theme_file_with_an_unreadable_colour_does_not_hide_every_theme()
    {
        // #291: "1..2" matched the rgb() pattern and then failed double.Parse, so api/themes failed for every theme.
        string directory = Directory.CreateTempSubdirectory("sr-themes-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(directory, "x.css"), """:root[data-theme="x"] { --text: rgb(1..2, 0, 0); --bg: rgb(1.2.3, 0, 0); }""");

            IReadOnlyList<DashboardTheme> themes = new ThemeCatalog(directory).List();

            themes.Should().Contain(t => t.Id == "x").And.Contain(t => t.BuiltIn);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("""{"version":1,"turns":null}""", null)]
    [InlineData("""{"turns":[null]}""", "an empty entry; skipped")]
    public void A_client_turns_file_with_null_entries_falls_back_to_the_built_in_patterns(string json, string? warning)
    {
        // #292: a null list or entry threw NullReferenceException, which Load did not catch, on every request.
        string file = Path.Combine(Directory.CreateTempSubdirectory("sr-turns-").FullName, ClientTurnTable.FileName);
        try
        {
            File.WriteAllText(file, json);

            ClientTurnTable table = ClientTurnTable.Load(file);

            table.Patterns.Should().BeEquivalentTo(ClientTurnTable.BuiltIn.Patterns);
            if (warning is null)
            {
                table.Warning.Should().BeNull();
            }
            else
            {
                table.Warning.Should().Contain(warning);
            }
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(file)!, recursive: true);
        }
    }

    [Theory]
    [InlineData("--config-dir=")]
    [InlineData("--config-dir", "")]
    [InlineData("--config-dir", "  ")]
    public void An_empty_config_dir_is_a_usage_error(params string[] args)
    {
        // #296: Path.GetFullPath("") threw instead of the usage error.
        HostSettings.Parse(args, _ => null).Error.Should().Be("--config-dir needs a directory.");
    }

    [Theory]
    [InlineData("1e300")]
    [InlineData("1e400")]
    [InlineData("999999999999")]
    public void A_retry_after_too_far_away_to_mean_anything_is_ignored(string value)
    {
        // #297: now.AddSeconds threw for a value from the remote server.
        QuotaResetParser.TryParseRetryAfter(value, Now, out _).Should().BeFalse();
        QuotaResetParser.TryParseRetryAfter("120", Now, out DateTimeOffset soon).Should().BeTrue();
        soon.Should().Be(Now.AddMinutes(2));
    }

    [Theory]
    [InlineData("Rate limited: try again in 99999999999 days")]
    [InlineData("Rate limited: try again in 999999999999999 hours")]
    [InlineData("Usage limit reached, resets Oct 0, 3pm")]
    public void A_quota_message_with_an_impossible_time_is_not_read_as_one(string message)
    {
        // #298: a huge duration overflowed TimeSpan. #299: day 0 passed the day check and new DateTime threw.
        QuotaResetParser.TryParse(message, Now, TimeZoneInfo.Utc, out _).Should().BeFalse();
        QuotaResetParser.TryParse("Rate limited: try again in 2 hours", Now, TimeZoneInfo.Utc, out DateTimeOffset reset).Should().BeTrue();
        reset.Should().Be(Now.AddHours(2));
    }

    [Fact]
    public async Task An_activity_series_needs_a_bucket_longer_than_zero()
    {
        // #300: a zero bucket never moved past the start, so Series looped forever and filled memory.
        Func<Task> series = () => Task.Run(() => ActivityReports.Series([], Now, Now.AddHours(1), TimeSpan.Zero)).WaitAsync(TimeSpan.FromSeconds(10));

        await series.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData("""{"data":[{"index":0,"embedding":[1,2]},{"index":0,"embedding":[3,4]}]}""")]
    [InlineData("""{"data":[{"index":1,"embedding":[1,2]},{"index":1,"embedding":[3,4]}]}""")]
    public async Task An_embeddings_reply_that_repeats_an_index_is_malformed(string reply)
    {
        // #306: the slot the repeated index left empty came back as an empty vector.
        var provider = new OpenAICompatibleEmbeddingProvider(
            new HttpClient(StubHttpMessageHandler.Json(reply, HttpStatusCode.OK)),
            new EmbeddingProviderOptions { Provider = EmbeddingProviderKinds.OpenAICompatible, Endpoint = new Uri("http://ollama:11434"), Model = "m" });

        Func<Task> embed = async () => await provider.GenerateEmbeddingsAsync(["a", "b"]);

        await embed.Should().ThrowAsync<InvalidOperationException>().WithMessage("*malformed*");
    }

    [Fact]
    public void A_window_wider_than_a_scaled_screen_is_fitted_not_thrown()
    {
        // #309: rounding the scaled width up put the clamp's upper bound below its lower one.
        WindowPlacement fitted = WindowPlacement.Fit(new WindowPlacement(100, 100, 2000, 2000), new ScreenArea(0, 0, 510, 510, 1.1));

        fitted.X.Should().Be(0);
        fitted.Y.Should().Be(0);
        fitted.Width.Should().BeApproximately(463.64, 0.01);
    }

    [Fact]
    public void A_negative_activity_limit_returns_nothing()
    {
        // #313: a negative limit became a negative list capacity, so GET api/activity?limit=-1 answered 500.
        new RoutingActivityLog().Recent(-1).Should().BeEmpty();
    }
}
