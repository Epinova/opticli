using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Xunit.Abstractions;

namespace OptiCli.Integration.Schema;

/// <summary>
/// Runs opticli command lines against the site under test (<c>--project</c>, <c>--json</c>) and checks each envelope: an
/// ok one's <c>data</c> against the result its command declares for that command line (the mode opticli picks), an error's
/// <c>error</c> against <c>ErrorBody</c>, and the envelope itself against the npm package's hand-written <c>Envelope</c>
/// (npm/src/envelope.ts). Collects every mismatch instead of stopping at the first, and which commands and modes were
/// seen, so a test can say what it covered.
/// </summary>
internal sealed class OutputCheck
{
    // npm/src/envelope.ts: OkEnvelope and ErrorEnvelope, which no C# type describes for the generator.
    private static readonly JsonNode OkEnvelope = JsonNode.Parse("""
        {"type": "object", "additionalProperties": false, "required": ["ok", "data", "meta"],
         "properties": {"ok": {"const": true}, "data": true, "meta": {"$ref": "#/$defs/EnvelopeMeta"}},
         "$defs": {"EnvelopeMeta": {"type": "object", "additionalProperties": false, "required": ["version"], "properties": {
           "source": {"enum": ["db", "agent", "cli"]}, "version": {"type": "string"}, "next": {"type": "string"},
           "warnings": {"type": "array", "items": {"type": "string"}},
           "database": {"type": "object", "additionalProperties": false, "required": ["local"], "properties": {
             "server": {"type": "string"}, "name": {"type": "string"}, "local": {"type": "boolean"},
             "development": {"type": "boolean"}, "role": {"type": "string"}}}}}}}
        """)!;

    private static readonly JsonNode ErrorEnvelope = JsonNode.Parse("""
        {"type": "object", "additionalProperties": false, "required": ["ok", "error"],
         "properties": {"ok": {"const": false}, "error": true,
           "meta": {"type": "object", "additionalProperties": false, "required": ["version"], "properties": {"version": {"type": "string"}}}}}
        """)!;

    /// <summary>
    /// npm/src/results.ts: what the hand-written <c>create</c> and <c>set</c> promise beyond <c>WriteOutput</c>
    /// (<c>CreatedContent</c> for a create that isn't a dry run, <c>SavedContent</c> for every set).
    /// </summary>
    private static readonly (string Command, bool DryRunsToo, JsonNode Schema)[] HandWritten =
    [
        ("create", false, JsonNode.Parse("""{"required": ["ref", "version", "guid", "type", "name"]}""")!),
        ("set", true, JsonNode.Parse("""{"required": ["ref"]}""")!),
    ];

    private readonly ITestOutputHelper output;
    private readonly List<string> problems;
    private readonly Dictionary<(string Command, string Mode), (int Runs, int WithItems)> seen;
    private readonly List<string> log;
    private readonly SortedDictionary<string, SortedSet<string>> valueSets;

    private OutputCheck(ITestOutputHelper output, OutputSchema schema, string project, IReadOnlyDictionary<string, string?>? environment, OutputCheck? parent = null)
    {
        this.output = output;
        Schema = schema;
        Project = project;
        Environment = environment;
        // A check made With another project or environment reports into its parent's results.
        problems = parent?.problems ?? [];
        seen = parent?.seen ?? [];
        log = parent?.log ?? [];
        valueSets = parent?.valueSets ?? new(StringComparer.Ordinal);
    }

    public OutputSchema Schema { get; }

    /// <summary>The site project every command line names (<c>--project</c>).</summary>
    public string Project { get; }

    public IReadOnlyDictionary<string, string?>? Environment { get; }

    /// <summary>Every command and mode checked with an ok result.</summary>
    public IReadOnlyCollection<(string Command, string Mode)> Seen => seen.Keys;

    public static async Task<OutputCheck> StartAsync(ITestOutputHelper output, string project, IReadOnlyDictionary<string, string?>? environment = null) =>
        new(output, await OutputSchema.LoadAsync(), project, environment);

    /// <summary>
    /// The same check for another project directory (a copy of the site's) or other environment variables (a sandboxed
    /// state directory, say): what it runs and finds is reported with this one's, by <see cref="Finish"/>.
    /// </summary>
    public OutputCheck With(string project, IReadOnlyDictionary<string, string?>? environment) => new(output, Schema, project, environment, this);

    /// <summary>Runs the command; it must succeed, and its data must be what the command declares.</summary>
    /// <param name="command">The command's words, space-separated (<c>"projects ready"</c>).</param>
    /// <returns>The data (null when it failed: recorded as a problem).</returns>
    public async Task<JsonNode?> Ok(string command, string[]? args = null, string[]? options = null, string? input = null)
    {
        var run = await Run(command, args, options, input);
        if (!run.Ok)
        {
            Problem(run, $"expected ok, got exit {run.ExitCode}: {Trim(run.Envelope?["error"]?.ToJsonString() ?? run.Stdout + run.Stderr)}");
            return null;
        }
        return run.Data;
    }

    /// <summary>A list command one row a page: the first page, and the next through <c>meta.next</c> when there is one.</summary>
    public async Task Paged(string command, string[]? args = null, string[]? options = null)
    {
        var first = await Run(command, args, [.. options ?? [], "--limit", "1"], null);
        if (!first.Ok)
        {
            Problem(first, $"expected ok, got exit {first.ExitCode}: {Trim(first.Stdout + first.Stderr)}");
        }
        else if (first.Envelope?["meta"]?["next"]?.GetValue<string>() is { } next)
        {
            await Ok(command, args, [.. options ?? [], "--limit", "1", "--cursor", next]);
        }
    }

    /// <summary>Runs the command; it must fail (with <paramref name="code"/>, if given), and its error must be an <c>ErrorBody</c>.</summary>
    public async Task<JsonNode?> Fails(string command, string[]? args = null, string[]? options = null, string? code = null, string? input = null)
    {
        var run = await Run(command, args, options, input);
        if (run.Ok || run.Envelope?["error"] is not JsonObject error)
        {
            Problem(run, $"expected an error{(code is null ? "" : $" ({code})")}, got exit {run.ExitCode}: {Trim(run.Stdout + run.Stderr)}");
            return null;
        }
        if (code is not null && error["code"]?.GetValue<string>() != code)
        {
            Problem(run, $"expected error {code}, got {Trim(error.ToJsonString())}");
        }
        return error;
    }

    /// <summary>Runs the command and checks whatever it printed, success or error: for what may fail on some sites.</summary>
    public async Task<CliRun> Any(string command, string[]? args = null, string[]? options = null, string? input = null) =>
        await Run(command, args, options, input);

    private async Task<CliRun> Run(string command, string[]? args, string[]? options, string? input)
    {
        // A ref an earlier step should have made, and didn't (that step's problem is recorded already).
        if ((args ?? []).Concat(options ?? []).Any(a => a is null))
        {
            var skipped = new CliRun([.. command.Split(' '), .. (args ?? []).Select(a => a ?? "<missing>")], -1, "", "");
            Problem(skipped, "not run: an earlier step didn't give what it needs");
            return skipped;
        }
        var invocation = new Invocation(command.Split(' '), args ?? [], [.. options ?? [], "--project", Project, "--json"]);
        var declared = Schema.ResultFor(invocation);
        var run = await OptiCliProcess.RunAsync(invocation.Args, input, Environment);
        if (run.Envelope is null)
        {
            Problem(run, $"no JSON envelope (exit {run.ExitCode}): {Trim(run.Stdout + run.Stderr)}");
            return run;
        }
        if (run.Ok)
        {
            Check(run, OkEnvelope, run.Envelope, "envelope");
            if (declared.Raw)
            {
                Problem(run, $"the schema says mode '{declared.Mode}' prints no JSON, but it printed an envelope");
            }
            else
            {
                Check(run, declared.Schema, run.Data, $"data ({declared.Mode})");
                foreach (var (_, _, schema) in HandWritten.Where(h => h.Command == declared.Command && (h.DryRunsToo || !invocation.Options.Contains("--dry-run"))))
                {
                    Check(run, schema, run.Data, "the npm package's hand-written result");
                }
            }
            CollectValueSets(run.Data);
            var items = run.Data is JsonArray array ? array.Count : 1;
            var key = (declared.Command, declared.Mode);
            var (runs, withItems) = seen.GetValueOrDefault(key);
            seen[key] = (runs + 1, withItems + (items > 0 ? 1 : 0));
            log.Add($"ok    {declared.Command} [{declared.Mode}] {(run.Data is JsonArray rows ? $"{rows.Count} rows" : "")}: {run.CommandLine}");
        }
        else
        {
            Check(run, ErrorEnvelope, run.Envelope, "error envelope");
            Check(run, Schema.ErrorSchema, run.Envelope["error"], "error");
            log.Add($"error {declared.Command} [{run.Envelope["error"]?["code"]}]: {run.CommandLine}");
        }
        return run;
    }

    /// <summary>The values seen of the closed value sets the site's agent makes (not opticli): which ones an agent sends.</summary>
    public IReadOnlyDictionary<string, SortedSet<string>> ValueSets => valueSets;

    private void CollectValueSets(JsonNode? node)
    {
        switch (node)
        {
            case JsonArray array:
                foreach (var item in array)
                {
                    CollectValueSets(item);
                }
                break;
            case JsonObject obj:
                // LanguageBranchChange {code, status, enabled}; SiteHostsView {site, status, hosts}; ProjectItemResult's outcome.
                if (obj.ContainsKey("code") && obj.ContainsKey("enabled") && obj["status"] is JsonValue languageStatus)
                {
                    Add("LanguageBranchStatus", languageStatus);
                }
                if (obj.ContainsKey("site") && obj.ContainsKey("hosts") && obj["status"] is JsonValue siteStatus)
                {
                    Add("SiteHostStatus", siteStatus);
                }
                if (obj["outcome"] is JsonValue outcome)
                {
                    Add("ProjectItemOutcome", outcome);
                }
                if (obj["editorUi"] is JsonValue editorUi)
                {
                    Add("ProjectResult.editorUi (a string)", editorUi);
                }
                foreach (var (_, value) in obj)
                {
                    CollectValueSets(value);
                }
                break;
        }

        void Add(string set, JsonValue value)
        {
            if (!valueSets.TryGetValue(set, out var values))
            {
                valueSets[set] = values = new SortedSet<string>(StringComparer.Ordinal);
            }
            values.Add(value.ToString());
        }
    }

    private void Check(CliRun run, JsonNode schema, JsonNode? instance, string what)
    {
        foreach (var problem in Schema.Validate(schema, instance))
        {
            Problem(run, $"{what}: {problem}");
        }
    }

    private void Problem(CliRun run, string message)
    {
        var text = $"opticli {run.CommandLine}\n    {message}";
        problems.Add(text);
        log.Add($"FAIL  {text}");
    }

    /// <summary>A problem found by the test itself (a value set seen, a step it couldn't take).</summary>
    public void Problem(string message)
    {
        problems.Add(message);
        log.Add($"FAIL  {message}");
    }

    public void Note(string message) => log.Add($"note  {message}");

    /// <summary>
    /// Writes what ran, and fails with every mismatch; also fails for a command and mode in <paramref name="required"/>
    /// that never printed a result, and lists the modes checked only with an empty list.
    /// </summary>
    public void Finish(IEnumerable<(string Command, string Mode)>? required = null)
    {
        var missing = (required ?? []).Where(r => !seen.ContainsKey(r)).Select(r => $"{r.Command} [{r.Mode}]").ToList();
        var summary = new StringBuilder();
        summary.AppendLine(CultureInfo.InvariantCulture, $"opticli {Schema.Version}, project {Project}: {log.Count(l => l.StartsWith("ok", StringComparison.Ordinal))} ok results checked, {log.Count(l => l.StartsWith("error", StringComparison.Ordinal))} errors, {seen.Count} commands/modes.");
        foreach (var ((command, mode), (runs, withItems)) in seen.OrderBy(s => s.Key.Command, StringComparer.Ordinal).ThenBy(s => s.Key.Mode, StringComparer.Ordinal))
        {
            summary.AppendLine(CultureInfo.InvariantCulture, $"  {command} [{mode}]: {runs} run(s){(withItems < runs ? $", {runs - withItems} empty" : "")}");
        }
        foreach (var (set, values) in valueSets)
        {
            summary.AppendLine(CultureInfo.InvariantCulture, $"  value set {set}: {string.Join(", ", values)}");
        }
        output.WriteLine(summary.ToString());
        foreach (var line in log)
        {
            output.WriteLine(line);
        }
        if (Variable("OPTICLI_IT_SCHEMA_LOG") is { } path)
        {
            File.AppendAllText(path, $"{summary}{string.Join('\n', log)}\n\n");
        }
        var failures = problems.Concat(missing.Select(m => $"never checked: {m}")).ToList();
        Assert.True(failures.Count == 0, $"{failures.Count} problem(s):\n{string.Join('\n', failures)}");
    }

    private static string? Variable(string name) => System.Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : null;

    private static string Trim(string text) => text.Length > 600 ? text[..600] + "…" : text.Trim();
}
