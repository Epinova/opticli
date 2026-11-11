using System.Globalization;
using EPiServer.DataAbstraction;
using OptiCli.Agent.Hosting;
using OptiCli.Agent.Http;
using OptiCli.Cms;
using OptiCli.Protocol;

namespace OptiCli.Agent.Languages;

/// <summary>
/// <c>POST /v1/languages</c>: enables or disables language branches through <c>ILanguageBranchRepository</c>, as admin mode's
/// Manage Website Languages does, which clears the CMS's cache. A code the database has no branch for gets one: a fresh
/// database has a handful (en and sv enabled), so <c>nb</c> or <c>pl</c> need one.
/// </summary>
/// <remarks>Here and not in <c>OptiCli.Cms</c>, which the MCP module compiles in: language branches are the developer's only.</remarks>
internal static class LanguageBranchesOperation
{
    public static LanguageBranchesResult Run(AgentRequest request, LanguageBranchesRequest body)
    {
        if (request.Service<AgentSettings>().SharedDatabase)
        {
            throw AgentException.Refused(LanguageBranchesRequest.SharedRefusal, LanguageBranchesRequest.SharedHint);
        }
        if (body.Codes is not { Count: > 0 })
        {
            throw AgentException.Usage("Give at least one language code.");
        }
        var repository = request.Service<ILanguageBranchRepository>();
        var all = repository.ListAll();
        var planned = new List<(CultureInfo Culture, LanguageBranch? Branch, string Status)>();
        foreach (var code in body.Codes.Select(c => c.Trim()).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var culture = Culture(code);
            var branch = all.FirstOrDefault(b => string.Equals(b.LanguageID, culture.Name, StringComparison.OrdinalIgnoreCase));
            var status = branch is null
                ? body.Enabled ? LanguageBranchStatus.Created : LanguageBranchStatus.Unchanged
                : branch.Enabled == body.Enabled ? LanguageBranchStatus.Unchanged
                : body.Enabled ? LanguageBranchStatus.Enabled : LanguageBranchStatus.Disabled;
            planned.Add((culture, branch, status));
        }

        var warnings = new List<string>();
        foreach (var missing in planned.Where(p => p.Branch is null && !body.Enabled))
        {
            warnings.Add($"The database has no '{missing.Culture.Name}' branch, so there is nothing to disable.");
        }
        if (!body.Enabled)
        {
            var disabling = planned.Where(p => p.Status == LanguageBranchStatus.Disabled).Select(p => p.Branch!.LanguageID).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (disabling.Count > 0 && all.Where(b => b.Enabled && !string.IsNullOrEmpty(b.LanguageID)).All(b => disabling.Contains(b.LanguageID)))
            {
                throw AgentException.Refused("That would disable every enabled language, and the CMS needs one.", "Enable another language first.");
            }
        }

        var saved = false;
        if (!body.DryRun)
        {
            foreach (var (culture, branch, status) in planned.Where(p => p.Status != LanguageBranchStatus.Unchanged))
            {
                // A new branch is enabled, and Everyone may edit it: the CMS's defaults, as admin mode's Add does.
                var writable = branch?.CreateWritableClone() ?? new LanguageBranch(culture);
                writable.Enabled = body.Enabled;
                repository.Save(writable);
                saved = true;
            }
        }
        return new LanguageBranchesResult
        {
            Languages = planned.Select(p => new LanguageBranchChange(
                p.Branch?.LanguageID ?? p.Culture.Name,
                p.Status,
                p.Status == LanguageBranchStatus.Unchanged ? p.Branch?.Enabled ?? false : body.Enabled)).ToList(),
            DryRun = body.DryRun,
            Saved = saved,
            Warnings = warnings.Count > 0 ? warnings : null,
        };
    }

    /// <exception cref="AgentException"><c>usage</c>: not a culture .NET knows.</exception>
    private static CultureInfo Culture(string code)
    {
        try
        {
            var culture = CultureInfo.GetCultureInfo(code, predefinedOnly: true);
            if (culture.Equals(CultureInfo.InvariantCulture))
            {
                throw new CultureNotFoundException();
            }
            return culture;
        }
        catch (CultureNotFoundException)
        {
            throw AgentException.Usage($"'{code}' is not a language code .NET knows.", "Give a culture name such as no, nb, sv or en-GB.");
        }
    }
}
