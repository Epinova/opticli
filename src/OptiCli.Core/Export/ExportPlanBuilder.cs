using System.Globalization;
using System.Text.Json.Nodes;
using OptiCli.Core.Cms;

namespace OptiCli.Core.Export;

/// <summary>A value an export left out, and why.</summary>
/// <param name="Language">The branch it is in; null for content without languages.</param>
/// <param name="Property">Where it is: <c>MainBody</c>, <c>Hero.Image</c>, <c>MainArea[2].Link</c>.</param>
public sealed record DroppedValue(Guid Guid, string? Language, string Property, string Reason);

/// <summary>Content the plan refers to but doesn't create: the target must have it.</summary>
/// <param name="From">The items that refer to it (by value, or as their parent or owner).</param>
/// <param name="Placement">Some item is placed under it (its parent, or the owner of its "For this page" folder).</param>
public sealed record ExternalReference(Guid Guid, IReadOnlyList<Guid> From, bool Placement);

/// <summary>
/// What the database a plan is for has, when the builder knows it (a sync knows its target): a value that refers to
/// content, a visitor group or a category it lacks is left out and listed in <see cref="ExportPlan.Dropped"/>, rather than
/// failing the plan. An export doesn't know where its plan goes and keeps them all.
/// </summary>
/// <param name="HasContent">The content exists there (outside the recycle bin or in it); content the plan creates always does.</param>
public sealed record ExportTarget(Func<Guid, bool> HasContent, Func<string, bool> HasVisitorGroup, Func<string, bool> HasCategory);

/// <param name="Operations">The plan's steps, in order.</param>
/// <param name="Texts">Long rich text, by its path relative to the export folder (<c>texts/…</c>), which the plan reads as <c>@file</c>.</param>
/// <param name="Skipped">Items left out: GUID and why.</param>
/// <param name="HeldBack">Values a later <c>set</c> writes because they refer to content created at or after their own step.</param>
/// <param name="VisitorGroups">Visitor group (and role) ids that personalized values show to.</param>
/// <param name="ProviderRefs">References to a content provider's content, kept as they are (<c>id__provider</c>).</param>
public sealed record ExportPlan(
    JsonArray Operations,
    IReadOnlyDictionary<string, string> Texts,
    IReadOnlyList<(Guid Guid, string Reason)> Skipped,
    IReadOnlyList<ExternalReference> ExternalRefs,
    IReadOnlyList<DroppedValue> Dropped,
    int HeldBack,
    IReadOnlyList<string> VisitorGroups,
    IReadOnlyList<string> ProviderRefs);

/// <summary>
/// Builds the plan an export writes: items in an order where parents (and the owners of "For this page" folders) come
/// first and, where the references allow it, content before what refers to it; each a <c>create</c>, <c>block</c> or
/// <c>upload</c> with its source GUID and every value except those that refer to content created by the same or a later
/// step; then a <c>set</c> per item with only those (a cycle, closed once both sides exist); then each other language
/// branch as a <c>translate</c> of its culture-specific values. A branch published in the source is published by the last
/// step that writes it. Pure: everything comes from the <see cref="ExportItem"/>s.
/// </summary>
public static class ExportPlanBuilder
{
    /// <summary>Rich text longer than this goes to a file of its own in <c>texts/</c>, so the plan stays readable.</summary>
    public const int TextFileLength = 4000;

    /// <summary>The plan id of an item: its GUID, so it can't be taken for a <c>$</c> in a text the source has.</summary>
    public static string PlanId(Guid guid) => $"c{guid:N}";

    /// <summary>Where an exported media item's file goes, relative to the export folder.</summary>
    public static string FilePath(Guid guid, string fileName) => $"files/{guid:N}/{fileName}";

    private const string CompositionField = Properties.Compositions.Field;

    /// <param name="target">What the plan's database has, when it is known (<see cref="ExportTarget"/>).</param>
    /// <param name="now">For a branch scheduled for publishing: only a time after this is carried over (default: now).</param>
    public static ExportPlan Build(IReadOnlyList<ExportItem> items, int textFileLength = TextFileLength, ExportTarget? target = null, DateTime? now = null)
    {
        var at = now ?? DateTime.UtcNow;
        var skipped = items.Where(i => i.Kind == ContentKind.Media && i.File is null)
            .Select(i => (i.Guid, "media without its file: its bytes weren't found (see --blobs), or --no-media"))
            .ToList();
        var planned = items.Where(i => !(i.Kind == ContentKind.Media && i.File is null)).ToList();
        var byGuid = planned.ToDictionary(i => i.Guid);
        var order = Order(planned, byGuid);
        var position = order.Select((item, i) => (item.Guid, i)).ToDictionary(p => p.Guid, p => p.i);

        var externals = new Dictionary<Guid, (HashSet<Guid> From, bool Placement)>();
        var dropped = new List<DroppedValue>();
        var groups = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var providers = new SortedSet<string>(StringComparer.Ordinal);
        var texts = new Dictionary<string, string>(StringComparer.Ordinal);
        var creates = new List<JsonObject>();
        var sets = new List<JsonObject>();
        var branches = new List<JsonObject>();
        var schedules = new List<JsonObject>();
        var heldBack = 0;

        for (var me = 0; me < order.Count; me++)
        {
            var item = order[me];
            var id = PlanId(item.Guid);
            var forward = false;
            var placement = false;

            string Ref(Guid guid)
            {
                if (position.TryGetValue(guid, out var at))
                {
                    forward |= at >= me && !placement;
                    return $"${PlanId(guid)}";
                }
                if (!placement && target is not null && !target.HasContent(guid))
                {
                    throw new ExportValues.DroppedValue($"it refers to {guid:D}, which the target doesn't have");
                }
                if (!externals.TryGetValue(guid, out var entry))
                {
                    entry = ([], false);
                }
                entry.From.Add(item.Guid);
                externals[guid] = (entry.From, entry.Placement || placement);
                return guid.ToString("D");
            }

            var master = item.Branches[0];
            var (values, later) = Values(item, master, isMaster: true, Ref, () => forward, f => forward = f, dropped, groups, providers, target);
            heldBack += later.Count;
            MoveTexts(item, master, values, texts, textFileLength);
            MoveTexts(item, master, later, texts, textFileLength);

            var step = new JsonObject { ["op"] = Op(item.Kind), ["id"] = id, ["guid"] = item.Guid.ToString("D") };
            placement = true;
            if (item.Kind == ContentKind.Media)
            {
                step["file"] = item.File;
            }
            if (item.Owner is { } owner && item.Kind is not ContentKind.Page and not ContentKind.Experience)
            {
                step["for"] = Ref(owner);
            }
            else if (item.Parent is { } parent)
            {
                step["parent"] = Ref(parent);
            }
            placement = false;
            step["type"] = item.Type;
            step["name"] = (string?)master.Document["name"] ?? item.Guid.ToString("D");
            if (item.Kind != ContentKind.Media && master.Language is { } language)
            {
                step["lang"] = language;
            }
            if (values.Count > 0)
            {
                step["properties"] = values;
            }
            creates.Add(step);

            var last = step;
            if (later.Count > 0)
            {
                last = new JsonObject { ["op"] = "set", ["ref"] = $"${id}" };
                if (master.Language is { } setLanguage)
                {
                    last["lang"] = setLanguage;
                }
                last["properties"] = later;
                sets.Add(last);
            }
            if (master.Published && item.Kind != ContentKind.Folder)
            {
                last["publish"] = true;
            }
            else if (Scheduled(master, at) is { } masterAt)
            {
                schedules.Add(Schedule(id, master.Language, masterAt));
            }

            foreach (var branch in item.Branches.Skip(1))
            {
                // Every item is created by now: nothing to hold back.
                var (branchValues, _) = Values(item, branch, isMaster: false, Ref, () => false, _ => { }, dropped, groups, providers, target);
                // translate takes no composition: a set on the new branch writes it.
                var composition = branchValues[CompositionField];
                branchValues.Remove(CompositionField);
                MoveTexts(item, branch, branchValues, texts, textFileLength);
                var translate = new JsonObject
                {
                    ["op"] = "translate",
                    ["ref"] = $"${id}",
                    ["lang"] = branch.Language,
                    ["name"] = (string?)branch.Document["name"],
                };
                if (branchValues.Count > 0)
                {
                    translate["properties"] = branchValues;
                }
                branches.Add(translate);
                var lastOfBranch = translate;
                if (composition is not null)
                {
                    lastOfBranch = new JsonObject
                    {
                        ["op"] = "set",
                        ["ref"] = $"${id}",
                        ["lang"] = branch.Language,
                        ["properties"] = new JsonObject { [CompositionField] = composition.DeepClone() },
                    };
                    branches.Add(lastOfBranch);
                }
                if (branch.Published)
                {
                    lastOfBranch["publish"] = true;
                }
                else if (Scheduled(branch, at) is { } branchAt)
                {
                    schedules.Add(Schedule(id, branch.Language, branchAt));
                }
            }
        }

        return new ExportPlan(
            new JsonArray(creates.Concat(sets).Concat(branches).Concat(schedules).Select(o => (JsonNode)o).ToArray()),
            texts,
            skipped,
            externals.Select(e => new ExternalReference(e.Key, e.Value.From.ToList(), e.Value.Placement)).ToList(),
            dropped,
            heldBack,
            groups.ToList(),
            providers.ToList());
    }

    /// <summary>
    /// When a branch that was never published is scheduled for publishing (its version waits for <c>DelayPublishUntil</c>,
    /// which get shows as its <c>startPublish</c>) at a time after <paramref name="now"/>: that time. A time that has passed
    /// (the source's scheduler hasn't run) leaves it a draft, as it is in the source.
    /// </summary>
    private static DateTime? Scheduled(ExportBranch branch, DateTime now) =>
        !branch.Published && (string?)branch.Document["status"] == "delayedPublish"
            && DateTime.TryParse((string?)branch.Document["startPublish"], CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var at)
            && at > now
            ? at
            : null;

    /// <summary>A publish of the branch's latest version at <paramref name="at"/>: the scheduled publish a create or translate can't say.</summary>
    private static JsonObject Schedule(string id, string? language, DateTime at)
    {
        var step = new JsonObject { ["op"] = "publish", ["ref"] = $"${id}" };
        if (language is not null)
        {
            step["lang"] = language;
        }
        step["publishAt"] = at.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", CultureInfo.InvariantCulture);
        return step;
    }

    /// <summary>
    /// Each branch of an item as a plan would write it, for comparing the same item in two databases (a sync's diff): its
    /// name, whether it is published, and its values with every reference by GUID. With <paramref name="target"/>, the values
    /// the plan would leave out are left out here too, and listed in <paramref name="dropped"/>.
    /// </summary>
    /// <returns>Per branch (by language, <c>*</c> for content without languages): <c>{name, published, values}</c>.</returns>
    public static IReadOnlyDictionary<string, JsonObject> Comparable(ExportItem item, ExportTarget? target = null, List<DroppedValue>? dropped = null)
    {
        var result = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        string Reference(Guid guid) => target is null || target.HasContent(guid)
            ? guid.ToString("D")
            : throw new ExportValues.DroppedValue($"it refers to {guid:D}, which the target doesn't have");
        foreach (var (branch, i) in item.Branches.Select((b, i) => (b, i)))
        {
            var forward = false;
            var (values, later) = Values(item, branch, i == 0, Reference, () => forward, f => forward = f, dropped ?? [], [], [], target);
            foreach (var (name, value) in later)
            {
                values[name] = value?.DeepClone();
            }
            result[branch.Language ?? "*"] = new JsonObject
            {
                ["name"] = (string?)branch.Document["name"],
                ["published"] = branch.Published,
                ["values"] = values,
            };
        }
        return result;
    }

    private static string Op(ContentKind kind) => kind switch
    {
        ContentKind.Media => "upload",
        _ when kind.IsBlock() => "block",
        _ => "create",
    };

    /// <summary>
    /// The branch's values as a plan writes them: its properties (all of them on the master branch, the culture-specific
    /// ones on another), its composition and the built-in settings; split into those its own step can write and those that
    /// refer to content created by that step or a later one (<paramref name="isForward"/>).
    /// </summary>
    private static (JsonObject Now, JsonObject Later) Values(
        ExportItem item, ExportBranch branch, bool isMaster, Func<Guid, string> reference, Func<bool> isForward, Action<bool> setForward,
        List<DroppedValue> dropped, SortedSet<string> groups, SortedSet<string> providers, ExportTarget? target = null)
    {
        var converter = new ExportValues(reference, target?.HasVisitorGroup);
        var now = new JsonObject();
        var later = new JsonObject();
        void Put(string name, JsonNode? value)
        {
            var target = isForward() ? later : now;
            target[name] = value;
            setForward(false);
        }

        var document = branch.Document;
        foreach (var (name, wrapped) in document["properties"] as JsonObject ?? [])
        {
            if (!isMaster && !item.CultureSpecific.Contains(name))
            {
                continue;
            }
            setForward(false);
            if (converter.Wrapped(wrapped, name, out var plain))
            {
                if (target is not null && (string?)wrapped?["type"] == "Category" && MissingCategory(plain, target) is { } missing)
                {
                    converter.Dropped.Add((name, $"the category '{missing}', which the target doesn't have"));
                    continue;
                }
                Put(name, Escape(plain));
            }
        }
        // Only a composition the branch stores (get says from which branch, culture). Never an empty one on another branch:
        // the target's branch is unpublished when the plan writes it (translate makes a draft), and saving such a branch with
        // an empty composition deletes the master branch's stored blocks on CMS 13.3 (the agent refuses it).
        if (document[CompositionField] is JsonObject { } composition && composition["culture"] is not null
            && (isMaster || item.CultureSpecific.Contains(CompositionField)))
        {
            setForward(false);
            JsonObject? written;
            try
            {
                written = converter.Composition(composition, CompositionField);
            }
            catch (ExportValues.DroppedValue ex)
            {
                // A shared block (or a personalized node) the target lacks: the composition is written whole or not at all.
                converter.Dropped.Add((CompositionField, ex.Message));
                written = null;
            }
            if (written is null)
            {
                setForward(false);
            }
            else if (isMaster || HasNodes(written))
            {
                Put(CompositionField, Escape(written));
            }
            else
            {
                converter.Dropped.Add((CompositionField, "an empty composition on a branch other than the master branch, which CMS 13 can't save without deleting the master branch's stored blocks"));
            }
        }
        foreach (var (name, value) in BuiltIns(item, branch, isMaster, converter))
        {
            if (target is not null && name == "Category" && MissingCategory(value, target) is { } missing)
            {
                converter.Dropped.Add((name, $"the category '{missing}', which the target doesn't have"));
                continue;
            }
            Put(name, value);
        }

        dropped.AddRange(converter.Dropped.Select(d => new DroppedValue(item.Guid, branch.Language, d.Property, d.Reason)));
        groups.UnionWith(converter.VisitorGroups);
        providers.UnionWith(converter.ProviderRefs);
        return (now, later);
    }

    /// <summary>The first category in a value (a list of names) that the target doesn't have; null when it has them all.</summary>
    private static string? MissingCategory(JsonNode? value, ExportTarget target) =>
        (value as JsonArray ?? []).Select(c => (string?)c).FirstOrDefault(c => c is not null && !target.HasCategory(c));

    /// <summary>Whether a composition (as a plan writes it) has a node: a section or element in an outline, a row in a grid.</summary>
    private static bool HasNodes(JsonObject composition) =>
        ((string[])["sections", "rows", "columns", "elements", "nodes"]).Any(list => composition[list] is JsonArray { Count: > 0 });

    /// <summary>
    /// The built-in settings as the source has them, under the names <c>set</c> takes: publish dates, a page's URL segment,
    /// simple address and shortcut, its menu visibility and sorting, and the built-in category (master branch only: they
    /// aren't culture-specific).
    /// </summary>
    private static IEnumerable<(string Name, JsonNode? Value)> BuiltIns(ExportItem item, ExportBranch branch, bool isMaster, ExportValues converter)
    {
        var document = branch.Document;
        if (item.Kind == ContentKind.Folder)
        {
            yield break;
        }
        if (Utc(document["startPublish"]) is { } start)
        {
            yield return ("StartPublish", start);
        }
        // Null clears a stop date the target has.
        yield return ("StopPublish", Utc(document["stopPublish"]));

        if (item.Kind.IsPage())
        {
            if (document["urlSegment"] is { } segment)
            {
                yield return ("PageURLSegment", segment.DeepClone());
            }
            // Not culture-specific: the CMS takes it on the master branch only.
            if (isMaster && document["visibleInMenu"] is { } visible)
            {
                yield return ("PageVisibleInMenu", visible.DeepClone());
            }
            yield return ("SimpleAddress", document["simpleAddress"]?.DeepClone());
            if (document["shortcut"] is not JsonObject shortcut)
            {
                yield return ("Shortcut", null);
            }
            else if (Shortcut(shortcut, converter) is { } written)
            {
                yield return ("Shortcut", written);
            }
            if (isMaster)
            {
                if ((string?)document["childSortOrder"] is { } order && ChildSortOrders.Contains(order))
                {
                    yield return ("ChildSortOrder", order);
                }
                if (document["sortIndex"] is { } sortIndex)
                {
                    yield return ("SortIndex", sortIndex.DeepClone());
                }
            }
        }

        // The built-in category is the master branch's unless a site makes it culture-specific, which the database
        // doesn't say, and the CMS refuses it on another branch otherwise: written with the master branch only.
        if (isMaster)
        {
            yield return ("Category", (document["category"] as JsonArray ?? []).DeepClone());
        }
    }

    /// <summary>The child orders <c>set</c> takes (<c>None</c> and <c>Rank</c> aren't).</summary>
    private static readonly HashSet<string> ChildSortOrders = new(StringComparer.OrdinalIgnoreCase)
    {
        "CreatedDescending", "CreatedAscending", "Alphabetical", "Index", "ChangedDescending", "PublishedAscending", "PublishedDescending",
    };

    /// <summary>
    /// A shortcut as <c>get</c> shows it, as <c>set</c> takes it: the page it points at by reference. Null (left out, and
    /// listed) when that page has no GUID.
    /// </summary>
    private static JsonObject? Shortcut(JsonObject shortcut, ExportValues converter)
    {
        var result = new JsonObject { ["type"] = shortcut["type"]?.DeepClone() };
        if (shortcut["to"] is { } to)
        {
            try
            {
                result["to"] = converter.Reference(to, "Shortcut.to");
            }
            catch (ExportValues.DroppedValue ex)
            {
                converter.Dropped.Add(("Shortcut", ex.Message));
                return null;
            }
        }
        else if (shortcut["url"] is { } url)
        {
            result["url"] = url.DeepClone();
        }
        foreach (var field in (string[])["anchor", "target"])
        {
            if (shortcut[field] is { } value)
            {
                result[field] = value.DeepClone();
            }
        }
        return result;
    }

    /// <summary>A date as the database stores it (UTC, which <c>get</c> shows without a zone), with its zone.</summary>
    private static JsonNode? Utc(JsonNode? value) =>
        (string?)value is { } text && DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var date)
            ? JsonValue.Create(date.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", CultureInfo.InvariantCulture))
            : null;

    /// <summary>
    /// A plan reads a string value that starts with <c>@</c> as a file: one the source has is written <c>@@</c>, a
    /// literal <c>@</c>.
    /// </summary>
    private static JsonNode? Escape(JsonNode? value)
    {
        switch (value)
        {
            case JsonValue leaf when leaf.TryGetValue<string>(out var text) && text.StartsWith('@'):
                return JsonValue.Create("@" + text);
            case JsonObject obj:
                foreach (var (key, child) in obj.ToList())
                {
                    obj[key] = Escape(child?.DeepClone());
                }
                return obj;
            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    array[i] = Escape(array[i]?.DeepClone());
                }
                return array;
            default:
                return value;
        }
    }

    /// <summary>Long rich text (top-level) into a file of its own in <c>texts/</c>, which the value then names as <c>@file</c>.</summary>
    private static void MoveTexts(ExportItem item, ExportBranch branch, JsonObject values, Dictionary<string, string> texts, int limit)
    {
        var wrapped = branch.Document["properties"] as JsonObject;
        foreach (var (name, value) in values.ToList())
        {
            if ((string?)wrapped?[name]?["type"] == "XhtmlString" && value is JsonValue leaf && leaf.TryGetValue<string>(out var text) && text.Length > limit)
            {
                // Escaped as a value: the file holds the text itself.
                var content = text.StartsWith("@@", StringComparison.Ordinal) ? text[1..] : text;
                var path = $"texts/{item.Guid:N}{(branch.Language is { } language ? $".{language}" : "")}.{name}.html";
                texts[path] = content;
                values[name] = $"@{path}";
            }
        }
    }

    /// <summary>
    /// Parents (and the owners of "For this page" folders) first, always; then, where the graph allows it, content before
    /// the content that refers to it; otherwise the source's tree order. Items whose references go round in a cycle are
    /// kept together, after everything the cycle refers to: within it the item earliest in the source's order goes first
    /// (once its parent is placed), and its references to the rest are held back for a later set.
    /// </summary>
    private static List<ExportItem> Order(IReadOnlyList<ExportItem> items, IReadOnlyDictionary<Guid, ExportItem> byGuid)
    {
        var hard = items.ToDictionary(i => i.Guid, i => new HashSet<Guid>(
            new[] { i.Parent, i.Owner }.OfType<Guid>().Where(g => g != i.Guid && byGuid.ContainsKey(g))));
        var soft = items.ToDictionary(i => i.Guid, i => References(i).Where(g => g != i.Guid && byGuid.ContainsKey(g) && !hard[i.Guid].Contains(g)).ToHashSet());
        var comparer = Comparer<ExportItem>.Create((a, b) => a.Order != b.Order ? a.Order.CompareTo(b.Order) : a.Guid.CompareTo(b.Guid));

        // Cycles (strongly connected components), in an order where each comes after the ones it refers to.
        var component = Components(items.Select(i => i.Guid).ToList(), items.ToDictionary(i => i.Guid, i => hard[i.Guid].Concat(soft[i.Guid]).ToList()));
        var members = items.GroupBy(i => component[i.Guid]).ToDictionary(g => g.Key, g => g.OrderBy(i => i, comparer).ToList());
        var dependsOn = members.Keys.ToDictionary(c => c, _ => new HashSet<int>());
        var dependents = members.Keys.ToDictionary(c => c, _ => new List<int>());
        foreach (var item in items)
        {
            foreach (var dependency in hard[item.Guid].Concat(soft[item.Guid]).Select(g => component[g]).Where(c => c != component[item.Guid]))
            {
                if (dependsOn[component[item.Guid]].Add(dependency))
                {
                    dependents[dependency].Add(component[item.Guid]);
                }
            }
        }
        var components = Comparer<int>.Create((a, b) => comparer.Compare(members[a][0], members[b][0]));
        var ready = new SortedSet<int>(members.Keys.Where(c => dependsOn[c].Count == 0), components);
        var result = new List<ExportItem>();
        while (ready.Count > 0)
        {
            var next = ready.Min;
            ready.Remove(next);
            result.AddRange(Within(members[next], hard, soft, comparer));
            foreach (var dependent in dependents[next])
            {
                if (dependsOn[dependent].Remove(next) && dependsOn[dependent].Count == 0)
                {
                    ready.Add(dependent);
                }
            }
        }
        return result;
    }

    /// <summary>
    /// The items of one cycle (or a single item) in order: parents first, content before what refers to it where it can;
    /// where nothing is free of references to the rest, the earliest item whose parent is placed.
    /// </summary>
    private static IEnumerable<ExportItem> Within(IReadOnlyList<ExportItem> items, Dictionary<Guid, HashSet<Guid>> hard, Dictionary<Guid, HashSet<Guid>> soft, IComparer<ExportItem> comparer)
    {
        if (items.Count == 1)
        {
            return items;
        }
        var inside = items.ToDictionary(i => i.Guid);
        var pendingHard = items.ToDictionary(i => i.Guid, i => hard[i.Guid].Count(inside.ContainsKey));
        var pendingSoft = items.ToDictionary(i => i.Guid, i => soft[i.Guid].Count(inside.ContainsKey));
        var ready = new SortedSet<ExportItem>(items.Where(i => pendingHard[i.Guid] == 0 && pendingSoft[i.Guid] == 0), comparer);
        var placeable = new SortedSet<ExportItem>(items.Where(i => pendingHard[i.Guid] == 0), comparer);
        var placed = new HashSet<Guid>();
        var result = new List<ExportItem>();
        while (result.Count < items.Count)
        {
            var next = ready.Count > 0 ? ready.Min! : placeable.Count > 0 ? placeable.Min! : items.Where(i => !placed.Contains(i.Guid)).Min(comparer)!;
            ready.Remove(next);
            placeable.Remove(next);
            placed.Add(next.Guid);
            result.Add(next);
            foreach (var item in items.Where(i => !placed.Contains(i.Guid)))
            {
                var wasPlaceable = pendingHard[item.Guid] == 0;
                if (hard[item.Guid].Contains(next.Guid))
                {
                    pendingHard[item.Guid]--;
                }
                if (soft[item.Guid].Contains(next.Guid))
                {
                    pendingSoft[item.Guid]--;
                }
                if (pendingHard[item.Guid] == 0)
                {
                    if (!wasPlaceable)
                    {
                        placeable.Add(item);
                    }
                    if (pendingSoft[item.Guid] == 0)
                    {
                        ready.Add(item);
                    }
                }
            }
        }
        return result;
    }

    /// <summary>The strongly connected components of the graph (Tarjan's algorithm, without recursion): item to component number.</summary>
    private static Dictionary<Guid, int> Components(IReadOnlyList<Guid> nodes, IReadOnlyDictionary<Guid, List<Guid>> edges)
    {
        var index = new Dictionary<Guid, int>();
        var low = new Dictionary<Guid, int>();
        var onStack = new HashSet<Guid>();
        var stack = new Stack<Guid>();
        var component = new Dictionary<Guid, int>();
        var next = 0;
        var count = 0;
        foreach (var start in nodes)
        {
            if (index.ContainsKey(start))
            {
                continue;
            }
            var work = new Stack<(Guid Node, int Edge)>();
            Visit(start);
            while (work.Count > 0)
            {
                var (node, edge) = work.Pop();
                if (edge < edges[node].Count)
                {
                    work.Push((node, edge + 1));
                    var to = edges[node][edge];
                    if (!index.ContainsKey(to))
                    {
                        Visit(to);
                    }
                    else if (onStack.Contains(to))
                    {
                        low[node] = Math.Min(low[node], index[to]);
                    }
                    continue;
                }
                if (low[node] == index[node])
                {
                    Guid member;
                    do
                    {
                        member = stack.Pop();
                        onStack.Remove(member);
                        component[member] = count;
                    }
                    while (member != node);
                    count++;
                }
                if (work.Count > 0)
                {
                    var parent = work.Peek().Node;
                    low[parent] = Math.Min(low[parent], low[node]);
                }
            }

            void Visit(Guid node)
            {
                index[node] = low[node] = next++;
                stack.Push(node);
                onStack.Add(node);
                work.Push((node, 0));
            }
        }
        return component;
    }

    /// <summary>Every content GUID the items' values refer to (not where they are placed).</summary>
    public static IReadOnlySet<Guid> Referenced(IEnumerable<ExportItem> items) => items.SelectMany(References).ToHashSet();

    /// <summary>The content an item's values refer to, in every branch it writes.</summary>
    private static HashSet<Guid> References(ExportItem item)
    {
        var found = new HashSet<Guid>();
        var forward = false;
        foreach (var (branch, i) in item.Branches.Select((b, i) => (b, i)))
        {
            Values(item, branch, i == 0, g => { found.Add(g); return g.ToString("D"); }, () => forward, f => forward = f, [], [], []);
        }
        return found;
    }
}
