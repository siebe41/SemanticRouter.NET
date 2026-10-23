using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SemanticRouter.AspNetCore;

namespace SemanticRouter.Tests.AspNetCore;

/// <summary>
/// A request rebuilt as an OpenAI <c>/v1/chat/completions</c> body: the tool choice from each client protocol, the
/// sampling settings, stop sequences and structured output.
/// </summary>
public sealed class OpenAIRequestBuilderTests
{
    private const string OpenAITools = """[{"type":"function","function":{"name":"get_weather","parameters":{"type":"object"}}}]""";
    private const string AnthropicTools = """[{"name":"get_weather","input_schema":{"type":"object"}}]""";

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static JsonObject Build(ChatCompletionRequest request) =>
        JsonNode.Parse(Encoding.UTF8.GetString(OpenAIRequestBuilder.Build(request, "qwen3").Span))!.AsObject();

    private static ChatCompletionRequest OpenAI(string extra) => ChatCompletionRequest.Parse(Json(
        $$"""{"model":"auto","messages":[{"role":"user","content":"weather in Oslo?"}],"tools":{{OpenAITools}}{{extra}}}"""));

    private static ChatCompletionRequest Anthropic(string choice) => ChatCompletionRequest.ParseAnthropic(Json(
        $$"""{"model":"auto","max_tokens":100,"messages":[{"role":"user","content":"weather in Oslo?"}],"tools":{{AnthropicTools}},"tool_choice":{{choice}}}"""));

    [Theory]
    [InlineData("\"none\"", "\"none\"")]
    [InlineData("\"required\"", "\"required\"")]
    [InlineData("""{"type":"function","function":{"name":"get_weather"}}""", """{"type":"function","function":{"name":"get_weather"}}""")]
    public void An_openai_tool_choice_is_kept(string choice, string expected)
    {
        JsonObject body = Build(OpenAI($",\"tool_choice\":{choice}"));

        JsonNode.DeepEquals(body["tool_choice"], JsonNode.Parse(expected)).Should().BeTrue(body.ToJsonString());
    }

    [Theory]
    [InlineData("""{"type":"none"}""", "\"none\"")]
    [InlineData("""{"type":"any"}""", "\"required\"")]
    [InlineData("""{"type":"tool","name":"get_weather"}""", """{"type":"function","function":{"name":"get_weather"}}""")]
    public void An_anthropic_tool_choice_becomes_the_openai_one(string choice, string expected)
    {
        JsonObject body = Build(Anthropic(choice));

        JsonNode.DeepEquals(body["tool_choice"], JsonNode.Parse(expected)).Should().BeTrue(body.ToJsonString());
    }

    [Fact]
    public void Auto_tool_choice_is_left_to_the_backend()
    {
        Build(OpenAI(string.Empty)).ContainsKey("tool_choice").Should().BeFalse();
        Build(Anthropic("""{"type":"auto"}""")).ContainsKey("tool_choice").Should().BeFalse();
    }

    [Fact]
    public void Sampling_settings_stop_sequences_and_the_user_are_carried_over()
    {
        JsonObject body = Build(ChatCompletionRequest.Parse(Json("""
            {"model":"auto","messages":[{"role":"user","content":"hi"}],
             "temperature":0.3,"top_p":0.9,"stop":["END","STOP"],"user":"u-42","max_tokens":64}
            """)));

        ((double)body["temperature"]!, (double)body["top_p"]!, (int)body["max_tokens"]!, (string?)body["user"]).Should().Be((0.3, 0.9, 64, "u-42"));
        body["stop"]!.AsArray().Select(s => (string?)s).Should().Equal("END", "STOP");
        body.ContainsKey("tools").Should().BeFalse();
    }

    [Fact]
    public void Anthropic_stop_sequences_become_stop()
    {
        JsonObject body = Build(ChatCompletionRequest.ParseAnthropic(Json("""
            {"model":"auto","max_tokens":10,"top_p":0.5,"stop_sequences":["\n\nHuman:"],"messages":[{"role":"user","content":"hi"}]}
            """)));

        body["stop"]!.AsArray().Select(s => (string?)s).Should().Equal("\n\nHuman:");
        ((double)body["top_p"]!).Should().Be(0.5);
    }

    [Fact]
    public void Json_mode_and_a_strict_schema_are_carried_over()
    {
        JsonObject jsonMode = Build(ChatCompletionRequest.Parse(Json("""
            {"model":"auto","messages":[{"role":"user","content":"hi"}],"response_format":{"type":"json_object"}}
            """)));
        JsonObject schema = Build(ChatCompletionRequest.Parse(Json("""
            {"model":"auto","messages":[{"role":"user","content":"hi"}],
             "response_format":{"type":"json_schema","json_schema":{"name":"answer","strict":true,"schema":{"type":"object","properties":{"a":{"type":"string"}}}}}}
            """)));
        JsonObject unnamed = Build(ChatCompletionRequest.Parse(Json("""
            {"model":"auto","messages":[{"role":"user","content":"hi"}],"response_format":{"type":"json_schema","json_schema":{}}}
            """)));

        jsonMode["response_format"]!.ToJsonString().Should().Be("""{"type":"json_object"}""");
        JsonNode.DeepEquals(
            schema["response_format"],
            JsonNode.Parse("""{"type":"json_schema","json_schema":{"name":"answer","schema":{"type":"object","properties":{"a":{"type":"string"}}},"strict":true}}"""))
            .Should().BeTrue(schema.ToJsonString());
        JsonNode.DeepEquals(
            unnamed["response_format"],
            JsonNode.Parse("""{"type":"json_schema","json_schema":{"name":"response","schema":{"type":"object"}}}"""))
            .Should().BeTrue("a schema without a name or body gets the defaults, and strict only when asked: " + unnamed.ToJsonString());
    }

    [Theory]
    [InlineData("""{"type":"object","properties":{"city":{"type":"string"}}}""", """{"type":"object","properties":{"city":{"type":"string"}}}""")]
    [InlineData("not json {", """{"type":"object"}""")]
    [InlineData("", """{"type":"object"}""")]
    public void A_schema_is_written_as_json_and_one_that_does_not_parse_as_any_object(string schema, string expected)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            OpenAIRequestBuilder.WriteRaw(writer, schema);
        }

        Encoding.UTF8.GetString(buffer.WrittenSpan).Should().Be(expected);
    }
}
