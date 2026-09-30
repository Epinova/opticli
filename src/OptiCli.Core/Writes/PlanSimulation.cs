using System.Text.Json.Nodes;

namespace OptiCli.Core.Writes;

/// <param name="Operation">What to dry-run: the planned content as a create, as it will be after the step.</param>
/// <param name="Target">Plan id of the planned content the step writes.</param>
/// <param name="StandIns">Refs to planned content that were replaced by existing content, as <c>$id → ref</c>.</param>
/// <param name="Unchecked">Top-level properties left out because their values refer to planned content.</param>
/// <param name="References">In those properties: which planned content each one refers to.</param>
public sealed record Simulation(
    WriteOperation Operation,
    string? Target,
    IReadOnlyDictionary<string, string> StandIns,
    IReadOnlyList<string> Unchecked,
    IReadOnlyList<(string Property, string PlanId)> References);

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
            if (later.Operation is SetOperation set && PlanId(set.Ref) == target && (set.Lang is null || set.Lang == language))
            {
                if (set.Properties is not null)
                {
                    PropertyArguments.Merge(properties, set.Properties);
                }
                name = set.Name ?? name;
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

    /// <summary>The content type a planned id is created as; null for an upload whose type follows from the file.</summary>
    public static string? TypeOf(string planId, IReadOnlyList<PlanStep> steps) =>
        steps.Select(s => s.Operation).FirstOrDefault(o => o.Id == planId) switch
        {
            CreateOperation create => create.Type,
            BlockCreateOperation block => block.Type,
            UploadOperation upload => upload.Type,
            _ => null,
        };

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
        var values = new JsonObject();
        var skipped = new List<string>();
        var references = new List<(string, string)>();
        foreach (var (name, value) in properties)
        {
            var found = new List<string>();
            var resolved = Resolve(value?.DeepClone(), existing, planned, found);
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

    private static JsonNode? Resolve(JsonNode? node, IReadOnlyDictionary<string, int> existing, IReadOnlySet<string> planned, List<string> found)
    {
        switch (node)
        {
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
                    obj[key] = Resolve(value?.DeepClone(), existing, planned, found);
                }
                return obj;
            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    array[i] = Resolve(array[i]?.DeepClone(), existing, planned, found);
                }
                return array;
            default:
                return node;
        }
    }
}
