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
        if (body.Replace is { } replaced)
        {
            return Replace(request, flow, body, replaced, extension, data);
        }

        var type = MediaType(request, flow, extension, body.Type);
        var (parent, _) = CreateEndpoint.Parent(request, flow, body.Parent, body.ForContent, body.DryRun);

        if (body.Guid is { } guid && ExistingContent.Find(flow, guid) is { } existing)
        {
            var name = string.IsNullOrWhiteSpace(body.Name) ? body.FileName.Trim() : body.Name;
            var updated = ExistingContent.Update(flow, existing, body.UpdateExisting, type, parent, null, name, body.Properties, body.Publish, body.RequestApproval, body.IncludeDraft, body.DryRun);
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
        var action = Approvals.Decide(request, parent.ContentLink, body.Publish, body.RequestApproval, $"New {type.Name} '{media.Name}'");

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
            return flow.Save(media, before, action ?? SaveAction.Save, body.DryRun,
                shown: null, baseVersion: null, saveUnchanged: true,
                precheck: CreateEndpoint.Availability(request, parent, body.ForContent is not null, type, flow.Types)) with { MediaType = type.Name };
        }
        catch when (blob is not null && flow.Saved(media) is null)
        {
            // Nothing references the file if the save failed.
            request.Service<IBlobFactory>().Delete(blob.ID);
            throw;
        }
    }

    /// <summary>A new version of existing media with the new file, validated, diffed and saved like a draft.</summary>
    private static WriteResult Replace(AgentRequest request, WriteFlow flow, UploadRequest body, string replaced, string extension, byte[]? data)
    {
        if (body.Parent is not null || body.ForContent is not null || body.Type is not null || body.Guid is not null || body.UpdateExisting)
        {
            throw AgentException.Usage("replace takes the media item; parent, forContent, type, guid and updateExisting don't apply.");
        }
        var link = flow.Locator.ResolveContent(replaced, "replace");
        var existing = flow.Locator.LoadAnyLanguage(link);
        if (existing is not MediaData media)
        {
            throw AgentException.Usage($"Content {link.ID} ('{existing.Name}') is not media, so it has no file to replace.",
                "Give a media item's ref (an image, a document, ...).");
        }
        var type = flow.Types.Load(media.ContentTypeID)!;
        var resolver = request.Service<ContentMediaResolver>();
        var accepted = resolver.ListAllMatching(extension).Concat(resolver.ListAllMatching(extension.TrimStart('.')))
            .Any(model => flow.Types.Load(model)?.ID == type.ID);
        if (!accepted)
        {
            throw AgentException.Usage($"{type.Name} (the type of {link.ID}) doesn't accept {extension} files, and a media item keeps its type.",
                "Upload the file as new media instead (without replace), or convert it to a format the type accepts.");
        }

        var what = $"{link.ID} ('{media.Name}')";
        IReadOnlyList<EPiServer.DataAbstraction.ContentVersion>? branch = media is IVersionable ? flow.Locator.Versions(link, null) : null;
        var latest = branch is null ? media : flow.Repository.Get<IContent>(ContentLocator.Latest(branch, link, null).ContentLink);
        if (branch is not null)
        {
            Approvals.RequireNotInReview(branch, what, null);
        }
        var action = Approvals.Decide(request, link, body.Publish, body.RequestApproval, what);
        var pending = action == SaveAction.Publish && branch is not null
            ? PendingDrafts.Require(flow.Locator.PendingDraft(branch, latest), body.IncludeDraft, body.DryRun, what, null)
            : null;
        var published = branch is null ? null : ContentLocator.PublishedVersion(branch);

        var before = PropertyValues.Snapshot(latest);
        var writable = (MediaData)((MediaData)latest).CreateWritableClone();
        if (!string.IsNullOrWhiteSpace(body.Name))
        {
            writable.Name = body.Name;
        }
        flow.Writer.Apply(writable, body.Properties);

        Blob? blob = null;
        if (!body.DryRun)
        {
            blob = request.Service<IBlobFactory>().CreateBlob(writable.BinaryDataContainer, extension);
            using (var stream = new MemoryStream(data!, writable: false))
            {
                blob.Write(stream);
            }
            writable.BinaryData = blob;
        }
        try
        {
            var result = flow.Save(writable, before, writable is IVersionable ? WriteFlow.DraftAction(action) : action ?? SaveAction.Save, body.DryRun,
                ContentSummaries.Describe(latest, flow.Types), latest.ContentLink.WorkID > 0 ? latest.ContentLink.WorkID : null,
                // A new file is a change even when no property is.
                saveUnchanged: true);
            return result with
            {
                MediaType = type.Name,
                PendingDraft = pending,
                PreviouslyPublished = result is { Saved: true, Published: true } ? published : null,
            };
        }
        catch when (blob is not null && !flow.Versions.List(link).Any(v => v.ContentLink.WorkID > (latest.ContentLink.WorkID)))
        {
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
