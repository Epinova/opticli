using System.Globalization;
using EPiServer.Core;
using OptiCli.Cms.Content;

namespace OptiCli.Cms.Projects;

/// <summary>Which of a project's items a request names (<see cref="OptiCli.Protocol.ProjectReadyRequest.Items"/>).</summary>
internal static class ProjectSelection
{
    /// <summary>
    /// The refs as project items: a content ref is every item of that content (in <paramref name="language"/>, if given), a
    /// version ref the item of that version. In the project's order, each once.
    /// </summary>
    /// <param name="items">The project's items.</param>
    /// <param name="refs">Each ref as given, and resolved (<see cref="ContentLocator.Resolve"/>).</param>
    /// <param name="project">The project, for the message.</param>
    /// <exception cref="AgentException"><c>not_found</c> for a ref that names no item, naming every such ref.</exception>
    public static IReadOnlyList<LoadedItem> Select(IReadOnlyList<LoadedItem> items, IReadOnlyList<(string Given, ContentReference Link)> refs, CultureInfo? language, string project)
    {
        var chosen = new HashSet<LoadedItem>();
        var missing = new List<string>();
        foreach (var (given, link) in refs)
        {
            var matches = items.Where(i => Names(i, link, language)).ToList();
            if (matches.Count == 0)
            {
                missing.Add(given.Trim());
            }
            chosen.UnionWith(matches);
        }
        if (missing.Count > 0)
        {
            var languageText = language is null ? "" : $" in '{language.Name}'";
            throw AgentException.NotFound(
                $"{(missing.Count == 1 ? $"{missing[0]} is" : $"{string.Join(", ", missing)} are")} not in project {project}{languageText}.",
                "Give content (or versions) the project has; read the project for its items. Nothing was changed.");
        }
        return items.Where(chosen.Contains).ToList();
    }

    private static bool Names(LoadedItem item, ContentReference link, CultureInfo? language) =>
        item.Item.ContentLink.ID == link.ID
        && string.Equals(item.Item.ContentLink.ProviderName ?? "", link.ProviderName ?? "", StringComparison.OrdinalIgnoreCase)
        && (link.WorkID == 0 || item.Item.ContentLink.WorkID == link.WorkID)
        && (language is null || ProjectContext.SameLanguage(item.Item.Language, language));
}
