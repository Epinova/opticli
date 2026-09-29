namespace OptiCli.Core.Cms;

/// <summary>Splits assembly-qualified names like <c>My.Site.Pages.ArticlePage, My.Site, Version=...</c>.</summary>
public static class ModelTypeNames
{
    public static string? ClassName(string? modelType)
    {
        var fullName = FullName(modelType);
        if (fullName is null)
        {
            return null;
        }
        var start = Math.Max(fullName.LastIndexOf('.'), fullName.LastIndexOf('+')) + 1;
        return fullName[start..];
    }

    public static string? Namespace(string? modelType)
    {
        var fullName = FullName(modelType);
        var dot = fullName?.LastIndexOf('.') ?? -1;
        return dot > 0 ? fullName![..dot] : null;
    }

    private static string? FullName(string? modelType)
    {
        if (string.IsNullOrWhiteSpace(modelType))
        {
            return null;
        }
        var comma = modelType.IndexOf(',');
        var fullName = (comma >= 0 ? modelType[..comma] : modelType).Trim();
        var generic = fullName.IndexOf('`');
        return generic >= 0 ? fullName[..generic] : fullName;
    }
}
