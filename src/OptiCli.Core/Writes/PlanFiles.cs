using System.Text.Json.Nodes;
using OptiCli.Core.Errors;

namespace OptiCli.Core.Writes;

/// <summary>
/// The files a plan reads, resolved against the plan's folder with the rules of <see cref="MediaFiles.ForPlan"/>: an
/// upload's <c>file</c>, and <c>"@path"</c> string values in <c>properties</c>, which become the file's text (as
/// <c>Prop=@file</c> does on the command line; <c>"@@..."</c> is a literal <c>@</c>).
/// </summary>
public static class PlanFiles
{
    /// <summary>Text files are sent in one request, which the site agent caps at 16 MB.</summary>
    public const int MaxTextBytes = 8 * 1024 * 1024;

    public const string Hint = "Plan files are relative to the plan and stay inside its folder; pass --allow-outside to apply if this is intended. Write @@ for a value that starts with a literal @.";

    /// <exception cref="UsageException">Any file escapes the folder or can't be read; <c>details.problems</c> lists every one.</exception>
    public static IReadOnlyList<PlanStep> Resolve(IEnumerable<PlanStep> steps, string planDirectory, bool allowOutside)
    {
        var problems = new List<string>();
        var resolved = steps.Select(step => Resolve(step, planDirectory, allowOutside, problems)).ToList();
        return problems.Count == 0
            ? resolved
            : throw new UsageException($"The plan has {problems.Count} problem(s): {string.Join(" ", problems)}", Hint) { Details = new { problems } };
    }

    private static PlanStep Resolve(PlanStep step, string planDirectory, bool allowOutside, List<string> problems)
    {
        var where = $"operations[{step.Index}]";
        var operation = step.Operation;
        if (operation is UploadOperation upload)
        {
            try
            {
                operation = upload with { File = MediaFiles.ForPlan(upload.File, planDirectory, allowOutside) };
            }
            catch (UsageException ex)
            {
                problems.Add($"{where}: {ex.Message}");
            }
        }

        JsonObject? Read(JsonObject? properties) => properties is null ? null : ReadValues(properties, where, planDirectory, allowOutside, problems);
        operation = operation switch
        {
            SetOperation set => set with { Properties = Read(set.Properties) },
            CreateOperation create => create with { Properties = Read(create.Properties) },
            BlockCreateOperation block => block with { Properties = Read(block.Properties) },
            UploadOperation media => media with { Properties = Read(media.Properties) },
            TranslateOperation translate => translate with { Properties = Read(translate.Properties) },
            CompositionEdit composition => composition with { Value = Read(composition.Value) },
            _ => operation,
        };
        return step with { Operation = operation };
    }

    /// <summary>A copy with every <c>"@path"</c> string, at any depth (local blocks, link texts), replaced by the file's text.</summary>
    private static JsonObject ReadValues(JsonObject properties, string where, string planDirectory, bool allowOutside, List<string> problems)
    {
        var copy = (JsonObject)properties.DeepClone();
        Visit(copy, "properties");
        return copy;

        void Visit(JsonNode? node, string path)
        {
            switch (node)
            {
                case JsonObject obj:
                    foreach (var (key, value) in obj.ToList())
                    {
                        if (Replacement(value, $"{path}.{key}") is { } text)
                        {
                            obj[key] = text;
                        }
                        else
                        {
                            Visit(value, $"{path}.{key}");
                        }
                    }
                    break;
                case JsonArray array:
                    for (var i = 0; i < array.Count; i++)
                    {
                        if (Replacement(array[i], $"{path}[{i}]") is { } text)
                        {
                            array[i] = text;
                        }
                        else
                        {
                            Visit(array[i], $"{path}[{i}]");
                        }
                    }
                    break;
            }
        }

        string? Replacement(JsonNode? node, string path)
        {
            if (node is not JsonValue leaf || !leaf.TryGetValue<string>(out var value) || !value.StartsWith('@'))
            {
                return null;
            }
            if (value.StartsWith("@@", StringComparison.Ordinal))
            {
                return value[1..];
            }
            try
            {
                var full = MediaFiles.ForPlan(value[1..], planDirectory, allowOutside);
                var file = new FileInfo(full);
                if (!file.Exists)
                {
                    problems.Add($"{where}: {path}: file {full} does not exist.");
                    return value;
                }
                if (file.Length > MaxTextBytes)
                {
                    problems.Add($"{where}: {path}: {full} is larger than {MaxTextBytes / (1024 * 1024)} MB.");
                    return value;
                }
                return File.ReadAllText(full);
            }
            catch (UsageException ex)
            {
                problems.Add($"{where}: {path}: {ex.Message}");
                return value;
            }
        }
    }
}
