using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;

namespace SemanticRouter.AspNetCore;

/// <summary>Why a connector probe failed.</summary>
public enum ConnectorProbeFailure
{
    /// <summary>The probe succeeded.</summary>
    None = 0,

    /// <summary>The server could not be reached (connection refused, DNS, timeout).</summary>
    Unreachable = 1,

    /// <summary>The server wants credentials and none are configured (HTTP 401 without an API key).</summary>
    AuthenticationRequired = 2,

    /// <summary>The server rejected the configured credentials (HTTP 401 or 403 with an API key).</summary>
    CredentialsRejected = 3,

    /// <summary>Any other unsuccessful HTTP status.</summary>
    HttpError = 4,

    /// <summary>The server answered with something that is not a model list.</summary>
    InvalidResponse = 5,
}

/// <summary>The outcome of <see cref="ConnectorProbe.ListModelsAsync"/>.</summary>
/// <param name="Failure">What went wrong, or <see cref="ConnectorProbeFailure.None"/>.</param>
/// <param name="Models">Model ids the server reported.</param>
/// <param name="StatusCode">The HTTP status, when the server answered.</param>
/// <param name="Message">A one-line explanation that names the setting to fix. Never contains a credential.</param>
public sealed record ConnectorProbeResult(ConnectorProbeFailure Failure, IReadOnlyList<string> Models, int? StatusCode, string Message)
{
    /// <summary>Whether the server answered with a model list.</summary>
    public bool Ok => Failure == ConnectorProbeFailure.None;
}

/// <summary>
/// Checks an HTTP connector without calling a model: asks <c>GET /v1/models</c> with the connector's credentials and
/// headers. Used by the dashboard's Discover button and by start-up checks; a 401 or 403 is reported as an
/// authentication problem that names the configuration key, not as "unreachable".
/// </summary>
public static class ConnectorProbe
{
    /// <summary>Lists the models an HTTP connector's server offers.</summary>
    /// <param name="name">The connector name, used in messages (<c>SemanticRouter:Connectors:{name}:ApiKey</c>).</param>
    /// <param name="connector">The connector's settings, with secret references already resolved.</param>
    /// <param name="client">The client to send the request with.</param>
    /// <param name="timeout">How long to wait for the answer.</param>
    /// <param name="cancellationToken">Cancels the probe.</param>
    public static async Task<ConnectorProbeResult> ListModelsAsync(
        string name, ConnectorOptions connector, HttpClient client, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(connector);
        ArgumentNullException.ThrowIfNull(client);
        if (connector.BaseAddress is not { IsAbsoluteUri: true } baseAddress)
        {
            return new(ConnectorProbeFailure.Unreachable, [], null, $"SemanticRouter:Connectors:{name}:BaseAddress is not set.");
        }

        var uri = new Uri(baseAddress, "/v1/models");
        bool hasKey = !string.IsNullOrEmpty(connector.ApiKey);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        if (connector.Type == ConnectorType.Anthropic)
        {
            request.Headers.TryAddWithoutValidation("anthropic-version", connector.AnthropicVersion);
            if (hasKey)
            {
                request.Headers.TryAddWithoutValidation("x-api-key", connector.ApiKey);
            }
        }
        else if (hasKey)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", connector.ApiKey);
        }

        foreach ((string header, string value) in connector.Headers)
        {
            request.Headers.Remove(header);
            request.Headers.TryAddWithoutValidation(header, value);
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            using HttpResponseMessage response = await client.SendAsync(request, timeoutSource.Token).ConfigureAwait(false);
            int status = (int)response.StatusCode;
            string statusText = status.ToString(CultureInfo.InvariantCulture);
            if (status is 401 or 403)
            {
                return new(
                    hasKey || status == 403 ? ConnectorProbeFailure.CredentialsRejected : ConnectorProbeFailure.AuthenticationRequired,
                    [], status,
                    $"{uri} answered HTTP {statusText}: {OpenAICompatibleConnector.CredentialAdvice(name, status, hasKey)}.");
            }

            if (!response.IsSuccessStatusCode)
            {
                return new(ConnectorProbeFailure.HttpError, [], status, $"{uri} answered HTTP {statusText}.");
            }

            using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeoutSource.Token).ConfigureAwait(false));
            if (!json.RootElement.TryGetArray("data", out JsonElement data))
            {
                return new(ConnectorProbeFailure.InvalidResponse, [], status, $"{uri} did not return a model list.");
            }

            string[] ids = data.EnumerateArray().Select(m => m.GetString("id")).Where(id => !string.IsNullOrEmpty(id)).Cast<string>().ToArray();
            return new(ConnectorProbeFailure.None, ids, status, $"{uri}: {ids.Length} model(s).");
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return new(ConnectorProbeFailure.Unreachable, [], null,
                $"{uri} is not reachable ({(ex is OperationCanceledException ? "timed out" : ex.Message)}).");
        }
        catch (JsonException)
        {
            return new(ConnectorProbeFailure.InvalidResponse, [], null, $"{uri} did not return JSON.");
        }
    }
}
