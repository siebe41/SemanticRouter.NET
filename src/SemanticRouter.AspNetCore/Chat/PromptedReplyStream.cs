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
/// string arguments, and trailing commas. With <see cref="PromptedToolParsing.Strict"/> only a fence with the request's
/// own label counts, and only at the end of the reply: calls followed by text are kept as text. The end of the reply
/// says when that happened (<see cref="PromptedReply.Ignored"/>) and, read leniently, when strict parsing would have
/// (<see cref="PromptedReply.StrictWouldRefuse"/>), so the two modes can be compared on real traffic.
/// <para>
/// In both modes a call that repeats one written in the request's tool results is kept as text, and the request's
/// label never reaches the client: the text released says plain <c>tool_call</c> instead.
/// </para>
/// </remarks>
/// <param name="toolNames">The request's tools; when given, near-miss formats that name one of them count as calls too.</param>
/// <param name="format">The request's fence label, parsing mode and quoted calls; the plain <c>tool_call</c> label, leniently, by default.</param>
internal sealed class PromptedReplyStream(IReadOnlyCollection<string>? toolNames = null, PromptedToolFormat? format = null)
{
    private static readonly string[] ToolLabels = [PromptedToolCalls.Fence, "tool_calls", "tool_use", "tool", "function_call", "function", "call"];
    private static readonly JsonDocumentOptions Lenient = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };

    private readonly PromptedToolFormat _format = format ?? PromptedToolFormat.Default;
    private readonly StringBuilder _reply = new(); // the undecided rest of the reply
    private readonly StringBuilder? _whole = format?.Parsing == PromptedToolParsing.Strict ? new() : null; // to tell what Lenient would have read

    private readonly List<(string Text, HeldKind Kind)> _held = [];
    private readonly List<ToolUseBlock> _calls = [];
    private readonly List<MalformedToolCall> _malformed = [];
    private int _position;
    private bool _midLine;
    private bool _waiting;
    private string _pending = string.Empty; // released text that waits for what follows: trailing whitespace, or a cut-off label
    private bool _holdAll;
    private bool _inCodeBlock;
    private bool _quoted;
    private bool _unlabelled; // a call was read from a block without the run's own label

    private enum HeldKind
    {
        Text,
        Malformed,
        Call,
    }

    private bool Strict => _format.Parsing == PromptedToolParsing.Strict;

    /// <summary>Adds a fragment of the reply and returns the text that can go to the client now.</summary>
    public string Feed(string delta)
    {
        _reply.Append(delta);
        _whole?.Append(delta);
        if (_waiting && !delta.Contains('\n', StringComparison.Ordinal))
        {
            return string.Empty; // a held block can only complete at a line end (or at the end of the reply)
        }

        return Advance(final: false);
    }

    /// <summary>Ends the reply: the text not yet released, and the calls.</summary>
    public PromptedReply Finish()
    {
        string tail = Redact(Advance(final: true) + _pending);

        // Strict: a call must end the reply. Text after one (another call aside) makes them all text again.
        int first = _held.FindIndex(h => h.Kind == HeldKind.Call);
        bool followed = first >= 0 && _held.Skip(first).Any(h => h.Kind != HeldKind.Call && !string.IsNullOrWhiteSpace(h.Text));
        bool misplaced = Strict && followed;

        // Strict: a call the model wrote without the run's label (```tool_call, ```json, bare JSON) stays text. Say so,
        // so a model that keeps forgetting the label can be told from one that answered in text.
        bool unlabelled = Strict && _calls.Count == 0 && LenientlyReadCalls();
        var text = new StringBuilder(tail);
        var clean = new StringBuilder(tail);
        foreach ((string held, HeldKind kind) in _held)
        {
            if (kind == HeldKind.Call && !misplaced)
            {
                continue;
            }

            text.Append(held);
            if (kind != HeldKind.Malformed)
            {
                clean.Append(held);
            }
        }

        string? ignored = string.Join("; ", new[]
        {
            _quoted ? "tool call copied from a tool result, kept as text" : null,
            misplaced ? "tool call not at the end of the reply, kept as text" : null,
            unlabelled ? "tool call without this run's label, kept as text" : null,
        }.OfType<string>());

        // Lenient: whether Strict would have taken the calls, to measure what switching costs.
        string? strictWouldRefuse = Strict || _calls.Count == 0 ? null : string.Join(", ", new[]
        {
            _unlabelled ? "without this run's label" : null,
            followed ? "not at the end of the reply" : null,
        }.OfType<string>());
        return new PromptedReply(
            Redact(text.ToString()), Redact(clean.ToString()), misplaced ? [] : _calls, _malformed, ignored.Length > 0 ? ignored : null,
            strictWouldRefuse is { Length: > 0 } ? strictWouldRefuse : null);
    }

    /// <summary>Whether the whole reply, read leniently, holds calls (none of them copied from a tool result).</summary>
    private bool LenientlyReadCalls()
    {
        var lenient = new PromptedReplyStream(toolNames, _format with { Parsing = PromptedToolParsing.Lenient });
        _ = lenient.Feed(_whole?.ToString() ?? string.Empty);
        return lenient.Finish().Calls.Count > 0;
    }

    /// <summary>The text with the request's label written as plain <c>tool_call</c>: the token is for the model only.</summary>
    private string Redact(string text)
    {
        // Until none is left: "tool_call" is how the label starts, so a replacement can complete another label. In any case,
        // as the fence is matched: a model that upper-cases the label must not hand it to the client.
        while (!string.Equals(_format.Label, PromptedToolCalls.Fence, StringComparison.OrdinalIgnoreCase)
            && text.Contains(_format.Label, StringComparison.OrdinalIgnoreCase))
        {
            text = text.Replace(_format.Label, PromptedToolCalls.Fence, StringComparison.OrdinalIgnoreCase);
        }

        return text;
    }

    /// <summary>How many characters before <paramref name="end"/> could be the start of the request's label.</summary>
    private int LabelStartBefore(string text, int end)
    {
        if (_format.Label == PromptedToolCalls.Fence)
        {
            return 0;
        }

        for (int length = Math.Min(_format.Label.Length - 1, end); length > 0; length--)
        {
            if (text.AsSpan(end - length, length).SequenceEqual(_format.Label.AsSpan(0, length)))
            {
                return length;
            }
        }

        return 0;
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
            _held.Add((text, malformed ? HeldKind.Malformed : HeldKind.Text));
            return;
        }

        // Trailing whitespace waits for the text after it: before a tool call it is noise. So does the start of the
        // request's label, which the next fragment may complete.
        text = Redact(_pending + text);
        int end = text.Length;
        while (end > 0 && char.IsWhiteSpace(text[end - 1]))
        {
            end--;
        }

        if (end == text.Length)
        {
            end -= LabelStartBefore(text, end);
        }

        emit.Append(text, 0, end);
        _pending = text[end..];
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
                || (!_inCodeBlock && !Strict && (content.StartsWith("{", StringComparison.Ordinal) || content.StartsWith("[{", StringComparison.Ordinal))))
            {
                return line;
            }

            if (newline < 0)
            {
                // The last line is still arriving: hold it while it could still become a fence or a JSON array.
                bool undecided = !final && ("```".AsSpan().StartsWith(content, StringComparison.Ordinal) || (!_inCodeBlock && !Strict && content is "["));
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
            Accept(calls, reply[start..end], labelled: false, emit);
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
        bool toolish = Strict
            ? string.Equals(label, _format.Label, StringComparison.Ordinal)
            : string.Equals(label, _format.Label, StringComparison.OrdinalIgnoreCase) || ToolLabels.Contains(label, StringComparer.OrdinalIgnoreCase);
        bool mayBeCall = toolish || (!Strict && (label.Length == 0 || label.Equals("json", StringComparison.OrdinalIgnoreCase)));
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
            Accept(calls, block, labelled: string.Equals(label, _format.Label, StringComparison.Ordinal), emit);
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

    private void Accept(List<ToolUseBlock> calls, string block, bool labelled, StringBuilder emit)
    {
        if (_format.QuotedCalls.Count > 0 && calls.Any(c => _format.QuotedCalls.Contains(PromptedToolCalls.CallKey(c.Name, c.InputJson))))
        {
            // The same call is written in a tool result: whoever wrote that page or file chose it, not the model.
            _quoted = true;
            Release(emit, block);
            return;
        }

        _calls.AddRange(calls);
        _unlabelled |= !labelled;
        _held.Add((block, HeldKind.Call)); // its text, should the calls turn out not to end the reply
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

        call = new ToolUseBlock { Id = JsonHelpers.NewToolUseId(), Name = name, InputJson = input };
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
/// <param name="Ignored">Why calls the reply did contain were kept as text (copied from a tool result, or with strict parsing not at the end or without the run's label), if they were.</param>
/// <param name="StrictWouldRefuse">With lenient parsing, why strict parsing would have kept the calls as text (without the run's label, not at the end), if it would.</param>
internal sealed record PromptedReply(
    string Text,
    string TextWithoutMalformed,
    IReadOnlyList<ToolUseBlock> Calls,
    IReadOnlyList<MalformedToolCall> Malformed,
    string? Ignored = null,
    string? StrictWouldRefuse = null);

/// <summary>A block the model meant as a tool call that could not be read, and why.</summary>
internal sealed record MalformedToolCall(string Block, string Problem);
