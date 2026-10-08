using System.Text;
using System.Text.Json;

namespace SemanticRouter.AspNetCore;

/// <summary>
/// Splits a prompted-tool-calling reply into text and tool calls while it streams. Text is released as soon as it
/// cannot be the start of a call; only a line that opens a fence or a JSON object is held back, until the block is
/// complete and can be told apart. Once a call (or a malformed call) has been seen, the rest of the reply is held:
/// text goes to the client before the calls.
/// </summary>
/// <remarks>
/// Besides the requested <c>```tool_call</c> fence, it accepts the near-misses models produce: another tool-ish fence
/// label (<c>tool_use</c>, <c>function_call</c>, …), a <c>```json</c> or unlabelled fence or a bare JSON object that names
/// one of the request's tools, an array of calls, OpenAI-style <c>{"function": {"name", "arguments"}}</c> objects with
/// string arguments, and trailing commas.
/// </remarks>
internal sealed class PromptedReplyStream(IReadOnlyCollection<string>? toolNames = null)
{
    private static readonly string[] ToolLabels = [PromptedToolCalls.Fence, "tool_calls", "tool_use", "tool", "function_call", "function", "call"];
    private static readonly JsonDocumentOptions Lenient = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };

    private readonly StringBuilder _reply = new(); // the undecided rest of the reply

    private readonly List<(string Text, bool Malformed)> _held = [];
    private readonly List<ToolUseBlock> _calls = [];
    private readonly List<MalformedToolCall> _malformed = [];
    private int _position;
    private bool _midLine;
    private bool _waiting;
    private string _pendingWhitespace = string.Empty;
    private bool _holdAll;
    private bool _inCodeBlock;

    /// <summary>Adds a fragment of the reply and returns the text that can go to the client now.</summary>
    public string Feed(string delta)
    {
        _reply.Append(delta);
        if (_waiting && !delta.Contains('\n', StringComparison.Ordinal))
        {
            return string.Empty; // a held block can only complete at a line end (or at the end of the reply)
        }

        return Advance(final: false);
    }

    /// <summary>Ends the reply: the text not yet released, and the calls.</summary>
    public PromptedReply Finish()
    {
        string tail = Advance(final: true) + _pendingWhitespace;
        var text = new StringBuilder(tail);
        var clean = new StringBuilder(tail);
        foreach ((string held, bool malformed) in _held)
        {
            text.Append(held);
            if (!malformed)
            {
                clean.Append(held);
            }
        }

        return new PromptedReply(text.ToString(), clean.ToString(), _calls, _malformed);
    }

    private string Advance(bool final)
    {
        string reply = _reply.ToString();
        var emit = new StringBuilder();
        _waiting = false;
        while (_position < reply.Length)
        {
            int start = FindHoldStart(reply, _position, final);
            if (start < 0)
            {
                Release(emit, reply[_position..]);
                _position = reply.Length;
                break;
            }

            if (start > _position)
            {
                Release(emit, reply[_position..start]);
                _position = start;
            }

            int end = Resolve(reply, start, final, emit);
            if (end < 0)
            {
                // The block at start needs more of the reply; once its first line is complete, only new lines matter.
                _waiting = reply.IndexOf('\n', start) >= 0;
                break;
            }

            _position = end;
        }

        if (_position > 0)
        {
            _midLine = reply[_position - 1] != '\n';
            _reply.Remove(0, _position);
            _position = 0;
        }

        return emit.ToString();
    }

    private void Release(StringBuilder emit, string text, bool malformed = false)
    {
        if (text.Length == 0)
        {
            return;
        }

        if (_holdAll)
        {
            _held.Add((text, malformed));
            return;
        }

        // Trailing whitespace waits for the text after it: before a tool call it is noise.
        text = _pendingWhitespace + text;
        int end = text.Length;
        while (end > 0 && char.IsWhiteSpace(text[end - 1]))
        {
            end--;
        }

        emit.Append(text, 0, end);
        _pendingWhitespace = text[end..];
    }

    /// <summary>The first line start at or after <paramref name="from"/> that may open a call, or -1.</summary>
    private int FindHoldStart(string reply, int from, bool final)
    {
        int line = from;
        if (line > 0 ? reply[line - 1] != '\n' : _midLine)
        {
            int next = reply.IndexOf('\n', line);
            if (next < 0)
            {
                return -1;
            }

            line = next + 1;
        }

        while (line <= reply.Length)
        {
            int newline = reply.IndexOf('\n', line);
            ReadOnlySpan<char> content = (newline < 0 ? reply.AsSpan(line) : reply.AsSpan(line, newline - line)).TrimStart(" \t");
            if (content.StartsWith("```", StringComparison.Ordinal)
                || (!_inCodeBlock && (content.StartsWith("{", StringComparison.Ordinal) || content.StartsWith("[{", StringComparison.Ordinal))))
            {
                return line;
            }

            if (newline < 0)
            {
                // The last line is still arriving: hold it while it could still become a fence or a JSON array.
                bool undecided = !final && ("```".AsSpan().StartsWith(content, StringComparison.Ordinal) || (!_inCodeBlock && content is "["));
                return undecided ? line : -1;
            }

            line = newline + 1;
        }

        return -1;
    }

    /// <summary>Decides the block at <paramref name="start"/>; returns the index after it, or -1 when it is incomplete.</summary>
    private int Resolve(string reply, int start, bool final, StringBuilder emit)
    {
        int lineEnd = reply.IndexOf('\n', start);
        string firstLine = (lineEnd < 0 ? reply[start..] : reply[start..lineEnd]).Trim();
        if (firstLine.Length == 0 || firstLine == "`" || firstLine == "``" || firstLine == "[")
        {
            if (!final)
            {
                return -1;
            }

            Release(emit, reply[start..]);
            return reply.Length;
        }

        if (firstLine.StartsWith("```", StringComparison.Ordinal))
        {
            return ResolveFence(reply, start, lineEnd, firstLine, final, emit);
        }

        int open = start + (reply.AsSpan(start).IndexOfAnyExcept(" \t"));
        int end = JsonEnd(reply, open);
        if (end < 0)
        {
            if (!final)
            {
                return -1;
            }

            Release(emit, reply[start..]);
            return reply.Length;
        }

        if (toolNames is not null && TryParseCalls(reply[open..end], requireKnownName: true, out List<ToolUseBlock> calls, out _))
        {
            Accept(calls);
        }
        else
        {
            Release(emit, reply[start..end]);
        }

        return end;
    }

    private int ResolveFence(string reply, int start, int lineEnd, string firstLine, bool final, StringBuilder emit)
    {
        if (lineEnd < 0 && !final)
        {
            return -1;
        }

        if (_inCodeBlock)
        {
            // The fence that closes an ordinary code block.
            _inCodeBlock = false;
            int closed = lineEnd < 0 ? reply.Length : lineEnd + 1;
            Release(emit, reply[start..closed]);
            return closed;
        }

        string label = firstLine.TrimStart('`').Trim();
        bool toolish = ToolLabels.Contains(label, StringComparer.OrdinalIgnoreCase);
        bool mayBeCall = toolish || label.Length == 0 || label.Equals("json", StringComparison.OrdinalIgnoreCase);
        if (!mayBeCall || lineEnd < 0)
        {
            // An ordinary code block (```python): it streams like text.
            _inCodeBlock = !mayBeCall && lineEnd >= 0;
            int next = lineEnd < 0 ? reply.Length : lineEnd + 1;
            Release(emit, reply[start..next]);
            return next;
        }

        int close = ClosingFence(reply, lineEnd + 1);
        if (close < 0 && !final)
        {
            return -1;
        }

        string body = reply[(lineEnd + 1)..(close < 0 ? reply.Length : close)];
        int after = close < 0 ? reply.Length : reply.IndexOf("```", close, StringComparison.Ordinal) + 3;
        string block = reply[start..after];
        if (TryParseCalls(body, requireKnownName: !toolish, out List<ToolUseBlock> calls, out string? problem))
        {
            Accept(calls);
        }
        else if (toolish)
        {
            _malformed.Add(new MalformedToolCall(block.Trim(), problem ?? "not a tool call"));
            _holdAll = true;
            Release(emit, block, malformed: true);
        }
        else
        {
            Release(emit, block);
        }

        return after;
    }

    private void Accept(List<ToolUseBlock> calls)
    {
        _calls.AddRange(calls);
        _holdAll = true;
    }

    private static int ClosingFence(string reply, int from)
    {
        int line = from;
        while (line < reply.Length)
        {
            int newline = reply.IndexOf('\n', line);
            ReadOnlySpan<char> content = (newline < 0 ? reply.AsSpan(line) : reply.AsSpan(line, newline - line)).TrimStart(" \t");
            if (content.StartsWith("```", StringComparison.Ordinal))
            {
                return line;
            }

            if (newline < 0)
            {
                break;
            }

            line = newline + 1;
        }

        return -1;
    }

    /// <summary>The index after the JSON object or array that opens at <paramref name="open"/>, or -1 when it is incomplete.</summary>
    private static int JsonEnd(string text, int open)
    {
        int depth = 0;
        bool inString = false;
        for (int i = open; i < text.Length; i++)
        {
            char c = text[i];
            if (inString)
            {
                if (c == '\\')
                {
                    i++;
                }
                else if (c == '"')
                {
                    inString = false;
                }
            }
            else if (c == '"')
            {
                inString = true;
            }
            else if (c is '{' or '[')
            {
                depth++;
            }
            else if (c is '}' or ']' && --depth == 0)
            {
                return i + 1;
            }
        }

        return -1;
    }

    /// <summary>Reads one call, an array of calls or a <c>tool_calls</c> wrapper.</summary>
    private bool TryParseCalls(string json, bool requireKnownName, out List<ToolUseBlock> calls, out string? problem)
    {
        calls = [];
        problem = null;
        try
        {
            using JsonDocument document = JsonDocument.Parse(json.Trim(), Lenient);
            JsonElement root = document.RootElement;
            IEnumerable<JsonElement> items = root.ValueKind == JsonValueKind.Array ? root.EnumerateArray()
                : root.ValueKind == JsonValueKind.Object && root.TryGetArray("tool_calls", out JsonElement wrapped) ? wrapped.EnumerateArray()
                : [root];
            foreach (JsonElement item in items)
            {
                if (!TryParseCall(item, requireKnownName, out ToolUseBlock? call, out problem))
                {
                    calls.Clear();
                    return false;
                }

                calls.Add(call!);
            }

            problem = calls.Count == 0 ? "no call in the block" : null;
            return calls.Count > 0;
        }
        catch (JsonException ex)
        {
            problem = "invalid JSON: " + ex.Message;
            return false;
        }
    }

    private bool TryParseCall(JsonElement item, bool requireKnownName, out ToolUseBlock? call, out string? problem)
    {
        call = null;
        if (item.ValueKind != JsonValueKind.Object)
        {
            problem = "a call must be a JSON object";
            return false;
        }

        JsonElement source = item.TryGetObject("function", out JsonElement function) ? function : item;
        string? name = source.GetString("name") ?? source.GetString("tool") ?? source.GetString("tool_name") ?? item.GetString("name");
        if (string.IsNullOrWhiteSpace(name))
        {
            problem = "the call has no \"name\"";
            return false;
        }

        if (toolNames is not null && !toolNames.Contains(name, StringComparer.Ordinal))
        {
            string? match = toolNames.FirstOrDefault(t => string.Equals(t, name, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                problem = requireKnownName ? "not a tool call" : $"there is no tool named '{name}'";
                return false;
            }

            name = match;
        }
        else if (requireKnownName && toolNames is null)
        {
            problem = "not a tool call";
            return false;
        }

        string input = "{}";
        foreach (string property in (string[])["input", "arguments", "parameters", "args"])
        {
            if (!source.TryGetProperty(property, out JsonElement value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.Object)
            {
                input = Normalized(value);
            }
            else if (value.ValueKind == JsonValueKind.String && TryObject(value.GetString()!, out string? parsed))
            {
                input = parsed!; // OpenAI style: arguments as a JSON string
            }
            else
            {
                problem = $"\"{property}\" must be a JSON object";
                return false;
            }

            break;
        }

        call = new ToolUseBlock(JsonHelpers.NewToolUseId(), name, input);
        problem = null;
        return true;
    }

    /// <summary>The element re-written as strict JSON (a lenient parse keeps trailing commas and comments in the raw text).</summary>
    private static string Normalized(JsonElement element)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, ChatJsonOptions.Writer))
        {
            element.WriteTo(writer);
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    private static bool TryObject(string text, out string? json)
    {
        json = null;
        try
        {
            using JsonDocument document = JsonDocument.Parse(text, Lenient);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            json = Normalized(document.RootElement);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

/// <summary>The end of a prompted reply.</summary>
/// <param name="Text">Text not yet released, malformed blocks included.</param>
/// <param name="TextWithoutMalformed">The same without the malformed blocks (when a repair replaced them).</param>
/// <param name="Calls">The tool calls, in order.</param>
/// <param name="Malformed">Blocks labelled as tool calls that could not be read.</param>
internal sealed record PromptedReply(string Text, string TextWithoutMalformed, IReadOnlyList<ToolUseBlock> Calls, IReadOnlyList<MalformedToolCall> Malformed);

/// <summary>A block the model meant as a tool call that could not be read, and why.</summary>
internal sealed record MalformedToolCall(string Block, string Problem);
