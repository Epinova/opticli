using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using OptiCli.Core.Errors;

namespace OptiCli.Core.Writes;

/// <summary>
/// Turns <c>Prop=value</c> arguments (plus an optional JSON object) into the property map the agent
/// accepts (<c>DraftRequest.Properties</c>).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><c>Heading=Hello</c>: a string; the site parses it the way the CMS parses imported values.</item>
/// <item><c>Heading=</c>: null, which clears the property.</item>
/// <item><c>MainBody=@body.html</c>: the file's contents; <c>@@text</c> is the literal <c>@text</c>.</item>
/// <item><c>Teaser.Heading=Hi</c>: a property of the local block <c>Teaser</c> (nested object).</item>
/// <item>The JSON object (<c>--values</c>) is merged on top, recursively for nested objects, for arrays
/// (ContentArea items, links, lists) and other structured values. A value in <c>get</c>'s <c>{type, value}</c> shape is
/// unwrapped (<see cref="GetShape"/>).</item>
/// </list>
/// </remarks>
public static partial class PropertyArguments
{
    public const string Syntax =
        "Prop=value (string, parsed by the site like an import), Prop= (clear), Prop=@file (file contents, @@ for a literal @), Block.Prop=value (local block property)";

    /// <exception cref="UsageException">An argument is malformed, a file is missing, or names conflict.</exception>
    public static JsonObject Parse(IEnumerable<string> assignments, string? json, string currentDirectory)
    {
        var result = new JsonObject();
        foreach (var assignment in assignments)
        {
            var (path, value) = Split(assignment, currentDirectory);
            Assign(result, path, value, assignment);
        }
        if (!string.IsNullOrWhiteSpace(json))
        {
            // get's {type, value} shape is taken too, so a value read with get can be written back as it is.
            Merge(result, GetShape.UnwrapAll(ParseObject(json)));
        }
        return result;
    }

    /// <summary>The agent's request shape.</summary>
    public static IReadOnlyDictionary<string, JsonElement>? ToRequest(JsonObject? properties) =>
        properties is null || properties.Count == 0
            ? null
            : properties.ToDictionary(p => p.Key, p => JsonSerializer.SerializeToElement(p.Value), StringComparer.OrdinalIgnoreCase);

    /// <exception cref="UsageException">Not a JSON object.</exception>
    public static JsonObject ParseObject(string json)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        }
        catch (JsonException ex)
        {
            throw new UsageException($"--values is not valid JSON: {ex.Message}", """Pass an object of property names to values, e.g. --values '{"MainArea":[{"ref":"123"}]}'.""");
        }
        return node as JsonObject ?? throw new UsageException("--values must be a JSON object of property names to values.");
    }

    /// <summary>Recursive merge: objects merge key by key (case-insensitively, like property names), anything else replaces.</summary>
    public static void Merge(JsonObject target, JsonObject overlay)
    {
        foreach (var (key, value) in overlay.ToList())
        {
            var existingKey = target.Select(p => p.Key).FirstOrDefault(k => k.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (existingKey is not null && target[existingKey] is JsonObject existing && value is JsonObject nested)
            {
                Merge(existing, nested);
                continue;
            }
            if (existingKey is not null)
            {
                target.Remove(existingKey);
            }
            target[key] = value?.DeepClone();
        }
    }

    private static (string[] Path, JsonNode? Value) Split(string assignment, string currentDirectory)
    {
        var equals = assignment.IndexOf('=');
        if (equals <= 0)
        {
            throw new UsageException($"'{assignment}' is not a property assignment.", $"Use {Syntax}.");
        }
        var name = assignment[..equals].Trim();
        if (!NamePattern().IsMatch(name))
        {
            throw new UsageException($"'{name}' is not a property name.", "Names are letters, digits and underscores; use Block.Prop for a local block's property.");
        }

        var raw = assignment[(equals + 1)..];
        JsonNode? value = raw switch
        {
            "" => null,
            _ when raw.StartsWith("@@", StringComparison.Ordinal) => JsonValue.Create(raw[1..]),
            _ when raw.StartsWith('@') => JsonValue.Create(ReadFile(raw[1..], currentDirectory, name)),
            _ => JsonValue.Create(raw),
        };
        return (name.Split('.'), value);
    }

    private static string ReadFile(string path, string currentDirectory, string property)
    {
        var full = Path.GetFullPath(path, currentDirectory);
        return File.Exists(full)
            ? File.ReadAllText(full)
            : throw new UsageException($"{property}=@{path}: file {full} does not exist.", "Use @@ for a value that starts with a literal @.");
    }

    private static void Assign(JsonObject root, string[] path, JsonNode? value, string assignment)
    {
        var current = root;
        for (var i = 0; i < path.Length - 1; i++)
        {
            var key = FindKey(current, path[i]);
            if (key is null)
            {
                var child = new JsonObject();
                current[path[i]] = child;
                current = child;
            }
            else if (current[key] is JsonObject child)
            {
                current = child;
            }
            else
            {
                throw new UsageException($"'{assignment}' sets a property of '{string.Join('.', path[..(i + 1)])}', which is also given a value.");
            }
        }

        var leaf = path[^1];
        var existing = FindKey(current, leaf);
        if (existing is not null)
        {
            throw new UsageException($"'{string.Join('.', path)}' is given more than once{(current[existing] is JsonObject ? " (also as a block with nested values)" : "")}.");
        }
        current[leaf] = value;
    }

    private static string? FindKey(JsonObject obj, string name) =>
        obj.Select(p => p.Key).FirstOrDefault(k => k.Equals(name, StringComparison.OrdinalIgnoreCase));

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)*$")]
    private static partial Regex NamePattern();
}
