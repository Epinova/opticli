using System.Text.Json.Nodes;

namespace OptiCli.Core.Writes;

/// <param name="Operation">What to dry-run: the planned content as a create, as it will be after the step.</param>
/// <param name="Target">Plan id of the planned content the step writes.</param>
/// <param name="StandIns">Refs to planned content that were replaced by existing content, as <c>$id → ref</c>.</param>
/// <param name="Unchecked">Top-level properties left out because their values refer to planned content.</param>
/// <param name="References">In those properties: which planned content each one refers to.</param>
/// <param name="Notes">Why the step is simulated and against what, for its warnings.</param>
public sealed record Simulation(
    WriteOperation Operation,
    string? Target,
    IReadOnlyDictionary<string, string> StandIns,
    IReadOnlyList<string> Unchecked,
    IReadOnlyList<(string Property, string PlanId)> References,
    IReadOnlyList<string>? Notes = null);

/// <summary>The existing content and language branch a plan step writes, as the database has them before the plan runs.</summary>
/// <param name="Language">The branch: the step's lang, else the one its URL selects, else the master language; null for content without languages.</param>
/// <param name="Master">The content's master language.</param>
/// <param name="BranchExists">Whether the database has that branch now (false: an earlier translate step creates it).</param>
/// <param name="Versioned">The step names a version (<c>123_456</c>, a publish's <c>version</c>).</param>
public sealed record PlanTarget(int Id, string? Language, string? Master, bool BranchExists, bool Versioned)
{
    public bool Same(PlanTarget other) => Id == other.Id && string.Equals(Language, other.Language, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// A dry run for a plan step whose content doesn't exist yet: the content the step saves is dry-run as a create under
/// the nearest existing ancestor instead (a stand-in), with the planned parent's type for the "allowed below" check.
/// It carries every value set on it up to and including the step, and publishes if the step does, so validators see
/// what the real run will save at that step.
/// </summary>
public static class PlanSimulation
{
    /// <param name="existing">Plan ids whose content already exists (<c>--update-existing</c>): real refs, not stand-ins.</param>
    /// <param name="updateExisting">Leave the GUID out, so the agent doesn't match it against content under the stand-in.</param>
    /// <returns>Null for steps that can't be simulated this way (area, translate, access, move, delete).</returns>
    public static Simulation? For(PlanStep step, IReadOnlyList<PlanStep> steps, IReadOnlyDictionary<string, int> existing, bool updateExisting)
    {
        var op = step.Operation;
        var (target, publish) = op switch
        {
            CreateOperation or BlockCreateOperation or UploadOperation => (op.Id, IsPublishing(op)),
            SetOperation set when PlanId(set.Ref) is { } id => (id, set.Publish),
            PublishOperation publishing when PlanId(publishing.Ref) is { } id && publishing.Version is null => (id, true),
            _ => ((string?)null, false),
        };
        var creating = op is CreateOperation or BlockCreateOperation or UploadOperation
            ? step
            : target is null
                ? null
                : steps.FirstOrDefault(s => s.Operation.Id == target && s.Index < step.Index && s.Operation is CreateOperation or BlockCreateOperation or UploadOperation);
        if (creating is null || (target is not null && existing.ContainsKey(target)))
        {
            return null;
        }

        // Everything set on the content up to this step, in plan order.
        var properties = Properties(creating.Operation)?.DeepClone().AsObject() ?? [];
        var name = NameOf(creating.Operation);
        var language = LanguageOf(creating.Operation);
        foreach (var later in steps.Where(s => s.Index > creating.Index && s.Index <= step.Index))
        {
            if (later.Operation is SetOperation set && PlanId(set.Ref) == target && SameLanguage(set.Lang, language))
            {
                if (set.Properties is not null)
                {
                    PropertyArguments.Merge(properties, set.Properties);
                }
                name = set.Name ?? name;
            }
            else if (later.Operation is AreaEdit area && PlanId(area.Ref) == target && SameLanguage(area.Lang, language))
            {
                Edit(properties, area);
            }
        }

        var standIns = new Dictionary<string, string>(StringComparer.Ordinal);
        string Place(string reference) => StandIn(reference, steps, existing, standIns, depth: 0);
        var (values, skipped, references) = Strip(properties, steps, existing);

        WriteOperation simulated = creating.Operation switch
        {
            CreateOperation create => create with
            {
                Parent = Place(create.Parent),
                Name = name ?? create.Name,
                Properties = values,
                Publish = publish,
                PlannedParentType = PlanId(create.Parent) is { } parentId && !existing.ContainsKey(parentId) ? TypeOf(parentId, steps) : null,
            },
            BlockCreateOperation block => block with
            {
                For = block.For is null ? null : Place(block.For),
                Parent = block.Parent is null ? null : Place(block.Parent),
                Name = name ?? block.Name,
                Properties = values,
                Publish = publish,
            },
            UploadOperation upload => upload with
            {
                For = upload.For is null ? null : Place(upload.For),
                Parent = upload.Parent is null ? null : Place(upload.Parent),
                Name = name ?? upload.Name,
                Properties = values,
                Publish = publish,
            },
            _ => throw new InvalidOperationException("Only creating steps are simulated."),
        };
        // A GUID is only checked for the step that creates the content: whether it exists already.
        simulated = simulated with { Id = null, ContentGuid = updateExisting || creating.Index != step.Index ? null : creating.Operation.ContentGuid };
        return new Simulation(simulated, target, standIns, skipped, references);
    }

    /// <summary>
    /// A step on existing content that earlier steps of the plan change first, which a dry run against today's database
    /// would get wrong. What those steps save is folded into the step's dry run instead:
    /// <list type="bullet">
    /// <item>a <c>publish</c> (of the latest version) after <c>set</c>, <c>area</c>, <c>translate</c>, or a create step that
    /// updates existing content: those changes, dry-run with publish, as one draft on the latest version (or as the new
    /// language branch). Its pending-draft check covers what the real publish will put live.</item>
    /// <item>a <c>set</c> on a language branch an earlier <c>translate</c> creates: the translate with every value set on
    /// the branch so far; an <c>area</c> edit there is dry-run on the master branch instead.</item>
    /// </list>
    /// </summary>
    /// <param name="steps">The plan's steps, with refs to existing content already resolved (<see cref="WritePlan.Resolve"/>).</param>
    /// <param name="targets">Per step index, the existing content and branch it writes (steps on planned content have none).</param>
    /// <param name="existing">Plan ids whose content already exists: values that refer to other plan ids are left out.</param>
    /// <returns>Null when no earlier step changes the step's content and branch, or the step isn't one of the above.</returns>
    /// <exception cref="Errors.ConflictException">
    /// A publish whose content an earlier step already publishes, with nothing saved in between: there is nothing left to
    /// publish (without <paramref name="updateExisting"/>, which makes such a publish change nothing).
    /// </exception>
    public static Simulation? OnExisting(PlanStep step, IReadOnlyList<PlanStep> steps, IReadOnlyDictionary<int, PlanTarget> targets, IReadOnlyDictionary<string, int> existing, bool updateExisting)
    {
        if (!targets.TryGetValue(step.Index, out var target))
        {
            return null;
        }
        var earlier = steps.Where(s => s.Index < step.Index && targets.TryGetValue(s.Index, out var other) && other.Same(target) && Writes(s.Operation, other)).ToList();
        if (earlier.Count == 0)
        {
            return null;
        }
        var translate = target.BranchExists ? null : earlier.FirstOrDefault(s => s.Operation is TranslateOperation);
        var reference = WriteOutput.Id(target.Id);
        switch (step.Operation)
        {
            case PublishOperation { Version: null } when !target.Versioned:
                if (Publishes(earlier[^1].Operation))
                {
                    return updateExisting ? null : throw new Errors.ConflictException(
                        $"Operation {earlier[^1].Index} already publishes {reference}{In(target)}, and nothing is saved in between, so this publish would find nothing to publish.",
                        "Remove this publish, or the earlier step's \"publish\": true.");
                }
                return Merged(step, earlier, target, translate, reference, publish: true, steps, existing,
                    $"Dry-run as {reference}{In(target)} will be after operation(s) {string.Join(", ", earlier.Select(s => s.Index))}, published (their changes aren't saved yet).");
            case SetOperation set when translate is not null:
                return Merged(step, [.. earlier, step], target, translate, reference, set.Publish, steps, existing,
                    $"Dry-run as the new '{target.Language}' branch of {reference} will be after this operation: operation {translate.Index} creates it.");
            case AreaEdit area when translate is not null && target.Master is { } master:
                return new Simulation(area with { Lang = master, Publish = false, BaseVersion = null, Force = true }, null, new Dictionary<string, string>(), [], [],
                    [$"Dry-run on the master branch ('{master}') as a stand-in: operation {translate.Index} creates the '{target.Language}' branch, so this area edit is checked there when the plan runs."]);
            default:
                return null;
        }
    }

    /// <summary>The earlier writes (and for a set, the step itself), applied in order, as one dry run.</summary>
    private static Simulation Merged(PlanStep step, IReadOnlyList<PlanStep> writes, PlanTarget target, PlanStep? translate, string reference, bool publish,
        IReadOnlyList<PlanStep> steps, IReadOnlyDictionary<string, int> existing, string note)
    {
        var properties = new JsonObject();
        string? name = null;
        var areas = new List<AreaEdit>();
        var notChecked = new List<string>();
        foreach (var write in writes)
        {
            if (write.Operation is AreaEdit area)
            {
                if (area.Item is { } item && PlanId(item) is not null)
                {
                    notChecked.Add(area.Property);
                }
                else
                {
                    areas.Add(area);
                }
                continue;
            }
            var (values, newName) = Values(write.Operation);
            if (values is not null)
            {
                // A whole value replaces what earlier area edits did to it.
                areas.RemoveAll(a => values.Any(v => v.Key.Equals(a.Property, StringComparison.OrdinalIgnoreCase)));
                PropertyArguments.Merge(properties, values);
            }
            name = newName ?? name;
        }

        var (stripped, skipped, references) = Strip(properties, steps, existing);
        notChecked.AddRange(skipped);
        WriteOperation operation;
        var notes = new List<string> { note };
        if (translate?.Operation is TranslateOperation creating)
        {
            operation = new TranslateOperation(reference, creating.Lang, name, stripped, publish);
            // A new branch takes no area edits in its dry run.
            notChecked.AddRange(areas.Select(a => a.Property));
        }
        else
        {
            // The master branch as the agent picks it, which also suits content without languages.
            var language = string.Equals(target.Language, target.Master, StringComparison.OrdinalIgnoreCase) ? null : target.Language;
            operation = new SetOperation(reference, stripped, name, language, publish) { AreaEdits = areas };
        }
        return new Simulation(operation with { IncludeDraft = step.Operation.IncludeDraft }, null, new Dictionary<string, string>(), notChecked.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), references, notes);
    }

    /// <summary>Steps that save values of the content they target, or publish its latest version.</summary>
    private static bool Writes(WriteOperation op, PlanTarget target) => op switch
    {
        TranslateOperation { Remove: true } => false,
        PublishOperation publish => publish.Version is null && !target.Versioned,
        _ => op is SetOperation or AreaEdit or TranslateOperation or CreateOperation or BlockCreateOperation or UploadOperation,
    };

    private static bool Publishes(WriteOperation op) => op switch
    {
        PublishOperation => true,
        SetOperation set => set.Publish,
        AreaEdit area => area.Publish,
        TranslateOperation translate => translate.Publish,
        _ => IsPublishing(op),
    };

    /// <summary>The values and name a write saves; a create step here updates existing content.</summary>
    private static (JsonObject? Properties, string? Name) Values(WriteOperation op) => op switch
    {
        SetOperation set => (set.Properties, set.Name),
        TranslateOperation translate => (translate.Properties, translate.Name),
        _ => (Properties(op), NameOf(op)),
    };

    private static string In(PlanTarget target) => target.Language is { } language ? $" in '{language}'" : "";

    private static bool SameLanguage(string? step, string? created) => step is null || string.Equals(step, created, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// An area step applied to the ContentArea value as <c>properties</c> gives it (<c>[{"ref": ...}]</c>). An item or
    /// position that isn't there is left to the real run to report.
    /// </summary>
    private static void Edit(JsonObject properties, AreaEdit area)
    {
        var key = properties.Select(p => p.Key).FirstOrDefault(k => k.Equals(area.Property, StringComparison.OrdinalIgnoreCase)) ?? area.Property;
        var items = properties[key] is JsonArray existing ? existing.DeepClone().AsArray() : [];
        int Find() => area.Index ?? items.ToList().FindIndex(i => i is JsonObject item && (string?)item["ref"] == area.Item);
        switch (area.Action)
        {
            case Protocol.AreaOps.Add when area.Item is not null && (area.At ?? items.Count) is var at && at >= 0 && at <= items.Count:
                var added = new JsonObject { ["ref"] = area.Item };
                if (area.Display is not null)
                {
                    added["displayOption"] = area.Display;
                }
                items.Insert(at, added);
                break;
            case Protocol.AreaOps.Remove when Find() is var index && index >= 0 && index < items.Count:
                items.RemoveAt(index);
                break;
            case Protocol.AreaOps.Move when Find() is var from && from >= 0 && from < items.Count && area.To is { } to && to >= 0 && to < items.Count:
                var moved = items[from];
                items.RemoveAt(from);
                items.Insert(to, moved);
                break;
            default:
                return;
        }
        properties[key] = items;
    }

    /// <summary>The content type a planned id is created as; null for an upload whose type follows from the file.</summary>
    public static string? TypeOf(string planId, IReadOnlyList<PlanStep> steps) =>
        steps.Select(s => s.Operation).FirstOrDefault(o => o.Id == planId) switch
        {
            CreateOperation create => create.Type,
            BlockCreateOperation block => block.Type,
            UploadOperation upload => upload.Type,
            _ => null,
        };

    /// <summary>
    /// A step on existing content whose only references to planned content are links in rich text: dry-run with those
    /// links pointing at the content's GUID (fixed by the plan, or a stand-in), so the HTML is checked.
    /// </summary>
    /// <returns>Null when the step refers to planned content in any other way (a ref, a ContentArea item, a block in text).</returns>
    public static WriteOperation? WithStandInLinks(WriteOperation op, IReadOnlyList<PlanStep> steps, IReadOnlyDictionary<string, int> existing)
    {
        var planned = steps.Select(s => s.Operation.Id).OfType<string>().Where(id => !existing.ContainsKey(id)).ToHashSet(StringComparer.Ordinal);
        var guids = WritePlan.FixedGuids(steps);
        var other = false;
        var links = false;
        var mapped = op.MapRefs(value =>
        {
            if (PlanId(value) is { } id)
            {
                other |= planned.Contains(id);
                return existing.TryGetValue(id, out var existingId) ? WriteOutput.Id(existingId) : value;
            }
            return TextRefs.Map(value, reference =>
            {
                if (existing.TryGetValue(reference.Id, out var existingId))
                {
                    return TextRefs.Value(reference.Attribute, existingId, guids.TryGetValue(reference.Id, out var known) ? known : null);
                }
                if (!planned.Contains(reference.Id))
                {
                    return null;
                }
                if (reference.Attribute != TextRefs.Href)
                {
                    other = true;
                    return null;
                }
                links = true;
                return TextRefs.PermanentLink(guids.TryGetValue(reference.Id, out var fixedGuid) ? fixedGuid : StandInGuid(reference.Id));
            });
        });
        return links && !other ? mapped : null;
    }

    /// <summary>A GUID for planned content whose own isn't known until it is created; nothing has it.</summary>
    private static Guid StandInGuid(string id) => StableGuids.Create(StandInNamespace, id);

    private static readonly Guid StandInNamespace = Guid.Parse("8f3c0b9e-2d47-4a51-9e6b-0c5a7d3e1f42");

    public static string? PlanId(string? reference) => reference is { Length: > 1 } && reference[0] == '$' ? reference[1..] : null;

    private static bool IsPublishing(WriteOperation op) => op switch
    {
        CreateOperation create => create.Publish,
        BlockCreateOperation block => block.Publish,
        UploadOperation upload => upload.Publish,
        _ => false,
    };

    private static JsonObject? Properties(WriteOperation op) => op switch
    {
        CreateOperation create => create.Properties,
        BlockCreateOperation block => block.Properties,
        UploadOperation upload => upload.Properties,
        _ => null,
    };

    private static string? NameOf(WriteOperation op) => op switch
    {
        CreateOperation create => create.Name,
        BlockCreateOperation block => block.Name,
        UploadOperation upload => upload.Name,
        _ => null,
    };

    private static string? LanguageOf(WriteOperation op) => op switch
    {
        CreateOperation create => create.Lang,
        BlockCreateOperation block => block.Lang,
        _ => null,
    };

    /// <summary>The existing content that stands in for <paramref name="reference"/>: itself, or the nearest existing ancestor.</summary>
    private static string StandIn(string reference, IReadOnlyList<PlanStep> steps, IReadOnlyDictionary<string, int> existing, Dictionary<string, string> standIns, int depth)
    {
        if (PlanId(reference) is not { } id)
        {
            return reference;
        }
        if (existing.TryGetValue(id, out var existingId))
        {
            return WriteOutput.Id(existingId);
        }
        var parent = steps.Select(s => s.Operation).FirstOrDefault(o => o.Id == id) switch
        {
            CreateOperation create => create.Parent,
            BlockCreateOperation block => block.For ?? block.Parent,
            UploadOperation upload => upload.For ?? upload.Parent,
            _ => null,
        };
        if (parent is null || depth > steps.Count)
        {
            return reference;
        }
        var standIn = StandIn(parent, steps, existing, standIns, depth + 1);
        standIns[reference] = standIn;
        return standIn;
    }

    /// <summary>
    /// The values with existing plan content resolved, minus the top-level properties that refer to content that
    /// doesn't exist yet (a ContentArea item, a reference, a link): those can only be checked when the plan runs.
    /// </summary>
    private static (JsonObject? Values, List<string> Skipped, List<(string, string)> References) Strip(JsonObject properties, IReadOnlyList<PlanStep> steps, IReadOnlyDictionary<string, int> existing)
    {
        var planned = steps.Select(s => s.Operation.Id).OfType<string>().Where(id => !existing.ContainsKey(id)).ToHashSet(StringComparer.Ordinal);
        var guids = WritePlan.FixedGuids(steps);
        var values = new JsonObject();
        var skipped = new List<string>();
        var references = new List<(string, string)>();
        foreach (var (name, value) in properties)
        {
            var found = new List<string>();
            var resolved = Resolve(value?.DeepClone(), existing, planned, guids, found);
            if (found.Count > 0)
            {
                skipped.Add(name);
                references.AddRange(found.Distinct().Select(id => (name, id)));
            }
            else
            {
                values[name] = resolved;
            }
        }
        return (values.Count > 0 ? values : null, skipped, references);
    }

    private static JsonNode? Resolve(JsonNode? node, IReadOnlyDictionary<string, int> existing, IReadOnlySet<string> planned, IReadOnlyDictionary<string, Guid> guids, List<string> found)
    {
        switch (node)
        {
            case JsonValue text when text.TryGetValue<string>(out var markup) && PlanId(markup) is null && TextRefs.Find(markup).Any():
                // Rich text: links to content that doesn't exist yet get a stand-in GUID, so the dry run validates the
                // HTML; a block in the text needs the content itself, so the property is left out.
                return JsonValue.Create(TextRefs.Map(markup, reference =>
                {
                    if (existing.TryGetValue(reference.Id, out var existingId))
                    {
                        return TextRefs.Value(reference.Attribute, existingId, guids.TryGetValue(reference.Id, out var known) ? known : null);
                    }
                    if (planned.Contains(reference.Id) && reference.Attribute == TextRefs.Href)
                    {
                        return TextRefs.PermanentLink(guids.TryGetValue(reference.Id, out var fixedGuid) ? fixedGuid : StandInGuid(reference.Id));
                    }
                    if (planned.Contains(reference.Id))
                    {
                        found.Add(reference.Id);
                    }
                    return null;
                }));
            case JsonValue leaf when leaf.TryGetValue<string>(out var text) && PlanId(text) is { } id:
                if (existing.TryGetValue(id, out var existingId))
                {
                    return JsonValue.Create(WriteOutput.Id(existingId));
                }
                if (planned.Contains(id))
                {
                    found.Add(id);
                }
                return node;
            case JsonObject obj:
                foreach (var (key, value) in obj.ToList())
                {
                    obj[key] = Resolve(value?.DeepClone(), existing, planned, guids, found);
                }
                return obj;
            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    array[i] = Resolve(array[i]?.DeepClone(), existing, planned, guids, found);
                }
                return array;
            default:
                return node;
        }
    }
}
