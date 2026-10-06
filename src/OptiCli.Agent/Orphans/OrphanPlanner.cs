using OptiCli.Protocol;

namespace OptiCli.Agent.Orphans;

/// <summary>A content type asked to be removed, with what the site said about it.</summary>
/// <param name="Refusal">Why it isn't an orphan (made in admin mode, a system type, a class the site loads); null when it is one.</param>
/// <param name="Usage">Content and values that use it, apart from <see cref="UsedBy"/>.</param>
/// <param name="UsedBy">Properties whose block type it is; the CMS refuses to remove it while any exists.</param>
internal sealed record TypeFacts(int Id, string Name, string? Refusal, TypeUsage Usage, IReadOnlyList<UsingProperty> UsedBy);

/// <summary>A property that has a block type as its type.</summary>
internal sealed record UsingProperty(int TypeId, string TypeName, int PropertyId, string Name)
{
    public string Label => $"{TypeName}.{Name}";
}

/// <summary>A property asked to be removed, with what the site said about it.</summary>
/// <param name="Refusal">Why it isn't an orphan (in the code, on a type made in admin mode); null when it is one.</param>
internal sealed record PropertyFacts(int Id, int TypeId, string TypeName, string Name, string? Refusal, StoredValueCounts Values);

/// <param name="Properties">Properties to remove on their own, first.</param>
/// <param name="Types">Types to remove, in this order (a type before the block type one of its properties uses).</param>
/// <param name="Kept">What stays, and why.</param>
internal sealed record OrphanPlan(IReadOnlyList<PropertyFacts> Properties, IReadOnlyList<TypeFacts> Types, IReadOnlyList<KeptOrphan> Kept);

/// <summary>
/// Decides what of a set of candidates can be removed, and in which order, from facts the site gathered: the orphan rules
/// themselves, apart from the CMS. Content keeps a type (there is no way around it), stored values keep a property unless
/// the caller allows them to go, and a block type stays while a property uses it, unless that property (or the type that
/// has it) is removed first in the same run.
/// </summary>
internal static class OrphanPlanner
{
    public const string ContentHint =
        "opticli never deletes content: `opticli find --type <type>` and `opticli trash --type <type>` show it, `opticli where-used --type <type>` where it is used. Content in the recycle bin has to be deleted for good in the CMS's edit UI (or by its Automatic Emptying of Trash job) first.";

    public static OrphanPlan Plan(IReadOnlyList<TypeFacts> types, IReadOnlyList<PropertyFacts> properties, bool allowDestructive)
    {
        var kept = new List<KeptOrphan>();
        var removableProperties = new List<PropertyFacts>();
        foreach (var property in properties)
        {
            if (property.Refusal is { } refusal)
            {
                kept.Add(Kept(property, AgentErrorCodes.Refused, refusal));
            }
            else if (property.Values.Any && !allowDestructive)
            {
                kept.Add(Kept(property, AgentErrorCodes.Refused,
                    $"It has stored values ({OrphanRemoval.Describe(property.Values)}), which would be deleted with it for good: --allow-destructive removes them too."));
            }
            else
            {
                removableProperties.Add(property);
            }
        }

        var pending = new List<TypeFacts>();
        foreach (var type in types)
        {
            if (type.Refusal is { } refusal)
            {
                kept.Add(new KeptOrphan(type.Name, null, AgentErrorCodes.Refused, refusal));
            }
            else if (type.Usage.InUse)
            {
                kept.Add(new KeptOrphan(type.Name, null, AgentErrorCodes.Conflict, $"In use: {OrphanRemoval.Describe(type.Usage with { UsedBy = [] })}.") { Usage = Usage(type) });
            }
            else
            {
                pending.Add(type);
            }
        }

        // A block type is free once every property that uses it goes in this run: removed on its own, or with its type.
        var removedPropertyIds = removableProperties.Select(p => p.Id).ToHashSet();
        var removedTypes = new List<TypeFacts>();
        var removedTypeIds = new HashSet<int>();
        bool changed;
        do
        {
            changed = false;
            foreach (var type in pending.ToList())
            {
                if (Blocking(type, removedPropertyIds, removedTypeIds).Count == 0)
                {
                    removedTypes.Add(type);
                    removedTypeIds.Add(type.Id);
                    pending.Remove(type);
                    changed = true;
                }
            }
        }
        while (changed);

        foreach (var type in pending)
        {
            var blocking = Blocking(type, removedPropertyIds, removedTypeIds);
            kept.Add(new KeptOrphan(type.Name, null, AgentErrorCodes.Conflict,
                $"It is the block type of {string.Join(", ", blocking.Select(p => p.Label))}, and the CMS keeps a block type while a property has it: remove {(blocking.Count == 1 ? "that property" : "those properties")} first (`opticli types remove-property <type> <property>`, when not in the code), or the type that has {(blocking.Count == 1 ? "it" : "them")}.")
            {
                Usage = Usage(type),
            });
        }

        // A property of a type that goes in this run goes with it.
        return new OrphanPlan(removableProperties.Where(p => !removedTypeIds.Contains(p.TypeId)).ToList(), removedTypes, kept);
    }

    private static List<UsingProperty> Blocking(TypeFacts type, HashSet<int> removedProperties, HashSet<int> removedTypes) =>
        type.UsedBy.Where(p => p.TypeId != type.Id && !removedProperties.Contains(p.PropertyId) && !removedTypes.Contains(p.TypeId)).ToList();

    private static TypeUsage Usage(TypeFacts type) => type.Usage with { UsedBy = type.UsedBy.Select(p => p.Label).ToList() };

    private static KeptOrphan Kept(PropertyFacts property, string code, string reason) =>
        new(property.TypeName, property.Name, code, reason) { Values = property.Values };
}
