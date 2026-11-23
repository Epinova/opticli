using OptiCli.Core.Errors;
using OptiCli.Protocol;

namespace OptiCli.Core.Writes;

/// <summary>
/// Optimizely Commerce's catalog content: <c>IContent</c> that Commerce's content provider serves from its own database,
/// not the CMS's. The site agent's content routes write it as they write CMS content; what differs is here. Its refs
/// (<see cref="CatalogRefSyntax"/>) are resolved by the site's Commerce agent, and it is passed to the site agent as
/// <c>id__CatalogContent</c>. <c>create</c> and <c>set</c> take it (properties, name, language, publish); the CMS-only
/// parts (ContentAreas, Visual Builder, projects, approvals, schedules, <c>--from</c>) don't apply to it.
/// </summary>
public sealed partial class WriteExecutor
{
    /// <summary>Catalog refs found so far in this run, by the ref as given: content found once stays found.</summary>
    private readonly Dictionary<string, CatalogRef> _catalogRefs = new(StringComparer.OrdinalIgnoreCase);

    private CommerceInfo? _commerce;

    /// <summary>The CMS-only options catalog content doesn't take, for messages.</summary>
    internal const string CatalogOptions = "properties, --name, --lang, --publish and --base-version";

    /// <summary>
    /// The catalog content a ref names, through the site's Commerce agent: <c>code:</c>, <c>catalog:</c> and
    /// <c>id__CatalogContent</c> refs, and GUIDs the CMS database doesn't have. Null when it names none, and for a GUID on
    /// a site without the Commerce agent.
    /// </summary>
    /// <exception cref="UsageException">A catalog-only ref on a site whose Commerce agent doesn't run.</exception>
    internal async Task<CatalogRef?> CatalogRefAsync(string reference, CancellationToken cancellationToken)
    {
        var key = reference.Trim();
        if (_catalogRefs.TryGetValue(key, out var known))
        {
            return known;
        }
        CatalogRefsResponse response;
        try
        {
            response = await (await AgentAsync(cancellationToken)).SendAsync<CatalogRefsResponse>(HttpMethod.Post, AgentRoutes.CommerceRefs, new CatalogRefsRequest([key]), cancellationToken);
        }
        catch (NotFoundException) when (!CatalogRefSyntax.IsCommerceOnly(key) && CatalogRefSyntax.ProviderId(key) is null)
        {
            // No Commerce agent: a GUID the CMS database doesn't have isn't catalog content either.
            return null;
        }
        catch (NotFoundException)
        {
            throw NoCommerceAgent(key);
        }
        if (response.Refs is not [{ Found: true } found])
        {
            return null;
        }
        _catalogRefs[key] = found;
        return found;
    }

    /// <summary>
    /// Several refs at once (a plan's GUIDs), by the ref as given: those that name catalog content. Empty on a site without
    /// the Commerce agent.
    /// </summary>
    internal async Task<IReadOnlyDictionary<string, CatalogRef>> CatalogRefsAsync(IEnumerable<string> references, CancellationToken cancellationToken)
    {
        var asked = references.Select(r => r.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var found = new Dictionary<string, CatalogRef>(StringComparer.OrdinalIgnoreCase);
        foreach (var batch in asked.Chunk(500))
        {
            CatalogRefsResponse response;
            try
            {
                response = await (await AgentAsync(cancellationToken)).SendAsync<CatalogRefsResponse>(HttpMethod.Post, AgentRoutes.CommerceRefs, new CatalogRefsRequest(batch), cancellationToken);
            }
            catch (NotFoundException)
            {
                return found;
            }
            foreach (var item in response.Refs.Where(r => r.Found))
            {
                found[item.Input] = _catalogRefs[item.Input] = item;
            }
        }
        return found;
    }

    /// <summary>Whether a type name (or GUID) is one of the content model's catalog types (Optimizely Commerce).</summary>
    internal bool IsCatalogType(string? nameOrGuid) =>
        !string.IsNullOrWhiteSpace(nameOrGuid) && TryType(nameOrGuid) is { } type && CatalogKinds.FromBase(type.Base) is not null;

    /// <summary>The site's Commerce (version, catalog root, catalogs), asked once per run.</summary>
    internal async Task<CommerceInfo> CommerceAsync(CancellationToken cancellationToken)
    {
        try
        {
            return _commerce ??= await (await AgentAsync(cancellationToken)).SendAsync<CommerceInfo>(HttpMethod.Get, AgentRoutes.Commerce, null, cancellationToken);
        }
        catch (NotFoundException)
        {
            throw NoCommerceAgent(null);
        }
    }

    private static UsageException NoCommerceAgent(string? reference) => new(
        (reference is null ? "This needs" : $"'{reference}' is a catalog ref, which needs") + " the site's Commerce agent, and it doesn't answer: the site has no Optimizely Commerce, or opticli's Commerce agent didn't load.",
        "`opticli serve --status` shows commerce (agent false: error says why); restart the site with `opticli serve --stop`, then `opticli serve`, after a build.");

    private static Target CatalogTarget(CatalogRef catalog) => new(0, null, catalog.Ref, null, null, catalog);

    private static NotFoundException CatalogNotFound(string reference, string what) => new(
        $"No catalog content '{reference.Trim()}' ({what}).",
        $"A catalog ref is {CatalogRefSyntax.Description}. Codes and catalog names are the same in every database; the id before __ is this database's.");

    private static UsageException CatalogOnlyCreateAndSet(string reference) => new(
        $"{reference} is catalog content (Optimizely Commerce): opticli changes it with create and set ({CatalogOptions}), and its relations, prices and stock with the commerce commands.",
        "Publish a draft with `opticli set <ref> --publish` and the change; move, delete, translate and the rest aren't supported for catalog content: use Commerce's catalog UI.");

    /// <summary>
    /// <c>create</c> of catalog content: what catalog content doesn't take is refused before anything is sent, and a plan's
    /// own project is left out. Other content is returned as it is.
    /// </summary>
    private CreateOperation CatalogCreate(CreateOperation op)
    {
        if (string.IsNullOrWhiteSpace(op.Type) || TryType(op.Type) is not { } type || CatalogKinds.FromBase(type.Base) is not { } kind)
        {
            return op;
        }
        var unsupported = new List<string>();
        if (op.For is not null)
        {
            unsupported.Add("for (catalog content has no \"For this page\" folder)");
        }
        if (op.Blueprint is not null)
        {
            unsupported.Add("blueprint");
        }
        AddCmsOnly(op, unsupported);
        if (unsupported.Count > 0)
        {
            throw new UsageException($"{type.Name} is a catalog type (Optimizely Commerce, a {kind}), which doesn't take {string.Join(", ", unsupported)}.",
                $"Catalog content takes parent, type, name, properties, lang, publish and guid. Nothing was saved.");
        }
        if (op.ProjectFromPlan)
        {
            // The plan's project is for its CMS content; catalog content is saved without it, as a folder is.
            op = op with { Project = null, Ready = false, ProjectFromPlan = false };
        }
        return op;
    }

    /// <summary>The type a name or GUID names; null when none does (the CMS path says so).</summary>
    private Cms.ContentTypeInfo? TryType(string nameOrGuid)
    {
        try
        {
            return session.Model.RequireType(nameOrGuid);
        }
        catch (OptiCliException)
        {
            return null;
        }
    }

    /// <summary>What a step for catalog content can't take: projects, approvals, schedules (a plan's own project aside).</summary>
    private static void AddCmsOnly(WriteOperation op, List<string> unsupported)
    {
        if ((op.Project is not null && !op.ProjectFromPlan) || (op.Ready && !op.ProjectFromPlan))
        {
            unsupported.Add("a project");
        }
        if (op.RequestApproval)
        {
            unsupported.Add("requestApproval");
        }
        if (op.PublishAt is not null)
        {
            unsupported.Add("publishAt");
        }
    }

    /// <summary><c>set</c> on catalog content: a new version with the properties and name, through the site agent's draft route.</summary>
    private async Task<WriteOutcome<WriteOutput>> CatalogSetAsync(SetOperation op, Target target, CatalogRef catalog, bool dryRun, CancellationToken cancellationToken)
    {
        var unsupported = new List<string>();
        if (op.From is not null)
        {
            unsupported.Add("--from");
        }
        if (op.Variation is not null)
        {
            unsupported.Add("--variation");
        }
        if (op.AreaEdits is not null || op.CompositionEdits is not null || op.WithBlocks is not null || op.WithoutBlocks is not null)
        {
            unsupported.Add("area or composition edits");
        }
        AddCmsOnly(op, unsupported);
        if (unsupported.Count > 0)
        {
            throw new UsageException($"{op.Ref} is catalog content (Optimizely Commerce), which doesn't take {string.Join(", ", unsupported)}.",
                $"set on catalog content takes {CatalogOptions}. Nothing was saved.");
        }
        var type = session.Model.RequireType(catalog.Type ?? throw new InternalException($"The site gave catalog content {catalog.Ref} no type."));
        var properties = PropertyNameCheck.Prepare(session.Model, type.Id, op.Properties);
        var request = new DraftRequest
        {
            Lang = session.Language(op.Lang)?.Code,
            Name = op.Name,
            Properties = PropertyArguments.ToRequest(properties),
            Publish = op.Publish,
            IncludeDraft = op.IncludeDraft,
            DryRun = dryRun,
            BaseVersion = op.Force ? null : op.BaseVersion,
        };
        if (request.Properties is null && request.Name is null && !op.Publish)
        {
            throw new UsageException("Nothing to set.", $"Give properties ({PropertyArguments.Syntax}), --values, --name or --publish.");
        }
        var result = await PostDraftAsync(AgentRoutes.Draft(target.ContentRef), request, cancellationToken);
        return Outcome(WriteOutput.From(result), null);
    }
}
