using System.Net;

namespace OptiCli.Core.Tests.Azure;

/// <summary>Answers requests by the routes given (method and path start, ignoring case as Azure does), and keeps what was asked.</summary>
internal sealed class FakeHttp : HttpMessageHandler
{
    private readonly List<(string Method, string Path, Func<HttpRequestMessage, HttpResponseMessage> Answer)> _routes = [];

    public List<(HttpRequestMessage Request, string? Body)> Requests { get; } = [];

    public FakeHttp On(string method, string pathStart, HttpStatusCode status, string body = "", Action<HttpResponseMessage>? change = null) =>
        On(method, pathStart, _ =>
        {
            var response = new HttpResponseMessage(status) { Content = new StringContent(body) };
            change?.Invoke(response);
            return response;
        });

    public FakeHttp On(string method, string pathStart, Func<HttpRequestMessage, HttpResponseMessage> answer)
    {
        _routes.Add((method, pathStart, answer));
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add((request, request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken)));
        // The last route added for a path wins, so a test can change an answer as it goes.
        for (var i = _routes.Count - 1; i >= 0; i--)
        {
            var (method, path, answer) = _routes[i];
            if (request.Method.Method == method && request.RequestUri!.AbsolutePath.StartsWith(path, StringComparison.OrdinalIgnoreCase))
            {
                return answer(request);
            }
        }
        return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent($"no route for {request.Method} {request.RequestUri}") };
    }
}
