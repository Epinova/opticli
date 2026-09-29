using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using OptiCli.Core.Errors;

namespace OptiCli.Core.Writes;

/// <param name="Index">Zero-based position in the plan.</param>
/// <param name="DependsOn">Plan ids (without <c>$</c>) of earlier steps whose content this step refers to.</param>
public sealed record PlanStep(int Index, WriteOperation Operation, IReadOnlySet<string> DependsOn);

/// <summary>
/// An <c>opticli apply</c> plan: <c>{"operations": [{"op": "create", "id": "page", ...}, {"op": "area", "ref": "$page", ...}]}</c>.
/// </summary>
/// <remarks>
/// <para>Each operation has the fields of the matching command (see <see cref="Fields"/>). A create or
/// block step may have an <c>id</c>; later steps refer to the content it creates as <c>"$id"</c>, in ref
/// fields and as a whole string value inside <c>properties</c>.</para>
/// <para>Parsing checks the whole shape at once and reports every problem: unknown ops and fields, missing
/// required fields, wrong JSON types, duplicate ids, and <c>$id</c>s that aren't defined by an earlier step.</para>
/// </remarks>
public sealed partial class WritePlan
{
    /// <summary>Allowed fields per op; required ones end with <c>*</c>.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> Fields = new Dictionary<string, string[]>
    {
        ["set"] = ["ref*", "properties", "name", "lang", "publish", "baseVersion", "force"],
        ["create"] = ["parent*", "type*", "name*", "properties", "lang", "publish", "id"],
        ["area"] = ["ref*", "property*", "action*", "item", "index", "at", "to", "display", "lang", "publish", "baseVersion", "force"],
        ["block"] = ["type*", "name*", "for", "parent", "properties", "lang", "publish", "id"],
        ["translate"] = ["ref*", "lang*", "name", "properties", "publish"],
        ["publish"] = ["ref*", "version", "lang"],
        ["move"] = ["ref*", "to*"],
        ["delete"] = ["ref*"],
    };

    private WritePlan(IReadOnlyList<PlanStep> steps) => Steps = steps;

    public IReadOnlyList<PlanStep> Steps { get; }

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
        if (root is not JsonObject { } plan || plan["operations"] is not JsonArray { Count: > 0 } operations || plan.Count != 1)
        {
            throw new UsageException("""A plan is an object with one field, "operations": a non-empty array.""", PlanHint);
        }

        var problems = new List<string>();
        var parsed = new List<(WriteOperation? Op, int Index)>();
        for (var i = 0; i < operations.Count; i++)
        {
            parsed.Add((ParseStep(operations[i], i, problems), i));
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
        return new WritePlan(steps);
    }

    public const string PlanHint =
        """Example: {"operations": [{"op": "create", "id": "page", "parent": "123", "type": "ArticlePage", "name": "News"}, {"op": "set", "ref": "$page", "properties": {"Heading": "Hello"}}]}. Run `opticli apply --help` for every op's fields.""";

    /// <summary>The step's operation with <c>$id</c>s replaced by the ids of the content created for them.</summary>
    public static WriteOperation Resolve(PlanStep step, IReadOnlyDictionary<string, int> created) =>
        step.DependsOn.Count == 0
            ? step.Operation
            : step.Operation.MapRefs(value =>
                value.StartsWith('$') && created.TryGetValue(value[1..], out var id) ? id.ToString(CultureInfo.InvariantCulture) : value);

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
                    : $"operations[{index}]: '{reference}' is not the id of an earlier create or block operation.");
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

    private static WriteOperation? ParseStep(JsonNode? node, int index, List<string> problems)
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
            "set" => new SetOperation(reader.Ref("ref"), reader.Object("properties"), reader.String("name"), reader.String("lang"), reader.Bool("publish"), reader.Int("baseVersion"), reader.Bool("force")),
            "create" => new CreateOperation(reader.Ref("parent"), reader.String("type") ?? "", reader.String("name") ?? "", reader.Object("properties"), reader.String("lang"), reader.Bool("publish")),
            "area" => new AreaEdit(reader.Ref("ref"), reader.String("property") ?? "", reader.String("action") ?? "", reader.OptionalRef("item"), reader.Int("index"), reader.Int("at"), reader.Int("to"), reader.String("display"), reader.String("lang"), reader.Bool("publish"), reader.Int("baseVersion"), reader.Bool("force")),
            "block" => new BlockCreateOperation(reader.String("type") ?? "", reader.String("name") ?? "", reader.OptionalRef("for"), reader.OptionalRef("parent"), reader.Object("properties"), reader.String("lang"), reader.Bool("publish")),
            "translate" => new TranslateOperation(reader.Ref("ref"), reader.String("lang") ?? "", reader.String("name"), reader.Object("properties"), reader.Bool("publish")),
            "publish" => new PublishOperation(reader.Ref("ref"), reader.Int("version"), reader.String("lang")),
            "move" => new MoveOperation(reader.Ref("ref"), reader.Ref("to")),
            _ => new DeleteOperation(reader.Ref("ref")),
        };

        if (operation is AreaEdit area && area.Action is not ("add" or "remove" or "move") && step["action"] is not null)
        {
            problems.Add($"{reader.Where}: \"action\" must be add, remove or move.");
        }
        if (operation is BlockCreateOperation block && (block.For is null) == (block.Parent is null))
        {
            problems.Add($"{reader.Where}: give exactly one of \"for\" and \"parent\".");
        }
        return operation with { Id = id };
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

        public int? Int(string name) => step[name] switch
        {
            null => null,
            JsonValue value when value.TryGetValue<int>(out var number) => number,
            _ => Problem<int?>(name, "an integer"),
        };

        public bool Bool(string name) => step[name] switch
        {
            null => false,
            JsonValue value when value.TryGetValue<bool>(out var flag) => flag,
            _ => Problem<bool>(name, "true or false"),
        };

        public JsonObject? Object(string name) => step[name] switch
        {
            null => null,
            JsonObject obj => (JsonObject)obj.DeepClone(),
            _ => Problem<JsonObject>(name, "an object of property names to values"),
        };

        private T? Problem<T>(string name, string expected)
        {
            problems.Add($"{Where}: \"{name}\" must be {expected}.");
            return default;
        }
    }
}
