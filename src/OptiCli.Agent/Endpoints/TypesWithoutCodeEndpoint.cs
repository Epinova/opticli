using EPiServer.DataAbstraction;
using OptiCli.Agent.Http;
using OptiCli.Protocol;

namespace OptiCli.Agent.Endpoints;

/// <summary>
/// <c>GET /v1/types-without-code</c>: content types the CMS has a model class on record for, which the running site
/// can't load. The CMS resolves <c>ModelTypeString</c> lazily into <c>ModelType</c> (null when the class is gone), so this
/// covers classes removed from the site's own code and from packages alike, which a source scan can't. On CMS 13 a type
/// with a GUID has no class on record: one its model sync made (<see cref="Compat.AgentBuild.FromCode"/>) that no model of
/// the running site matches counts too.
/// </summary>
/// <remarks>The agent's own: the MCP module has no use for it.</remarks>
internal static class TypesWithoutCodeEndpoint
{
    public static TypesWithoutCodeResult Handle(AgentRequest request) => new(
        request.Service<IContentTypeRepository>().List()
            .Where(t => Compat.AgentBuild.FromCode(t) && t.ModelType is null && !OrphanRemoval.SystemTypes.Contains(t.Name, StringComparer.Ordinal))
            .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .Select(t => new TypeWithoutCode(t.ID, t.GUID, t.Name, string.IsNullOrWhiteSpace(t.ModelTypeString) ? null : t.ModelTypeString))
            .ToList());
}
