using System.Globalization;
using OptiCli.Core.Content;

namespace OptiCli.Core.Properties;

public static class PropertyPaths
{
    /// <summary>
    /// A readable path for a stored value: <c>Heading</c>, <c>Hero.Heading</c> (local block),
    /// <c>Facts[2].Label</c> (block list), <c>MainArea[0].Text</c> (inline block in a ContentArea) or
    /// <c>Facts[2].Links[0]</c> (item of a list of references inside a block).
    /// </summary>
    public static string Describe(CmsModel model, int definitionId, string? scopeName)
    {
        var leaf = Name(model, definitionId);
        if (ScopePath.Parse(scopeName) is not { } scope)
        {
            return leaf;
        }
        var parts = scope.Steps.Select(step =>
            Name(model, step.PropertyId) + (step.Index is { } index ? $"[{index.ToString(CultureInfo.InvariantCulture)}]" : ""));
        var item = scope.LeafIndex is { } position ? $"[{position.ToString(CultureInfo.InvariantCulture)}]" : "";
        return string.Join(".", parts.Append(leaf + item));
    }

    /// <summary>The top-level property a stored value belongs to.</summary>
    public static int TopLevel(int definitionId, string? scopeName) =>
        ScopePath.Parse(scopeName) is { Steps.Count: > 0 } scope ? scope.Steps[0].PropertyId : definitionId;

    private static string Name(CmsModel model, int id) => model.Properties.GetValueOrDefault(id)?.Name ?? $"#{id.ToString(CultureInfo.InvariantCulture)}";
}
