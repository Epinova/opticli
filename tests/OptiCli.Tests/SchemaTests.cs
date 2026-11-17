using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Nodes;
using OptiCli.Cli;
using OptiCli.Commands;
using OptiCli.Core.Output;
using OptiCli.Core.Queries;

namespace OptiCli.Tests;

/// <summary>
/// <c>opticli schema</c>, which the npm package's TypeScript is generated from: every command declares a result type the
/// schema can describe, and the schema says what the code does.
/// </summary>
public class SchemaTests
{
    private static readonly RootCommand Root = Program.CreateRoot(new GlobalOptions());

    private static readonly JsonObject Schema = SchemaCommand.Describe(Root);

    /// <summary>
    /// Where a value's shape is unknown by design (<c>x-dynamic</c>): property values, which depend on the content type;
    /// SQL cells; a plan step's result, which is any operation's output or an error's details; and error details.
    /// Anything else needs a type (or <see cref="OutputSchemaAttribute"/>).
    /// </summary>
    private static readonly HashSet<string> DynamicPlaces =
    [
        "ContentDocument.properties",
        "ContentDocument.composition",
        "PropertyChange.before",
        "PropertyChange.after",
        "SiteChangedProperty.asked",
        "SiteChangedProperty.saved",
        "CompositionFieldChange.before",
        "CompositionFieldChange.after",
        "SqlResult.rows",
        "PlanStepResult.result",
        "ErrorBody.details",
    ];

    private static JsonObject Definitions => Schema["$defs"]!.AsObject();

    private static JsonObject Command(string path) =>
        Schema["commands"]!.AsArray().Select(c => c!.AsObject()).Single(c => string.Join(' ', c["path"]!.AsArray().Select(p => p!.GetValue<string>())) == path);

    private static string Ref(JsonNode? schema) => schema?["$ref"]?.GetValue<string>().Replace("#/$defs/", "", StringComparison.Ordinal) ?? throw new InvalidOperationException($"Not a $ref: {schema}");

    [Fact]
    public void Every_command_with_an_action_declares_its_result()
    {
        var missing = Walk(Root, []).Where(c => c.Command.Action is not null and not SummaryHelpAction and not System.CommandLine.Help.HelpAction && CommandRunner.Results(c.Command) is null).Select(c => c.Path);

        Assert.Empty(missing);
    }

    [Fact]
    public void Every_visible_command_with_an_action_is_in_the_schema()
    {
        var expected = Walk(Root, []).Where(c => CommandRunner.Results(c.Command) is not null && !c.Hidden).Select(c => c.Path).Order(StringComparer.Ordinal).ToList();
        var described = Schema["commands"]!.AsArray().Select(c => string.Join(' ', c!["path"]!.AsArray().Select(p => p!.GetValue<string>()))).Order(StringComparer.Ordinal).ToList();

        Assert.Equal(expected, described);
        Assert.DoesNotContain("schema", described);
    }

    [Fact]
    public void Every_result_type_can_be_described()
    {
        var problems = new List<string>();
        foreach (var (name, definition) in Definitions)
        {
            var node = definition!.AsObject();
            if (name.Contains("Anonymous", StringComparison.Ordinal) || name.Contains('<', StringComparison.Ordinal))
            {
                problems.Add($"{name}: an anonymous type; declare a record with the same property names");
            }
            if (node["type"]?.GetValue<string>() == "object" && node["properties"]!.AsObject().Count == 0)
            {
                problems.Add($"{name} ({node["x-csharp"]}): no public properties, so it is written as {{}}");
            }
            foreach (var (property, schema) in node["properties"]?.AsObject() ?? [])
            {
                if (IsDynamic(schema) && !DynamicPlaces.Contains($"{name}.{property}"))
                {
                    problems.Add($"{name}.{property}: object, JsonNode or JsonElement, which the schema can't describe; give it a type, or [OutputSchema] if a type would change the JSON");
                }
            }
        }
        foreach (var command in Schema["commands"]!.AsArray())
        {
            var results = new[] { command!["result"] }.Concat(command["modes"]?.AsArray().Select(m => m!["result"]) ?? []);
            if (results.Any(IsDynamic))
            {
                problems.Add($"{string.Join(' ', command["path"]!.AsArray())}: its data is object, JsonNode or JsonElement");
            }
        }

        Assert.Empty(problems);
    }

    [Fact]
    public void Every_dynamic_place_still_exists()
    {
        var stale = DynamicPlaces.Where(place => place.Split('.') is [var type, var property] && Definitions[type]?["properties"]?[property] is null);

        Assert.Empty(stale);
    }

    [Fact]
    public void Every_reference_resolves()
    {
        var unresolved = Refs(Schema).Where(r => !Definitions.ContainsKey(r)).Distinct();

        Assert.Empty(unresolved);
    }

    [Fact]
    public void Global_options_are_listed_once()
    {
        var global = Schema["globalOptions"]!.AsArray().Select(o => o!["name"]!.GetValue<string>()).ToList();
        var repeated = Schema["commands"]!.AsArray().SelectMany(c => c!["options"]!.AsArray().Select(o => o!["name"]!.GetValue<string>())).Where(global.Contains).Distinct();

        Assert.Equal(["--project", "--connection", "--connection-name", "--db", "--profile", "--json", "--text"], global);
        Assert.Empty(repeated);
    }

    [Fact]
    public void A_list_command_describes_its_items_options_and_paging()
    {
        var languages = Command("languages");

        Assert.Equal("array", languages["result"]!["type"]!.GetValue<string>());
        Assert.Equal("LanguageInfo", Ref(languages["result"]!["items"]));
        Assert.True(languages["lists"]!.GetValue<bool>());
        Assert.False(languages["writes"]!.GetValue<bool>());
        var names = languages["options"]!.AsArray().Select(o => o!["name"]!.GetValue<string>()).ToList();
        Assert.Contains("--all", names);
        Assert.Contains("--cursor", names);
        Assert.DoesNotContain("--jsonl", names);
        var code = Definitions["LanguageInfo"]!["properties"]!["code"]!;
        Assert.Equal("string", code["type"]!.GetValue<string>());
    }

    [Fact]
    public void A_write_says_so_and_names_its_output()
    {
        var set = Command("set");

        Assert.True(set["writes"]!.GetValue<bool>());
        Assert.Equal("WriteOutput", Ref(set["result"]));
        Assert.Null(set["modes"]);
        Assert.Contains(set["options"]!.AsArray(), o => o!["name"]!.GetValue<string>() == "--in-project");
        var properties = set["arguments"]!.AsArray().Single(a => a!["name"]!.GetValue<string>() == "properties")!;
        Assert.Equal(0, properties["minimum"]!.GetValue<int>());
        Assert.Null(properties["maximum"]);
    }

    [Fact]
    public void Serve_has_a_result_per_mode()
    {
        var serve = Command("serve");
        var modes = serve["modes"]!.AsArray().ToDictionary(m => m!["when"]!["option"]!.GetValue<string>(), m => Ref(m!["result"]));

        Assert.Equal("AgentStatus", Ref(serve["result"]));
        Assert.Equal("AgentStatus", modes["--status"]);
        Assert.Equal("ServeLogs", modes["--logs"]);
        Assert.Equal("ServeStopOutput", modes["--stop"]);
        Assert.Equal("ServeForegroundOutput", modes["--foreground"]);
        var foreground = Definitions["ServeForegroundOutput"]!["anyOf"]!.AsArray().Select(Ref);
        Assert.Equal(["AgentStatus", "ServeStopOutput"], foreground);
    }

    [Theory]
    [InlineData("--https")]
    [InlineData("--scheduler")]
    public void A_three_state_flag_is_passed_as_false_explicitly(string name)
    {
        var option = Command("serve")["options"]!.AsArray().Single(o => o!["name"]!.GetValue<string>() == name)!;

        Assert.True(option["explicitFalse"]!.GetValue<bool>());
        Assert.Null(option["default"]);
        Assert.Equal("boolean", option["type"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public void Find_rows_depend_on_status()
    {
        var find = Command("find");
        var modes = find["modes"]!.AsArray();

        Assert.Equal("ContentIdentity", Ref(find["result"]!["items"]));
        Assert.Equal("--status", modes[0]!["when"]!["option"]!.GetValue<string>());
        Assert.Equal(["scheduled", "expired"], modes[0]!["when"]!["values"]!.AsArray().Select(v => v!.GetValue<string>()));
        Assert.Equal("TimedItem", Ref(modes[0]!["result"]!["items"]));
        Assert.Equal(["draft"], modes[1]!["when"]!["values"]!.AsArray().Select(v => v!.GetValue<string>()));
        Assert.Equal("DraftItem", Ref(modes[1]!["result"]!["items"]));
        var status = find["options"]!.AsArray().Single(o => o!["name"]!.GetValue<string>() == "--status")!;
        Assert.Equal("any", status["default"]!.GetValue<string>());
        Assert.Equal(5, status["type"]!["enum"]!.AsArray().Count);
    }

    [Fact]
    public void Access_changes_with_any_change_option()
    {
        var access = Command("access");
        var when = access["modes"]!.AsArray().Single()!["when"]!["anyOf"]!.AsArray().Select(c => c!["option"]!.GetValue<string>());

        Assert.Equal("AccessView", Ref(access["result"]));
        Assert.Equal("AccessOutput", Ref(access["modes"]![0]!["result"]));
        Assert.Equal(["--grant", "--user", "--revoke", "--break-inheritance", "--inherit"], when);
    }

    [Fact]
    public void Env_is_a_write_and_raw_without_json()
    {
        var env = Command("env");

        Assert.True(env["writes"]!.GetValue<bool>());
        Assert.NotNull(env["writesNote"]);
        Assert.True(env["raw"]!.GetValue<bool>());
        Assert.Null(env["result"]);
        Assert.Equal("EnvOutput", Ref(env["modes"]![0]!["result"]));
        Assert.True(Command("skill print")["raw"]!.GetValue<bool>());
    }

    [Fact]
    public void Nullable_properties_may_be_absent()
    {
        var output = Definitions["WriteOutput"]!.AsObject();
        var required = output["required"]!.AsArray().Select(r => r!.GetValue<string>()).ToList();

        // A dry-run create has no ref yet.
        Assert.DoesNotContain("ref", required);
        Assert.Contains("dryRun", required);
        Assert.False(output["additionalProperties"]!.GetValue<bool>());
    }

    [Fact]
    public void Doc_comments_become_descriptions()
    {
        Assert.Contains("How every command names a content item", Definitions["ContentIdentity"]!["description"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains("for language-invariant content", Definitions["ContentIdentity"]!["properties"]!["language"]!["description"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal("anyOf", Definitions["JobView"]!["properties"]!["running"]!.AsObject().First().Key);
    }

    [Fact]
    public void A_derived_property_with_an_order_comes_last()
    {
        var properties = Definitions["PageUsageRow"]!["properties"]!.AsObject().Select(p => p.Key).ToList();

        Assert.Equal("ref", properties[0]);
        Assert.Equal("via", properties[^1]);
    }

    [Fact]
    public void Types_that_share_a_name_are_qualified()
    {
        var types = new SchemaCommand.TypeSchemas(SchemaCommand.XmlDocs.Load(), [typeof(ProjectInfo), typeof(Core.Discovery.ProjectInfo)]);

        types.Schema(typeof(ProjectInfo), null);
        types.Schema(typeof(Core.Discovery.ProjectInfo), null);

        Assert.Contains("QueriesProjectInfo", types.Definitions.Select(d => d.Key));
        Assert.Contains("DiscoveryProjectInfo", types.Definitions.Select(d => d.Key));
        Assert.DoesNotContain("ProjectInfo", types.Definitions.Select(d => d.Key));
    }

    [Fact]
    public void Polymorphic_rows_are_written_as_the_row_they_are()
    {
        IReadOnlyList<TypeUsageRow> rows = [new InstanceUsage("1", Guid.Empty, "A", "en", Core.Content.VersionStatus.Published, 0, []), new InlineUsages(2, [])];

        Assert.Equal(
            """[{"ref":"1","guid":"00000000-0000-0000-0000-000000000000","name":"A","language":"en","status":"published","count":0,"usages":[]},{"count":2,"usages":[],"inline":true}]""",
            JsonOutput.Serialize(rows));
    }

    [Fact]
    public void A_page_usage_row_is_the_usage_then_via()
    {
        var usage = new Usage("1", Guid.Empty, "ArticlePage", "A", "en", Core.Content.VersionStatus.Published, null, null, "MainArea", "contentArea", ["property"], null, null) { Blueprint = true };

        var row = JsonOutput.Serialize(new PageUsageRow(new PageUsage(usage, ["5", "6"])));
        var direct = JsonOutput.Serialize(new PageUsageRow(new PageUsage(usage, [])));

        Assert.Equal(JsonOutput.Serialize(usage)[..^1] + ""","via":["5","6"]}""", row);
        Assert.Equal(JsonOutput.Serialize(usage), direct);
    }

    [Fact]
    public void Serve_stop_records_write_what_the_anonymous_objects_did()
    {
        var stopped = new Core.Serve.StopResult(Core.Serve.ServeMode.Background, 123, 5000, Stopped: true, Graceful: false);

        Assert.Equal(JsonOutput.Serialize(stopped), JsonOutput.Serialize(Core.Serve.ServeStopOutput.From(stopped)));
        Assert.Equal(JsonOutput.Serialize(new { stopped = true }), JsonOutput.Serialize(Core.Serve.ServeStopOutput.Ended));
        Assert.Equal(JsonOutput.Serialize(new { stopped = false, message = "Nothing was running for this project." }),
            JsonOutput.Serialize(Core.Serve.ServeStopOutput.Nothing("Nothing was running for this project.")));
        Assert.Equal(JsonOutput.Serialize(new { log = "a.log", lines = new[] { "x" }, previous = (List<string>?)null }),
            JsonOutput.Serialize(new ServeLogs("a.log", ["x"], null)));
    }

    private static bool IsDynamic(JsonNode? schema) => schema is JsonObject node &&
        (node["x-dynamic"] is not null || IsDynamic(node["items"]) || IsDynamic(node["additionalProperties"]) || (node["anyOf"]?.AsArray().Any(IsDynamic) ?? false));

    private static IEnumerable<string> Refs(JsonNode? node) => node switch
    {
        JsonObject o => (o["$ref"] is JsonValue r ? [r.GetValue<string>().Replace("#/$defs/", "", StringComparison.Ordinal)] : Array.Empty<string>()).Concat(o.SelectMany(p => Refs(p.Value))),
        JsonArray a => a.SelectMany(Refs),
        _ => [],
    };

    private static IEnumerable<(string Path, Command Command, bool Hidden)> Walk(Command command, List<string> path, bool hidden = false)
    {
        yield return (string.Join(' ', path), command, hidden);
        foreach (var sub in command.Subcommands)
        {
            foreach (var found in Walk(sub, [.. path, sub.Name], hidden || sub.Hidden))
            {
                yield return found;
            }
        }
    }
}
