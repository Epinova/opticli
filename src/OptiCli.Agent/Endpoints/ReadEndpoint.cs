using OptiCli.Agent.Http;
using OptiCli.Cms;
using OptiCli.Cms.Operations;
using OptiCli.Protocol;

namespace OptiCli.Agent.Endpoints;

/// <summary><c>GET /v1/content/{ref}</c>: the query string as a <see cref="ReadRequest"/> for <see cref="ReadOperation"/>.</summary>
internal static class ReadEndpoint
{
    public static ContentItem Handle(AgentRequest request)
    {
        var query = request.Context.Request.Query;
        if (query.Keys.FirstOrDefault(k => !ReadQuery.All.Contains(k, StringComparer.OrdinalIgnoreCase)) is { } unknown)
        {
            throw AgentException.Usage($"Unknown query parameter '{unknown}'.", $"Accepted: {string.Join(", ", ReadQuery.All)}.");
        }
        var body = new ReadRequest(query[ReadQuery.Language].ToString(), query[ReadQuery.Version].ToString());
        return ReadOperation.Run(request.Call, request.Argument, body);
    }
}
