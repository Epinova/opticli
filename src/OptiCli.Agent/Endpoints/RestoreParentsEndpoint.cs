using System.Globalization;
using EPiServer.Core;
using EPiServer.DataAbstraction;
using OptiCli.Agent.Http;
using OptiCli.Cms;
using OptiCli.Protocol;

namespace OptiCli.Agent.Endpoints;

/// <summary>
/// <c>GET /v1/restore-parents?ids=</c>: the parent the CMS stored for each item when it was last moved, read through the
/// CMS's own repository (<see cref="IParentRestoreRepository"/>), which knows how its Dynamic Data Store maps the entries
/// to columns; <c>restore</c> reads the same. For <c>opticli trash</c> while <c>serve</c> runs.
/// </summary>
/// <remarks>The agent's own, not <c>OptiCli.Cms</c>'s: the MCP module neither lists the recycle bin nor restores.</remarks>
internal static class RestoreParentsEndpoint
{
    public const string Ids = "ids";

    public static RestoreParentsResult Handle(AgentRequest request)
    {
        var query = request.Context.Request.Query;
        if (query.Keys.FirstOrDefault(k => !string.Equals(k, Ids, StringComparison.OrdinalIgnoreCase)) is { } unknown)
        {
            throw AgentException.Usage($"Unknown query parameter '{unknown}'.", $"Accepted: {Ids}.");
        }
        var ids = new List<int>();
        foreach (var part in query[Ids].ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            ids.Add(int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0
                ? id
                : throw AgentException.Usage($"'{part}' is not a content id.", $"{Ids} is a comma-separated list of content ids."));
        }
        if (ids.Count > RestoreParentsResult.MaxIds)
        {
            throw AgentException.Usage($"At most {RestoreParentsResult.MaxIds} ids at a time.");
        }
        var repository = request.Service<IParentRestoreRepository>();
        var parents = new Dictionary<string, string>();
        foreach (var id in ids.Distinct())
        {
            var parent = repository.GetParentLink(new ContentReference(id));
            if (!ContentReference.IsNullOrEmpty(parent))
            {
                parents[id.ToString(CultureInfo.InvariantCulture)] = parent.ToReferenceWithoutVersion().ToString();
            }
        }
        return new RestoreParentsResult(parents);
    }
}
