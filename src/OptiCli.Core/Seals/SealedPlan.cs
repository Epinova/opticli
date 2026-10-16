using System.Text.Json.Nodes;
using OptiCli.Core.Content;
using OptiCli.Core.Errors;
using OptiCli.Core.Writes;

namespace OptiCli.Core.Seals;

/// <summary>
/// What a plan may do to production through a sealed apply: create, change, move, translate and publish content, nothing
/// that deletes, takes offline, changes access or writes a file; and only by GUID, as content ids and URLs name other
/// content in another database. Checked when sealing and again when the sealed plan runs.
/// </summary>
public static class SealedPlan
{
    /// <summary><c>details.reason</c> of the refusal.</summary>
    public const string Reason = "sealedPlan";

    /// <summary>Ops a sealed plan never has, with why.</summary>
    public static readonly IReadOnlyDictionary<string, string> RefusedOps = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["delete"] = "deletes content",
        ["access"] = "changes access rights",
        ["unpublish"] = "takes content offline",
        ["discard"] = "deletes a draft",
        ["upload"] = "writes a media file, which would land in the local blob folder while production's database points at it (media goes to production through the CMS's edit UI)",
    };

    /// <exception cref="RefusedException">The plan does something a sealed apply doesn't (<c>details.problems</c> lists every one).</exception>
    /// <param name="model">The content model, to find values of reference properties given as ids; null skips that check.</param>
    public static void Require(WritePlan plan, CmsModel? model)
    {
        var problems = Problems(plan, model);
        if (problems.Count > 0)
        {
            throw new RefusedException(
                $"The plan has {problems.Count} step(s) a sealed apply to production doesn't run: {string.Join(" ", problems.Take(5))}{(problems.Count > 5 ? " …" : "")}",
                "A sealed apply creates, changes, moves, translates and publishes content by GUID, and nothing else. Leave those steps out (the user does them in the CMS's edit UI), then seal again.")
            {
                Details = new { reason = Reason, problems },
            };
        }
    }

    public static IReadOnlyList<string> Problems(WritePlan plan, CmsModel? model)
    {
        var references = model is null ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) : ReferenceProperties(model);
        var problems = new List<string>();
        foreach (var step in plan.Steps)
        {
            var op = step.Operation;
            var where = $"operations[{step.Index}] ({op.Kind})";
            if (RefusedOps.TryGetValue(op.Kind, out var why))
            {
                problems.Add($"{where} {why}.");
                continue;
            }
            if (op is TranslateOperation { Remove: true })
            {
                problems.Add($"{where} removes a language branch.");
            }
            if (op.IncludeDraft)
            {
                problems.Add($"{where} has includeDraft: it would publish someone else's unpublished changes.");
            }
            if (op is SetOperation { Force: true } or AreaEdit { Force: true } or CompositionEdit { Force: true })
            {
                problems.Add($"{where} has force: it would skip the check that nobody saved a newer version.");
            }
            if (op is SetOperation { BaseVersion: not null } or AreaEdit { BaseVersion: not null } or CompositionEdit { BaseVersion: not null } or PublishOperation { Version: not null }
                || op is SetOperation { From.IsPublished: false } or AreaEdit { From.IsPublished: false } or CompositionEdit { From.IsPublished: false })
            {
                problems.Add($"{where} names a version by its id, which is another version in production (\"from\": \"published\" works).");
            }
            if (op is CreateOperation or BlockCreateOperation && op.ContentGuid is null)
            {
                problems.Add($"{where} has no GUID (\"guid\", or the plan's \"guidNamespace\"): run again, it would make a second copy, and the seal couldn't check it.");
            }
            if (op is CreateOperation { Blueprint: { } blueprint } && !Guid.TryParse(blueprint, out _))
            {
                problems.Add($"{where} makes content from blueprint '{blueprint}': give the blueprint by its GUID.");
            }
            foreach (var reference in op.Refs.OfType<string>().Where(r => !ByGuid(r)))
            {
                problems.Add($"{where} refers to '{reference}': give content by its GUID (or $id of content the plan creates), as ids and URLs name other content in production.");
            }
            foreach (var (path, value) in Values(op).SelectMany(v => IdValues(v.Values, v.Name, references)))
            {
                problems.Add($"{where} {path} is '{value}', a content id: give the GUID (or $id), as ids name other content in production.");
            }
        }
        return problems;
    }

    /// <summary>The existing content the plan writes, by GUID: the GUIDs its creating steps give, and the GUID refs of the steps that change content.</summary>
    public static IReadOnlyList<Guid> Touched(WritePlan plan) =>
        plan.Steps.Select(s => s.Operation).SelectMany(op => op switch
        {
            CreateOperation or BlockCreateOperation or UploadOperation => new[] { op.ContentGuid },
            SetOperation set => new[] { GuidOf(set.Ref) },
            AreaEdit area => new[] { GuidOf(area.Ref) },
            CompositionEdit composition => new[] { GuidOf(composition.Ref) },
            TranslateOperation translate => new[] { GuidOf(translate.Ref) },
            PublishOperation publish => new[] { GuidOf(publish.Ref) },
            MoveOperation move => new[] { GuidOf(move.Ref) },
            RestoreOperation restore => new[] { GuidOf(restore.Ref) },
            _ => [],
        }).OfType<Guid>().Distinct().ToList();

    /// <summary>The other content the plan's steps name by GUID (parents, owners, move targets, area items): it must still be there.</summary>
    public static IReadOnlyList<Guid> Referenced(WritePlan plan)
    {
        var touched = Touched(plan).ToHashSet();
        return plan.Steps.SelectMany(s => s.Operation.Refs).Select(GuidOf).OfType<Guid>().Where(g => !touched.Contains(g)).Distinct().ToList();
    }

    private static Guid? GuidOf(string? reference) => Guid.TryParse(reference, out var guid) ? guid : null;

    /// <summary>A GUID, or <c>$id</c> of content the plan creates (<see cref="WritePlan"/> checks that it is one).</summary>
    private static bool ByGuid(string reference) => Guid.TryParse(reference, out _) || reference.StartsWith('$');

    private static IEnumerable<(string Name, JsonObject Values)> Values(WriteOperation op)
    {
        var values = op switch
        {
            SetOperation set => set.Properties,
            CreateOperation create => create.Properties,
            BlockCreateOperation block => block.Properties,
            TranslateOperation translate => translate.Properties,
            AreaEdit area => area.Values,
            CompositionEdit composition => composition.Value,
            _ => null,
        };
        return values is null ? [] : [(op is CompositionEdit ? "value" : op is AreaEdit ? "values" : "properties", values)];
    }

    /// <summary>The names a plan sets the expiry by (<c>StopPublish</c>, or a page's <c>PageStopPublish</c>).</summary>
    private static readonly HashSet<string> ExpiryNames = new(StringComparer.OrdinalIgnoreCase) { "StopPublish", "PageStopPublish" };

    /// <summary>
    /// Whether the step sets an expiry (<c>StopPublish</c>) that is <paramref name="now"/> or earlier: once published, that
    /// content is offline, as after an unpublish. Not refused (content already expired in production has one too, and a
    /// sync carries it), but counted in what the user confirms (<see cref="SealSummary.Expiring"/>).
    /// </summary>
    public static bool SetsPassedExpiry(WriteOperation op, DateTimeOffset now) =>
        Values(op).Any(v => v.Values.Any(p => ExpiryNames.Contains(p.Key) && p.Value is JsonValue leaf && leaf.TryGetValue<string>(out var text)
            && DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeLocal, out var when) && when <= now));

    /// <summary>The names of reference properties (ContentReference, PageReference, lists of them) of any content type.</summary>
    private static HashSet<string> ReferenceProperties(CmsModel model) =>
        model.Properties.Values
            .Where(p => p.BaseType is PropertyBaseType.ContentReference or PropertyBaseType.PageReference
                || p.TypeName is "ContentReference" or "PageReference" or "ContentReferenceList")
            .Select(p => p.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Strings that name content by id: a <c>ref</c> inside a value (a ContentArea item, a composition's shared block), and
    /// the value of a property that some content type has as a reference, at any depth (local blocks). Conservative: a
    /// numeric text in a same-named property of another kind is refused too.
    /// </summary>
    internal static IEnumerable<(string Path, string Value)> IdValues(JsonNode? node, string path, IReadOnlySet<string> references, bool reference = false)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, value) in obj)
                {
                    foreach (var found in IdValues(value, $"{path}.{key}", references, key == "ref" || references.Contains(key)))
                    {
                        yield return found;
                    }
                }
                break;
            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    foreach (var found in IdValues(array[i], $"{path}[{i}]", references, reference))
                    {
                        yield return found;
                    }
                }
                break;
            case JsonValue leaf when reference && leaf.TryGetValue<string>(out var text) && text.Trim().Length > 0 && !ByGuid(text.Trim()):
                yield return (path, text);
                break;
            case JsonValue leaf when reference && leaf.TryGetValue<long>(out var number):
                yield return (path, number.ToString(System.Globalization.CultureInfo.InvariantCulture));
                break;
        }
    }
}
