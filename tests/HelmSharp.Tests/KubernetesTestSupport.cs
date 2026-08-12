using System.Net;
using System.Text;
using k8s;

namespace HelmSharp.Tests;

internal static class KubernetesTestClientBuilder
{
    public static Kubernetes Create(KubernetesApiHandler handler)
        => new(new KubernetesClientConfiguration
        {
            Host = "https://helmsharp.test",
            SkipTlsVerify = true
        }, handler);
}

internal sealed class KubernetesApiHandler : DelegatingHandler
{
    private readonly Dictionary<(string Method, string Path), Queue<KubernetesResponse>> _responses = [];
    private readonly Dictionary<(string Method, string Path), KubernetesResponse> _persistentResponses = [];
    private readonly List<RecordedKubernetesRequest> _requests = [];

    public IReadOnlyList<RecordedKubernetesRequest> Requests => _requests;

    public KubernetesApiHandler Respond(
        HttpMethod method,
        string path,
        HttpStatusCode statusCode,
        string content = "{}")
    {
        var key = (method.Method, path);
        if (!_responses.TryGetValue(key, out var responses))
        {
            responses = [];
            _responses.Add(key, responses);
        }

        responses.Enqueue(new KubernetesResponse(statusCode, content));
        return this;
    }

    public KubernetesApiHandler RespondAlways(
        HttpMethod method,
        string path,
        HttpStatusCode statusCode,
        string content = "{}")
    {
        _persistentResponses[(method.Method, path)] = new KubernetesResponse(statusCode, content);
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var pathAndQuery = request.RequestUri?.PathAndQuery ?? string.Empty;
        var content = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken);
        _requests.Add(new RecordedKubernetesRequest(request.Method, pathAndQuery, content));

        var key = (request.Method.Method, pathAndQuery);
        if (_responses.TryGetValue(key, out var responses) && responses.TryDequeue(out var response))
            return response.ToHttpResponse(request);

        if (_persistentResponses.TryGetValue(key, out var persistentResponse))
            return persistentResponse.ToHttpResponse(request);

        return new KubernetesResponse(HttpStatusCode.NotFound, """
            { "kind": "Status", "apiVersion": "v1", "status": "Failure", "code": 404 }
            """).ToHttpResponse(request);
    }
}

internal sealed record RecordedKubernetesRequest(HttpMethod Method, string PathAndQuery, string? Content);

internal sealed record KubernetesResponse(HttpStatusCode StatusCode, string Content)
{
    public HttpResponseMessage ToHttpResponse(HttpRequestMessage request)
        => new(StatusCode)
        {
            RequestMessage = request,
            Content = new StringContent(Content, Encoding.UTF8, "application/json")
        };
}

internal static class AsyncEnumerableTestExtensions
{
    public static async Task<IReadOnlyList<T>> CollectAsync<T>(IAsyncEnumerable<T> source)
    {
        var values = new List<T>();
        await foreach (var value in source)
            values.Add(value);
        return values;
    }

    public static async Task DrainAsync<T>(IAsyncEnumerable<T> source)
    {
        await foreach (var _ in source)
        {
        }
    }
}

internal sealed class DeterministicTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    private DateTimeOffset _utcNow = utcNow;

    public override DateTimeOffset GetUtcNow() => _utcNow;

    public void Advance(TimeSpan duration) => _utcNow = _utcNow.Add(duration);
}

internal sealed class DeterministicPolling(DeterministicTimeProvider timeProvider, bool advanceClock = true)
{
    public List<TimeSpan> Delays { get; } = [];

    public Task DelayAsync(TimeSpan duration, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Delays.Add(duration);
        if (advanceClock)
            timeProvider.Advance(duration);
        return Task.CompletedTask;
    }
}
