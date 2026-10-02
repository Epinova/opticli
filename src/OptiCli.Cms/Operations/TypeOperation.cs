using EPiServer.DataAbstraction;
using OptiCli.Cms.Types;
using OptiCli.Core.Text;
using OptiCli.Protocol;

namespace OptiCli.Cms.Operations;

/// <summary>One content type as the site runs it (the agent's <c>GET /v1/types/{name}</c>).</summary>
internal static class TypeOperation
{
    /// <param name="name">The type's name (any casing) or GUID.</param>
    public static ContentTypeModel Run(CmsCall call, string name)
    {
        var types = call.Service<IContentTypeRepository>();
        var describer = new ContentTypeDescriber(types, call.Service<ContentTypeAvailabilityService>());
        return describer.Describe(Find(call, types, name));
    }

    /// <summary>By GUID, exact name, or case-insensitive name; otherwise not_found with close matches.</summary>
    public static ContentType Find(CmsCall call, IContentTypeRepository types, string nameOrGuid)
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
            similar.Count > 0
                ? $"Did you mean {string.Join(", ", similar)}?"
                : call.ForCaller("List types with opticli types.",
                    "get_content shows the type of existing content, and get_content_type on a parent's type the types allowed below it."));
    }
}
