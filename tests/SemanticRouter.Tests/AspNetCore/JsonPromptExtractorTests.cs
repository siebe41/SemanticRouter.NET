using System.Text.Json;
using Microsoft.AspNetCore.Http;
using SemanticRouter.AspNetCore;

namespace SemanticRouter.Tests.AspNetCore;

public class JsonPromptExtractorTests
{
    private const string Conversation = """
        {
          "model": "auto",
          "messages": [
            { "role": "system", "content": "You are terse." },
            { "role": "user", "content": "first question" },
            { "role": "assistant", "content": "first answer" },
            { "role": "user", "content": [ { "type": "text", "text": "second" }, { "type": "image_url", "image_url": {} }, { "type": "text", "text": "question" } ] }
          ]
        }
        """;

    private static string? Extract(string json, PromptMessageSelection selection = PromptMessageSelection.LastUserMessage)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return new JsonPromptExtractor(selection).ExtractPrompt(new DefaultHttpContext(), document.RootElement);
    }

    [Fact]
    public void Takes_the_last_user_message_and_joins_text_parts()
    {
        Extract(Conversation).Should().Be("second\nquestion");
    }

    [Fact]
    public void Can_select_all_user_messages()
    {
        Extract(Conversation, PromptMessageSelection.AllUserMessages).Should().Be("first question\n\nsecond\nquestion");
    }

    [Fact]
    public void Can_select_the_entire_conversation()
    {
        Extract(Conversation, PromptMessageSelection.AllMessages)
            .Should().Be("You are terse.\n\nfirst question\n\nfirst answer\n\nsecond\nquestion");
    }

    [Theory]
    [InlineData("""{ "prompt": "why is the sky blue?" }""", "why is the sky blue?")]
    [InlineData("""{ "input": "embed me" }""", "embed me")]
    [InlineData("""{ "input": ["one", "two"] }""", "one\ntwo")]
    [InlineData("""{ "messages": [ { "role": "assistant", "content": "hi" } ], "prompt": "fallback" }""", "fallback")]
    public void Supports_completion_and_input_shapes(string json, string expected)
    {
        Extract(json).Should().Be(expected);
    }

    [Theory]
    [InlineData("""[ "not", "an", "object" ]""")]
    [InlineData("""{ "model": "x" }""")]
    [InlineData("""{ "messages": "nope" }""")]
    [InlineData("""{ "messages": [ { "role": "user", "content": 42 } ] }""")]
    [InlineData("""{ "prompt": 123 }""")]
    public void Returns_null_when_there_is_no_prompt(string json)
    {
        Extract(json).Should().BeNull();
    }
}
