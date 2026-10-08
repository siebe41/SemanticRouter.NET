using System.Text;
using System.Text.Json;

namespace SemanticRouter.AspNetCore;

public sealed partial class ChatCompletionRequest
{
    /// <summary>The namespace whose tools a Responses client calls without a namespace (Codex's own tools).</summary>
    public const string DefaultToolNamespace = "functions";

    /// <summary>
    /// Parses an OpenAI Responses API body (<c>POST /v1/responses</c>), as Codex CLI sends it: <c>instructions</c>,
    /// <c>input</c> (a string or items: messages, <c>function_call</c>, <c>function_call_output</c>,
    /// <c>custom_tool_call</c>, <c>custom_tool_call_output</c>, <c>reasoning</c>, <c>additional_tools</c>), top-level
    /// <c>tools</c> (functions, custom tools, namespaces, hosted tools), <c>tool_choice</c>, <c>reasoning</c> and
    /// <c>text.format</c>.
    /// </summary>
    public static ChatCompletionRequest ParseResponses(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return new ChatCompletionRequest { Protocol = ChatProtocol.Responses };
        }

        var turns = new List<ChatTurn>();
        var tools = new List<ToolDefinition>();
        var hosted = new List<string>();
        var protocolOnly = new List<string>();
        bool hasImages = false;

        if (root.GetString("instructions") is { Length: > 0 } instructions)
        {
            turns.Add(ChatTurn.FromText("system", instructions));
        }

        if (root.TryGetArray("tools", out JsonElement topTools))
        {
            ReadResponsesTools(topTools, null, tools, hosted);
        }

        if (root.TryGetProperty("input", out JsonElement input))
        {
            if (input.ValueKind == JsonValueKind.String)
            {
                turns.Add(ChatTurn.FromText("user", input.GetString()!));
            }
            else if (input.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in input.EnumerateArray())
                {
                    ReadResponsesItem(item, turns, tools, hosted, protocolOnly, ref hasImages);
                }
            }
        }

        // Tool calls in the history use the names backends will see.
        var byClientName = tools.GroupBy(t => (t.Namespace, t.OriginalName)).ToDictionary(g => g.Key, g => g.First().Name);
        turns = turns.Select(turn => turn.Blocks.Any(b => b is ToolUseBlock)
            ? turn with
            {
                Blocks = turn.Blocks.Select(b => b is ToolUseBlock use && use.Name.Contains('\u0000', StringComparison.Ordinal) ? Rename(use, byClientName) : b).ToArray(),
            }
            : turn).ToList();

        ToolChoice toolChoice = default;
        if (root.TryGetProperty("tool_choice", out JsonElement choice))
        {
            toolChoice = choice.ValueKind switch
            {
                JsonValueKind.String => choice.GetString() switch
                {
                    "none" => new ToolChoice(ToolChoiceMode.None),
                    "required" => new ToolChoice(ToolChoiceMode.Required),
                    _ => default,
                },
                JsonValueKind.Object when choice.GetString("name") is { } name
                    => new ToolChoice(ToolChoiceMode.Named, byClientName.TryGetValue((choice.GetString("namespace"), name), out string? wire) ? wire : name),
                _ => default,
            };
        }

        if (root.TryGetArray("include", out JsonElement include) && include.EnumerateArray().Any(i => i.GetString()?.Contains("logprobs", StringComparison.Ordinal) == true))
        {
            protocolOnly.Add("logprobs");
        }

        if (root.GetString("previous_response_id") is { Length: > 0 })
        {
            protocolOnly.Add("previous_response_id");
        }

        root.TryGetObject("reasoning", out JsonElement reasoning);
        root.TryGetObject("text", out JsonElement text);
        return Finish(new ChatCompletionRequest
        {
            Protocol = ChatProtocol.Responses,
            Model = root.GetString("model"),
            Stream = root.GetBool("stream"),
            Turns = turns,
            Tools = tools,
            ToolChoice = toolChoice,
            HasImages = hasImages,
            MaxTokens = root.GetInt("max_output_tokens"),
            Temperature = root.GetDouble("temperature"),
            TopP = root.GetDouble("top_p"),
            UserId = root.GetString("safety_identifier") ?? root.GetString("user"),
            Reasoning = reasoning.GetString("effort") is { } effort && effort != "none" ? new ReasoningRequest(effort, null) : null,
            ResponseFormat = text.TryGetObject("format", out JsonElement format) ? ReadResponseFormat(format, nested: false) : null,
            ParallelToolCalls = root.TryGetProperty("parallel_tool_calls", out JsonElement parallel) && parallel.ValueKind == JsonValueKind.False ? false : null,
            HostedTools = hosted.Distinct(StringComparer.Ordinal).ToArray(),
            ProtocolOnlyFeatures = protocolOnly.Distinct(StringComparer.Ordinal).ToArray(),
        });
    }

    /// <summary>The backend-safe name of a tool: <c>namespace__name</c> for tools outside the default namespace.</summary>
    public static string WireToolName(string? @namespace, string name) =>
        string.IsNullOrEmpty(@namespace) || @namespace == DefaultToolNamespace ? name : $"{@namespace}__{name}";

    private static ToolUseBlock Rename(ToolUseBlock use, Dictionary<(string? Namespace, string Name), string> byClientName)
    {
        string[] parts = use.Name.Split('\u0000');
        string? ns = parts[0].Length == 0 ? null : parts[0];
        string name = parts[1];
        string wire = byClientName.TryGetValue((ns, name), out string? known) ? known
            : byClientName.TryGetValue((null, name), out string? unqualified) ? unqualified
            : WireToolName(ns, name);
        return use with { Name = wire };
    }

    private static void ReadResponsesTools(JsonElement array, string? @namespace, List<ToolDefinition> tools, List<string> hosted)
    {
        foreach (JsonElement tool in array.EnumerateArray())
        {
            switch (tool.GetString("type"))
            {
                case "function" when tool.GetString("name") is { Length: > 0 } name:
                    string? ns = @namespace == DefaultToolNamespace ? null : @namespace;
                    tools.Add(new ToolDefinition(
                        WireToolName(ns, name),
                        tool.GetString("description"),
                        tool.TryGetObject("parameters", out JsonElement parameters) ? parameters.GetRawText() : """{"type":"object","properties":{}}""",
                        ToolKind.Function,
                        ns,
                        ns is null ? null : name));
                    break;

                case "custom" when tool.GetString("name") is { Length: > 0 } name:
                    string? customNs = @namespace == DefaultToolNamespace ? null : @namespace;
                    string description = tool.GetString("description") ?? string.Empty;
                    if (tool.TryGetObject("format", out JsonElement format) && format.GetString("type") == "grammar" && format.GetString("definition") is { Length: > 0 } grammar)
                    {
                        description += $"\n\nThe input is free text that must match this {format.GetString("syntax") ?? "grammar"} grammar:\n{grammar.Trim()}";
                    }

                    tools.Add(new ToolDefinition(WireToolName(customNs, name), description, ToolDefinition.CustomInputSchema, ToolKind.Custom, customNs, customNs is null ? null : name));
                    break;

                case "namespace" when tool.TryGetArray("tools", out JsonElement inner):
                    ReadResponsesTools(inner, tool.GetString("name"), tools, hosted);
                    break;

                case { } type when type != "function" && type != "custom":
                    hosted.Add(type);
                    break;
            }
        }
    }

    private static void ReadResponsesItem(
        JsonElement item, List<ChatTurn> turns, List<ToolDefinition> tools, List<string> hosted, List<string> protocolOnly, ref bool hasImages)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        string type = item.GetString("type") ?? (item.GetString("role") is not null ? "message" : string.Empty);
        switch (type)
        {
            case "message":
                string role = item.GetString("role") ?? "user";
                var blocks = new List<ContentBlock>();
                if (item.TryGetProperty("content", out JsonElement content))
                {
                    ReadResponsesContent(content, blocks, protocolOnly, ref hasImages);
                }

                AddTurn(turns, role is "developer" or "system" ? "system" : role, blocks);
                break;

            case "function_call":
                // The namespace is resolved against the declared tools once all of them are known.
                AddTurn(turns, "assistant", [new ToolUseBlock(
                    item.GetString("call_id") ?? item.GetString("id") ?? JsonHelpers.NewToolUseId(),
                    (item.GetString("namespace") ?? string.Empty) + "\u0000" + (item.GetString("name") ?? string.Empty),
                    JsonHelpers.EnsureJsonObject(item.GetString("arguments")))]);
                break;

            case "custom_tool_call":
                AddTurn(turns, "assistant", [new ToolUseBlock(
                    item.GetString("call_id") ?? item.GetString("id") ?? JsonHelpers.NewToolUseId(),
                    (item.GetString("namespace") ?? string.Empty) + "\u0000" + (item.GetString("name") ?? string.Empty),
                    JsonHelpers.InputObject(item.GetString("input") ?? string.Empty))]);
                break;

            case "function_call_output" or "custom_tool_call_output":
                bool images = false;
                string output = item.TryGetProperty("output", out JsonElement value) ? ReadToolOutput(value, ref images) : string.Empty;
                hasImages |= images;
                AddTurn(turns, "user", [new ToolResultBlock(item.GetString("call_id") ?? string.Empty, output, IsError: false)]);
                break;

            case "additional_tools" when item.TryGetArray("tools", out JsonElement declared):
                ReadResponsesTools(declared, null, tools, hosted);
                break;

            case "reasoning":
                // Encrypted reasoning only the issuing OpenAI model can read; carried on pass-through only.
                protocolOnly.Add("reasoning items");
                break;

            case "item_reference":
                protocolOnly.Add("item_reference");
                break;

            default:
                if (type.EndsWith("_call", StringComparison.Ordinal) || type.EndsWith("_call_output", StringComparison.Ordinal))
                {
                    // web_search_call, file_search_call, computer_call, …: hosted tool traffic.
                    hosted.Add(type);
                }

                break;
        }
    }

    /// <summary>Appends blocks to the last turn when it has the same role (tool calls and their text belong together).</summary>
    private static void AddTurn(List<ChatTurn> turns, string role, List<ContentBlock> blocks)
    {
        if (blocks.Count == 0)
        {
            return;
        }

        if (turns.Count > 0 && turns[^1].Role == role && role != "system"
            && (role == "assistant" || (turns[^1].IsToolResultOnly && blocks.All(b => b is ToolResultBlock))))
        {
            turns[^1] = turns[^1] with { Blocks = [.. turns[^1].Blocks, .. blocks] };
            return;
        }

        turns.Add(new ChatTurn(role, blocks));
    }

    private static void ReadResponsesContent(JsonElement content, List<ContentBlock> blocks, List<string> protocolOnly, ref bool hasImages)
    {
        if (content.ValueKind == JsonValueKind.String)
        {
            if (content.GetString() is { Length: > 0 } text)
            {
                blocks.Add(new TextBlock(text));
            }

            return;
        }

        if (content.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (JsonElement part in content.EnumerateArray())
        {
            switch (part.GetString("type"))
            {
                case "input_text" or "output_text" or "text" when part.GetString("text") is { } text:
                    blocks.Add(new TextBlock(text));
                    break;
                case "input_image":
                    hasImages = true;
                    blocks.Add(ImageFromUrl(part.GetString("image_url")));
                    break;
                case "input_file":
                    protocolOnly.Add("input_file");
                    break;
                case "refusal" when part.GetString("refusal") is { } refusal:
                    blocks.Add(new TextBlock(refusal));
                    break;
            }
        }
    }

    private static string ReadToolOutput(JsonElement value, ref bool hasImages)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            return value.GetString()!;
        }

        var builder = new StringBuilder();
        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement part in value.EnumerateArray())
            {
                if (part.GetString("type") == "input_image")
                {
                    hasImages = true;
                }
                else if (part.GetString("text") is { } text)
                {
                    Append(builder, text);
                }
            }
        }

        return builder.ToString();
    }
}
