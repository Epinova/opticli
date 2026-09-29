using System.CommandLine;
using OptiCli.Cli;
using OptiCli.Core.Cms;

namespace OptiCli.Commands;

internal static class TypesCommand
{
    private sealed record TypeSummary(string Name, ContentKind Kind, int Instances, string? DisplayName, Guid Guid);

    public static Command Create(GlobalOptions options)
    {
        var kind = new Option<string?>("--kind") { Description = "Only types of this kind.", HelpName = "page|block|media|folder|other" };
        kind.AcceptOnlyFromAmong(Enum.GetNames<ContentKind>().Select(n => n.ToLowerInvariant()).ToArray());
        var unused = new Option<bool>("--unused") { Description = "Only types with no (non-deleted) content items." };
        var sort = new Option<string>("--sort") { Description = "name, or instances (most used first).", DefaultValueFactory = _ => "name", HelpName = "name|instances" };
        sort.AcceptOnlyFromAmong("name", "instances");
        var list = new ListOptions(options);

        var command = new Command("types", """
            List content types with how many (non-deleted) content items use each, by name (--sort instances: most used first).
            Kinds: page, block, media (incl. images and video), folder, other (settings, system types).
            Example: opticli types --kind block --sort instances --limit 10
            """);
        command.Options.Add(kind);
        command.Options.Add(unused);
        command.Options.Add(sort);
        list.AddTo(command);

        CommandRunner.SetHandler(command, options, async (context, cancellationToken) =>
        {
            await using var db = await context.OpenDatabaseAsync(cancellationToken);
            IEnumerable<ContentTypeInfo> types = await ContentTypeReader.ListAsync(db, cancellationToken);

            if (context.Parse.GetValue(kind) is { } wanted)
            {
                var parsed = Enum.Parse<ContentKind>(wanted, ignoreCase: true);
                types = types.Where(t => t.Kind == parsed);
            }
            if (context.Parse.GetValue(unused))
            {
                types = types.Where(t => t.Instances == 0);
            }

            if (context.Parse.GetValue(sort) == "instances")
            {
                types = types.OrderByDescending(t => t.Instances).ThenBy(t => t.Name, StringComparer.Ordinal);
            }
            var summaries = types.Select(t => new TypeSummary(t.Name, t.Kind, t.Instances, t.DisplayName, t.Guid)).ToList();
            return CommandResult.From(list.Apply(context.Parse, summaries));
        });
        return command;
    }
}
