using EPiServer.Core;
using EPiServer.DataAbstraction;
using EPiServer.DataAccess;
using EPiServer.Framework.Blobs;
using OptiCli.Agent.Content;
using OptiCli.Agent.Http;
using OptiCli.Protocol;

namespace OptiCli.Agent.Endpoints;

/// <summary>
/// A file as new media: the type the CMS maps its extension to, a blob in the item's own container, then the same
/// validate, diff and save as <see cref="CreateEndpoint"/>.
/// </summary>
internal static class UploadEndpoint
{
    /// <summary>Base64 grows the file by a third; the rest of the JSON is small.</summary>
    public const int MaxBodyBytes = UploadRequest.MaxBytes / 3 * 4 + 1024 * 1024;

    public static WriteResult Handle(AgentRequest request, UploadRequest body)
    {
        var flow = new WriteFlow(request);
        var extension = MediaFileNames.Extension(body.FileName) ?? throw AgentException.Usage(
            $"fileName '{body.FileName}' must be a file name with an extension, without directories.");
        var data = Data(body);

        var type = MediaType(request, flow, extension, body.Type);
        var (parent, _) = CreateEndpoint.Parent(request, flow, body.Parent, body.ForContent, body.DryRun);

        if (body.Guid is { } guid && ExistingContent.Find(flow, guid) is { } existing)
        {
            var name = string.IsNullOrWhiteSpace(body.Name) ? body.FileName.Trim() : body.Name;
            var updated = ExistingContent.Update(flow, existing, body.UpdateExisting, type, parent, null, name, body.Properties, body.Publish, body.DryRun);
            return updated with
            {
                MediaType = type.Name,
                Validation = [.. updated.Validation ?? [], new ValidationIssue(null, "The media item exists, so its file was kept; replace it in the CMS edit UI if it changed.", "warning")],
            };
        }

        var media = flow.Repository.GetDefault<MediaData>(parent.ContentLink, type.ID);
        if (body.Guid is { } fixedGuid)
        {
            media.ContentGuid = fixedGuid;
        }
        var before = PropertyValues.Snapshot(media);
        media.Name = string.IsNullOrWhiteSpace(body.Name) ? body.FileName.Trim() : body.Name;
        flow.Writer.Apply(media, body.Properties);

        Blob? blob = null;
        if (!body.DryRun)
        {
            blob = request.Service<IBlobFactory>().CreateBlob(media.BinaryDataContainer, extension);
            using (var stream = new MemoryStream(data!, writable: false))
            {
                blob.Write(stream);
            }
            media.BinaryData = blob;
        }

        try
        {
            return flow.Save(media, before, body.Publish ? SaveAction.Publish : SaveAction.Save, body.DryRun,
                shown: null, baseVersion: null, saveUnchanged: true) with { MediaType = type.Name };
        }
        catch when (blob is not null && flow.Saved(media) is null)
        {
            // Nothing references the file if the save failed.
            request.Service<IBlobFactory>().Delete(blob.ID);
            throw;
        }
    }

    private static byte[]? Data(UploadRequest body)
    {
        if (body.Data is null)
        {
            return body.DryRun ? null : throw AgentException.Usage("data (the file content, base64) is required unless dryRun.");
        }
        byte[] data;
        try
        {
            data = Convert.FromBase64String(body.Data);
        }
        catch (FormatException)
        {
            throw AgentException.Usage("data is not valid base64.");
        }
        return data.Length switch
        {
            0 => throw AgentException.Usage("The file is empty."),
            > UploadRequest.MaxBytes => throw AgentException.Usage($"The file is larger than {UploadRequest.MaxBytes / (1024 * 1024)} MB."),
            _ => data,
        };
    }

    /// <summary>
    /// The requested type if it accepts the extension; else the CMS's first type that names the extension. Types that
    /// accept any file (no extensions listed, often a content provider's) are only used when asked for by name.
    /// </summary>
    private static ContentType MediaType(AgentRequest request, WriteFlow flow, string extension, string? requested)
    {
        var resolver = request.Service<ContentMediaResolver>();
        List<ContentType> Matching(string ext) => resolver.ListAllMatching(ext).Concat(resolver.ListAllMatching(ext.TrimStart('.')))
            .Distinct()
            .Select(model => flow.Types.Load(model))
            .OfType<ContentType>()
            .ToList();
        var anyFile = Matching(".opticli-no-such-extension");
        var accepting = Matching(extension);
        var naming = accepting.Where(t => !anyFile.Any(a => a.ID == t.ID)).ToList();

        if (requested is not null)
        {
            var type = TypeEndpoint.Find(flow.Types, requested);
            return accepting.Any(t => t.ID == type.ID)
                ? type
                : throw AgentException.Usage($"{type.Name} does not accept {extension} files.", Alternatives(extension, naming, anyFile));
        }
        return naming.FirstOrDefault() ?? throw AgentException.Usage(
            $"No media type is registered for {extension} files.", Alternatives(extension, naming, anyFile));
    }

    private static string Alternatives(string extension, IReadOnlyList<ContentType> naming, IReadOnlyList<ContentType> anyFile)
    {
        var hints = new List<string>();
        if (naming.Count > 0)
        {
            hints.Add($"Types for {extension}: {string.Join(", ", naming.Select(t => t.Name))}.");
        }
        if (anyFile.Count > 0)
        {
            hints.Add($"Types that accept any file (only with --type): {string.Join(", ", anyFile.Select(t => t.Name))}.");
        }
        if (naming.Count == 0)
        {
            hints.Add("Media types list their extensions in [MediaDescriptor(ExtensionString = \"...\")] on the class.");
        }
        return string.Join(" ", hints);
    }
}
