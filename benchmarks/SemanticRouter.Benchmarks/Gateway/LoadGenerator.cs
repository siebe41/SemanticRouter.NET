using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;

namespace SemanticRouter.Benchmarks.Gateway;

/// <summary>One request as the client saw it.</summary>
/// <param name="StartedAt">When it was sent, from the start of the run.</param>
/// <param name="FirstByte">Time to the first byte of the body.</param>
/// <param name="Total">Time to the end of the body.</param>
/// <param name="Status">The HTTP status, or 0 when the request failed.</param>
/// <param name="Body">The body, for checks (kept only when asked for).</param>
/// <param name="Attempt">The router's <c>X-Prompt-Route-Attempt</c>, when it sent one.</param>
internal sealed record Sample(TimeSpan StartedAt, TimeSpan FirstByte, TimeSpan Total, int Status, string? Body, string? Attempt);

/// <summary>A closed loop of clients: each sends its next request as soon as the last one ends.</summary>
internal sealed class LoadGenerator(HttpClient http)
{
    private int _sequence;

    /// <summary>A fresh client with enough connections for every worker.</summary>
    public static HttpClient Client() => new(new SocketsHttpHandler { MaxConnectionsPerServer = 1024, PooledConnectionLifetime = Timeout.InfiniteTimeSpan })
    {
        Timeout = TimeSpan.FromSeconds(60),
    };

    /// <summary>Runs <paramref name="concurrency"/> clients for <paramref name="duration"/>, after <paramref name="warmUp"/> that is not recorded.</summary>
    public async Task<List<Sample>> RunAsync(Uri target, int concurrency, TimeSpan duration, TimeSpan warmUp, bool stream = false, bool keepBodies = false, TimeSpan? cancelAfterFirstByte = null)
    {
        var samples = new List<Sample>();
        long started = Stopwatch.GetTimestamp();
        TimeSpan end = warmUp + duration;
        await Task.WhenAll(Enumerable.Range(0, concurrency).Select(async _ =>
        {
            var mine = new List<Sample>();
            while (Stopwatch.GetElapsedTime(started) < end)
            {
                TimeSpan at = Stopwatch.GetElapsedTime(started);
                Sample sample = await SendAsync(target, stream, keepBodies, cancelAfterFirstByte, at);
                if (at >= warmUp)
                {
                    mine.Add(sample);
                }
            }

            lock (samples)
            {
                samples.AddRange(mine);
            }
        }));

        return samples;
    }

    /// <summary>Sends exactly <paramref name="count"/> requests from <paramref name="concurrency"/> clients.</summary>
    public async Task<List<Sample>> SendAsync(Uri target, int concurrency, int count, bool stream, TimeSpan? cancelAfterFirstByte = null)
    {
        var samples = new List<Sample>();
        int left = count;
        long started = Stopwatch.GetTimestamp();
        await Task.WhenAll(Enumerable.Range(0, concurrency).Select(async _ =>
        {
            while (Interlocked.Decrement(ref left) >= 0)
            {
                Sample sample = await SendAsync(target, stream, keepBodies: true, cancelAfterFirstByte, Stopwatch.GetElapsedTime(started));
                lock (samples)
                {
                    samples.Add(sample);
                }
            }
        }));

        return samples;
    }

    private async Task<Sample> SendAsync(Uri target, bool stream, bool keepBodies, TimeSpan? cancelAfterFirstByte, TimeSpan at)
    {
        // A different prompt every time, as from many unrelated clients: every one is classified, and none is a retry.
        // Plain numbers: a run of hex digits could pass for a secret, which keeps a prompt local (and this setup has
        // no local model).
        int n = Interlocked.Increment(ref _sequence);
        string prompt = string.Create(CultureInfo.InvariantCulture, $"Summarize the status of work item {n} for the weekly report, ticket {Random.Shared.Next(100_000)}.");
        string json = $$"""{"model":"auto","stream":{{(stream ? "true" : "false")}},"messages":[{"role":"user","content":"{{prompt}}"}]}""";
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(target, "/v1/chat/completions"))
        {
            Content = new StringContent(json, Encoding.UTF8, new MediaTypeHeaderValue("application/json")),
        };

        long sent = Stopwatch.GetTimestamp();
        TimeSpan firstByte = TimeSpan.Zero;
        using var cancel = new CancellationTokenSource();
        try
        {
            using HttpResponseMessage response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel.Token);
            string? attempt = response.Headers.TryGetValues("X-Prompt-Route-Attempt", out IEnumerable<string>? values) ? values.FirstOrDefault() : null;
            Stream body = await response.Content.ReadAsStreamAsync(cancel.Token);
            await using (body)
            {
                var text = keepBodies || response.StatusCode != System.Net.HttpStatusCode.OK ? new MemoryStream() : null;
                byte[] buffer = new byte[16 * 1024];
                bool first = true;
                int read;
                while ((read = await body.ReadAsync(buffer, cancel.Token)) > 0)
                {
                    if (first)
                    {
                        firstByte = Stopwatch.GetElapsedTime(sent);
                        first = false;
                        if (cancelAfterFirstByte is { } wait)
                        {
                            // A client that goes away mid-reply (Esc in Claude Code).
                            cancel.CancelAfter(wait);
                        }
                    }

                    text?.Write(buffer, 0, read);
                }

                return new Sample(at, firstByte, Stopwatch.GetElapsedTime(sent), (int)response.StatusCode, text is null ? null : Encoding.UTF8.GetString(text.ToArray()), attempt);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
        {
            return new Sample(at, firstByte, Stopwatch.GetElapsedTime(sent), 0, ex.Message, null);
        }
    }

    /// <summary>The <paramref name="p"/> quantile of <paramref name="values"/>, in milliseconds.</summary>
    public static double Percentile(IEnumerable<TimeSpan> values, double p)
    {
        double[] sorted = [.. values.Select(v => v.TotalMilliseconds).Order()];
        return sorted.Length == 0 ? double.NaN : sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Length) - 1, 0, sorted.Length - 1)];
    }
}
