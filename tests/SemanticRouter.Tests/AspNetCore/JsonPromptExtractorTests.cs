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
    [InlineData("<system_reminder>\nIMPORTANT: The tools listed below are deferred.\n</system_reminder>\nsing me a song", "sing me a song")]
    [InlineData("<current_datetime>2026-10-12T11:19:05.499-05:00</current_datetime>\n<copilot_tauri_workspace>\nproject_session_id: 30fa247c\n</copilot_tauri_workspace>\n\ntell me a joke", "tell me a joke")]
    [InlineData("<System-Reminder note=\"x\">a</System-Reminder>ask <system-reminder>b</system-reminder>me", "ask me")]
    [InlineData("<context>keep this</context> please", "<context>keep this</context> please")]
    public void Ignores_context_blocks_clients_inject(string content, string expected)
    {
        string json = JsonSerializer.Serialize(new { messages = new[] { new { role = "user", content } } });
        Extract(json).Should().Be(expected, "the router classifies what the person typed, not the client's boilerplate");
    }

    [Fact]
    public void A_user_message_that_is_only_injected_context_does_not_hide_the_one_before_it()
    {
        string json = JsonSerializer.Serialize(new
        {
            messages = new[]
            {
                new { role = "system", content = "You are Copilot." },
                new { role = "user", content = "<current_datetime>2026-10-12T11:19:05-05:00</current_datetime>\ntell me a joke" },
                new { role = "user", content = "<system_reminder>\nIMPORTANT: The tools listed below are deferred.\n</system_reminder>" },
            },
        });

        Extract(json).Should().Be("tell me a joke");
        Extract(json, PromptMessageSelection.AllMessages).Should().Be("You are Copilot.\n\ntell me a joke");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_blank_last_user_message_does_not_hide_the_one_before(bool stripSystemReminders)
    {
        using JsonDocument document = JsonDocument.Parse("""{"messages":[{"role":"user","content":"hello"},{"role":"user","content":"  "}]}""");

        new JsonPromptExtractor(PromptMessageSelection.LastUserMessage, stripSystemReminders).ExtractPrompt(new DefaultHttpContext(), document.RootElement)
            .Should().Be("hello");
        JsonPromptExtractor.StripSystemReminders("  hello \n").Should().Be("hello", "the result is trimmed, as documented");
    }

    [Fact]
    public void Configured_tags_are_ignored_too()
    {
        string json = JsonSerializer.Serialize(new { messages = new[] { new { role = "user", content = "<environment_details>cwd: /src</environment_details>\nfix the build" } } });
        using JsonDocument document = JsonDocument.Parse(json);

        new JsonPromptExtractor(PromptMessageSelection.LastUserMessage, stripSystemReminders: true, injectedTags: ["<environment_details>", "not a tag!"]).ExtractPrompt(new DefaultHttpContext(), document.RootElement)
            .Should().Be("fix the build");
        Extract(json).Should().StartWith("<environment_details>", "only the configured extra tags are added to the built-in ones");
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
