using System.Text.Json.Nodes;
using OptiCli.Integration.Schema;
using Xunit.Abstractions;

namespace OptiCli.Integration;

/// <summary>
/// What opticli prints against what it declares (<c>opticli schema --json</c>, which the npm package's types are
/// generated from): every command run as a process against the site under test, each JSON result validated with a
/// standard JSON Schema validator against the result its command declares for that command line (the mode opticli
/// picks), errors against <c>ErrorBody</c>, and the envelope against the npm package's. A declared type, value set or
/// required property that doesn't match the real output fails, naming the command line, the place and the value.
/// Set <c>OPTICLI_IT_SCHEMA_LOG</c> to a file to keep every command line run.
/// </summary>
public sealed class SchemaOutputTests(ITestOutputHelper output) : IDisposable
{
    private readonly string scratch = Directory.CreateTempSubdirectory("opticli-schema-").FullName;

    /// <summary>The check bites: output that differs from the declared type in each way that matters fails. Needs no site.</summary>
    [Fact]
    public async Task The_validator_fails_output_that_differs_from_the_declared_type()
    {
        var schema = await OutputSchema.LoadAsync();
        var languages = schema.ResultFor(new Invocation(["languages"], [], ["--json"]));
        var language = """{"id": 1, "code": "en", "name": "English", "enabled": true, "sortIndex": 10, "contentItems": 3, "masterOf": []}""";
        Assert.Empty(schema.Validate(languages.Schema, JsonNode.Parse($"[{language}]")));

        // A property it doesn't declare, a required one missing, the wrong type, a value outside a closed set, a bad format.
        Assert.Contains(schema.Validate(languages.Schema, JsonNode.Parse("""[{"id": 1, "code": "en", "enabled": true, "sortIndex": 10, "contentItems": 3, "masterOf": [], "extra": 1}]""")), p => p.Contains("/0", StringComparison.Ordinal));
        Assert.NotEmpty(schema.Validate(languages.Schema, JsonNode.Parse("""[{"id": 1, "enabled": true, "sortIndex": 10, "contentItems": 3, "masterOf": []}]""")));
        Assert.NotEmpty(schema.Validate(languages.Schema, JsonNode.Parse("""[{"id": "1", "code": "en", "enabled": true, "sortIndex": 10, "contentItems": 3, "masterOf": []}]""")));
        var find = schema.ResultFor(new Invocation(["find"], [], ["--type", "StandardPage", "--json"]));
        Assert.Equal("otherwise", find.Mode);
        Assert.Empty(schema.Validate(find.Schema, JsonNode.Parse("""[{"ref": "5", "guid": "6e0a3c1d-4f3b-4c55-8d0e-2b7f5a9c1e02", "status": "published", "kind": "page"}]""")));
        Assert.NotEmpty(schema.Validate(find.Schema, JsonNode.Parse("""[{"ref": "5", "guid": "6e0a3c1d-4f3b-4c55-8d0e-2b7f5a9c1e02", "status": "live", "kind": "page"}]""")));
        Assert.NotEmpty(schema.Validate(find.Schema, JsonNode.Parse("""[{"ref": "5", "guid": "not-a-guid"}]""")));
        var trash = schema.ResultFor(new Invocation(["trash"], [], ["--json"]));
        var item = """[{"ref": "5", "guid": "6e0a3c1d-4f3b-4c55-8d0e-2b7f5a9c1e02", "type": "StandardPage", "name": "x", "status": "published", "deletedBy": "opticli", "deleted": "%", "descendants": 0}]""";
        Assert.Empty(schema.Validate(trash.Schema, JsonNode.Parse(item.Replace("%", "2026-11-12T09:09:44.118Z", StringComparison.Ordinal))));
        Assert.NotEmpty(schema.Validate(trash.Schema, JsonNode.Parse(item.Replace("%", "yesterday", StringComparison.Ordinal))));

        // The mode is picked as opticli picks it, and so is the type checked.
        var drafts = schema.ResultFor(new Invocation(["find"], [], ["--type", "StandardPage", "--status", "draft", "--json"]));
        Assert.Equal("--status draft", drafts.Mode);
        Assert.Equal("--status", schema.ResultFor(new Invocation(["serve"], [], ["--status", "--json"])).Mode);
        Assert.Equal("<project>", schema.ResultFor(new Invocation(["projects"], ["12"], ["--json"])).Mode);
        Assert.Equal("otherwise", schema.ResultFor(new Invocation(["access"], ["5"], ["--json"])).Mode);
        Assert.StartsWith("--grant", schema.ResultFor(new Invocation(["access"], ["5"], ["--grant", "Everyone=Read", "--json"])).Mode, StringComparison.Ordinal);

        // What the npm package types by hand is checked too: get's properties, a plan step's result by its op.
        var get = schema.ResultFor(new Invocation(["get"], ["5"], ["--json"]));
        var document = """{"ref": "5", "guid": "6e0a3c1d-4f3b-4c55-8d0e-2b7f5a9c1e02", "type": "StartPage", "name": "Start", "status": "published", "kind": "page", "version": "5_6", "languages": ["en"], "properties": %}""";
        Assert.Empty(schema.Validate(get.Schema, JsonNode.Parse(document.Replace("%", """{"Heading": {"type": "String", "value": "Hi", "culture": "en"}}""", StringComparison.Ordinal))));
        Assert.NotEmpty(schema.Validate(get.Schema, JsonNode.Parse(document.Replace("%", """{"Heading": "Hi"}""", StringComparison.Ordinal))));
        var apply = schema.ResultFor(new Invocation(["apply"], ["-"], ["--json"]));
        var step = """{"dryRun": false, "operations": [{"index": 0, "op": "%", "status": "saved", "result": {"ref": "5", "guid": "6e0a3c1d-4f3b-4c55-8d0e-2b7f5a9c1e02", "moved": true, "dryRun": false, "descendants": 0}, "undo": "x"}], "created": {}}""";
        Assert.Empty(schema.Validate(apply.Schema, JsonNode.Parse(step.Replace("%", "move", StringComparison.Ordinal))));
        Assert.NotEmpty(schema.Validate(apply.Schema, JsonNode.Parse(step.Replace("%", "set", StringComparison.Ordinal))));
        // A failed step's result is its error's details: not checked as an output.
        Assert.Empty(schema.Validate(apply.Schema, JsonNode.Parse(step.Replace("%", "set", StringComparison.Ordinal).Replace("saved", "failed", StringComparison.Ordinal))));
    }

    /// <summary>Every read in every mode; needs no running site (and doesn't start or stop one).</summary>
    [SiteFact]
    public async Task Reads_print_what_the_schema_declares()
    {
        var check = await OutputCheck.StartAsync(output, SiteSettings.ProjectDirectory!);
        var unavailable = await Stopping(check, () => SchemaReads.RunAsync(check, scratch)) ?? [];
        var running = check.Seen.Contains(("serve", "otherwise"));
        check.Finish(SchemaReads.Required(check, running, unavailable));
    }

    /// <summary>Every write, dry run and real, on the edge-case site with its agent running; what it makes is removed again.</summary>
    [SiteFact]
    public async Task Writes_print_what_the_schema_declares()
    {
        var check = await OutputCheck.StartAsync(output, SiteSettings.ProjectDirectory!);
        var writes = new SchemaWrites(check, scratch);
        var ran = await Stopping(check, writes.RunAsync);
        check.Finish(ran ? writes.Required() : null);
    }

    /// <summary>The commands that change the user's opticli config or the project's files, with a user config of their own.</summary>
    [SiteFact]
    public async Task Local_commands_print_what_the_schema_declares()
    {
        var config = Directory.CreateDirectory(Path.Combine(scratch, "config")).FullName;
        var check = await OutputCheck.StartAsync(output, SiteSettings.ProjectDirectory!, new Dictionary<string, string?> { ["XDG_CONFIG_HOME"] = config });
        var running = (await check.Ok("serve", options: ["--status"]))?["state"]?.GetValue<string>() == "running";
        var forgot = await Stopping(check, () => SchemaLocal.RunAsync(check, scratch));
        check.Finish(SchemaLocal.Required(running, forgot));
    }

    /// <summary>Runs a battery; an exception that stops it is a problem like any other, so what ran is still reported.</summary>
    private static async Task<T?> Stopping<T>(OutputCheck check, Func<Task<T>> battery)
    {
        try
        {
            return await battery();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            check.Problem($"the battery stopped: {ex}");
            return default;
        }
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(scratch, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
