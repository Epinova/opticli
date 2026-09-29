using OptiCli.Core.Errors;
using OptiCli.Core.Text;

namespace OptiCli.Core.Cms;

public static class ContentTypeLookup
{
    /// <summary>
    /// Finds a type by GUID, by name, or by the class name of its model type (all case-insensitive).
    /// </summary>
    /// <exception cref="NotFoundException">No match; the hint suggests close names.</exception>
    public static ContentTypeInfo Find(IReadOnlyList<ContentTypeInfo> types, string nameOrGuid)
    {
        var input = nameOrGuid.Trim();
        var match = Guid.TryParse(input, out var guid)
            ? types.FirstOrDefault(t => t.Guid == guid)
            : types.FirstOrDefault(t => string.Equals(t.Name, input, StringComparison.OrdinalIgnoreCase))
              ?? types.FirstOrDefault(t => string.Equals(t.ClassName, input, StringComparison.OrdinalIgnoreCase));

        return match ?? throw new NotFoundException(
            $"No content type '{input}'.",
            Suggestions.DidYouMean(input, types.Select(t => t.Name)) ?? "Run `opticli types` to list content types.");
    }
}
