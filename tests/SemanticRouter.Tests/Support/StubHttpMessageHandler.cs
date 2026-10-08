using System.Net;
using System.Text;

namespace SemanticRouter.Tests.Support;

/// <summary>Records requests (with their bodies) and replies via a delegate.</summary>
internal sealed class StubHttpMessageHandler(Func<HttpRequestMessage, string, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<(HttpRequestMessage Request, string Body)> Requests { get; } = [];

    /// <summary>The configured reply, for composing handlers.</summary>
    public HttpResponseMessage Respond(HttpRequestMessage request, string body) => respond(request, body);

    public static StubHttpMessageHandler Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new((_, _) => new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") });

    /// <summary>Answers each path with its JSON (the request body is passed for POSTs); other paths get 404.</summary>
    public static StubHttpMessageHandler Paths(params (string Path, Func<string, string> Json)[] routes) => new(Route(routes));

    /// <summary>Like <see cref="Paths"/>, but only on <paramref name="port"/>: other ports refuse the connection.</summary>
    public static StubHttpMessageHandler PathsOnPort(int port, params (string Path, Func<string, string> Json)[] routes)
    {
        Func<HttpRequestMessage, string, HttpResponseMessage> route = Route(routes);
        return new((request, body) => request.RequestUri!.Port == port ? route(request, body) : throw new HttpRequestException("Connection refused"));
    }

    private static Func<HttpRequestMessage, string, HttpResponseMessage> Route((string Path, Func<string, string> Json)[] routes) =>
        (request, body) => routes.FirstOrDefault(r => r.Path == request.RequestUri!.AbsolutePath) is { Json: not null } route
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(route.Json(body), Encoding.UTF8, "application/json") }
            : new HttpResponseMessage(HttpStatusCode.NotFound);

    public static StubHttpMessageHandler Throws() =>
        new((_, _) => throw new HttpRequestException("Connection refused"));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add((request, body));
        return respond(request, body);
    }
}
