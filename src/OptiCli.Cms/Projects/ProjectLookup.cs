using System.Globalization;
using EPiServer.DataAbstraction;

namespace OptiCli.Cms.Projects;

/// <summary>
/// Which project a request names: by its id, or by its exact name. The CMS doesn't keep names unique, so a name is
/// taken to mean the one project that isn't published with it (opticli refuses to make a second one); failing that the
/// only project that has it at all. A name more than one candidate has is a <c>usage</c> error listing their ids.
/// </summary>
internal static class ProjectLookup
{
    /// <param name="projects">Every project (<c>ProjectRepository.List()</c>).</param>
    /// <param name="byId">The project with an id, when <paramref name="reference"/> is a number (<c>ProjectRepository.Get</c>).</param>
    /// <param name="reference">An id or a name, as the request gives it.</param>
    /// <exception cref="AgentException"><c>usage</c> for an empty or ambiguous name; <c>not_found</c>.</exception>
    public static Project Find(IEnumerable<Project> projects, Func<int, Project?> byId, string? reference)
    {
        var name = reference?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            throw AgentException.Usage("Name the project: its id or its name.");
        }
        // An id first: a project named "12" is still found by its name when no project has that id.
        if (int.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0 && byId(id) is { } found)
        {
            return found;
        }

        var all = projects.ToList();
        var named = all.Where(p => string.Equals(p.Name?.Trim(), name, StringComparison.OrdinalIgnoreCase)).ToList();
        var candidates = named.Count > 1 ? named.Where(p => p.Status != ProjectStatus.Published).ToList() : named;
        if (candidates.Count == 0)
        {
            candidates = named;
        }
        return candidates.Count switch
        {
            1 => candidates[0],
            0 => throw NotFound(name, all),
            _ => throw AgentException.Usage(
                $"{candidates.Count} projects are named '{name}': {string.Join(", ", candidates.OrderBy(p => p.ID).Select(Describe))}.",
                "Name the project by its id."),
        };
    }

    /// <summary>"12 ('Spring campaign', active)".</summary>
    public static string Describe(Project project) =>
        $"{project.ID.ToString(CultureInfo.InvariantCulture)} ('{project.Name}', {ProjectRules.StatusName(project.Status)})";

    /// <summary>A name an unpublished project has already: the conflict <c>projects create</c> refuses.</summary>
    public static Project? Taken(IEnumerable<Project> projects, string name) =>
        projects.Where(p => p.Status != ProjectStatus.Published && string.Equals(p.Name?.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p.ID)
            .FirstOrDefault();

    private static AgentException NotFound(string name, IReadOnlyList<Project> all)
    {
        var close = OptiCli.Core.Text.Suggestions.Closest(name, all.Where(p => p.Status != ProjectStatus.Published).Select(p => p.Name ?? ""));
        return AgentException.NotFound(
            int.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out _) ? $"No project with id or name '{name}'." : $"No project named '{name}'.",
            close.Count > 0 ? $"Did you mean {string.Join(", ", close.Select(c => $"'{c}'"))}?" : null);
    }
}
