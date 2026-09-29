using System.CommandLine;
using OptiCli.Cli;
using OptiCli.Core.Cms;
using OptiCli.Core.Errors;
using OptiCli.Core.Queries;

namespace OptiCli.Commands;

internal static class BlobCommand
{
    private sealed record BlobResult(string Ref, Guid Guid, string Type, string? Name, string? Url, BlobLocation? Blob, BlobLocation? Thumbnail, string? Note);

    public static Command Create(GlobalOptions options)
    {
        var content = new ContentOptions();
        var command = new Command("blob", """
            Show where a media item's file lives: blob URI, path on disk and whether the file exists (also for the thumbnail).
            The disk path is for the file blob provider: EPiServer:Cms:FileBlobProvider:Path in appsettings.json or
            appsettings.Development.json, default App_Data/blobs.
            Example: opticli blob /globalassets/logo.png
            """);
        content.AddTo(command, withLang: false);

        CommandRunner.SetHandler(command, options, async (context, cancellationToken) =>
        {
            await using var session = await context.OpenContentAsync(cancellationToken);
            var located = await content.LocateAsync(context, session, cancellationToken);
            var header = await session.HeaderAsync(located.Id, cancellationToken);
            var kind = session.Model.Kind(header.TypeId);
            var row = header.LanguageRow(null);
            if (kind != ContentKind.Media && row?.BlobUri is null)
            {
                throw new UsageException($"{located.Id} is a {kind.ToString().ToLowerInvariant()} ({session.Model.TypeName(header.TypeId)}), not media.", "blob only works on images, videos and other media files.");
            }

            var project = context.TryGetProject(out _)?.Directory;
            var identity = session.Identities.Describe(header, null);
            return new CommandResult(new BlobResult(
                identity.Ref!, header.Guid, identity.Type!, identity.Name, identity.Url,
                row?.BlobUri is { } blob ? BlobLocator.Locate(blob, project) : null,
                row?.ThumbnailUri is { } thumbnail ? BlobLocator.Locate(thumbnail, project) : null,
                row?.BlobUri is null ? "The item has no blob." : project is null ? "No project found, so the blob folder could not be resolved." : null));
        });
        return command;
    }
}
