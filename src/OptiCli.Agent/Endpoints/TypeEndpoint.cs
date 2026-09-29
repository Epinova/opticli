using EPiServer.DataAbstraction;
using OptiCli.Agent.Http;
using OptiCli.Agent.Types;
using OptiCli.Core.Text;
using OptiCli.Protocol;

namespace OptiCli.Agent.Endpoints;

internal static class TypeEndpoint
{
    public static ContentTypeModel Handle(AgentRequest request)
    {
        var types = request.Service<IContentTypeRepository>();
        var describer = new ContentTypeDescriber(types, request.Service<ContentTypeAvailabilityService>());
        return describer.Describe(Find(types, request.Argument));
    }

    /// <summary>By GUID, exact name, or case-insensitive name; otherwise not_found with close matches.</summary>
    public static ContentType Find(IContentTypeRepository types, string nameOrGuid)
    {
        var value = nameOrGuid.Trim();
        var found = Guid.TryParse(value, out var guid)
            ? types.Load(guid)
            : types.Load(value) ?? types.List().FirstOrDefault(t => t.Name.Equals(value, StringComparison.OrdinalIgnoreCase));
        if (found is not null)
        {
            return found;
        }

        var similar = Suggestions.Closest(value, types.List().Select(t => t.Name));
        throw AgentException.NotFound(
            $"No content type '{value}'.",
            similar.Count > 0 ? $"Did you mean {string.Join(", ", similar)}?" : "List types with opticli types.");
    }
}
