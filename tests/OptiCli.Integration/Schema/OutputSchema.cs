using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;

namespace OptiCli.Integration.Schema;

/// <summary>A command line as the schema describes it: the command's words, its positional arguments, its options.</summary>
/// <param name="Path">The command's words (<c>["projects", "ready"]</c>).</param>
/// <param name="Arguments">The positional arguments, in order (a variadic one's values one by one).</param>
/// <param name="Options">The options as typed (<c>["--status", "draft", "--limit", "5"]</c>); global ones too.</param>
internal sealed record Invocation(IReadOnlyList<string> Path, IReadOnlyList<string> Arguments, IReadOnlyList<string> Options)
{
    public string Command => string.Join(' ', Path);

    public IReadOnlyList<string> Args => [.. Path, .. Arguments, .. Options];
}

/// <summary>The result a command declares for one invocation: the mode that applies (first match wins) and its JSON Schema.</summary>
/// <param name="Mode">"otherwise", or the mode's condition as the schema writes it.</param>
internal sealed record DeclaredResult(string Command, string Mode, JsonNode Schema, bool Raw);

/// <summary>
/// opticli's description of its commands (<c>opticli schema --json</c>, from the build under test): which result a command
/// line declares, picked from its <c>modes</c> as opticli picks them (and as the npm package's overloads do), and a
/// standard JSON Schema 2020-12 validator (JsonSchema.Net) for what it printed.
/// </summary>
internal sealed class OutputSchema
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static OutputSchema? loaded;

    private readonly JsonObject defs;
    private readonly Dictionary<string, JsonObject> commands;
    private readonly HashSet<string> globalFlags;
    private readonly ConcurrentDictionary<string, JsonSchema> compiled = new(StringComparer.Ordinal);

    private OutputSchema(JsonObject schema)
    {
        defs = schema["$defs"]!.AsObject();
        NpmTypes(defs);
        commands =schema["commands"]!.AsArray().Select(c => c!.AsObject()).ToDictionary(c => string.Join(' ', c["path"]!.AsArray().Select(w => w!.GetValue<string>())), StringComparer.Ordinal);
        globalFlags = schema["globalOptions"]!.AsArray().Where(o => o!["type"]!["type"]?.GetValue<string>() == "boolean").Select(o => o!["name"]!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
        Version = schema["opticli"]?.GetValue<string>() ?? "?";
    }

    public string Version { get; }

    /// <summary>
    /// What the npm package types where the schema leaves a value open (<c>x-dynamic</c>; npm/scripts/overrides.mjs and
    /// npm/src/results.ts), so real output is checked against those types too: <c>get</c>'s properties as
    /// <c>PropertyOut</c>, <c>sql</c>'s cells as <c>SqlValue</c>, an error's details as an object, and a plan step's
    /// result as the output of its op (<c>AppliedStep</c>).
    /// </summary>
    private static void NpmTypes(JsonObject defs)
    {
        defs["NpmPropertyOut"] = JsonNode.Parse("""
            {"type": "object", "required": ["type", "value"], "properties": {
              "type": {"type": "string"}, "culture": {"type": "string"}, "truncated": {"type": "boolean"}, "length": {"type": "integer"}}}
            """);
        defs["ContentDocument"]!["properties"]!["properties"] = JsonNode.Parse("""{"type": "object", "additionalProperties": {"$ref": "#/$defs/NpmPropertyOut"}}""");
        defs["ErrorBody"]!["properties"]!["details"] = JsonNode.Parse("""{"type": "object"}""");
        defs["SqlResult"]!["properties"]!["rows"] = JsonNode.Parse("""
            {"type": "array", "items": {"type": "object", "additionalProperties": {"type": ["string", "number", "boolean", "null"]}}}
            """);
        var byOp = new (string[] Ops, string Result)[]
        {
            (["create", "block", "upload", "set", "area", "composition", "publish", "unpublish", "discard"], """{"$ref": "#/$defs/WriteOutput"}"""),
            (["translate"], """{"anyOf": [{"$ref": "#/$defs/WriteOutput"}, {"$ref": "#/$defs/RemoveLanguageOutput"}]}"""),
            (["move", "delete"], """{"$ref": "#/$defs/MoveOutput"}"""),
            (["restore"], """{"$ref": "#/$defs/RestoreOutput"}"""),
            (["access"], """{"$ref": "#/$defs/AccessOutput"}"""),
            (["site"], """{"$ref": "#/$defs/SiteHostsView"}"""),
            (["language"], """{"$ref": "#/$defs/LanguagesOutput"}"""),
        };
        // A failed or invalid step's result is the error's details instead.
        defs["PlanStepResult"]!["allOf"] = new JsonArray(byOp.Select(o => (JsonNode)new JsonObject
        {
            ["if"] = new JsonObject
            {
                ["required"] = new JsonArray("op", "result"),
                ["properties"] = new JsonObject
                {
                    ["op"] = new JsonObject { ["enum"] = new JsonArray(o.Ops.Select(op => (JsonNode)op).ToArray()) },
                    ["status"] = new JsonObject { ["not"] = JsonNode.Parse("""{"enum": ["failed", "invalid"]}""") },
                },
            },
            ["then"] = new JsonObject { ["properties"] = new JsonObject { ["result"] = JsonNode.Parse(o.Result) } },
        }).ToArray());
    }

    /// <summary>Every command and mode the schema declares a JSON result for: <c>(command, mode)</c>.</summary>
    public IEnumerable<(string Command, string Mode, bool Writes)> Modes() =>
        commands.Values.SelectMany(c =>
        {
            var path = string.Join(' ', c["path"]!.AsArray().Select(w => w!.GetValue<string>()));
            var modes = (c["modes"]?.AsArray() ?? []).Select(m => (path, Describe(m!["when"]!), m["writes"]?.GetValue<bool>() ?? false));
            return c["result"] is null ? modes : modes.Append((path, "otherwise", c["writes"]?.GetValue<bool>() ?? false));
        });

    public static async Task<OutputSchema> LoadAsync()
    {
        await Gate.WaitAsync();
        try
        {
            if (loaded is null)
            {
                var run = await OptiCliProcess.RunAsync(["schema", "--json"]);
                if (!run.Ok || run.Data is not JsonObject data)
                {
                    throw new InvalidOperationException($"opticli schema --json failed ({run.ExitCode}): {run.Stdout}{run.Stderr}");
                }
                loaded = new OutputSchema(data);
            }
            return loaded;
        }
        finally
        {
            Gate.Release();
        }
    }

    public bool Has(string command) => commands.ContainsKey(command);

    /// <summary>The values an option takes when it has a fixed set (<c>find --status</c>), else none.</summary>
    public IReadOnlyList<string> OptionValues(string command, string option) =>
        commands[command]["options"]!.AsArray().FirstOrDefault(o => o!["name"]!.GetValue<string>() == option)?["type"]?["enum"]?.AsArray().Select(v => v!.GetValue<string>()).ToList()
        ?? throw new InvalidOperationException($"{command} {option} has no fixed values in the schema.");

    /// <summary>The result the schema declares for this command line: the first mode whose condition holds, else the command's.</summary>
    public DeclaredResult ResultFor(Invocation invocation)
    {
        var command = commands.TryGetValue(invocation.Command, out var found) ? found : throw new InvalidOperationException($"The schema has no command '{invocation.Command}'.");
        var given = Given(command, invocation);
        foreach (var mode in command["modes"]?.AsArray() ?? [])
        {
            if (Holds(mode!["when"]!, given, invocation))
            {
                return Declared(invocation.Command, Describe(mode["when"]!), mode["result"], mode["raw"]);
            }
        }
        return Declared(invocation.Command, "otherwise", command["result"], command["raw"]);
    }

    /// <summary>What an error envelope's <c>error</c> must be.</summary>
    public JsonNode ErrorSchema => new JsonObject { ["$ref"] = "#/$defs/ErrorBody" };

    /// <summary>
    /// The places where <paramref name="instance"/> doesn't match <paramref name="schema"/> (a result, or a <c>$defs</c>
    /// reference), each with the JSON Pointer, the keyword and the value found there; empty when it matches. Formats
    /// (<c>uuid</c>, <c>date-time</c>) are checked too.
    /// </summary>
    public IReadOnlyList<string> Validate(JsonNode schema, JsonNode? instance)
    {
        var key = schema.ToJsonString();
        var compiledSchema = compiled.GetOrAdd(key, _ =>
        {
            var root = schema.DeepClone().AsObject();
            root["$schema"] = "https://json-schema.org/draft/2020-12/schema";
            root["$id"] = $"https://opticli.example/schema/{Guid.NewGuid():N}";
            var all = defs.DeepClone().AsObject();
            foreach (var (name, definition) in (root["$defs"] as JsonObject)?.ToList() ?? [])
            {
                all[name] = definition?.DeepClone();
            }
            root["$defs"] = all;
            return JsonSchema.FromText(root.ToJsonString());
        });
        var result = compiledSchema.Evaluate(instance, new EvaluationOptions { OutputFormat = OutputFormat.List, RequireFormatValidation = true });
        if (result.IsValid)
        {
            return [];
        }
        // List output: every node that failed, with its own errors. anyOf branches fail too when another holds, but then
        // the anyOf itself is valid and its branches' errors aren't reported upward; keep the leaves of failing paths. An
        // if that doesn't hold (a plan step's result checked by its op: every other op's condition) is no problem.
        var problems = new List<string>();
        foreach (var detail in result.Details.Where(d => !d.IsValid && d.HasErrors && !d.EvaluationPath.ToString().Split('/').Contains("if")))
        {
            var at = detail.InstanceLocation.ToString();
            var value = Pick(instance, at);
            foreach (var (keyword, message) in detail.Errors!)
            {
                problems.Add($"{(at.Length == 0 ? "/" : at)} [{detail.EvaluationPath}] {keyword}: {message} (value: {Short(value)})");
            }
        }
        return problems.Count > 0 ? problems : ["invalid (no detail)"];
    }

    private DeclaredResult Declared(string command, string mode, JsonNode? result, JsonNode? raw) =>
        new(command, mode, result?.DeepClone() ?? new JsonObject(), raw?.GetValue<bool>() == true && result is null);

    /// <summary>Which options were given, with their values, as opticli's parser reads them (a flag takes no value).</summary>
    private Dictionary<string, List<string>> Given(JsonObject command, Invocation invocation)
    {
        var flags = command["options"]!.AsArray()
            .Where(o => o!["type"]!["type"]?.GetValue<string>() == "boolean")
            .Select(o => o!["name"]!.GetValue<string>())
            .Concat(globalFlags)
            .ToHashSet(StringComparer.Ordinal);
        var given = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        for (var i = 0; i < invocation.Options.Count; i++)
        {
            var name = invocation.Options[i];
            if (!name.StartsWith("--", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"{invocation.Command}: '{name}' isn't an option; arguments go in Arguments.");
            }
            var values = given.TryGetValue(name, out var list) ? list : given[name] = [];
            if (flags.Contains(name))
            {
                // A three-state flag may be followed by true or false.
                if (i + 1 < invocation.Options.Count && invocation.Options[i + 1] is "true" or "false")
                {
                    values.Add(invocation.Options[++i]);
                }
                else
                {
                    values.Add("true");
                }
            }
            else if (i + 1 < invocation.Options.Count)
            {
                values.Add(invocation.Options[++i]);
            }
        }
        return given;
    }

    private bool Holds(JsonNode when, Dictionary<string, List<string>> given, Invocation invocation)
    {
        if (when["anyOf"] is JsonArray any)
        {
            return any.Any(w => Holds(w!, given, invocation));
        }
        if (when["argument"] is { } argument)
        {
            var command = commands[invocation.Command];
            var index = command["arguments"]!.AsArray().Select(a => a!["name"]!.GetValue<string>()).ToList().IndexOf(argument.GetValue<string>());
            return index >= 0 && invocation.Arguments.Count > index;
        }
        var option = when["option"]!.GetValue<string>();
        if (!given.TryGetValue(option, out var values) || values.Count == 0 || values.All(v => v == "false"))
        {
            return false;
        }
        return when["values"] is not JsonArray allowed || values.Any(v => allowed.Any(a => a!.GetValue<string>() == v));
    }

    private static string Describe(JsonNode when) => when["anyOf"] is JsonArray any
        ? string.Join(" | ", any.Select(w => Describe(w!)))
        : when["argument"] is { } argument ? $"<{argument.GetValue<string>()}>"
        : when["values"] is JsonArray values ? $"{when["option"]} {string.Join('|', values.Select(v => v!.GetValue<string>()))}"
        : when["option"]!.GetValue<string>();

    private static JsonNode? Pick(JsonNode? root, string pointer)
    {
        var node = root;
        foreach (var part in pointer.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal)))
        {
            node = node switch
            {
                JsonObject obj => obj[part],
                JsonArray array when int.TryParse(part, out var index) && index < array.Count => array[index],
                _ => null,
            };
        }
        return node;
    }

    private static string Short(JsonNode? node)
    {
        var text = node?.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) ?? "absent";
        return text.Length > 300 ? text[..300] + "…" : text;
    }
}
