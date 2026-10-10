using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace SemanticRouter.AspNetCore;

/// <summary>
/// Sends notices and the weekly digest to the configured webhooks (<see cref="NotificationOptions.Webhooks"/>): JSON,
/// Slack-compatible or ntfy. A failed delivery is retried a few times with a growing pause, then logged; delivery runs
/// in the background and never holds up routing.
/// </summary>
internal sealed partial class WebhookNotifier(RouterRuntimeProvider provider, IHttpClientFactory clients, ILogger<WebhookNotifier> logger)
{
    /// <summary>The named HTTP client the webhooks are sent with.</summary>
    public const string ClientName = "SemanticRouter.Webhooks";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The pauses before each retry; replaceable for tests.</summary>
    internal static IReadOnlyList<TimeSpan> RetryDelays { get; set; } = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(60)];

    /// <summary>Whether any webhook wants notices or the digest.</summary>
    public bool Wants(bool digest) =>
        provider.Current.Resolved.Notifications.Webhooks.Values.Any(h => !string.IsNullOrEmpty(h.Url) && (digest ? h.Digest : h.Notices));

    /// <summary>Sends a notice that appeared (or, with <paramref name="cleared"/>, no longer applies) to every webhook that takes notices.</summary>
    public Task SendNoticeAsync(RouterNotice notice, bool cleared, CancellationToken cancellationToken = default)
    {
        NotificationOptions options = provider.Current.Resolved.Notifications;
        return Task.WhenAll(options.Webhooks
            .Where(h => h.Value.Notices && !string.IsNullOrEmpty(h.Value.Url))
            .Select(h => DeliverAsync(h.Key, h.Value, () => NoticeMessage(h.Value.Format, notice, cleared, options.DashboardUrl), cancellationToken)));
    }

    /// <summary>Sends the digest to every webhook that takes it.</summary>
    public Task SendDigestAsync(DigestResponse digest, CancellationToken cancellationToken = default)
    {
        NotificationOptions options = provider.Current.Resolved.Notifications;
        return Task.WhenAll(options.Webhooks
            .Where(h => h.Value.Digest && !string.IsNullOrEmpty(h.Value.Url))
            .Select(h => DeliverAsync(h.Key, h.Value, () => DigestMessage(h.Value.Format, digest, options.DashboardUrl), cancellationToken)));
    }

    /// <summary>A notice as a request body (and headers, for ntfy).</summary>
    internal static HttpContent NoticeMessage(WebhookFormat format, RouterNotice notice, bool cleared, string? dashboardUrl)
    {
        string? link = Link(dashboardUrl, notice.Link);
        string title = cleared ? $"No longer needs a decision: {notice.Title}" : $"Needs a decision: {notice.Title}";
        return format switch
        {
            WebhookFormat.Slack => JsonContent(new { text = cleared ? title : $"*{title}*\n{notice.Detail}" + (link is null ? string.Empty : $"\n<{link}|Decide on the dashboard>") }),
            WebhookFormat.Ntfy => Ntfy(cleared ? notice.Title : notice.Detail, title, cleared ? "white_check_mark" : "warning", cleared ? 3 : 4, link),
            _ => JsonContent(new
            {
                type = "notice",
                state = cleared ? "cleared" : "appeared",
                key = notice.Key,
                kind = notice.Kind.ToString(),
                title = notice.Title,
                detail = notice.Detail,
                link = link ?? notice.Link,
                since = notice.Since,
            }),
        };
    }

    /// <summary>The digest as a request body.</summary>
    internal static HttpContent DigestMessage(WebhookFormat format, DigestResponse digest, string? dashboardUrl)
    {
        string summary = DigestText(digest);
        string? link = Link(dashboardUrl, "#/overview");
        return format switch
        {
            WebhookFormat.Slack => JsonContent(new { text = $"*SemanticRouter this week*\n{summary}" + (link is null ? string.Empty : $"\n<{link}|Open the dashboard>") }),
            WebhookFormat.Ntfy => Ntfy(summary, "SemanticRouter this week", "bar_chart", 2, link),
            _ => JsonContent(new { type = "digest", summary, digest }),
        };
    }

    /// <summary>"1,234 prompts routed, 62% served locally, $3.20 spent, $18.40 saved vs opus. …"</summary>
    internal static string DigestText(DigestResponse d)
    {
        CultureInfo c = CultureInfo.InvariantCulture;
        var lines = new List<string>
        {
            string.Format(c, "{0:N0} prompts routed, {1:0}% served locally, ${2:0.00} spent", d.Prompts, d.LocalShare * 100, d.CostUsd)
                + (d.ReferenceModel is { } reference ? string.Format(c, ", ${0:0.00} saved against {1}", d.SavedUsd, reference) : string.Empty) + ".",
        };
        if (d.Escalated + d.HeldByConversation + d.Degraded + d.Failed > 0)
        {
            lines.Add(string.Format(c, "{0:N0} raised a tier, {1:N0} degraded, {2:N0} failed.", d.Escalated + d.HeldByConversation, d.Degraded, d.Failed));
        }

        lines.Add(d.Signals > 0 ? string.Format(c, "{0:N0} signs a decision fell short.", d.Signals) : "No signs that a decision fell short.");
        if (d.Learned.Count > 0)
        {
            lines.Add($"Router learned {d.Learned.Count} thing{(d.Learned.Count == 1 ? string.Empty : "s")}: {string.Join("; ", d.Learned.Take(3).Select(l => l.Summary))}.");
        }

        if (d.Notices.Count > 0)
        {
            lines.Add("Needs a decision: " + string.Join("; ", d.Notices.Select(n => n.Title)) + ".");
        }

        return string.Join("\n", lines);
    }

    private static string? Link(string? dashboardUrl, string page) =>
        dashboardUrl is { Length: > 0 } dashboard ? dashboard.TrimEnd('/') + "/" + page : null;

    private static StringContent JsonContent<T>(T value) => new(JsonSerializer.Serialize(value, Json), Encoding.UTF8, "application/json");

    private static StringContent Ntfy(string body, string title, string tags, int priority, string? link)
    {
        var content = new StringContent(body, Encoding.UTF8, "text/plain");
        content.Headers.Add("Title", Ascii.IsValid(title) ? title : $"=?UTF-8?B?{Convert.ToBase64String(Encoding.UTF8.GetBytes(title))}?=");
        content.Headers.Add("Tags", tags);
        content.Headers.Add("Priority", priority.ToString(CultureInfo.InvariantCulture));
        if (link is not null)
        {
            content.Headers.Add("Click", link);
        }

        return content;
    }

    /// <summary>POSTs a fresh message, retrying failures after <see cref="RetryDelays"/>; returns whether it was delivered.</summary>
    private async Task<bool> DeliverAsync(string name, WebhookOptions hook, Func<HttpContent> message, CancellationToken cancellationToken)
    {
        HttpClient client = clients.CreateClient(ClientName);
        for (int attempt = 0; ; attempt++)
        {
            string problem;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, hook.Url) { Content = message() };
                request.Headers.UserAgent.Add(new ProductInfoHeaderValue("SemanticRouter", "1.0"));
                using HttpResponseMessage response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    return true;
                }

                problem = $"HTTP {(int)response.StatusCode}";
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                problem = ex.Message;
            }

            if (attempt >= RetryDelays.Count)
            {
                LogFailed(logger, name, attempt + 1, problem);
                return false;
            }

            await Task.Delay(RetryDelays[attempt], cancellationToken).ConfigureAwait(false);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Webhook {Name} could not be reached after {Attempts} attempts ({Problem}); the message is dropped.")]
    private static partial void LogFailed(ILogger logger, string name, int attempts, string problem);
}
