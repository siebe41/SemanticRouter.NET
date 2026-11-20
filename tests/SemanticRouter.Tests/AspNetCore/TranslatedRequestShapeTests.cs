using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SemanticRouter.AspNetCore;

namespace SemanticRouter.Tests.AspNetCore;

/// <summary>
/// Translated requests the upstream would reject with a 400: the output limit sent to an OpenAI reasoning model, and
/// empty turns sent to the Messages API.
/// </summary>
public sealed class TranslatedRequestShapeTests
{
    private const string ClaudeCodeBody = """
        {"model":"auto","max_tokens":2048,"messages":[{"role":"user","content":"hi"}]}
        """;

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static JsonObject Parse(ReadOnlyMemory<byte> body) => JsonNode.Parse(Encoding.UTF8.GetString(body.Span))!.AsObject();

    [Fact]
    public void The_output_limit_is_max_completion_tokens_when_asked_for()
    {
        ChatCompletionRequest request = ChatCompletionRequest.ParseAnthropic(Json(ClaudeCodeBody));

        JsonObject plain = Parse(OpenAIRequestBuilder.Build(request, "qwen3"));
        JsonObject reasoning = Parse(OpenAIRequestBuilder.Build(request, "o4", reasoning: true, maxCompletionTokens: true));

        ((int)plain["max_tokens"]!).Should().Be(2048);
        plain.ContainsKey("max_completion_tokens").Should().BeFalse();
        ((int)reasoning["max_completion_tokens"]!).Should().Be(2048);
        reasoning.ContainsKey("max_tokens").Should().BeFalse("OpenAI's reasoning models reject max_tokens");
    }

    [Theory]
    [InlineData("https://api.openai.com", true, null, true)]
    [InlineData("https://api.openai.com", false, null, false)]
    [InlineData("http://localhost:11434", true, null, false)]
    [InlineData("http://192.168.1.20:8000", true, null, false)]
    [InlineData("http://localhost:11434", true, "max_completion_tokens", true)]
    [InlineData("https://api.openai.com", false, "max_completion_tokens", true)]
    [InlineData("https://api.openai.com", true, "max_tokens", false)]
    public void Max_completion_tokens_goes_to_remote_reasoning_models_unless_the_connector_says_otherwise(
        string baseAddress, bool reasoning, string? parameter, bool expected)
    {
        var options = new ConnectorOptions { BaseAddress = new Uri(baseAddress), MaxTokensParameter = parameter };

        OpenAICompatibleConnector.UsesMaxCompletionTokens(options, reasoning).Should().Be(expected);
    }

    [Fact]
    public void An_unknown_max_tokens_parameter_is_refused()
    {
        var document = new RouterConfiguration();
        document.Connectors["openai"] = new ConnectorOptions { BaseAddress = new Uri("https://api.openai.com"), MaxTokensParameter = "max_output_tokens" };

        document.GetDocumentValidationErrors().Should().Contain(e => e.Contains("'openai' MaxTokensParameter", StringComparison.Ordinal));

        document.Connectors["openai"].MaxTokensParameter = "max_completion_tokens";
        document.GetDocumentValidationErrors().Should().NotContain(e => e.Contains("MaxTokensParameter", StringComparison.Ordinal));
        RouterConfiguration.FromJson(document.ToJson()).Connectors["openai"].MaxTokensParameter.Should().Be("max_completion_tokens");
    }

    [Theory]
    [InlineData("\"\"")]
    [InlineData("null")]
    public void Empty_turns_are_left_out_for_anthropic_and_their_neighbours_merge(string emptyContent)
    {
        // A cancelled turn leaves an empty assistant message between two user messages.
        ChatCompletionRequest request = ChatCompletionRequest.Parse(Json($$"""
            {"model":"auto","messages":[
              {"role":"system","content":"S"},
              {"role":"user","content":"first try"},
              {"role":"assistant","content":{{emptyContent}}},
              {"role":"user","content":"second try"},
              {"role":"assistant","content":"answer"},
              {"role":"user","content":""},
              {"role":"user","content":"thanks"}]}
            """));

        JsonObject body = Parse(AnthropicRequestBuilder.Build(request, "claude-opus-5-5"));

        JsonArray messages = body["messages"]!.AsArray();
        messages.Select(m => (string?)m!["role"]).Should().Equal("user", "assistant", "user");
        messages.Should().OnlyContain(m => m!["content"]!.AsArray().Count > 0, "the Messages API rejects \"content\": []");
        messages[0]!["content"]!.AsArray().Select(b => (string?)b!["text"]).Should().Equal("first try", "second try");
        messages[2]!["content"]!.AsArray().Select(b => (string?)b!["text"]).Should().Equal("thanks");
    }

    [Fact]
    public void A_turn_with_only_thinking_is_left_out_for_anthropic()
    {
        ChatCompletionRequest request = ChatCompletionRequest.ParseAnthropic(Json("""
            {"model":"auto","max_tokens":100,"messages":[
              {"role":"user","content":"hi"},
              {"role":"assistant","content":[{"type":"thinking","thinking":"hmm","signature":"semantic-router-relayed"}]},
              {"role":"user","content":"well?"}]}
            """));

        JsonObject body = Parse(AnthropicRequestBuilder.Build(request, "claude-opus-5-5"));

        JsonArray messages = body["messages"]!.AsArray();
        messages.Should().ContainSingle();
        messages[0]!["content"]!.AsArray().Select(b => (string?)b!["text"]).Should().Equal("hi", "well?");
    }
}
