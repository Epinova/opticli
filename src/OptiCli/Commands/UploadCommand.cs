using System.CommandLine;
using OptiCli.Cli;
using OptiCli.Core.Writes;

namespace OptiCli.Commands;

internal static class UploadCommand
{
    public static Command Create(GlobalOptions options)
    {
        var file = new Argument<string>("file") { Description = "The file to upload (at most 50 MB)." };
        var forContent = new Option<string?>("--for") { Description = "Put it in this content's \"For this page\" assets folder (created if missing).", HelpName = "ref" };
        var parent = new Option<string?>("--parent") { Description = "Put it in this media folder instead.", HelpName = "ref" };
        var name = new Option<string?>("--name") { Description = "Content name. Default: the file name.", HelpName = "name" };
        var type = new Option<string?>("--type")
        {
            Description = "Media type (see `opticli types --kind media`). Default: the type the site maps the file's extension to; a type that doesn't accept it fails with the ones that do.",
            HelpName = "type",
        };
        var guid = new Option<Guid?>("--guid") { Description = "The new content's GUID (default: a new one). Fails with a conflict if it exists.", HelpName = "guid" };
        var replace = new Option<string?>("--replace")
        {
            Description = "Replace the file of this existing media item instead: a new version (a draft unless --publish) with the file, of the same media type, which must accept its extension. No --for, --parent or --type.",
            HelpName = "media-ref",
        };
        var write = new WriteOptions();
        var command = new Command("upload", """
            Upload a file (PDF, image, video, ...) as a new media item, as a draft unless --publish. Needs `opticli serve`.
            Give exactly one of --for (a page's or block's own "For this page" assets folder) or --parent (a media folder; make
            one with `opticli create <parent> --type SysContentFolder --name ...`), or --replace to give existing media a new file. Properties such as alt text are set as for
            `create`. Prints the new item's ref and version, and where the site stored the file (as `opticli blob` does).
            Example: opticli upload report.pdf --parent 456 --name "Annual report" --dry-run
            """);
        command.Arguments.Add(file);
        command.Options.Add(forContent);
        command.Options.Add(parent);
        command.Options.Add(name);
        command.Options.Add(type);
        command.Options.Add(guid);
        command.Options.Add(replace);
        write.AddProperties(command);
        write.AddCommon(command);

        CommandRunner.SetHandler(command, options, async (context, cancellationToken) =>
        {
            var parse = context.Parse;
            var operation = new UploadOperation(
                Path.GetFullPath(parse.GetValue(file)!, context.Environment.CurrentDirectory),
                parse.GetValue(forContent), parse.GetValue(parent), parse.GetValue(name), parse.GetValue(type),
                write.ParseProperties(context), parse.GetValue(write.Publish))
            {
                ContentGuid = parse.GetValue(guid),
                Replace = parse.GetValue(replace),
            };
            await using var session = await context.OpenContentAsync(cancellationToken);
            return WriteOptions.Result(await context.Writes(session).RunAsync(write.WithApproval(operation, parse), parse.GetValue(write.DryRun), cancellationToken));
        });
        return command;
    }
}
