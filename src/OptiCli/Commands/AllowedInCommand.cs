using System.CommandLine;
using OptiCli.Cli;
using OptiCli.Core.Cms;
using OptiCli.Core.Queries;
using OptiCli.Core.SourceScan;

namespace OptiCli.Commands;

internal static class AllowedInCommand
{
    public static Command Create(GlobalOptions options)
    {
        var name = new Argument<string>("type") { Description = "The content type that should fit (name, class name or GUID), e.g. a block type." };
        var kind = new Option<string?>("--kind") { Description = "Only properties of content types of this kind: page, block, media, folder or other; on CMS 13 also experience, section, element or contract.", HelpName = "kind" };
        kind.AcceptOnlyFromAmong(Enum.GetNames<ContentKind>().Select(n => n.ToLowerInvariant()).ToArray());
        var explicitOnly = new Option<bool>("--explicit") { Description = "Only properties whose [AllowedTypes] names the type or one of its base classes (leave out unrestricted ones)." };
        var list = new ListOptions(options);
        var command = new Command("allowed-in", """
            List the ContentArea and reference properties that can hold a content type, from [AllowedTypes] in the C# code.
            allowed: explicit (the attribute names the type, or a base class/interface: see matchedBy) or any (a ContentArea
            or reference list without the attribute); properties that restrict the type are left out. Rules an editor
            descriptor or metadata extender applies at runtime are not visible: a uiHint on the row points at one.
            Placed instances (what is actually there): opticli find --type <OwnerType> --where <Property>=<ref>.
            CMS 13: allowed composition (property composition) is a Visual Builder experience's outline for a SectionEnabled
            type, or a section's columns for an ElementEnabled one (matchedBy); the ContentArea a composition is stored in
            (UnstructuredData) isn't listed. `opticli where-used --type <type>` shows where its inline blocks are.
            Example: opticli allowed-in TeaserBlock --kind page --explicit
            """);
        command.Arguments.Add(name);
        command.Options.Add(kind);
        command.Options.Add(explicitOnly);
        list.AddTo(command);

        CommandRunner.SetHandler(command, options, async (context, cancellationToken) =>
        {
            var project = context.Project;
            await using var session = await context.OpenContentAsync(cancellationToken);
            var target = session.Model.RequireType(context.Parse.GetValue(name)!);
            var index = CSharpSourceIndex.Build(project.SourceRoot);
            IEnumerable<AllowedIn> rows = AllowedInQuery.Find(session.Model, index, project.SourceRoot, target, context.Parse.GetValue(explicitOnly));
            if (context.Parse.GetValue(kind) is { } wanted)
            {
                var parsed = Enum.Parse<ContentKind>(wanted, ignoreCase: true);
                rows = rows.Where(r => r.Kind == parsed);
            }
            return CommandResult.From(list.Apply(context.Parse, rows.ToList()));
        });
        return command;
    }
}
