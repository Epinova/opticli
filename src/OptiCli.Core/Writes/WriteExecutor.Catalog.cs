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
    /// <exception cref="NotFoundException">A catalog-only ref on a site whose Commerce agent doesn't run.</exception>
    /// <param name="fresh">Ask the site even when the ref was found before in this run (for languages an earlier step may have added).</param>
    internal async Task<CatalogRef?> CatalogRefAsync(string reference, CancellationToken cancellationToken, bool fresh = false)
    {
        var key = reference.Trim();
        if (!fresh && _catalogRefs.TryGetValue(key, out var known))
        {
            return known;
        }
        var commerceOnly = CatalogRefSyntax.IsCommerceOnly(key) || CatalogRefSyntax.ProviderId(key) is not null;
        if (!commerceOnly && !HasCatalogTypes)
        {
            // A GUID the CMS database doesn't have, on a site without a catalog model: not catalog content, and no need
            // to ask a site (which may not run: the GUID is then simply not found, as before).
            return null;
        }
        CatalogRefsResponse response;
        try
        {
            response = await (await AgentAsync(cancellationToken)).SendAsync<CatalogRefsResponse>(HttpMethod.Post, AgentRoutes.CommerceRefs, new CatalogRefsRequest([key]), cancellationToken);
        }
        catch (OptiCliException ex) when (!commerceOnly && ex is NotFoundException or UnreachableException)
        {
            // No Commerce agent, or no site running: the GUID is as not found as the CMS database says.
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

    /// <summary>
    /// Property values that are catalog refs only the Commerce agent resolves (<c>code:</c>, <c>catalog:</c>,
    /// <c>catalog-root</c>) in reference properties (ContentReference, PageReference, ContentArea, reference lists), as their
    /// <c>id__CatalogContent</c> ref, which the site takes. Text properties are left as they are, whatever they say.
    /// </summary>
    /// <exception cref="NotFoundException">A catalog ref that names nothing.</exception>
    private async Task<System.Text.Json.Nodes.JsonObject?> CatalogValuesAsync(int typeId, System.Text.Json.Nodes.JsonObject? properties, CancellationToken cancellationToken)
    {
        if (properties is null || !properties.Any(p => HasCatalogRef(p.Value)))
        {
            return properties;
        }
        var references = session.Model.PropertiesOf(typeId).Where(IsReference).Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var copy = (System.Text.Json.Nodes.JsonObject)properties.DeepClone();
        foreach (var name in copy.Select(p => p.Key).Where(references.Contains).ToList())
        {
            copy[name] = await MapCatalogRefsAsync(copy[name], name, cancellationToken);
        }
        return copy;
    }

    private static bool IsReference(Content.PropertyDefinition property) =>
        property.BaseType is Content.PropertyBaseType.ContentReference or Content.PropertyBaseType.PageReference
        || property.TypeName.Contains("ContentArea", StringComparison.Ordinal) || property.TypeName.Contains("ContentReference", StringComparison.Ordinal);

    private static bool HasCatalogRef(System.Text.Json.Nodes.JsonNode? node) => node switch
    {
        System.Text.Json.Nodes.JsonValue value => value.TryGetValue<string>(out var text) && CatalogRefSyntax.IsCommerceOnly(text),
        System.Text.Json.Nodes.JsonArray array => array.Any(HasCatalogRef),
        System.Text.Json.Nodes.JsonObject obj => obj.Any(p => HasCatalogRef(p.Value)),
        _ => false,
    };

    private async Task<System.Text.Json.Nodes.JsonNode?> MapCatalogRefsAsync(System.Text.Json.Nodes.JsonNode? node, string property, CancellationToken cancellationToken)
    {
        switch (node)
        {
            case System.Text.Json.Nodes.JsonValue value when value.TryGetValue<string>(out var text) && CatalogRefSyntax.IsCommerceOnly(text):
                return System.Text.Json.Nodes.JsonValue.Create(await CatalogAsync(text, $"the value of {property}", cancellationToken));
            case System.Text.Json.Nodes.JsonArray array:
                var items = new System.Text.Json.Nodes.JsonArray();
                foreach (var item in array.ToList())
                {
                    items.Add(await MapCatalogRefsAsync(item?.DeepClone(), property, cancellationToken));
                }
                return items;
            case System.Text.Json.Nodes.JsonObject obj:
                var mapped = new System.Text.Json.Nodes.JsonObject();
                foreach (var (key, child) in obj.ToList())
                {
                    mapped[key] = await MapCatalogRefsAsync(child?.DeepClone(), property, cancellationToken);
                }
                return mapped;
            default:
                return node?.DeepClone();
        }
    }

    /// <summary>Whether the content model has catalog types: a Commerce site (a CMS-only site's database has none).</summary>
    internal bool HasCatalogTypes => _hasCatalogTypes ??= session.Model.Types.Any(t => CatalogKinds.FromBase(t.Base) is not null);

    private bool? _hasCatalogTypes;

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

    /// <summary>The Commerce agent doesn't answer: <c>not_found</c>, as the <c>commerce</c> commands report it.</summary>
    private static NotFoundException NoCommerceAgent(string? reference) => new(
        (reference is null ? "This needs" : $"'{reference}' is a catalog ref, which needs") + " the site's Commerce agent, and it doesn't answer: the site has no Optimizely Commerce, or opticli's Commerce agent didn't load.",
        "`opticli serve --status` shows commerce (agent false: error says why); restart the site with `opticli serve --stop`, then `opticli serve`, after a build.");

    /// <summary>
    /// A block's or media's place, which can't be catalog content: Commerce gives catalog content no "For this page" folder
    /// (the CMS says it "isn't IResourceable"), and media and blocks don't go in a catalog. Product images are uploaded to a
    /// media folder and attached as the item's CommerceMediaCollection.
    /// </summary>
    /// <exception cref="UsageException">It is catalog content.</exception>
    private static Target NotCatalog(Target target, string reference, string what) =>
        target.Catalog is not { } catalog ? target : throw new UsageException(
            $"{reference} is catalog content ({catalog.Kind}): blocks and media don't go {(what == "for" ? "in a \"For this page\" folder of it" : "below it")}, which catalog content doesn't have.",
            "Upload an image to a media folder instead (opticli upload <file> --parent <folder>, e.g. the global assets), and attach it to the item with CommerceMediaCollection: opticli set <catalog-ref> --values '{\"CommerceMediaCollection\": [\"<media ref>\"]}'. Nothing was saved.");

    private static Target CatalogTarget(CatalogRef catalog) => new(0, null, catalog.Ref, null, null, catalog);

    private static NotFoundException CatalogNotFound(string reference, string what) => new(
        $"No catalog content '{reference.Trim()}' ({what}).",
        $"A catalog ref is {CatalogRefSyntax.Description}. Codes and catalog names are the same in every database; the id before __ is this database's.");

    private static UsageException CatalogOnlyCreateSetAndTranslate(string reference) => new(
        $"{reference} is catalog content (Optimizely Commerce): opticli changes it with create, set and translate ({CatalogOptions}), and its relations, prices and stock with the commerce commands.",
        "Publish a draft with `opticli set <ref> --publish` and the change; move, delete, translate --remove and the rest aren't supported for catalog content: use Commerce's catalog UI.");

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
    /// <param name="allowNothing">A request without changes is sent as it is (translate's look at an existing branch, as a dry run).</param>
    private async Task<WriteOutcome<WriteOutput>> CatalogSetAsync(SetOperation op, Target target, CatalogRef catalog, bool dryRun, CancellationToken cancellationToken, bool allowNothing = false)
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
        var lang = session.Language(op.Lang)?.Code;
        catalog = await RequireCatalogLanguageAsync(catalog, lang, op.Ref, cancellationToken);
        RequireSharedName(catalog, op.Name, lang, op.Ref);
        var type = session.Model.RequireType(catalog.Type ?? throw new InternalException($"The site gave catalog content {catalog.Ref} no type."));
        var properties = PropertyNameCheck.Prepare(session.Model, type.Id, await CatalogValuesAsync(type.Id, op.Properties, cancellationToken));
        var request = new DraftRequest
        {
            Lang = lang,
            Name = op.Name,
            Properties = PropertyArguments.ToRequest(properties),
            Publish = op.Publish,
            IncludeDraft = op.IncludeDraft,
            DryRun = dryRun,
            BaseVersion = op.Force ? null : op.BaseVersion,
        };
        if (request.Properties is null && request.Name is null && !op.Publish && !allowNothing)
        {
            throw new UsageException("Nothing to set.", $"Give properties ({PropertyArguments.Syntax}), --values, --name or --publish.");
        }
        var result = await PostDraftAsync(AgentRoutes.Draft(target.ContentRef), request, cancellationToken);
        return Outcome(WriteOutput.From(result), null);
    }

    /// <summary>
    /// <c>translate</c> of catalog content. Commerce 15 makes a language branch of it as the CMS does, through the site
    /// agent's language route. Commerce 14 keeps catalog content in every language of its catalog from the start (it saves
    /// a copy of its own in each; the agent's <c>PendingDrafts</c> leaves it out), so the branch is there already, and the
    /// translation is saved into it as <c>set --lang</c> saves it (<c>existing: true</c>); so is a translation into a
    /// branch made earlier, on either major.
    /// </summary>
    private async Task<WriteOutcome<WriteOutput>> CatalogTranslateAsync(TranslateOperation op, Target target, CatalogRef catalog, bool dryRun, CancellationToken cancellationToken)
    {
        var unsupported = new List<string>();
        if (op.WithBlocks)
        {
            unsupported.Add("--with-blocks (catalog content has no \"For this page\" folder)");
        }
        AddCmsOnly(op, unsupported);
        if (unsupported.Count > 0)
        {
            throw new UsageException($"{op.Ref} is catalog content (Optimizely Commerce), which doesn't take {string.Join(", ", unsupported)}.",
                "translate on catalog content takes properties, --lang and --publish. Nothing was saved.");
        }
        var language = session.Language(op.Lang) ?? throw new UsageException("translate needs --lang <code>.");
        // The languages it has now: an earlier step (or run, in the same session) may have made the branch.
        catalog = await CatalogRefAsync(catalog.Ref!, cancellationToken, fresh: true) ?? catalog;
        catalog = await RequireCatalogLanguageAsync(catalog, language.Code, op.Ref, cancellationToken);
        RequireSharedName(catalog, op.Name, language.Code, op.Ref);
        if (catalog.Languages?.Contains(language.Code, StringComparer.OrdinalIgnoreCase) == true)
        {
            // With nothing to save, what the branch has: from a dry run, which saves nothing.
            var nothing = op.Properties is null && op.Name is null && !op.Publish;
            var set = await CatalogSetAsync(new SetOperation(op.Ref, op.Properties, op.Name, language.Code, op.Publish, Force: true) { IncludeDraft = op.IncludeDraft },
                target, catalog, dryRun || nothing, cancellationToken, allowNothing: nothing);
            return set with { Output = set.Output with { Existing = true, DryRun = dryRun } };
        }
        var type = session.Model.RequireType(catalog.Type ?? throw new InternalException($"The site gave catalog content {catalog.Ref} no type."));
        var properties = PropertyNameCheck.Prepare(session.Model, type.Id, await CatalogValuesAsync(type.Id, op.Properties, cancellationToken));
        var result = await PostAsync<WriteResult>(AgentRoutes.Languages(target.ContentRef), new LanguageBranchRequest
        {
            Lang = language.Code,
            Properties = PropertyArguments.ToRequest(properties),
            Publish = op.Publish,
            DryRun = dryRun,
        }, cancellationToken);
        return Outcome(WriteOutput.From(result), null);
    }

    /// <summary>
    /// Catalog content is kept only in the languages of its catalog (its default language and <c>CatalogLanguages</c>).
    /// In another, Commerce 14 has no branch to save into, Commerce 15 saves a version it then can't load, and a
    /// <c>create</c> is saved in the catalog's default language instead.
    /// </summary>
    /// <param name="item">The content written, or for <c>create</c> its parent.</param>
    /// <returns><paramref name="item"/>, as the site has it now when the language was added since it was found.</returns>
    /// <exception cref="UsageException">The language isn't one of the catalog's.</exception>
    private async Task<CatalogRef> RequireCatalogLanguageAsync(CatalogRef item, string? lang, string reference, CancellationToken cancellationToken)
    {
        if (!HasLanguage(item, lang))
        {
            // Found earlier in this run (a plan's dry run, say): an earlier step may have given the catalog the language.
            item = await CatalogRefAsync(item.Ref!, cancellationToken, fresh: true) ?? item;
        }
        if (HasLanguage(item, lang))
        {
            return item;
        }
        var languages = item.CatalogLanguages!;
        var isCatalog = item.Kind == CatalogKinds.Catalog;
        var wanted = string.Join(", ", languages.Append(lang).Select(l => $"\"{l}\""));
        throw new UsageException(
            $"{reference} is {(isCatalog ? "a catalog" : "in a catalog")} whose languages are {string.Join(", ", languages)}: Commerce keeps catalog content only in its catalog's languages (its default language and CatalogLanguages), so nothing can be saved in '{lang}'.",
            $"Add the language to the catalog first: opticli set {(isCatalog ? reference : "catalog:<name>")} --values '{{\"CatalogLanguages\": [{wanted}]}}' (`opticli commerce children` lists the catalogs). Nothing was saved.");
    }

    private static bool HasLanguage(CatalogRef item, string? lang) =>
        lang is null || item.CatalogLanguages is not { Count: > 0 } languages || languages.Contains(lang, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Commerce keeps one name for catalog content, the master language's: a name saved in another language is dropped
    /// (the content still loads with the master's, on Commerce 14 and 15). The name shown in a language is its DisplayName.
    /// </summary>
    /// <exception cref="UsageException">A name other than the master language's, in another language.</exception>
    private static void RequireSharedName(CatalogRef catalog, string? name, string? lang, string reference)
    {
        if (name is null || lang is null || catalog.MasterLanguage is not { } master || lang.Equals(master, StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, catalog.Name, StringComparison.Ordinal))
        {
            return;
        }
        throw new UsageException(
            $"{reference} is catalog content, whose name Commerce keeps once for every language (the master language's, '{catalog.Name}'): a name given in '{lang}' would be dropped. The name shown in a language is its DisplayName.",
            $"Give DisplayName instead (opticli set {reference} --lang {lang} DisplayName=...), or --name without --lang to rename it. Nothing was saved.");
    }
}
