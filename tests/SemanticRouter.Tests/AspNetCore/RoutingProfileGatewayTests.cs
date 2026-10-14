using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using SemanticRouter.AspNetCore;
using SemanticRouter.Tests.Support;
using static SemanticRouter.Tests.Support.TestEmbeddings;

namespace SemanticRouter.Tests.AspNetCore;

/// <summary>Routing profiles at the gateway: how a request picks one, what it changes, and what it costs.</summary>
public sealed class RoutingProfileGatewayTests : RouterHostTest
{
    private const string CloudReply = """{"choices":[{"message":{"role":"assistant","content":"from cloud"},"finish_reason":"stop"}],"usage":{"prompt_tokens":100,"completion_tokens":50}}""";
    private const string LocalReply = """{"choices":[{"message":{"role":"assistant","content":"from local"},"finish_reason":"stop"}],"usage":{"prompt_tokens":10,"completion_tokens":5}}""";

    public RoutingProfileGatewayTests()
    {
        ClassifyAs(tier: 4);
        CloudHttp = StubHttpMessageHandler.Json(CloudReply);
        LocalHttp = StubHttpMessageHandler.Json(LocalReply);
    }

    private Task<HttpClient> StartProfilesAsync(Action<PromptRouterMiddlewareOptions>? middleware = null, Action<SemanticRouterOptions>? routing = null) =>
        StartAsync(
            o =>
            {
                o.Mode = PromptRouterMode.Dispatch;
                o.PathPrefixes.Add("/v1/chat/completions");
                o.VirtualModels["private"] = new VirtualModel { Profile = "private", Description = "Never leaves this machine." };
                o.VirtualModels["fast"] = new VirtualModel { Strategy = SelectionStrategy.CheapestCapable };
                middleware?.Invoke(o);
            },
            o =>
            {
                o.Profiles["private"] = new RoutingProfile { LocalOnly = true, Description = "Local models only." };
                o.Profiles["open"] = new RoutingProfile();
                o.Models.First(m => m.Name == "local").Capabilities.Add("tools");
                routing?.Invoke(o);
            });

    private static StringContent Chat(string prompt, string model = "auto", string? tools = null) => Json(
        $$"""{"model":"{{model}}","messages":[{"role":"user","content":{{JsonSerializer.Serialize(prompt)}}}]{{(tools is null ? string.Empty : ",\"tools\":" + tools)}}}""");

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, HttpContent content, params (string Name, string Value)[] headers)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions") { Content = content };
        foreach ((string name, string value) in headers)
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        return await client.SendAsync(request);
    }

    private static string Header(HttpResponseMessage response, string name) => response.Headers.GetValues(name).Single();

    [Fact]
    public async Task A_header_model_name_or_virtual_model_picks_the_profile()
    {
        HttpClient client = await StartProfilesAsync();

        HttpResponseMessage plain = await PostAsync(client, Chat("design a platform"));
        HttpResponseMessage header = await PostAsync(client, Chat("design a platform"), ("X-Router-Profile", "private"));
        HttpResponseMessage suffix = await PostAsync(client, Chat("design a platform", "auto@private"));
        HttpResponseMessage named = await PostAsync(client, Chat("design a platform", "private"));

        Header(plain, "X-Prompt-Route-Model").Should().Be("cloud");
        plain.Headers.Contains("X-Prompt-Route-Profile").Should().BeFalse();
        foreach (HttpResponseMessage response in new[] { header, suffix, named })
        {
            Header(response, "X-Prompt-Route-Model").Should().Be("local");
            Header(response, "X-Prompt-Route-Profile").Should().Be("private");
            Header(response, "X-Prompt-Route-Local-Only").Should().Be("true");
        }

        IReadOnlyList<RoutingActivityEntry> activity = Services.GetRequiredService<RoutingActivityLog>().Recent(10);
        activity.Select(e => e.Profile).Should().Equal("private", "private", "private", null);
    }

    [Fact]
    public async Task An_unknown_profile_is_refused_in_the_clients_format()
    {
        HttpClient client = await StartProfilesAsync();

        HttpResponseMessage response = await PostAsync(client, Chat("hi", "auto@nope"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("Unknown routing profile 'nope'").And.Contain("private");
        CloudHttp.Requests.Should().BeEmpty();
        LocalHttp.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_gateway_keys_profile_cannot_be_left_with_a_header()
    {
        HttpClient client = await StartProfilesAsync(o =>
        {
            o.GatewayKeys["laptop"] = "key-laptop-0123456789";
            o.ClientProfiles["laptop"] = "private";
        });

        HttpResponseMessage response = await PostAsync(client, Chat("design a platform", "auto@open"),
            ("Authorization", "Bearer key-laptop-0123456789"), ("X-Router-Profile", "open"));

        Header(response, "X-Prompt-Route-Profile").Should().Be("private");
        Header(response, "X-Prompt-Route-Model").Should().Be("local");
        CloudHttp.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Rules_pick_a_profile_from_the_tools_offered_and_the_project()
    {
        HttpClient client = await StartProfilesAsync(o =>
        {
            o.ProfileRules.Add(new ProfileRule { Profile = "private", ToolsInclude = { "Bash" } });
            o.ProfileRules.Add(new ProfileRule { Profile = "private", Projects = { "acme-*" } });
        });
        const string bash = """[{"type":"function","function":{"name":"Bash","parameters":{"type":"object"}}}]""";
        const string read = """[{"type":"function","function":{"name":"Read","parameters":{"type":"object"}}}]""";

        HttpResponseMessage withBash = await PostAsync(client, Chat("fix the build", tools: bash));
        HttpResponseMessage withRead = await PostAsync(client, Chat("fix the build", tools: read));
        HttpResponseMessage project = await PostAsync(client, Chat("fix the build"), ("X-Router-Project", "acme-payments"));

        Header(withBash, "X-Prompt-Route-Profile").Should().Be("private");
        withRead.Headers.Contains("X-Prompt-Route-Profile").Should().BeFalse();
        Header(project, "X-Prompt-Route-Profile").Should().Be("private");
        Services.GetRequiredService<RoutingActivityLog>().Recent(1)[0].Project.Should().Be("acme-payments");
    }

    [Fact]
    public async Task A_secret_in_the_conversation_keeps_it_local_when_asked()
    {
        HttpClient client = await StartProfilesAsync(o => o.LocalOnlyWhen.Secrets = true);

        HttpResponseMessage secret = await PostAsync(client, Chat("why does this fail? OPENAI_API_KEY=sk-proj-abcdefghijklmnopqrstuvwxyz123456"));
        HttpResponseMessage clean = await PostAsync(client, Chat("why does this fail? OPENAI_API_KEY is set"));

        Header(secret, "X-Prompt-Route-Model").Should().Be("local");
        Header(secret, "X-Prompt-Route-Local-Only").Should().Be("true");
        Header(clean, "X-Prompt-Route-Model").Should().Be("cloud");
        Services.GetRequiredService<RoutingActivityLog>().Recent(2).Select(e => e.Signals).Should().Equal(null, "secret");
    }

    [Fact]
    public async Task A_local_only_profile_does_not_fail_over_to_the_cloud()
    {
        LocalHttp = StubHttpMessageHandler.Json("""{"error":"down"}""", HttpStatusCode.InternalServerError);
        HttpClient client = await StartProfilesAsync();

        HttpResponseMessage response = await PostAsync(client, Chat("design a platform"), ("X-Router-Profile", "private"));

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        LocalHttp.Requests.Should().NotBeEmpty();
        CloudHttp.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_spent_profile_budget_keeps_the_profile_on_free_models()
    {
        HttpClient client = await StartProfilesAsync(routing: o => o.Profiles["work"] = new RoutingProfile());
        RouterRuntimeProvider provider = Services.GetRequiredService<RouterRuntimeProvider>();
        RouterConfiguration config = provider.Current.Configuration.Clone();
        config.Usage.Budgets.Add(new BudgetDefinition { Name = "work-daily", Profile = "work", MaxTokens = 100 });
        provider.Apply(config, persist: false);

        HttpResponseMessage first = await PostAsync(client, Chat("design a platform"), ("X-Router-Profile", "work"));
        HttpResponseMessage second = await PostAsync(client, Chat("design a platform"), ("X-Router-Profile", "work"));
        HttpResponseMessage other = await PostAsync(client, Chat("design a platform"));

        Header(first, "X-Prompt-Route-Model").Should().Be("cloud");
        Header(second, "X-Prompt-Route-Model").Should().Be("local", "150 tokens spent the 100-token budget; the free local model still serves");
        Header(second, "X-Prompt-Route-Budget").Should().Be("work-daily");
        Header(other, "X-Prompt-Route-Model").Should().Be("cloud", "the budget covers the profile only");
        IUsageLedger ledger = Services.GetRequiredService<IUsageLedger>();
        ledger.Summarize(DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddHours(1), UsageGrouping.Profile)
            .Select(g => (g.Key, g.Totals.Requests)).Should().BeEquivalentTo(new[] { ("work", 2L), ("(no profile)", 1L) });
    }

    [Fact]
    public async Task Virtual_models_and_profiles_are_listed_for_chat_uis()
    {
        HttpClient client = await StartProfilesAsync();

        using JsonDocument models = JsonDocument.Parse(await client.GetStringAsync("/v1/models"));

        string[] ids = [.. models.RootElement.GetProperty("data").EnumerateArray().Select(m => m.GetProperty("id").GetString()!)];
        ids.Should().ContainInOrder("auto", "auto:priority", "private", "fast", "auto@private", "auto@open", "local", "cloud");
        models.RootElement.GetProperty("data").EnumerateArray().Single(m => m.GetProperty("id").GetString() == "private")
            .GetProperty("description").GetString().Should().Be("Never leaves this machine.");
    }

    [Fact]
    public async Task The_tester_compares_profiles_for_one_prompt()
    {
        HttpClient client = await StartProfilesAsync(routing: o => o.Profiles["capped"] = new RoutingProfile { MaxTier = 2 });

        JsonElement open = await ReadAsync(await client.PostAsJsonAsync("/router/api/classify", new { prompt = "design a platform" }));
        JsonElement capped = await ReadAsync(await client.PostAsJsonAsync("/router/api/classify", new { prompt = "design a platform", profile = "capped" }));
        JsonElement @private = await ReadAsync(await client.PostAsJsonAsync("/router/api/classify", new { prompt = "design a platform", profile = "private" }));
        HttpResponseMessage unknown = await client.PostAsJsonAsync("/router/api/classify", new { prompt = "x", profile = "nope" });

        open.GetProperty("models")[0].GetProperty("model").GetString().Should().Be("cloud");
        open.GetProperty("requiredTier").GetInt32().Should().Be(4);
        capped.GetProperty("requiredTier").GetInt32().Should().Be(2, "the tier the profile routes with, not the classifier's");
        capped.GetProperty("profile").GetString().Should().Be("capped");
        @private.GetProperty("models").EnumerateArray().Select(m => m.GetProperty("model").GetString()).Should().Equal("local");
        unknown.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    [Fact]
    public async Task A_full_cli_queue_sends_the_request_to_another_model()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Cli = new FakeCliProcessRunner(_ =>
        {
            started.TrySetResult(); // the connector only starts a process once the request holds its one slot
            return new FakeCliProcess([ClaudeStream.Success("Hi")], until: release.Task);
        });
        HttpClient client = await StartAsync(
            o =>
            {
                o.Mode = PromptRouterMode.Dispatch;
                o.PathPrefixes.Add("/v1/chat/completions");
            },
            o => o.Models.First(m => m.Name == "cloud").Priority = 5,
            s => s.Configure<SemanticRouterConnectorsOptions>(o =>
            {
                o.Connectors["claude"].MaxConcurrency = 1;
                o.Connectors["claude"].MaxQueue = 0;
            }),
            withCli: true);

        Task<HttpResponseMessage> busy = PostAsync(client, Chat("design a platform", "auto:priority"));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(30));
        HttpResponseMessage overflow = await PostAsync(client, Chat("design a platform", "auto:priority"));
        release.SetResult();

        Header(await busy, "X-Prompt-Route-Model").Should().Be("claude-cli");
        Header(overflow, "X-Prompt-Route-Model").Should().Be("cloud");
        Services.GetRequiredService<RoutingActivityLog>().Recent(2).Select(e => e.Detail ?? string.Empty)
            .Should().Contain(d => d.Contains("MaxQueue 0", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_local_only_request_is_not_sent_to_an_embedding_endpoint_off_this_machine()
    {
        Classifier.Setup(c => c.ClassifyWithoutEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(PromptClassificationResult.Bypass(1, RoutingReason.NoMatchFallback));
        HttpClient client = await StartAsync(
            o =>
            {
                o.Mode = PromptRouterMode.Dispatch;
                o.PathPrefixes.Add("/v1/chat/completions");
            },
            o => o.Profiles["private"] = new RoutingProfile { LocalOnly = true },
            services: s => s.Configure<EmbeddingProviderOptions>(e =>
            {
                e.Provider = EmbeddingProviderKind.OpenAICompatible;
                e.Endpoint = new Uri("https://api.openai.com");
                e.Model = "text-embedding-3-small";
            }));

        HttpResponseMessage local = await client.PostAsync("/v1/chat/completions", Chat("look up account 4471", "auto@private"));
        Classifier.Verify(c => c.ClassifyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never());
        await client.PostAsync("/v1/chat/completions", Chat("hello"));

        local.StatusCode.Should().Be(HttpStatusCode.OK);
        Classifier.Verify(c => c.ClassifyWithoutEmbeddingAsync("look up account 4471", It.IsAny<CancellationToken>()), Times.Once());
        Classifier.Verify(c => c.ClassifyAsync("hello", It.IsAny<CancellationToken>()), Times.Once(), "other requests are classified as usual");
        Services.GetRequiredService<RouterRuntimeProvider>().Current.Warnings.Should().Contain(w => w.Contains("not on this machine", StringComparison.Ordinal));
    }
}

