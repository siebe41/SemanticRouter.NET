using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;

namespace SemanticRouter.AspNetCore;

/// <summary>
/// A setting an upgrade to a newer built-in template left as someone had it, although the template changed it too.
/// </summary>
/// <param name="Setting">The setting, for people: <c>Middleware.ModelAliases.*opus*</c>, <c>Routes[smalltalk].Tier</c>.</param>
/// <param name="Path">
/// Where it is in the configuration document: property names, and <c>[Name=smalltalk]</c> for the item of a list with
/// that name.
/// </param>
/// <param name="Yours">The value kept; <see langword="null"/> when you had removed it.</param>
/// <param name="Default">The template's new value; <see langword="null"/> when the template no longer has it.</param>
internal sealed record TemplateDefault(string Setting, IReadOnlyList<string> Path, JsonNode? Yours, JsonNode? Default);

/// <summary>The settings an upgrade from template <paramref name="From"/> to <paramref name="To"/> kept, still to decide.</summary>
internal sealed record TemplateDefaultsPending(int From, int To, DateTimeOffset At, IReadOnlyList<TemplateDefault> Settings);

/// <summary>
/// What an upgrade to a newer built-in template kept as someone had it (the host's template upgrade records it), until
/// a person takes the new default for each setting or keeps theirs. Kept in the state database
/// (<see cref="IStateValueStore"/>), so it outlasts a restart.
/// </summary>
internal sealed class TemplateDefaults(RouterRuntimeProvider provider, IServiceProvider services)
{
    private const string Key = "template_defaults";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly Lock _gate = new();
    private TemplateDefaultsPending? _memory;

    /// <summary>What is still to decide, or <see langword="null"/>.</summary>
    public TemplateDefaultsPending? Pending
    {
        get
        {
            lock (_gate)
            {
                return Load();
            }
        }
    }

    /// <summary>Records what an upgrade kept, replacing what an earlier one left; nothing kept clears it.</summary>
    public void Record(TemplateDefaultsPending pending)
    {
        ArgumentNullException.ThrowIfNull(pending);
        lock (_gate)
        {
            Save(pending.Settings.Count == 0 ? null : pending);
        }
    }

    /// <summary>
    /// Takes the template's new value for <paramref name="setting"/>, as a change on top of the running configuration
    /// (saved when that is), and stops asking about it. <see langword="null"/> when no such setting is pending.
    /// </summary>
    /// <exception cref="InvalidOperationException">The configuration no longer has where the setting was (it is dropped from the list too).</exception>
    public (TemplateDefault Setting, bool Saved)? UseDefault(string setting, string? actor)
    {
        lock (_gate)
        {
            if (Load() is not { } pending || pending.Settings.FirstOrDefault(s => s.Setting == setting) is not { } found)
            {
                return null;
            }

            (RouterRuntime Runtime, bool Saved)? applied = provider.ApplyKeepingSaveState(
                running => WithValue(running, found.Path, found.Default), "template", actor, $"{found.Setting}: the template {pending.To} default");
            Save(pending with { Settings = [.. pending.Settings.Where(s => s != found)] });
            return applied is { } done
                ? (found, done.Saved)
                : throw new InvalidOperationException($"The configuration no longer has {found.Setting}, so there is nothing to change.");
        }
    }

    /// <summary>Keeps <paramref name="setting"/> as it is and stops asking about it. Returns whether it was pending.</summary>
    public bool Keep(string setting)
    {
        lock (_gate)
        {
            if (Load() is not { } pending || !pending.Settings.Any(s => s.Setting == setting))
            {
                return false;
            }

            Save(pending with { Settings = [.. pending.Settings.Where(s => s.Setting != setting)] });
            return true;
        }
    }

    /// <summary>
    /// <paramref name="document"/> with <paramref name="value"/> at <paramref name="path"/> (<see langword="null"/>
    /// removes it), or <see langword="null"/> when the document no longer has where it goes.
    /// </summary>
    internal static RouterConfiguration? WithValue(RouterConfiguration document, IReadOnlyList<string> path, JsonNode? value)
    {
        if (path.Count == 0)
        {
            return null;
        }

        JsonNode? parent = JsonNode.Parse(document.ToJson());
        JsonNode root = parent!;
        foreach (string step in path.Take(path.Count - 1))
        {
            parent = Child(parent, step);
            if (parent is null)
            {
                return null;
            }
        }

        string last = path[^1];
        if (Item(last) is { } item)
        {
            if (parent is not JsonArray list)
            {
                return null;
            }

            int at = list.ToList().FindIndex(n => Matches(n, item));
            if (value is null)
            {
                if (at < 0)
                {
                    return null;
                }

                list.RemoveAt(at);
            }
            else if (at >= 0)
            {
                list[at] = value.DeepClone();
            }
            else
            {
                list.Add(value.DeepClone());
            }
        }
        else if (parent is JsonObject properties)
        {
            if (value is null)
            {
                properties.Remove(last);
            }
            else
            {
                properties[last] = value.DeepClone();
            }
        }
        else
        {
            return null;
        }

        return RouterConfiguration.FromJson(root.ToJsonString());
    }

    /// <summary>The value at <paramref name="path"/> in <paramref name="document"/>, or <see langword="null"/>.</summary>
    internal static JsonNode? ValueAt(RouterConfiguration document, IReadOnlyList<string> path)
    {
        JsonNode? node = JsonNode.Parse(document.ToJson());
        foreach (string step in path)
        {
            node = Child(node, step);
        }

        return node;
    }

    private static JsonNode? Child(JsonNode? node, string step) => Item(step) is { } item
        ? (node as JsonArray)?.FirstOrDefault(n => Matches(n, item))
        : (node as JsonObject)?.TryGetPropertyValue(step, out JsonNode? child) == true ? child : null;

    /// <summary>The key and name of an item step, <c>[Name=smalltalk]</c>; <see langword="null"/> for a property.</summary>
    private static (string Key, string Name)? Item(string step) =>
        step is ['[', .., ']'] && step.IndexOf('=', StringComparison.Ordinal) is > 1 and var equals
            ? (step[1..equals], step[(equals + 1)..^1])
            : null;

    private static bool Matches(JsonNode? node, (string Key, string Name) item) =>
        node is JsonObject o && o.TryGetPropertyValue(item.Key, out JsonNode? key) && key is JsonValue value
        && string.Equals(value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : value.ToJsonString(), item.Name, StringComparison.OrdinalIgnoreCase);

    private TemplateDefaultsPending? Load()
    {
        if (services.GetService<IStateValueStore>() is not { } store)
        {
            return _memory;
        }

        try
        {
            return store.Load(Key) is { Length: > 0 } json ? JsonSerializer.Deserialize<TemplateDefaultsPending>(json, Json) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private void Save(TemplateDefaultsPending? pending)
    {
        if (pending is { Settings.Count: 0 })
        {
            pending = null;
        }

        if (services.GetService<IStateValueStore>() is { } store)
        {
            store.Save(Key, pending is null ? string.Empty : JsonSerializer.Serialize(pending, Json));
        }
        else
        {
            _memory = pending;
        }
    }
}
