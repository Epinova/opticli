using EPiServer.DataAbstraction;
using OptiCli.Agent.Hosting;
using OptiCli.Agent.Http;
using OptiCli.Cms;
using OptiCli.Core.Text;
using OptiCli.Protocol;

namespace OptiCli.Agent.Orphans;

/// <summary>
/// <c>POST /v1/types/remove</c>: removes content types and properties that removed code left in the database, through the
/// CMS's <see cref="IContentTypeRepository.Delete(ContentType)"/> and its property delete (CMS 12's
/// <c>IPropertyDefinitionRepository.Delete</c>, CMS 13's save of the type without the property), which clear the content
/// model's and the content's caches and raise the CMS's events.
/// </summary>
/// <remarks>
/// <para>Here and not in <c>OptiCli.Cms</c>, which the MCP module compiles in: the content model is the developer's only,
/// and a production site must never be able to lose part of it through opticli.</para>
/// <para>The site decides what is an orphan, whatever the CLI checked: a type whose class (<c>ModelTypeString</c>) the
/// site can't load, never one made in admin mode or one of the CMS's own; a property the CMS marked as gone from its type's
/// code (<c>ExistsOnModel</c> false), on a type defined in code, that the site's model doesn't have. The CMS's own deletes
/// check much less: a property's goes ahead whatever is stored (the values go with it), and a type's clears page-type
/// values that name it, so <see cref="OrphanPlanner"/> refuses both unless allowed.</para>
/// </remarks>
internal static class OrphanRemovalOperation
{
    public const string RestartWarning =
        "Removed through the site `opticli serve` runs, which uses the new content model now. Another process running this site against the same database (your IDE's, say) keeps its cached content types until it restarts.";

    public static OrphanRemovalResult Run(AgentRequest request, OrphanRemovalRequest body)
    {
        if (request.Service<AgentSettings>().SharedDatabase)
        {
            throw AgentException.Refused(OrphanRemoval.SharedRefusal, OrphanRemoval.SharedHint);
        }
        // One removal at a time in this site: two requests checking the same type or property and then both removing would
        // each have checked a state the other changes.
        lock (Gate)
        {
            var services = request.Context.RequestServices;
            return Run(new ContentModelSource(services), body, body.DryRun ? null : RemovalRecords.For(services), request.Call.Aborted);
        }
    }

    private static readonly object Gate = new();

    /// <param name="records">Where each record goes before its removal; null for a dry run.</param>
    internal static OrphanRemovalResult Run(IContentModelSource source, OrphanRemovalRequest body, RemovalRecords? records, CancellationToken aborted = default)
    {
        RequireNoNulls(body);
        var named = body.Types is { Count: > 0 } || body.Properties is { Count: > 0 };
        if (named == body.Prune)
        {
            throw AgentException.Usage(body.Prune ? "prune doesn't take named types or properties." : "Name the types or properties to remove, or ask to prune.");
        }
        if (body.PruneProperties && !body.Prune)
        {
            throw AgentException.Usage("pruneProperties needs prune.");
        }

        var types = source.Types();
        var kept = new List<KeptOrphan>();
        var typeCandidates = new List<SiteType>();
        var propertyCandidates = new List<(SiteType Type, SiteProperty Property)>();
        // Where each named item was in the request, to report them in that order.
        var order = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        void Named(string type, string? property) => order.TryAdd(Label(type, property), order.Count);
        if (body.Prune)
        {
            typeCandidates.AddRange(types.Where(t => TypeRefusal(t) is null));
            foreach (var type in types.Where(t => t.FromCode && !typeCandidates.Contains(t)))
            {
                foreach (var property in type.Properties.Where(p => !p.ExistsOnModel && !p.InModel))
                {
                    if (body.PruneProperties)
                    {
                        propertyCandidates.Add((type, property));
                    }
                    else
                    {
                        kept.Add(new KeptOrphan(type.Name, property.Name, AgentErrorCodes.Refused,
                            "Not in its type's code (existsOnModel: false). Properties are pruned only with --properties, since one added in admin mode looks the same; `opticli types remove-property` removes one by name."));
                    }
                }
            }
        }
        else
        {
            foreach (var name in Distinct(body.Types ?? []))
            {
                if (Find(types, name) is { } type)
                {
                    Named(type.Name, null);
                    if (!typeCandidates.Contains(type))
                    {
                        typeCandidates.Add(type);
                    }
                }
                else
                {
                    Named(name, null);
                    kept.Add(new KeptOrphan(name, null, AgentErrorCodes.NotFound, $"No content type is named '{name}'.{Suggest(name, types.Select(t => t.Name))}"));
                }
            }
            foreach (var reference in body.Properties ?? [])
            {
                if (Find(types, reference.Type) is not { } type)
                {
                    Named(reference.Type, reference.Property);
                    kept.Add(new KeptOrphan(reference.Type, reference.Property, AgentErrorCodes.NotFound, $"No content type is named '{reference.Type}'.{Suggest(reference.Type, types.Select(t => t.Name))}"));
                }
                else if (type.Properties.FirstOrDefault(p => string.Equals(p.Name, reference.Property.Trim(), StringComparison.OrdinalIgnoreCase)) is { } property)
                {
                    Named(type.Name, property.Name);
                    if (!propertyCandidates.Any(c => c.Property.Id == property.Id))
                    {
                        propertyCandidates.Add((type, property));
                    }
                }
                else
                {
                    Named(type.Name, reference.Property);
                    kept.Add(new KeptOrphan(type.Name, reference.Property, AgentErrorCodes.NotFound, $"{type.Name} has no property '{reference.Property}'.{Suggest(reference.Property, type.Properties.Select(p => p.Name))}"));
                }
            }
        }

        var typeFacts = typeCandidates.Select(t => TypeRefusal(t) is { } refusal
                ? new TypeFacts(t.Id, t.Name, refusal, TypeUsage.None, [])
                : new TypeFacts(t.Id, t.Name, null, source.Usage(t), UsedBy(types, t)))
            .ToList();
        var propertyFacts = propertyCandidates.Select(c => PropertyRefusal(c.Type, c.Property) is { } refusal
                ? new PropertyFacts(c.Property.Id, c.Type.Id, c.Type.Name, c.Property.Name, refusal, new StoredValueCounts(0, 0))
                : new PropertyFacts(c.Property.Id, c.Type.Id, c.Type.Name, c.Property.Name, null, source.Values(c.Property)))
            .ToList();
        var plan = OrphanPlanner.Plan(typeFacts, propertyFacts, body.AllowDestructive);
        kept.AddRange(plan.Kept);
        if (named && kept.Count > 0)
        {
            throw Refusal(kept.OrderBy(k => order.GetValueOrDefault(Label(k.Type, k.Property), int.MaxValue)).ToList(), order.Count);
        }

        var byId = types.ToDictionary(t => t.Id);
        var removedTypes = plan.Types.Select(t => Record(byId[t.Id], types)).ToList();
        var removedProperties = plan.Properties
            .Select(p => (Facts: p, Property: byId[p.TypeId].Properties.Single(x => x.Id == p.Id)))
            .ToList();
        var warnings = new List<string>();
        if (removedTypes.Count + removedProperties.Count > 0)
        {
            warnings.Add(OrphanRemoval.NoUndo);
            if (removedProperties.Count > 0)
            {
                warnings.Add(OrphanRemoval.AdminModeLookalike);
            }
            if (removedProperties.Where(p => p.Facts.Values.Any).Select(p => $"{p.Facts.TypeName}.{p.Facts.Name} ({OrphanRemoval.Describe(p.Facts.Values)})").ToList() is { Count: > 0 } withValues)
            {
                warnings.Add($"{(body.DryRun ? "Would delete" : "Deleted")} the stored values of {string.Join(", ", withValues)}, in every version and language, for good.");
            }
        }
        var propertyRecords = removedProperties.Select(p => new RemovedProperty(p.Facts.TypeName, p.Property.Record, p.Facts.Values)).ToList();
        var removing = removedTypes.Count + removedProperties.Count > 0;
        if (!body.DryRun && removing)
        {
            if (records is null)
            {
                throw new InvalidOperationException("A removal needs somewhere to record it.");
            }
            var steps = removedProperties.Select((p, i) => new Step($"{p.Facts.TypeName}.{p.Facts.Name}", null, propertyRecords[i],
                    () => Changed(p.Facts.Values, source.Values(p.Property)), () => source.Remove(p.Property)))
                .Concat(plan.Types.Select((t, i) => new Step(t.Name, removedTypes[i], null,
                    () => source.Usage(byId[t.Id]) is { InUse: true } usage ? $"it is in use now: {OrphanRemoval.Describe(usage)}" : null, () => source.Remove(byId[t.Id]))))
                .ToList();
            Remove(steps, records, warnings, aborted);
            warnings.Add(RestartWarning);
        }
        return new OrphanRemovalResult(removedTypes, propertyRecords, kept, body.DryRun, Removed: !body.DryRun && removing)
        {
            Warnings = warnings.Count > 0 ? warnings : null,
            RecordFile = !body.DryRun && removing ? records?.Path : null,
        };
    }

    /// <summary>One removal: its record, a last check just before it, and the CMS's delete.</summary>
    /// <param name="Recheck">What changed since the plan was made (null: nothing), checked again just before removing it.</param>
    private sealed record Step(string Label, RemovedContentType? Type, RemovedProperty? Property, Func<string?> Recheck, Action Run);

    /// <summary>The values counted again: different counts, or a content provider that appeared, stop the run.</summary>
    internal static string? Changed(StoredValueCounts planned, StoredValueCounts now) =>
        planned.Content == now.Content && planned.Versions == now.Versions && planned.ProviderUse == now.ProviderUse
            ? null
            : $"its stored values changed since they were checked ({OrphanRemoval.Describe(planned)}; now {OrphanRemoval.Describe(now)})";

    /// <summary>
    /// A raw request (not the CLI's) may hold nulls where names go: a usage error, not a failure inside the site.
    /// </summary>
    private static void RequireNoNulls(OrphanRemovalRequest body)
    {
        if (body.Types?.Any(t => string.IsNullOrWhiteSpace(t)) == true)
        {
            throw AgentException.Usage("types holds an empty name.", "Give each content type's name or GUID.");
        }
        if (body.Properties?.Any(p => p is null || string.IsNullOrWhiteSpace(p.Type) || string.IsNullOrWhiteSpace(p.Property)) == true)
        {
            throw AgentException.Usage("properties holds an entry without a type or a property.", """Give each as {"type": "...", "property": "..."}.""");
        }
    }

    /// <summary>Why a type isn't an orphan of removed code; null when it is one.</summary>
    internal static string? TypeRefusal(SiteType type)
    {
        if (OrphanRemoval.SystemTypes.Contains(type.Name, StringComparer.Ordinal))
        {
            return $"{type.Name} is one of the CMS's own types.";
        }
        if (!type.FromCode)
        {
            return $"{type.Name} was made in admin mode (it has no class on record), so it isn't left over from removed code; remove it in admin mode (Content Types) if it should go.";
        }
        if (type.HasClass)
        {
            return $"{type.Name} has a class the site loads ({type.ModelType}). Remove the class from the code first: when the site starts, the CMS removes a type whose class is gone if nothing uses it.";
        }
        return null;
    }

    /// <summary>Why a property isn't an orphan of removed code; null when it is one.</summary>
    internal static string? PropertyRefusal(SiteType type, SiteProperty property)
    {
        if (!type.FromCode)
        {
            return $"{type.Name} was made in admin mode, so its properties were too: remove it in admin mode (Content Types) if it should go.";
        }
        if (!type.HasClass && property.ExistsOnModel)
        {
            return $"{type.Name}'s class is gone, but the CMS still counts {property.Name} as in it: remove the whole type (`opticli types remove {type.Name}`) once nothing uses it.";
        }
        if (property.ExistsOnModel || property.InModel)
        {
            return $"{property.Name} is in {type.Name}'s code. Remove it from the class first: when the site starts, the CMS removes a property gone from the code if it has no values, and marks it existsOnModel: false if it has.";
        }
        return null;
    }

    /// <summary>The properties whose block type <paramref name="type"/> is, on any type.</summary>
    private static List<UsingProperty> UsedBy(IReadOnlyList<SiteType> types, SiteType type) =>
        types.SelectMany(t => t.Properties.Where(p => p.BlockType == type.Guid).Select(p => new UsingProperty(t.Id, t.Name, p.Id, p.Name))).ToList();

    private static RemovedContentType Record(SiteType type, IReadOnlyList<SiteType> types) =>
        new(type.Id, type.Guid, type.Name, type.Base, type.DisplayName, type.Description, type.ModelType ?? "", type.Properties.Select(p => p.Record).ToList())
        {
            AllowedChildren = type.AllowedChildren,
            AvailableUnder = types.Where(t => t.Id != type.Id && t.AllowedChildren?.Contains(type.Name, StringComparer.OrdinalIgnoreCase) == true).Select(t => t.Name).ToList() is { Count: > 0 } under ? under : null,
        };

    /// <summary>
    /// Properties first (a block type is free once they are gone), then types in the planned order. Each one is checked
    /// once more and recorded before the CMS removes it, so the record file holds everything removed, however the run ends.
    /// </summary>
    private static void Remove(IReadOnlyList<Step> steps, RemovalRecords records, List<string> warnings, CancellationToken aborted)
    {
        var done = new List<Step>();
        foreach (var step in steps)
        {
            try
            {
                if (aborted.IsCancellationRequested)
                {
                    throw new AgentException(AgentErrorCodes.Internal, "The caller stopped waiting, so nothing more was removed.", "the CLI timed out or was interrupted");
                }
                if (step.Recheck() is { } changed)
                {
                    throw AgentException.Conflict($"{step.Label} wasn't removed: {changed}.", "Run the dry run again to see what is left and why.");
                }
                if (step.Type is { } type)
                {
                    records.Removing(type);
                }
                else
                {
                    records.Removing(step.Property!);
                }
            }
            catch (Exception ex)
            {
                throw Partial(ex, step, done, steps, records, warnings, recorded: false);
            }
            try
            {
                step.Run();
                done.Add(step);
            }
            catch (Exception ex)
            {
                try
                {
                    records.Failed(step.Label, ex.Message);
                }
                catch (Exception recordFailure)
                {
                    Console.Error.WriteLine($"[opticli] Couldn't record that {step.Label} wasn't removed: {recordFailure.Message}");
                }
                throw Partial(ex, step, done, steps, records, warnings, recorded: true);
            }
        }
    }

    /// <summary>
    /// The error for a run that stopped at <paramref name="failed"/>: the failure's own code (the CMS's refusal is a
    /// conflict), what was removed before it in full (<c>details.removed</c>), and what wasn't.
    /// </summary>
    private static AgentException Partial(Exception ex, Step failed, IReadOnlyList<Step> done, IReadOnlyList<Step> steps, RemovalRecords records, List<string> warnings, bool recorded)
    {
        // CMS 13 validates a type when it is saved without a property (a contract it implements may need it).
        var refusedByCms = ex is DataAbstractionException or System.ComponentModel.DataAnnotations.ValidationException;
        var code = ex switch
        {
            AgentException agent => agent.Code,
            _ when refusedByCms => AgentErrorCodes.Conflict,
            _ => AgentErrorCodes.Internal,
        };
        var message = refusedByCms ? $"The CMS refused to remove {failed.Label}: {ex.Message}" : ex.Message;
        var rest = steps.SkipWhile(s => s != failed).Select(s => s.Label).ToList();
        var removed = done.Count == 0 ? null : new OrphanRemovalResult(
            done.Where(s => s.Type is not null).Select(s => s.Type!).ToList(),
            done.Where(s => s.Property is not null).Select(s => s.Property!).ToList(),
            [], DryRun: false, Removed: true)
        {
            Warnings = [.. warnings, RestartWarning],
            RecordFile = records.Path,
        };
        var hint = ex is AgentException { Hint: { } own } ? own : "Run the dry run again to see what is left and why.";
        return new AgentException(code,
            done.Count == 0 ? message : $"{message} Already removed: {string.Join(", ", done.Select(s => s.Label))} (details.removed has their records, as does {records.Path}); not removed: {string.Join(", ", rest)}.",
            hint)
        {
            Removal = removed,
            Validation = (ex as AgentException)?.Validation,
        };
    }

    /// <summary>
    /// Named items that can't go, in the order they were named: the error's code is the weightiest of theirs (a safety rule,
    /// then content in the way, then a name that doesn't exist), and each one is in its validation list.
    /// </summary>
    private static AgentException Refusal(IReadOnlyList<KeptOrphan> kept, int named)
    {
        var code = new[] { AgentErrorCodes.Refused, AgentErrorCodes.Conflict, AgentErrorCodes.NotFound }.First(c => kept.Any(k => k.Code == c));
        var issues = kept.Select(k => new ValidationIssue(Label(k.Type, k.Property), k.Reason)).ToList();
        var message = kept.Count == 1 && named == 1
            ? $"{issues[0].Property}: {issues[0].Message}"
            : $"{(kept.Count >= named ? $"None of the {named} can go" : $"{kept.Count} of the {named} can't go")} (details.validation has each): {string.Join(" ", issues.Select(i => $"{i.Property}: {i.Message}"))}";
        var hints = new List<string>();
        if (kept.Any(k => k.Usage is { } u && u.Content + u.InRecycleBin + u.InlineBlocks + u.OtherUses > 0))
        {
            hints.Add(OrphanPlanner.ContentHint);
        }
        if (kept.Any(k => k.Usage?.PageTypeValues > 0))
        {
            hints.Add(OrphanPlanner.PageTypeHint);
        }
        if (kept.Any(k => k.Values?.ProviderUse == true))
        {
            hints.Add(OrphanRemoval.ProviderHint);
        }
        if (kept.Any(k => k.Values?.Any == true && k.Code == AgentErrorCodes.Refused))
        {
            hints.Add("Show the user the values that would go, and pass --allow-destructive only if they agree; `opticli type <type>` shows the property.");
        }
        var hint = hints.Count > 0
            ? string.Join(" ", hints)
            : "`opticli types --orphaned` lists the types whose class is gone, `opticli type <type>` the properties that aren't in the code (existsOnModel: false).";
        return new AgentException(code, $"Nothing was removed. {message}", hint) { Validation = issues, Reason = AgentErrorReasons.Orphans };
    }

    private static string Label(string type, string? property) => property is null ? type : $"{type}.{property}";

    private static SiteType? Find(IReadOnlyList<SiteType> types, string name)
    {
        var trimmed = name.Trim();
        return types.FirstOrDefault(t => string.Equals(t.Name, trimmed, StringComparison.OrdinalIgnoreCase))
            ?? (Guid.TryParse(trimmed, out var guid) ? types.FirstOrDefault(t => t.Guid == guid) : null);
    }

    private static IEnumerable<string> Distinct(IEnumerable<string> names) => names.Select(n => n.Trim()).Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase);

    private static string Suggest(string name, IEnumerable<string> candidates) =>
        Suggestions.DidYouMean(name, candidates) is { } suggestion ? $" {suggestion}" : "";
}
