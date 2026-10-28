using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;

namespace SemanticRouter.AspNetCore;

/// <summary>A cloud model's list prices, in USD per million tokens.</summary>
public sealed record ModelPrice
{
    /// <summary>The price list's id for the model, e.g. <c>anthropic/claude-sonnet-5.5</c>.</summary>
    public required string Id { get; init; }

    /// <summary>Input tokens.</summary>
    public required decimal Input { get; init; }

    /// <summary>Output tokens.</summary>
    public required decimal Output { get; init; }

    /// <summary>Input tokens read from the prompt cache, when the list prices them.</summary>
    public decimal? CacheRead { get; init; }

    /// <summary>Input tokens written to the prompt cache, when the list prices them (most providers charge the input price).</summary>
    public decimal? CacheWrite { get; init; }

    /// <summary>The context window, when the list gives it.</summary>
    public int? ContextWindow { get; init; }
}

/// <summary>A price the list has for a catalog model.</summary>
public sealed record ModelPriceMatch
{
    /// <summary>The list's entry.</summary>
    public required ModelPrice Price { get; init; }

    /// <summary>
    /// Whether the entry is the connector's own provider (Anthropic for an Anthropic connector, OpenAI for api.openai.com, the
    /// exact model on OpenRouter). <see langword="false"/> when the connector's host is not one the list knows, such as a
    /// gateway serving the same model, which may charge differently.
    /// </summary>
    public required bool SameProvider { get; init; }
}

/// <summary>
/// Cloud providers' list prices, from OpenRouter's public model list (<c>GET https://openrouter.ai/api/v1/models</c>,
/// no key needed): input, output and prompt-cache prices for the models of Anthropic, OpenAI, Google, DeepSeek, Mistral,
/// xAI and many others. Providers' own APIs do not report prices, so this is what fills a model's prices in for you.
/// </summary>
/// <remarks>
/// OpenRouter passes the providers' list prices through for their first-party models, so a model id the provider uses
/// (<c>claude-sonnet-5-5</c>, <c>gpt-6.1-sol</c>) finds its provider's price. Prices change: a lookup is a suggestion to
/// check, written to the draft, not applied.
/// </remarks>
public sealed partial class ModelPriceList
{
    /// <summary>Where the list comes from by default.</summary>
    public static readonly Uri DefaultSource = new("https://openrouter.ai/api/v1/models");

    /// <summary>The named <see cref="HttpClient"/> the list is fetched with.</summary>
    public const string HttpClientName = "SemanticRouter.Prices";

    /// <summary>OpenRouter's vendor prefixes, most likely first when a model id alone matches several.</summary>
    private static readonly string[] FirstParty = ["anthropic", "openai", "google", "x-ai", "deepseek", "mistralai", "meta-llama", "qwen", "moonshotai", "z-ai", "cohere", "amazon", "microsoft", "nvidia"];

    /// <summary>API hosts and the vendor whose models they serve at its own prices.</summary>
    private static readonly (string Host, string Vendor)[] Hosts =
    [
        ("api.anthropic.com", "anthropic"),
        ("api.openai.com", "openai"),
        ("generativelanguage.googleapis.com", "google"),
        ("api.deepseek.com", "deepseek"),
        ("api.mistral.ai", "mistralai"),
        ("api.x.ai", "x-ai"),
        ("api.moonshot.ai", "moonshotai"),
        ("api.cohere.com", "cohere"),
    ];

    private readonly Dictionary<string, ModelPrice> _byId = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<ModelPrice>> _bySlug = new(StringComparer.Ordinal);

    private ModelPriceList(IEnumerable<ModelPrice> prices, Uri source, DateTimeOffset fetched)
    {
        Source = source;
        Fetched = fetched;
        foreach (ModelPrice price in prices)
        {
            if (!_byId.TryAdd(price.Id, price))
            {
                continue;
            }

            // Variants (":free", ":batch") and aliases ("~anthropic/claude-opus-latest") are found by their exact id only.
            if (!price.Id.StartsWith('~') && !price.Id.Contains(':', StringComparison.Ordinal))
            {
                string slug = Slug(price.Id);
                if (!_bySlug.TryGetValue(slug, out List<ModelPrice>? list))
                {
                    _bySlug[slug] = list = [];
                }

                list.Add(price);
            }
        }
    }

    /// <summary>Where the list was fetched from.</summary>
    public Uri Source { get; }

    /// <summary>When it was fetched.</summary>
    public DateTimeOffset Fetched { get; }

    /// <summary>Every model priced.</summary>
    public IReadOnlyCollection<ModelPrice> Prices => _byId.Values;

    /// <summary>
    /// Reads OpenRouter's model list (<c>{"data":[{"id", "context_length", "pricing":{"prompt", "completion",
    /// "input_cache_read", "input_cache_write"}}]}</c>, prices in USD per token as strings). Models with a variable
    /// price (routers, priced -1) are left out.
    /// </summary>
    /// <param name="json">The list.</param>
    /// <param name="source">Where it came from.</param>
    /// <param name="fetched">When.</param>
    /// <exception cref="JsonException">It is not such a list.</exception>
    public static ModelPriceList Parse(string json, Uri source, DateTimeOffset fetched)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(source);
        using JsonDocument document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("The price list has no \"data\" array.");
        }

        var prices = new List<ModelPrice>();
        foreach (JsonElement model in data.EnumerateArray())
        {
            if (model.ValueKind != JsonValueKind.Object
                || !model.TryGetProperty("id", out JsonElement id) || id.GetString() is not { Length: > 0 } name
                || !model.TryGetProperty("pricing", out JsonElement pricing) || pricing.ValueKind != JsonValueKind.Object
                || PerMillion(pricing, "prompt") is not { } input || PerMillion(pricing, "completion") is not { } output)
            {
                continue;
            }

            int? context = model.TryGetProperty("context_length", out JsonElement length) && length.TryGetInt32(out int tokens) && tokens > 0 ? tokens : null;
            prices.Add(new ModelPrice
            {
                Id = name,
                Input = input,
                Output = output,
                CacheRead = PerMillion(pricing, "input_cache_read"),
                CacheWrite = PerMillion(pricing, "input_cache_write"),
                ContextWindow = context,
            });
        }

        return new ModelPriceList(prices, source, fetched);
    }

    /// <summary>
    /// The list's price for <paramref name="model"/> served by <paramref name="connector"/>, or <see langword="null"/>.
    /// The connector says whose price it is: its provider's (by connector type, CLI preset or API host), or on
    /// OpenRouter the exact model. A connector on another host matches the model id alone, and says so.
    /// </summary>
    /// <param name="connector">The connector's settings.</param>
    /// <param name="model">The provider's model id (<see cref="ModelDefinition.Model"/>, or the catalog name).</param>
    public ModelPriceMatch? Find(ConnectorOptions connector, string model)
    {
        ArgumentNullException.ThrowIfNull(connector);
        if (string.IsNullOrWhiteSpace(model))
        {
            return null;
        }

        string id = model.Trim();
        string? host = connector.Type is ConnectorTypes.OpenAICompatible or ConnectorTypes.Anthropic or ConnectorTypes.Responses ? connector.BaseAddress?.Host.ToLowerInvariant() : null;
        if (host is "openrouter.ai" or "www.openrouter.ai")
        {
            return _byId.TryGetValue(id, out ModelPrice? exact) ? new ModelPriceMatch { Price = exact, SameProvider = true } : null;
        }

        string? vendor = VendorOf(connector, host);

        // Claude Code's aliases follow the family's latest model, as OpenRouter's "~anthropic/claude-opus-latest" does.
        if (vendor == "anthropic" && id.ToLowerInvariant() is "haiku" or "sonnet" or "opus" or "fable"
            && _byId.TryGetValue($"~anthropic/claude-{id.ToLowerInvariant()}-latest", out ModelPrice? latest))
        {
            return new ModelPriceMatch { Price = latest, SameProvider = true };
        }

        if (id.Contains('/', StringComparison.Ordinal) && _byId.TryGetValue(id, out ModelPrice? named))
        {
            return new ModelPriceMatch
            {
                Price = named,
                SameProvider = vendor is null || named.Id.StartsWith(vendor + "/", StringComparison.OrdinalIgnoreCase),
            };
        }

        if (!_bySlug.TryGetValue(Slug(id), out List<ModelPrice>? candidates))
        {
            return null;
        }

        if (vendor is not null)
        {
            ModelPrice? own = candidates.FirstOrDefault(p => p.Id.StartsWith(vendor + "/", StringComparison.OrdinalIgnoreCase));
            return own is null ? null : new ModelPriceMatch { Price = own, SameProvider = true };
        }

        // An id without a vendor ("gpt-x") ranks last.
        ModelPrice best = candidates.OrderBy(p => Rank(p.Id.Split('/')[0])).First();

        // A subscription (a CLI, Copilot) is valued at the model maker's own price; a gateway may charge differently.
        return new ModelPriceMatch { Price = best, SameProvider = connector.RunsProgram };
    }

    private static int Rank(string vendor) => Array.IndexOf(FirstParty, vendor) is var rank && rank >= 0 ? rank : int.MaxValue;

    /// <summary>Whose prices a connector pays: <c>anthropic</c>, <c>openai</c>, …, or <see langword="null"/> when its host is not a known provider's.</summary>
    private static string? VendorOf(ConnectorOptions connector, string? host) => connector.Type switch
    {
        ConnectorTypes.Anthropic when host is null || host.EndsWith("anthropic.com", StringComparison.Ordinal) => "anthropic",
        ConnectorTypes.Cli => connector.Preset switch
        {
            CliPreset.ClaudeCode => "anthropic",
            CliPreset.Codex => "openai",
            _ => null,
        },
        _ when host is null => connector.Type == ConnectorTypes.Responses ? "openai" : null,
        _ => Hosts.FirstOrDefault(h => host == h.Host || host.EndsWith("." + h.Host, StringComparison.Ordinal)).Vendor,
    };

    /// <summary>
    /// A model id without its vendor, variant, date or <c>-latest</c>, with dots as dashes: <c>anthropic/claude-sonnet-4.5</c>,
    /// <c>claude-sonnet-4-5-20250929</c> and <c>models/claude-sonnet-4-5</c> are all <c>claude-sonnet-4-5</c>.
    /// </summary>
    internal static string Slug(string id)
    {
        string slug = id.Trim().ToLowerInvariant();
        slug = slug[(slug.LastIndexOf('/') + 1)..];
        if (slug.IndexOf(':', StringComparison.Ordinal) is int colon and > 0)
        {
            slug = slug[..colon];
        }

        slug = slug.Replace('.', '-').Replace('_', '-');
        return DateSuffix().Replace(slug, string.Empty);
    }

    private static decimal? PerMillion(JsonElement pricing, string name)
    {
        if (!pricing.TryGetProperty(name, out JsonElement value))
        {
            return null;
        }

        string? text = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null,
        };
        if (!decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal perToken) || perToken < 0)
        {
            return null;
        }

        // Rounded to a millionth of a dollar and without trailing zeros, so 0.000003 per token reads as 3, not 3.000000.
        return decimal.Round(perToken * 1_000_000m, 6) / 1.000000000000000000000000000000000m;
    }

    [GeneratedRegex(@"(-\d{8}|-\d{4}-\d{2}-\d{2}|-latest)$", RegexOptions.CultureInvariant)]
    private static partial Regex DateSuffix();
}

/// <summary>
/// Fetches the <see cref="ModelPriceList"/> when a price is looked up, and keeps it for six hours. Nothing is fetched
/// unless someone asks (the dashboard's Look up prices, or Discover models for a cloud connector); offline mode and the
/// machine policy's allowed endpoints are respected.
/// </summary>
internal sealed class ModelPriceSource(IServiceProvider services, TimeProvider time) : IDisposable
{
    private static readonly TimeSpan KeepFor = TimeSpan.FromHours(6);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ModelPriceList? _list;

    /// <summary>The list, or why there is none.</summary>
    public async Task<(ModelPriceList? List, string? Problem)> GetAsync(CancellationToken cancellationToken)
    {
        Uri source = ModelPriceList.DefaultSource;
        if (services.GetService<RouterRuntimeProvider>()?.Policy.Refuses(source) is { } refused)
        {
            return (null, $"Prices are looked up at {source.Host}, and {refused}.");
        }

        if (_list is { } cached && time.GetUtcNow() - cached.Fetched < KeepFor)
        {
            return (cached, null);
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_list is { } fresh && time.GetUtcNow() - fresh.Fetched < KeepFor)
            {
                return (fresh, null);
            }

            HttpClient client = services.GetRequiredService<IHttpClientFactory>().CreateClient(ModelPriceList.HttpClientName);
            using HttpResponseMessage response = await client.GetAsync(source, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return (_list, $"{source.Host} answered {(int)response.StatusCode} {response.ReasonPhrase}.");
            }

            string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            _list = ModelPriceList.Parse(json, source, time.GetUtcNow());
            return (_list, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            // An older list is still better than none.
            return (_list, ex is TaskCanceledException ? $"{source.Host} did not answer in time." : $"Couldn't read the price list from {source.Host}: {ex.Message}");
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();
}
