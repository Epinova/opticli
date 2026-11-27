using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using OptiCli.Core.Errors;
using OptiCli.Protocol;

namespace OptiCli.Core.Writes;

/// <param name="Index">Zero-based position in the plan.</param>
/// <param name="DependsOn">Plan ids (without <c>$</c>) of earlier steps whose content this step refers to.</param>
public sealed record PlanStep(int Index, WriteOperation Operation, IReadOnlySet<string> DependsOn);

/// <summary>
/// An <c>opticli apply</c> plan: <c>{"operations": [{"op": "create", "id": "page", ...}, {"op": "area", "ref": "$page", ...}]}</c>.
/// </summary>
/// <remarks>
/// <para>Each operation has the fields of the matching command (see <see cref="Fields"/>). A create, block or
/// upload step may have an <c>id</c>; later steps refer to the content it creates as <c>"$id"</c>, in ref
/// fields and as a whole string value inside <c>properties</c>. So may an area add with a <c>type</c>, for the block it
/// makes where the site makes new blocks in a ContentArea shared ones (<see cref="PlanBlocks"/>); an inline block has no
/// ref.</para>
/// <para>Parsing checks the whole shape at once and reports every problem: unknown ops and fields, missing
/// required fields, wrong JSON types, duplicate ids, and <c>$id</c>s that aren't defined by an earlier step.</para>
/// </remarks>
public sealed partial class WritePlan
{
    /// <summary>Allowed fields per op; required ones end with <c>*</c>.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> Fields = new Dictionary<string, string[]>
    {
        ["set"] = ["ref*", "properties", "name", "lang", "variation", "publish", "publishAt", "includeDraft", "requestApproval", "from", "baseVersion", "force", "project", "ready"],
        ["create"] = ["parent", "for", "type", "blueprint", "name*", "properties", "lang", "publish", "publishAt", "includeDraft", "requestApproval", "id", "guid", "project", "ready"],
        ["area"] = ["ref*", "property*", "action*", "item", "type", "values", "name", "index", "at", "to", "display", "lang", "publish", "publishAt", "includeDraft", "requestApproval", "from", "baseVersion", "force", "project", "ready", "id"],
        ["composition"] = ["ref*", "action*", "node", "nodeType", "in", "at", "value", "lang", "variation", "publish", "publishAt", "includeDraft", "requestApproval", "from", "baseVersion", "force", "project", "ready"],
        ["block"] = ["type*", "name*", "for", "parent", "properties", "lang", "publish", "includeDraft", "requestApproval", "id", "guid", "project", "ready"],
        ["upload"] = ["file*", "for", "parent", "replace", "name", "type", "properties", "publish", "includeDraft", "requestApproval", "id", "guid", "project", "ready"],
        ["translate"] = ["ref*", "lang*", "name", "properties", "publish", "includeDraft", "requestApproval", "withBlocks", "remove", "confirm", "project", "ready"],
        ["publish"] = ["ref*", "version", "lang", "publishAt", "includeDraft", "requestApproval", "withBlocks", "withoutBlocks"],
        ["unpublish"] = ["ref*", "lang"],
        ["discard"] = ["ref*", "version", "lang", "includeDraft"],
        ["move"] = ["ref*", "to*"],
        ["delete"] = ["ref*", "ignoreReferences"],
        ["restore"] = ["ref*", "to"],
        ["access"] = ["ref*", "grant", "grantUsers", "revoke", "breakInheritance", "inherit", "allowUnknownRole"],
        ["site"] = ["name*", "startPage*", "hosts*", "https", "siteAssets"],
        ["language"] = ["lang*", "enabled"],
        // Optimizely Commerce (the site's Commerce agent).
        ["relation"] = ["parent*", "child*", "remove", "allowDestructive", "quantity", "sortOrder"],
        ["price"] = ["entry*", "market*", "currency*", "amount", "priceType", "priceCode", "minQuantity", "validFrom", "validUntil", "remove", "allowDestructive"],
        ["warehouse"] = ["code*", "name", "active", "primary", "fulfillmentCenter", "pickupLocation", "deliveryLocation", "sortOrder"],
        ["inventory"] = ["entry*", "warehouse*", "inStock", "backorder", "preorder", "tracked"],
        ["market"] = ["market*", "name", "enabled", "currencies", "defaultCurrency", "languages", "defaultLanguage", "countries", "description", "pricesIncludeTax", "remove", "allowDestructive"],
    };

    /// <summary>
    /// What <c>export</c> says about where a plan came from (database, time, versions): <c>apply</c> accepts it and doesn't
    /// read it.
    /// </summary>
    public const string SourceField = "source";

    /// <summary>Ops that create content, and so can have an <c>id</c> and a <c>guid</c>.</summary>
    public static readonly IReadOnlySet<string> CreatingOps = new HashSet<string>(StringComparer.Ordinal) { "create", "block", "upload" };

    /// <summary>The plan's own field that puts every save step's draft in a project (<see cref="Project"/>).</summary>
    public const string ProjectField = "project";

    private WritePlan(IReadOnlyList<PlanStep> steps, Guid? guidNamespace, string? project)
    {
        Steps = steps;
        GuidNamespace = guidNamespace;
        Project = project;
    }

    public IReadOnlyList<PlanStep> Steps { get; }

    /// <summary>The plan's <c>guidNamespace</c>: every creating step's GUID is derived from it and the step's <c>id</c>.</summary>
    public Guid? GuidNamespace { get; }

    /// <summary>
    /// The plan's <c>project</c> (an id or a name): every set, create, area, composition, block, upload and translate step
    /// saves its draft in it, unless the step names another (its own <c>project</c>) or none (<c>"project": null</c>). A
    /// name no unpublished project has is created when the plan runs. Plans never publish a project.
    /// </summary>
    public string? Project { get; }

    /// <exception cref="UsageException">The plan is malformed; <c>details.problems</c> lists every problem.</exception>
    public static WritePlan Parse(string json)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        }
        catch (JsonException ex)
        {
            throw new UsageException($"The plan is not valid JSON: {ex.Message}");
        }
        if (root is not JsonObject { } plan || plan["operations"] is not JsonArray { Count: > 0 } operations
            || plan.Any(p => p.Key is not ("operations" or "guidNamespace" or ProjectField or SourceField)))
        {
            throw new UsageException("""A plan is an object with "operations", a non-empty array, and optionally "guidNamespace", "project" (and "source", which `export` writes).""", PlanHint);
        }

        var problems = new List<string>();
        if (plan.ContainsKey(SourceField) && plan[SourceField] is not JsonObject)
        {
            problems.Add("\"source\" must be an object (export writes where the plan came from there; apply doesn't read it).");
        }
        Guid? guidNamespace = null;
        if (plan["guidNamespace"] is { } namespaceNode)
        {
            guidNamespace = namespaceNode is JsonValue namespaceValue && namespaceValue.TryGetValue<string>(out var text) && Guid.TryParse(text, out var parsedNamespace) && parsedNamespace != Guid.Empty
                ? parsedNamespace
                : null;
            if (guidNamespace is null)
            {
                problems.Add("\"guidNamespace\" must be a GUID (make one with `uuidgen`, once per plan, and keep it).");
            }
        }
        string? planProject = null;
        if (plan.ContainsKey(ProjectField))
        {
            planProject = ProjectName(plan[ProjectField]);
            if (planProject is null)
            {
                problems.Add("\"project\" must be a project's name, or its id (a string or a number).");
            }
        }
        var parsed = new List<(WriteOperation? Op, int Index)>();
        for (var i = 0; i < operations.Count; i++)
        {
            parsed.Add((ParseStep(operations[i], i, guidNamespace, planProject, problems), i));
        }

        foreach (var duplicate in parsed.Where(p => p.Op?.ContentGuid is not null).GroupBy(p => p.Op!.ContentGuid).Where(g => g.Count() > 1))
        {
            problems.Add($"operations[{string.Join(", ", duplicate.Select(d => d.Index))}] all have GUID {duplicate.Key:D}.");
        }

        var steps = new List<PlanStep>();
        var defined = new Dictionary<string, int>(StringComparer.Ordinal);
        var allIds = parsed.Where(p => p.Op?.Id is not null).Select(p => p.Op!.Id!).ToHashSet(StringComparer.Ordinal);
        foreach (var (op, index) in parsed)
        {
            if (op is null)
            {
                continue;
            }
            var dependsOn = Dependencies(op, index, defined, allIds, problems);
            if (op.Id is { } id)
            {
                if (!defined.TryAdd(id, index))
                {
                    problems.Add($"operations[{index}]: id '{id}' is already used by operations[{defined[id]}].");
                }
            }
            steps.Add(new PlanStep(index, op, dependsOn));
        }

        if (problems.Count > 0)
        {
            throw new UsageException($"The plan has {problems.Count} problem(s): {string.Join(" ", problems)}", PlanHint) { Details = new { problems } };
        }
        return new WritePlan(steps, guidNamespace, planProject);
    }

    /// <summary>A <c>project</c> value: a name, or an id as a number; null when it is neither (or empty).</summary>
    private static string? ProjectName(JsonNode? node) => node switch
    {
        JsonValue value when value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) => text.Trim(),
        JsonValue value when value.TryGetValue<int>(out var id) && id > 0 => id.ToString(CultureInfo.InvariantCulture),
        _ => null,
    };

    public const string PlanHint =
        """Example: {"operations": [{"op": "create", "id": "page", "parent": "123", "type": "ArticlePage", "name": "News"}, {"op": "set", "ref": "$page", "properties": {"Heading": "Hello"}}]}. Run `opticli apply --help` for every op's fields.""";

    /// <summary>
    /// The step's operation with <c>$id</c>s replaced by the ids of the content created for them, and those in rich text
    /// (<see cref="TextRefs"/>) by what the CMS stores, once the id or GUID they need is known.
    /// </summary>
    /// <param name="guids">The GUID of each plan id: fixed by the plan, or of content created so far.</param>
    /// <param name="refs">The ref of each plan id whose content the CMS doesn't store (catalog content: <c>1__CatalogContent</c>).</param>
    public static WriteOperation Resolve(PlanStep step, IReadOnlyDictionary<string, int> created, IReadOnlyDictionary<string, Guid>? guids = null, IReadOnlyDictionary<string, string>? refs = null) =>
        step.Operation.MapRefs(value =>
            value.StartsWith('$') && created.TryGetValue(value[1..], out var id)
                ? id.ToString(CultureInfo.InvariantCulture)
                : value.StartsWith('$') && refs is not null && refs.TryGetValue(value[1..], out var reference)
                ? reference
                : TextRefs.Map(value, found => TextRefs.Value(
                    found.Attribute,
                    created.TryGetValue(found.Id, out var contentId) ? contentId : null,
                    guids is not null && guids.TryGetValue(found.Id, out var guid) ? guid : null)));

    /// <summary>The GUIDs a plan fixes for its content (a step's <c>guid</c>, or from <c>guidNamespace</c>), by plan id.</summary>
    public static Dictionary<string, Guid> FixedGuids(IEnumerable<PlanStep> steps) => steps
        .Where(s => s.Operation is { Id: not null, ContentGuid: not null })
        .ToDictionary(s => s.Operation.Id!, s => s.Operation.ContentGuid!.Value, StringComparer.Ordinal);

    /// <summary>
    /// The <c>$id</c>s in rich text (<see cref="TextRefs"/>), once <c>@file</c> values have been read: an earlier step's
    /// content becomes a dependency; a later step's (or this one's) only works where its GUID is fixed in advance and
    /// the GUID is all the text needs.
    /// </summary>
    /// <exception cref="UsageException">References that can't work; <c>details.problems</c> lists every one.</exception>
    public static IReadOnlyList<PlanStep> LinkText(IReadOnlyList<PlanStep> steps)
    {
        var creating = steps.Where(s => s.Operation.Id is not null).GroupBy(s => s.Operation.Id!).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var problems = new List<string>();
        var linked = new List<PlanStep>();
        foreach (var step in steps)
        {
            var dependsOn = new HashSet<string>(step.DependsOn, StringComparer.Ordinal);
            var where = $"operations[{step.Index}]";
            step.Operation.MapRefs(value =>
            {
                foreach (var found in TextRefs.Find(value).Distinct())
                {
                    var reference = $"{found.Attribute}=\"${found.Id}\"";
                    if (!creating.TryGetValue(found.Id, out var target))
                    {
                        problems.Add($"{where}: {reference} in text is not the id of a create, block, upload or area add operation.");
                    }
                    else if (target.Index < step.Index)
                    {
                        dependsOn.Add(found.Id);
                    }
                    else if (!TextRefs.NeedsOnlyGuid(found.Attribute))
                    {
                        problems.Add($"{where}: {reference} needs the content's id, which only exists once operations[{target.Index}] has run; move that operation before this one.");
                    }
                    else if (target.Operation.ContentGuid is null)
                    {
                        problems.Add($"{where}: the text links to '${found.Id}', which {(target.Index == step.Index ? "this operation" : $"a later operation (operations[{target.Index}])")} creates. Its GUID is only known in advance with a \"guidNamespace\" on the plan (or a \"guid\" on that operation): add one, or move that operation before this one.");
                    }
                }
                return value;
            });
            linked.Add(step with { DependsOn = dependsOn });
        }
        return problems.Count == 0
            ? linked
            : throw new UsageException($"The plan has {problems.Count} problem(s): {string.Join(" ", problems)}", PlanHint) { Details = new { problems } };
    }

    /// <summary>
    /// On CMS 13, which refuses to save a link to content that doesn't exist (its reference validation): rich text that
    /// links to content this step or a later one creates. With <c>--update-existing</c>, content that exists already counts
    /// as existing.
    /// </summary>
    /// <exception cref="UsageException">Every such link, in <c>details.problems</c>.</exception>
    public static void RequireNoForwardLinks(IReadOnlyList<PlanStep> steps, IReadOnlyDictionary<string, int> existing)
    {
        var creating = steps.Where(s => s.Operation.Id is not null).GroupBy(s => s.Operation.Id!).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var problems = new List<string>();
        foreach (var step in steps)
        {
            step.Operation.MapRefs(value =>
            {
                foreach (var found in TextRefs.Find(value).Distinct())
                {
                    if (creating.TryGetValue(found.Id, out var target) && target.Index >= step.Index && !existing.ContainsKey(found.Id))
                    {
                        problems.Add($"operations[{step.Index}]: the text links to '${found.Id}', which {(target.Index == step.Index ? "this operation" : $"a later operation (operations[{target.Index}])")} creates.");
                    }
                }
                return value;
            });
        }
        if (problems.Count > 0)
        {
            throw new UsageException(
                $"CMS 13 refuses to save a link to content that doesn't exist yet, and the plan has {problems.Count} such link(s): {string.Join(" ", problems)}",
                "Move the operation that creates the linked content before the one that links to it. For links both ways, create both first and add one of the links with a later set operation.")
            {
                Details = new { problems },
            };
        }
    }

    private static IReadOnlySet<string> Dependencies(WriteOperation op, int index, IReadOnlyDictionary<string, int> defined, IReadOnlySet<string> allIds, List<string> problems)
    {
        var dependsOn = new HashSet<string>(StringComparer.Ordinal);
        var refFields = op.Refs.OfType<string>().Where(r => r.StartsWith('$')).ToHashSet(StringComparer.Ordinal);
        foreach (var reference in refFields)
        {
            var id = reference[1..];
            if (defined.ContainsKey(id))
            {
                dependsOn.Add(id);
            }
            else
            {
                problems.Add(allIds.Contains(id)
                    ? $"operations[{index}]: '{reference}' is created by a later operation; move that one before it."
                    : $"operations[{index}]: '{reference}' is not the id of an earlier create, block, upload or area add operation.");
            }
        }

        // Inside property values only exact "$id" strings of plan ids count; anything else is text.
        op.MapRefs(value =>
        {
            if (value.StartsWith('$') && !refFields.Contains(value) && allIds.Contains(value[1..]) && !dependsOn.Contains(value[1..]))
            {
                if (defined.ContainsKey(value[1..]))
                {
                    dependsOn.Add(value[1..]);
                }
                else
                {
                    problems.Add($"operations[{index}]: '{value}' is created by a later operation (or by this one).");
                }
            }
            return value;
        });
        return dependsOn;
    }

    private static WriteOperation? ParseStep(JsonNode? node, int index, Guid? guidNamespace, string? planProject, List<string> problems)
    {
        var where = $"operations[{index}]";
        if (node is not JsonObject step)
        {
            problems.Add($"{where}: must be an object.");
            return null;
        }
        if (step["op"] is not JsonValue opValue || !opValue.TryGetValue<string>(out var op) || !Fields.TryGetValue(op, out var fields))
        {
            problems.Add($"{where}: \"op\" must be one of {string.Join(", ", Fields.Keys)}.");
            return null;
        }

        var reader = new StepReader(step, $"{where} ({op})", problems);
        var names = fields.Select(f => f.TrimEnd('*')).Append("op").ToHashSet(StringComparer.Ordinal);
        foreach (var key in step.Select(p => p.Key).Where(k => !names.Contains(k)))
        {
            problems.Add($"{reader.Where}: unknown field \"{key}\" (allowed: {string.Join(", ", names.Where(n => n != "op"))}).");
        }
        foreach (var required in fields.Where(f => f.EndsWith('*')).Select(f => f.TrimEnd('*')))
        {
            if (step[required] is null)
            {
                problems.Add($"{reader.Where}: \"{required}\" is required.");
            }
        }

        var id = reader.String("id");
        if (id is not null && !IdPattern().IsMatch(id))
        {
            problems.Add($"{reader.Where}: id '{id}' must start with a letter and contain only letters, digits, '_' and '-'.");
        }

        WriteOperation operation = op switch
        {
            "set" => new SetOperation(reader.Ref("ref"), reader.Object("properties"), reader.String("name"), reader.String("lang"), reader.Bool("publish"), reader.Int("baseVersion"), reader.Bool("force"))
            {
                From = reader.From("from"),
                Variation = reader.String("variation"),
            },
            "create" => new CreateOperation(reader.OptionalRef("parent"), reader.String("type") ?? "", reader.String("name") ?? "", reader.Object("properties"), reader.String("lang"), reader.Bool("publish"))
            {
                Blueprint = reader.OptionalRef("blueprint"),
                For = reader.OptionalRef("for"),
            },
            "composition" => new CompositionEdit(reader.Ref("ref"), reader.String("action") ?? "", reader.String("node"), reader.String("nodeType"), reader.String("in"), reader.Int("at"),
                reader.Object("value"), reader.String("lang"), reader.Bool("publish"), reader.Int("baseVersion"), reader.Bool("force"))
            {
                From = reader.From("from"),
                Variation = reader.String("variation"),
            },
            "area" => new AreaEdit(reader.Ref("ref"), reader.String("property") ?? "", reader.String("action") ?? "", reader.OptionalRef("item"), reader.Int("index"), reader.Int("at"), reader.Int("to"), reader.String("display"), reader.String("lang"), reader.Bool("publish"), reader.Int("baseVersion"), reader.Bool("force"))
            {
                From = reader.From("from"),
                Type = reader.String("type"),
                Values = reader.Object("values"),
                Name = reader.String("name"),
            },
            "block" => new BlockCreateOperation(reader.String("type") ?? "", reader.String("name") ?? "", reader.OptionalRef("for"), reader.OptionalRef("parent"), reader.Object("properties"), reader.String("lang"), reader.Bool("publish")),
            "upload" => new UploadOperation(reader.String("file") ?? "", reader.OptionalRef("for"), reader.OptionalRef("parent"), reader.String("name"), reader.String("type"), reader.Object("properties"), reader.Bool("publish"))
            {
                Replace = reader.OptionalRef("replace"),
            },
            "translate" => new TranslateOperation(reader.Ref("ref"), reader.String("lang") ?? "", reader.String("name"), reader.Object("properties"), reader.Bool("publish"))
            {
                WithBlocks = reader.Bool("withBlocks"),
                Remove = reader.Bool("remove"),
                Confirm = reader.Bool("confirm"),
            },
            "publish" => new PublishOperation(reader.Ref("ref"), reader.Int("version"), reader.String("lang"))
            {
                WithBlocks = reader.RefList("withBlocks"),
                WithoutBlocks = reader.RefList("withoutBlocks"),
            },
            "unpublish" => new UnpublishOperation(reader.Ref("ref"), reader.String("lang")),
            "discard" => new DiscardOperation(reader.Ref("ref"), reader.Int("version"), reader.String("lang")),
            "move" => new MoveOperation(reader.Ref("ref"), reader.Ref("to")),
            "restore" => new RestoreOperation(reader.Ref("ref"), reader.OptionalRef("to")),
            "access" => new AccessOperation(reader.Ref("ref"), reader.Levels("grant"), reader.Levels("grantUsers"), reader.Strings("revoke"),
                reader.Bool("breakInheritance"), reader.Bool("inherit"), reader.Bool("allowUnknownRole")),
            "site" => new SiteOperation(reader.String("name") ?? "", reader.Ref("startPage"),
                step["hosts"] is null ? [] : Sites.SiteHostSpecs.FromJson(step["hosts"], reader.Where, problems), reader.OptionalBool("https"),
                SiteAssets(reader.String("siteAssets"), reader.Where, problems)),
            "language" => new LanguageOperation(reader.Strings("lang") ?? [], reader.OptionalBool("enabled") ?? true),
            "relation" => new RelationOperation(reader.Ref("parent"), reader.Ref("child"), reader.Bool("remove"))
            {
                Quantity = reader.Decimal("quantity"),
                SortOrder = reader.Int("sortOrder"),
                AllowDestructive = reader.Bool("allowDestructive"),
            },
            "price" => new PriceOperation(reader.Ref("entry"), reader.String("market") ?? "", reader.String("currency") ?? "", reader.Decimal("amount"))
            {
                PriceType = reader.String("priceType"),
                PriceCode = reader.String("priceCode"),
                MinQuantity = reader.Decimal("minQuantity"),
                ValidFrom = reader.Time("validFrom"),
                ValidUntil = reader.Time("validUntil"),
                Remove = reader.Bool("remove"),
                AllowDestructive = reader.Bool("allowDestructive"),
            },
            "warehouse" => new WarehouseOperation(reader.String("code") ?? "")
            {
                Name = reader.String("name"),
                Active = reader.OptionalBool("active"),
                Primary = reader.OptionalBool("primary"),
                FulfillmentCenter = reader.OptionalBool("fulfillmentCenter"),
                PickupLocation = reader.OptionalBool("pickupLocation"),
                DeliveryLocation = reader.OptionalBool("deliveryLocation"),
                SortOrder = reader.Int("sortOrder"),
            },
            "inventory" => new InventoryOperation(reader.Ref("entry"), reader.String("warehouse") ?? "")
            {
                InStock = reader.Decimal("inStock"),
                Backorder = reader.Decimal("backorder"),
                Preorder = reader.Decimal("preorder"),
                Tracked = reader.OptionalBool("tracked"),
            },
            "market" => new MarketOperation(reader.String("market") ?? "")
            {
                Name = reader.String("name"),
                Enabled = reader.OptionalBool("enabled"),
                Currencies = reader.Strings("currencies"),
                DefaultCurrency = reader.String("defaultCurrency"),
                Languages = reader.Strings("languages"),
                DefaultLanguage = reader.String("defaultLanguage"),
                Countries = reader.Strings("countries"),
                Description = reader.String("description"),
                PricesIncludeTax = reader.OptionalBool("pricesIncludeTax"),
                Remove = reader.Bool("remove"),
                AllowDestructive = reader.Bool("allowDestructive"),
            },
            _ => new DeleteOperation(reader.Ref("ref"), reader.Bool("ignoreReferences")),
        };

        if (operation is AreaEdit area && area.Action is not ("add" or "remove" or "move" or "set") && step["action"] is not null)
        {
            problems.Add($"{reader.Where}: \"action\" must be add, remove, move or set.");
        }
        if (operation is AreaEdit { Action: "set" } setting && (setting.Index is null || setting.Item is not null || setting.Type is not null || (setting.Values is null && setting.Name is null)))
        {
            problems.Add($"{reader.Where}: \"action\": \"set\" takes the inline block's \"index\" and \"values\" or \"name\" (no \"item\" or \"type\").");
        }
        else if (operation is AreaEdit inline && inline.Action != "set" && (inline.Type is not null || inline.Values is not null || inline.Name is not null))
        {
            if (inline.Action != "add")
            {
                problems.Add($"{reader.Where}: \"type\", \"values\" and \"name\" are for adding (or setting) an inline block; remove and move name one by \"index\".");
            }
            else if (inline.Type is null)
            {
                problems.Add($"{reader.Where}: \"values\" and \"name\" are a new inline block's: give its \"type\" too.");
            }
            else if (inline.Item is not null)
            {
                problems.Add($"{reader.Where}: give \"item\" (a shared block) or \"type\" (a new inline block), not both.");
            }
        }
        if (operation is AreaEdit && id is not null && (operation is not AreaEdit { Action: "add", Type: not null } || step["item"] is not null))
        {
            problems.Add($"{reader.Where}: \"id\" names the block an add with \"type\" makes (a shared block, where the site's edit UI makes those), for later operations to reach as '${id}'; other area operations make nothing to name.");
        }
        if (operation is CompositionEdit edit && CompositionEdits.Problem(edit) is { } compositionProblem && step["action"] is not null)
        {
            problems.Add($"{reader.Where}: {compositionProblem}");
        }
        if (operation is CreateOperation { Type.Length: 0, Blueprint: null } && step["type"] is null)
        {
            problems.Add($"{reader.Where}: \"type\" is required (or \"blueprint\", whose type it then is).");
        }
        var (based, from) = operation switch
        {
            SetOperation set => (set.Ref, set.From),
            AreaEdit areaEdit => (areaEdit.Ref, areaEdit.From),
            CompositionEdit compositionEdit => (compositionEdit.Ref, compositionEdit.From),
            _ => ("", null),
        };
        if (from is not null && based.StartsWith('$'))
        {
            problems.Add($"{reader.Where}: \"from\" bases a change on a version of existing content, and '{based}' is created by the plan; leave \"from\" out.");
        }
        if (operation is AccessOperation access)
        {
            foreach (var (name, levels) in (access.Grant ?? new Dictionary<string, string>()).Concat(access.GrantUsers ?? new Dictionary<string, string>()))
            {
                if (!AccessLevels.TryParse(levels, out _, out var error))
                {
                    problems.Add($"{reader.Where}: {name}: {error}");
                }
            }
        }
        if (operation is UploadOperation { Replace: not null } replacing)
        {
            if (replacing.For is not null || replacing.Parent is not null || replacing.Type is not null || step["id"] is not null || step["guid"] is not null)
            {
                problems.Add($"{reader.Where}: \"replace\" replaces the file of existing media; it takes no \"for\", \"parent\", \"type\", \"id\" or \"guid\".");
            }
        }
        else if (operation is CreateOperation create && (create.For is null) == (create.Parent is null))
        {
            problems.Add($"{reader.Where}: give exactly one of \"parent\" and \"for\" (a folder in that content's \"For this page\" folder).");
        }
        else if ((operation is BlockCreateOperation block && (block.For is null) == (block.Parent is null))
            || (operation is UploadOperation upload && (upload.For is null) == (upload.Parent is null)))
        {
            problems.Add($"{reader.Where}: give exactly one of \"for\" and \"parent\" (or, for an upload, \"replace\").");
        }
        // The step's own project, none ("project": null), or the plan's for the ops that save a version.
        string? project;
        var fromPlan = false;
        if (step.ContainsKey(ProjectField))
        {
            project = step[ProjectField] is null ? null : ProjectName(step[ProjectField]);
            if (step[ProjectField] is not null && project is null)
            {
                problems.Add($"{reader.Where}: \"project\" must be a project's name, its id (a string or a number), or null for none.");
            }
        }
        else
        {
            fromPlan = planProject is not null && WriteOperation.ProjectOps.Contains(op) && operation is not TranslateOperation { Remove: true };
            project = fromPlan ? planProject : null;
        }
        operation = operation with
        {
            Id = id,
            ContentGuid = operation is UploadOperation { Replace: not null } ? null : ContentGuid(reader, op, id, guidNamespace),
            IncludeDraft = reader.Bool("includeDraft"),
            RequestApproval = reader.Bool("requestApproval"),
            PublishAt = reader.Time("publishAt"),
            Project = project,
            Ready = reader.Bool("ready"),
            ProjectFromPlan = fromPlan,
        };
        if (step[ProjectField] is not null || fromPlan || operation.Ready)
        {
            if (operation.ProjectProblem("\"publish\", \"publishAt\"") is { } projectProblem)
            {
                problems.Add($"{reader.Where}: {projectProblem}{(fromPlan ? " (The plan's \"project\" applies to this step: give it \"project\": null to save it without one.)" : "")}");
            }
        }
        return operation;
    }

    /// <summary>A <c>site</c> step's <c>siteAssets</c>, lower case; a problem when it is neither own nor global.</summary>
    private static string? SiteAssets(string? value, string where, List<string> problems)
    {
        if (value is null)
        {
            return null;
        }
        if (SiteAssetsModes.Parse(value) is { } mode)
        {
            return mode;
        }
        problems.Add($"{where}: \"siteAssets\" must be {SiteAssetsModes.Own} or {SiteAssetsModes.Global}, not '{value}'.");
        return null;
    }

    /// <summary>
    /// The step's <c>guid</c>, else one derived from the plan's namespace and the step's id. Only for steps that make
    /// content: an upload that replaces a file (<c>replace</c>) keeps the media's own GUID, so it has neither.
    /// </summary>
    private static Guid? ContentGuid(StepReader reader, string op, string? id, Guid? guidNamespace)
    {
        if (!CreatingOps.Contains(op))
        {
            return null;
        }
        if (reader.String("guid") is { } text)
        {
            if (Guid.TryParse(text, out var guid) && guid != Guid.Empty)
            {
                return guid;
            }
            reader.Problem($"\"guid\" '{text}' is not a GUID.");
            return null;
        }
        if (guidNamespace is not { } ns)
        {
            return null;
        }
        if (id is null)
        {
            reader.Problem("needs an \"id\" (or a \"guid\"): with a guidNamespace, the id is what its GUID is made from, so a rerun finds it.");
            return null;
        }
        return StableGuids.Create(ns, id);
    }

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_-]*$")]
    private static partial Regex IdPattern();

    /// <summary>Typed field access that records type errors instead of throwing, so all problems are reported together.</summary>
    private sealed class StepReader(JsonObject step, string where, List<string> problems)
    {
        public string Where { get; } = where;

        public string? String(string name) => step[name] switch
        {
            null => null,
            JsonValue value when value.TryGetValue<string>(out var text) => text,
            _ => Problem<string>(name, "a string"),
        };

        /// <summary>Refs may be written as numbers (<c>123</c>) or strings (<c>"123"</c>, a GUID, a URL, <c>"$id"</c>).</summary>
        public string? OptionalRef(string name) => step[name] switch
        {
            null => null,
            JsonValue value when value.TryGetValue<string>(out var text) => text,
            JsonValue value when value.TryGetValue<int>(out var number) => number.ToString(CultureInfo.InvariantCulture),
            _ => Problem<string>(name, "a ref (number or string)"),
        };

        public string Ref(string name) => OptionalRef(name) ?? "";

        /// <summary>A list of refs, each as <see cref="OptionalRef"/> takes it: <c>[123, "456", "$intro"]</c>.</summary>
        public IReadOnlyList<string>? RefList(string name)
        {
            if (step[name] is null)
            {
                return null;
            }
            if (step[name] is not JsonArray array || array.Any(item => item is not JsonValue value || !(value.TryGetValue<string>(out _) || value.TryGetValue<int>(out _))))
            {
                return Problem<IReadOnlyList<string>>(name, "an array of refs (numbers or strings)");
            }
            return array.Select(item => item!.AsValue().TryGetValue<string>(out var text) ? text : item!.GetValue<int>().ToString(CultureInfo.InvariantCulture)).ToList();
        }

        /// <summary>What a change is based on: <c>"published"</c>, or a version as a number (<c>456</c>) or string (<c>"123_456"</c>).</summary>
        public FromVersion? From(string name) => step[name] switch
        {
            null => null,
            JsonValue value when value.TryGetValue<int>(out var number) && number > 0 => new FromVersion(number),
            JsonValue value when value.TryGetValue<string>(out var text) && FromVersion.TryParse(text, out var from) => from,
            _ => Problem<FromVersion>(name, FromVersion.Syntax),
        };

        /// <summary>A number, or a string that is one: <c>49.9</c>, <c>"49.90"</c>.</summary>
        public decimal? Decimal(string name) => step[name] switch
        {
            null => null,
            JsonValue value when value.TryGetValue<decimal>(out var number) => number,
            JsonValue value when value.TryGetValue<string>(out var text) && decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => Problem<decimal?>(name, "a number"),
        };

        public int? Int(string name) => step[name] switch
        {
            null => null,
            JsonValue value when value.TryGetValue<int>(out var number) => number,
            _ => Problem<int?>(name, "an integer"),
        };

        /// <summary>A time with an offset, or in UTC without one: <c>"2025-03-01T08:00:00Z"</c>.</summary>
        public DateTimeOffset? Time(string name) => step[name] switch
        {
            null => null,
            JsonValue value when value.TryGetValue<string>(out var text) && PublishTimes.TryParse(text, out var at) => at,
            _ => Problem<DateTimeOffset?>(name, "a date and time, e.g. \"2025-03-01T08:00:00Z\" (UTC unless it has an offset)"),
        };

        public bool Bool(string name) => step[name] switch
        {
            null => false,
            JsonValue value when value.TryGetValue<bool>(out var flag) => flag,
            _ => Problem<bool>(name, "true or false"),
        };

        public bool? OptionalBool(string name) => step[name] switch
        {
            null => null,
            JsonValue value when value.TryGetValue<bool>(out var flag) => flag,
            _ => Problem<bool?>(name, "true or false"),
        };

        /// <summary>Name to levels: <c>{"Authenticated": "Read"}</c>, or the levels as an array (<c>["Read", "Edit"]</c>).</summary>
        public IReadOnlyDictionary<string, string>? Levels(string name)
        {
            if (step[name] is null)
            {
                return null;
            }
            if (step[name] is not JsonObject obj)
            {
                return Problem<IReadOnlyDictionary<string, string>>(name, """an object of names to levels, e.g. {"Authenticated": "Read"}""");
            }
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, value) in obj)
            {
                string? levels = value switch
                {
                    JsonValue text when text.TryGetValue<string>(out var single) => single,
                    JsonArray array when array.All(a => a is JsonValue v && v.TryGetValue<string>(out _)) => string.Join(',', array.Select(a => a!.GetValue<string>())),
                    _ => null,
                };
                if (levels is null)
                {
                    problems.Add($"{Where}: \"{name}\".{key} must be levels as a string (\"Read,Edit\") or an array of strings.");
                    continue;
                }
                if (!result.TryAdd(key, levels))
                {
                    problems.Add($"{Where}: \"{name}\" names '{key}' more than once.");
                }
            }
            return result;
        }

        /// <summary>An array of strings, or one string.</summary>
        public IReadOnlyList<string>? Strings(string name) => step[name] switch
        {
            null => null,
            JsonValue value when value.TryGetValue<string>(out var single) => [single],
            JsonArray array when array.All(a => a is JsonValue v && v.TryGetValue<string>(out _)) => array.Select(a => a!.GetValue<string>()).ToList(),
            _ => Problem<IReadOnlyList<string>>(name, "an array of strings"),
        };

        public JsonObject? Object(string name) => step[name] switch
        {
            null => null,
            JsonObject obj => (JsonObject)obj.DeepClone(),
            _ => Problem<JsonObject>(name, "an object of property names to values"),
        };

        public void Problem(string message) => problems.Add($"{Where}: {message}");

        private T? Problem<T>(string name, string expected)
        {
            problems.Add($"{Where}: \"{name}\" must be {expected}.");
            return default;
        }
    }
}
