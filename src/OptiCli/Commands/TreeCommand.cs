using System.CommandLine;
using System.Text;
using OptiCli.Cli;
using OptiCli.Core.Output;
using OptiCli.Core.Queries;

namespace OptiCli.Commands;

internal static class TreeCommand
{
    private sealed record TreeResult(TreeNode Root, bool? Capped);

    public static Command Create(GlobalOptions options)
    {
        var content = new ContentOptions();
        var depth = new Option<int>("--depth") { Description = "Levels below the root to include.", DefaultValueFactory = _ => 2, HelpName = "n" };
        var limit = new Option<int?>("--limit") { Description = $"Children listed per node (default {Paging.DefaultLimit}); the rest are counted in 'more'.", HelpName = "n" };
        var blueprints = BlueprintsOption.Create();
        var command = new Command("tree", $"""
            Show the subtree under a content item (default: 2 levels down).
            Each node: ref, type, name, status, languages, URL and child count; children in the order the CMS lists them.
            At most {TreeReader.MaxNodes} nodes are loaded (capped: true when cut). 1 is the root of all content.
            CMS 13: Visual Builder content has a kind (experience, section, element); blueprints are left out unless
            --blueprints, and counted on their parent (blueprints: N).
            Example: opticli tree 1 --depth 1
            """);
        content.AddTo(command);
        command.Options.Add(depth);
        command.Options.Add(limit);
        command.Options.Add(blueprints);

        CommandRunner.SetHandler(command, options, async (context, cancellationToken) =>
        {
            var levels = context.Parse.GetValue(depth);
            var perNode = context.Parse.GetValue(limit) ?? Paging.DefaultLimit;
            if (levels < 0 || perNode < 1)
            {
                throw new Core.Errors.UsageException("--depth must be 0 or more and --limit at least 1.");
            }

            await using var session = await context.OpenContentAsync(cancellationToken);
            var located = await content.LocateAsync(context, session, cancellationToken);
            var language = content.Language(context, session, located);
            var (root, capped) = await new TreeReader(session, context.Parse.GetValue(blueprints)).TreeAsync(located.Id, levels, perNode, language, cancellationToken);
            return new CommandResult(new TreeResult(root, capped ? true : null), Text: Render(root, capped));
        });
        return command;
    }

    private static string Render(TreeNode root, bool capped)
    {
        var text = new StringBuilder();
        void Write(TreeNode node, int indent)
        {
            text.Append(' ', indent * 2)
                .Append(indent == 0 ? "" : "- ")
                .Append(node.Name ?? "(no name)")
                .Append("  [").Append(node.Ref).Append(' ').Append(node.Type).Append(' ').Append(node.Status)
                .Append(node.Languages.Count > 0 ? " " + string.Join(",", node.Languages) : "")
                .Append(node.Deleted == true ? " deleted" : "")
                .Append(node.Kind is { } kind ? " " + kind : "")
                .Append(node.Blueprint == true ? " blueprint" : "")
                .Append(']')
                .Append(node.Url is null ? "" : "  " + node.Url)
                .Append(node.Children is null && node.ChildCount > 0 ? $"  ({node.ChildCount} children)" : "")
                .Append(node.Blueprints is { } left ? $"  ({left} blueprint{(left == 1 ? "" : "s")}: --blueprints)" : "")
                .AppendLine();
            foreach (var child in node.Children ?? [])
            {
                Write(child, indent + 1);
            }
            if (node.More is { } more)
            {
                text.Append(' ', (indent + 1) * 2).Append($"… {more} more").AppendLine();
            }
        }
        Write(root, 0);
        if (capped)
        {
            text.AppendLine($"(capped at {TreeReader.MaxNodes} nodes; use a smaller --depth or a deeper root)");
        }
        return text.ToString();
    }
}
