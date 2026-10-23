using System.Text.Json;
using SemanticRouter.AspNetCore;

namespace SemanticRouter.Tests.AspNetCore;

public class ChatCompletionRequestTests
{
    private static ChatCompletionRequest Parse(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return ChatCompletionRequest.Parse(document.RootElement);
    }

    [Fact]
    public void Parses_messages_stream_and_model()
    {
        ChatCompletionRequest request = Parse("""
            {"model":"auto:best","stream":true,"messages":[
              {"role":"system","content":"S"},
              {"role":"user","content":[{"type":"text","text":"look"},{"type":"image_url","image_url":{"url":"x"}}]}]}
            """);

        request.Model.Should().Be("auto:best");
        request.Stream.Should().BeTrue();
        request.Messages.Should().Equal(new ChatMessage { Role = "system", Content = "S" }, new ChatMessage { Role = "user", Content = "look" });
        request.HasImages.Should().BeTrue();
        request.HasTools.Should().BeFalse();
        request.EstimatedInputTokens.Should().BePositive();
    }

    [Fact]
    public void Detects_tools_and_counts_their_definitions_toward_input_size()
    {
        ChatCompletionRequest without = Parse("""{"messages":[{"role":"user","content":"weather?"}]}""");
        ChatCompletionRequest with = Parse("""
            {"messages":[{"role":"user","content":"weather?"}],
             "tools":[{"type":"function","function":{"name":"get_weather","description":"Gets the weather for a city","parameters":{"type":"object"}}}]}
            """);

        with.HasTools.Should().BeTrue();
        with.EstimatedInputTokens.Should().BeGreaterThan(without.EstimatedInputTokens + 10);
    }

    [Fact]
    public void Accepts_a_bare_prompt()
    {
        Parse("""{"prompt":"hi"}""").Messages.Should().Equal(new ChatMessage { Role = "user", Content = "hi" });
    }

    [Theory]
    [InlineData(null, null, null)]
    [InlineData("auto", null, null)]
    [InlineData("AUTO:CHEAPEST", SelectionStrategy.CheapestCapable, null)]
    [InlineData("auto:best", SelectionStrategy.MostCapable, null)]
    [InlineData("auto:priority", SelectionStrategy.Priority, null)]
    [InlineData("claude-cli", null, "claude-cli")]
    public void Model_aliases_resolve_to_strategies_or_explicit_models(string? model, SelectionStrategy? strategy, string? requested)
    {
        ModelAliases.Resolve(model).Should().Be((strategy, requested));
    }
}
