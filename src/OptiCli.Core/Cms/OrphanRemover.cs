using System.Text;
using System.Text.Json.Nodes;
using OptiCli.Core.Errors;
using OptiCli.Core.Output;
using OptiCli.Core.Serve;
using OptiCli.Protocol;

namespace OptiCli.Core.Cms;

/// <summary>
/// <c>types remove</c>, <c>types remove-property</c> and <c>types prune</c>: content types and properties that removed code
/// left in the database, removed through the running site (<see cref="AgentRoutes.TypesRemove"/>), which decides what is
/// an orphan. Only the shared-database refusal and the arguments are checked here first; there is no database fallback,
/// since only the site can say which classes it can load.
/// </summary>
public static class OrphanRemover
{
    /// <summary>What the commands print: the site's answer, its warnings moved to <c>meta.warnings</c>.</summary>
    public sealed record Output(
        IReadOnlyList<RemovedContentType> Types,
        IReadOnlyList<RemovedProperty> Properties,
        IReadOnlyList<KeptOrphan>? Kept,
        bool Removed,
        bool? DryRun)
    {
        /// <summary>The file the site recorded each removal in before making it (<see cref="OrphanRemovalResult.RecordFile"/>).</summary>
        public string? RecordFile { get; init; }
    }

    /// <exception cref="RefusedException">A shared (remote) database: checked before the site is asked.</exception>
    public static void RequireLocal(bool sharedDatabase)
    {
        if (sharedDatabase)
        {
            throw new RefusedException(OrphanRemoval.SharedRefusal, OrphanRemoval.SharedHint);
        }
    }

    /// <summary><c>types remove-property &lt;type&gt; &lt;prop&gt;...</c> as references; a property named twice counts once.</summary>
    /// <exception cref="UsageException">No type, or no property.</exception>
    public static IReadOnlyList<OrphanPropertyRef> Properties(string type, IReadOnlyList<string> properties)
    {
        var name = type.Trim();
        if (name.Length == 0)
        {
            throw new UsageException("The content type is empty.", "opticli types remove-property <type> <property>...");
        }
        var names = properties.Select(p => p.Trim()).Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (names.Count == 0)
        {
            throw new UsageException("Name at least one property to remove.", $"`opticli type {name}` lists its properties; existsOnModel: false marks those not in the code.");
        }
        return names.Select(p => new OrphanPropertyRef(name, p)).ToList();
    }

    /// <summary>Asks the site; the answer's warnings come back apart from what was removed.</summary>
    /// <exception cref="NotFoundException">The site's agent is older than this opticli (no such route).</exception>
    public static async Task<(Output Output, IReadOnlyList<string> Warnings)> RunAsync(AgentClient agent, OrphanRemovalRequest request, CancellationToken cancellationToken)
    {
        OrphanRemovalResult result;
        try
        {
            result = await agent.SendAsync<OrphanRemovalResult>(HttpMethod.Post, AgentRoutes.TypesRemove, request, cancellationToken);
        }
        catch (OptiCliException ex) when (IsOlderAgent(ex))
        {
            throw new NotFoundException("The site's agent is older than this opticli and can't remove content types or properties.", AgentErrors.OutOfDateHint);
        }
        return (new Output(result.Types, result.Properties, request.Prune ? result.Kept : null, result.Removed, result.DryRun ? true : null) { RecordFile = result.RecordFile },
            result.Warnings ?? []);
    }

    /// <summary>
    /// An agent from before this route answers with no route at all, or (0.13) takes <c>types/remove</c> for reading the
    /// type named "remove", which it only does with GET.
    /// </summary>
    internal static bool IsOlderAgent(OptiCliException ex) =>
        (ex is NotFoundException && ex.Message.StartsWith("No agent route", StringComparison.Ordinal))
        || (ex is UsageException && ex.Message.Contains("/types/remove does not accept POST", StringComparison.Ordinal));

    /// <summary>
    /// The text a terminal gets: every field of every removed (or kept) item, in full. The output is the only record of
    /// what was removed besides the record file, so nothing is cut or put in a table column.
    /// </summary>
    public static string Text(Output output)
    {
        var text = new StringBuilder();
        var verb = output.DryRun == true ? "Would remove" : output.Removed ? "Removed" : "Nothing removed";
        Section(text, $"{verb}: content types", output.Types);
        Section(text, $"{verb}: properties", output.Properties);
        if (output.Kept is { } kept)
        {
            Section(text, "Kept", kept);
        }
        if (output.RecordFile is { } file)
        {
            text.AppendLine($"recordFile: {file}");
        }
        return text.ToString();
    }

    private static void Section<T>(StringBuilder text, string title, IReadOnlyList<T> items)
    {
        text.AppendLine($"{title} ({items.Count}):");
        foreach (var item in items)
        {
            Write(text, JsonOutput.ToNode(item), 1, item: true);
        }
        text.AppendLine();
    }

    private static void Write(StringBuilder text, JsonNode? node, int depth, bool item)
    {
        var pad = new string(' ', depth * 2);
        if (node is not JsonObject obj)
        {
            text.AppendLine($"{pad}{(item ? "- " : "")}{Scalar(node)}");
            return;
        }
        var first = true;
        foreach (var (key, value) in obj)
        {
            var prefix = item && first ? $"{pad}- " : $"{pad}{(item ? "  " : "")}";
            first = false;
            var inner = depth + (item ? 2 : 1);
            switch (value)
            {
                case JsonObject:
                    text.AppendLine($"{prefix}{key}:");
                    Write(text, value, inner, item: false);
                    break;
                case JsonArray array when array.Count > 0 && array.Any(v => v is JsonObject):
                    text.AppendLine($"{prefix}{key}:");
                    foreach (var element in array)
                    {
                        Write(text, element, inner, item: true);
                    }
                    break;
                case JsonArray array:
                    text.AppendLine($"{prefix}{key}: {(array.Count == 0 ? "(none)" : string.Join(", ", array.Select(Scalar)))}");
                    break;
                default:
                    text.AppendLine($"{prefix}{key}: {Scalar(value)}");
                    break;
            }
        }
    }

    private static string Scalar(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var s) ? s : node?.ToJsonString() ?? "(none)";
}
