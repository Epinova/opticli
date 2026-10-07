using System.CommandLine;
using System.Text.Json.Nodes;
using OptiCli.Cli;
using OptiCli.Core.Errors;
using OptiCli.Core.Writes;

namespace OptiCli.Commands;

internal static class CompositionCommand
{
    public static Command Create(GlobalOptions options)
    {
        var content = new ContentOptions();
        var action = new Argument<string>("action") { Description = "add, remove, move or set." };
        action.AcceptOnlyFromAmong([.. CompositionEdits.Actions]);
        var items = new Argument<string[]>("items")
        {
            Description = "add <section|row|column|element> [Prop=value ...] | remove <node> | move <node> | set <node> [Prop=value ...]. A node is its key (as get shows it), or its name if only one node has it; root is the composition itself.",
            Arity = ArgumentArity.OneOrMore,
        };
        var parent = new Option<string?>("--in") { Description = "add, move: the node to put it in (key or name). Default for add: the composition itself (an experience's sections); for move: where it is.", HelpName = "node" };
        var at = new Option<int?>("--at") { Description = "add, move: zero-based position among the parent's children. Default: the end.", HelpName = "n" };
        var type = new Option<string?>("--type") { Description = "add: a new inline section or element of this block type (see `opticli types --kind section`, `--kind element`).", HelpName = "type" };
        var reference = new Option<string?>("--ref") { Description = "add: place this shared block (a ref) instead of a new inline one; every placement shows its values.", HelpName = "ref" };
        var blueprint = new Option<string?>("--blueprint") { Description = "add section: a new inline section copied from this section blueprint (ref, GUID or name), with its rows.", HelpName = "blueprint" };
        var name = new Option<string?>("--name") { Description = "add, set: the node's name (the edit UI shows it). Default for a new section or element: its type's display name.", HelpName = "name" };
        var template = new Option<string?>("--template") { Description = "add, set: display template key (see `opticli display-templates`); set --template \"\" removes it and its settings.", HelpName = "key" };
        var settings = new Option<string[]>("--setting")
        {
            Description = "add, set: a display setting of the template, repeatable: key=value; set key= removes one.",
            HelpName = "key=value",
            AllowMultipleArgumentsPerToken = false,
        };
        var node = new Option<string?>("--node")
        {
            Description = "add: the whole node as JSON, as get's composition shows one, children included, e.g. a section with its rows, columns and elements. Instead of --type/--ref/--blueprint/--name/--template/--setting and properties.",
            HelpName = "json",
        };
        var variation = new Option<string?>("--variation") { Description = "CMS 13: change this content variation (its key) instead of the content itself; made from the published version if it has no version yet.", HelpName = "key" };
        var values = new Option<string?>("--values") { Description = """add, set: the block's property values as a JSON object, merged over the Prop=value arguments, as for `opticli set`.""", HelpName = "json" };
        var write = new WriteOptions();
        var command = new Command("composition", """
            CMS 13: change a Visual Builder composition (experience or section) on a new version, a draft unless --publish. Needs `opticli serve`.
            Read it first with `opticli get <ref> --fields composition`: sections → rows → columns → elements, each with its key. A node is named by its key, or by its name when only one node has it.
              add <section|row|column|element>: a new node in --in (an experience's sections by default) at --at, with --type
                (an inline block, and its Prop=value properties) or --ref (a shared block) or, for a section, --blueprint; or the
                whole node, children included, as --node JSON. Sections hold rows, rows columns, columns elements.
              remove <node>: with everything in it. move <node> --in <node> --at n.
              set <node>: an inline block's properties (Prop=value, --values), --name, --template, --setting k=v (k= removes).
            The CMS validates the result (structure, element types, required properties); display templates and settings are
            checked against the site's (`opticli display-templates`). Prints the new version and the nodes it changed. To write
            the whole composition at once, give it as composition in `opticli set --values` (or composition=@file.json).
            Example: opticli composition 123 add element --in "Right column" --type TextElement Heading=Hi --dry-run
            Also:    opticli composition 123 set Hero --template heroLook --setting background=dark
                     opticli composition 123 move 5faf72ec-fe24-4c6d-8052-69b5cd80101a --in Left --at 0
            """);
        content.AddTo(command);
        command.Arguments.Add(action);
        command.Arguments.Add(items);
        foreach (var option in new Option[] { parent, at, type, reference, blueprint, name, template, settings, node, values, variation })
        {
            command.Options.Add(option);
        }
        write.AddCommon(command);
        write.AddPublishAt(command);
        write.AddIncludeDraft(command);
        write.AddConcurrency(command);

        CommandRunner.SetHandler(command, options, async (context, cancellationToken) =>
        {
            var parse = context.Parse;
            var verb = parse.GetValue(action)!;
            var given = parse.GetValue(items) ?? [];
            var first = given[0];
            var assignments = given[1..];
            if (verb is "remove" or "move" && assignments.Length > 0)
            {
                throw new UsageException($"composition {verb} takes one node, got {given.Length} values.", CompositionEdits.Syntax);
            }
            var properties = PropertyArguments.Parse(assignments, parse.GetValue(values), context.Environment.CurrentDirectory);
            var nodeJson = parse.GetValue(node);
            var describing = new (string Name, bool Given)[]
            {
                ("--type", parse.GetValue(type) is not null), ("--ref", parse.GetValue(reference) is not null), ("--blueprint", parse.GetValue(blueprint) is not null),
                ("--name", parse.GetValue(name) is not null), ("--template", parse.GetValue(template) is not null), ("--setting", (parse.GetValue(settings) ?? []).Length > 0),
                ("--node", nodeJson is not null), ("properties", properties.Count > 0),
            };
            var allowed = verb switch
            {
                "add" => nodeJson is null ? ["--type", "--ref", "--blueprint", "--name", "--template", "--setting", "properties"] : new[] { "--node" },
                "set" => ["--name", "--template", "--setting", "properties"],
                _ => [],
            };
            if (describing.FirstOrDefault(d => d.Given && !allowed.Contains(d.Name)) is { Given: true } extra)
            {
                throw new UsageException(nodeJson is not null && verb == "add"
                    ? $"--node is the whole node; give {extra.Name} inside it."
                    : $"composition {verb} takes no {extra.Name}.", CompositionEdits.Syntax);
            }
            if (verb is "remove" or "set" && (parse.GetValue(parent) is not null || parse.GetValue(at) is not null))
            {
                throw new UsageException($"composition {verb} takes no --in or --at.", verb == "set" ? "Move a node with composition move." : null);
            }

            JsonObject? value = verb switch
            {
                "add" when nodeJson is not null => ParseNode(nodeJson),
                "add" => Node(parse.GetValue(type), parse.GetValue(reference), parse.GetValue(blueprint), parse.GetValue(name), parse.GetValue(template), Settings(parse.GetValue(settings), removing: false), properties),
                "set" => Change(parse.GetValue(name), parse.GetValue(template), Settings(parse.GetValue(settings), removing: true), properties),
                _ => null,
            };
            var operation = new CompositionEdit(
                parse.GetValue(content.Ref)!,
                verb,
                verb == "add" ? null : first,
                verb == "add" ? first : null,
                parse.GetValue(parent),
                parse.GetValue(at),
                value,
                parse.GetValue(content.Lang),
                parse.GetValue(write.Publish),
                write.ParseBaseVersion(context),
                parse.GetValue(write.Force))
            {
                IncludeDraft = parse.GetValue(write.IncludeDraft),
                From = write.ParseFrom(context),
                Variation = parse.GetValue(variation),
            };
            await using var session = await context.OpenContentAsync(cancellationToken);
            return WriteOptions.Result(await context.Writes(session, parse.GetValue(content.Site)).RunAsync(write.WithApproval(operation, parse), parse.GetValue(write.DryRun), cancellationToken));
        });
        return command;
    }

    private static JsonObject ParseNode(string json)
    {
        try
        {
            return System.Text.Json.Nodes.JsonNode.Parse(json, documentOptions: new System.Text.Json.JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = System.Text.Json.JsonCommentHandling.Skip }) as JsonObject
                ?? throw new UsageException("--node must be a JSON object: one node as get's composition shows it.", CompositionInput.Hint);
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new UsageException($"--node is not valid JSON: {ex.Message}", CompositionInput.Hint);
        }
    }

    /// <summary>A new node in the composition's JSON shape.</summary>
    private static JsonObject Node(string? type, string? reference, string? blueprint, string? name, string? template, JsonObject? settings, JsonObject properties)
    {
        var node = new JsonObject();
        Put(node, "type", type);
        Put(node, "ref", reference);
        Put(node, "blueprint", blueprint);
        Put(node, "name", name);
        Put(node, "displayTemplate", template);
        if (settings is { Count: > 0 })
        {
            node["displaySettings"] = settings;
        }
        if (properties.Count > 0)
        {
            node["properties"] = properties;
        }
        return node;
    }

    /// <summary>What <c>set</c> changes: name, display template and settings, properties.</summary>
    private static JsonObject Change(string? name, string? template, JsonObject? settings, JsonObject properties)
    {
        var change = new JsonObject();
        Put(change, "name", name);
        Put(change, "displayTemplate", template);
        if (settings is { Count: > 0 })
        {
            change["displaySettings"] = settings;
        }
        if (properties.Count > 0)
        {
            change["properties"] = properties;
        }
        return change;
    }

    /// <summary><c>--setting key=value</c>s; with <paramref name="removing"/>, <c>key=</c> is null (removes it).</summary>
    private static JsonObject? Settings(string[]? given, bool removing)
    {
        if (given is not { Length: > 0 })
        {
            return null;
        }
        var result = new JsonObject();
        foreach (var setting in given)
        {
            var equals = setting.IndexOf('=');
            if (equals <= 0)
            {
                throw new UsageException($"--setting '{setting}' is not key=value.", "E.g. --setting background=dark.");
            }
            var key = setting[..equals].Trim();
            var value = setting[(equals + 1)..].Trim();
            if (value.Length == 0 && !removing)
            {
                throw new UsageException($"--setting {key}= has no value.", "A new node only takes settings with values.");
            }
            result[key] = value.Length == 0 ? null : value;
        }
        return result;
    }

    private static void Put(JsonObject node, string name, string? value)
    {
        if (value is not null)
        {
            node[name] = value;
        }
    }
}
