using EPiServer.Core;
using EPiServer.DataAbstraction;
using EPiServer.DataAccess;
using EPiServer.Framework.Blobs;
using OptiCli.Cms.Content;
using OptiCli.Protocol;

namespace OptiCli.Cms.Operations;

/// <summary>
/// A file as new media (the agent's <c>POST /v1/media</c>): the type the CMS maps its extension to, a blob in the item's
/// own container, then the same validate, diff and save as <see cref="CreateOperation"/>. Or a new file for existing media.
/// </summary>
internal static class UploadOperation
{
    public static WriteResult Run(CmsCall call, UploadRequest body)
    {
        var flow = new WriteFlow(call);
        var extension = MediaFileNames.Extension(body.FileName) ?? throw AgentException.Usage(
            $"fileName '{body.FileName}' must be a file name with an extension, without directories.");
        var data = Data(body);
        if (!call.MayWriteScript)
        {
            RequireNoScript(extension, data);
        }
        if (body.Replace is { } replaced)
        {
            return Replace(call, flow, body, replaced, extension, data);
        }

        var type = MediaType(call, flow, extension, body.Type);
        var (parent, _) = CreateOperation.Parent(flow, body.Parent, body.ForContent, body.DryRun);

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
        var action = Approvals.Decide(call, parent.ContentLink, body.Publish, body.RequestApproval, $"New {type.Name} '{media.Name}'");

        Blob? blob = null;
        if (!body.DryRun)
        {
            blob = call.Service<IBlobFactory>().CreateBlob(media.BinaryDataContainer, extension);
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
                precheck: CreateOperation.Availability(call, parent, body.ForContent is not null, type, flow.Types)) with { MediaType = type.Name };
        }
        catch when (blob is not null && flow.Saved(media) is null)
        {
            // Nothing references the file if the save failed.
            call.Service<IBlobFactory>().Delete(blob.ID);
            throw;
        }
    }

    /// <summary>A new version of existing media with the new file, validated, diffed and saved like a draft.</summary>
    private static WriteResult Replace(CmsCall call, WriteFlow flow, UploadRequest body, string replaced, string extension, byte[]? data)
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
        var resolver = call.Service<ContentMediaResolver>();
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
        var action = Approvals.Decide(call, link, body.Publish, body.RequestApproval, what);
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
            blob = call.Service<IBlobFactory>().CreateBlob(writable.BinaryDataContainer, extension);
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
            call.Service<IBlobFactory>().Delete(blob.ID);
            throw;
        }
    }

    /// <summary>
    /// Files a browser runs script in when it opens them from the site, on its own origin, with the visitor's (or the
    /// editor's) session: HTML, XML (also through an XSLT stylesheet), JavaScript, and compressed SVG, which can't be read
    /// here. An SVG file is read instead (<see cref="MarkupSafety.SvgFile"/>), so an ordinary drawing still uploads.
    /// </summary>
    private static readonly HashSet<string> ScriptFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        ".html", ".htm", ".xhtml", ".xht", ".shtml", ".mht", ".mhtml", ".js", ".mjs", ".xml", ".xsl", ".xslt", ".svgz",
    };

    /// <summary>Raster image extensions: such a file is served as an image, and starts with its format's signature, never with markup.</summary>
    private static readonly HashSet<string> RasterImages = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".jpe", ".png", ".gif", ".webp", ".bmp", ".ico", ".tif", ".tiff", ".avif", ".heic", ".heif",
    };

    /// <summary>Whether the bytes, after a byte order mark and whitespace, start with '&lt;': markup, which a browser that sniffs could take for HTML.</summary>
    private static bool StartsLikeMarkup(byte[] data)
    {
        var (start, step) = data switch
        {
            [0xEF, 0xBB, 0xBF, ..] => (3, 1),
            [0xFF, 0xFE, ..] => (2, 2),
            [0xFE, 0xFF, ..] => (3, 2),
            _ => (0, 1),
        };
        for (var i = start; i < data.Length; i += step)
        {
            if (data[i] is not ((byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r' or (byte)'\f'))
            {
                return data[i] == (byte)'<';
            }
        }
        return false;
    }

    /// <summary>For an editor (<see cref="CmsCall.MayWriteScript"/>): no file that could run script on the site's origin.</summary>
    /// <exception cref="AgentException"><c>usage</c>.</exception>
    internal static void RequireNoScript(string extension, byte[]? data)
    {
        if (ScriptFiles.Contains(extension))
        {
            throw AgentException.Usage($"{extension} files can run script when they are opened from the site, so they aren't uploaded here.",
                "Ask the user to upload it in the CMS edit UI, if the site should have it. Nothing was uploaded.");
        }
        if (RasterImages.Contains(extension) && data is not null && StartsLikeMarkup(data))
        {
            throw AgentException.Usage($"The file is named {extension}, but it starts like markup (HTML or XML), not like an image.",
                "Upload the image itself, with the extension of its format. Nothing was uploaded.");
        }
        if (extension.Equals(".svg", StringComparison.OrdinalIgnoreCase) && data is not null && MarkupSafety.SvgFile(data) is { } problem)
        {
            throw AgentException.Usage($"The SVG file has {problem}, which could run script when it is opened from the site.",
                "Upload a drawing without it (export it again as a plain SVG), or ask the user to upload it in the CMS edit UI. Nothing was uploaded.");
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
    private static ContentType MediaType(CmsCall call, WriteFlow flow, string extension, string? requested)
    {
        var resolver = call.Service<ContentMediaResolver>();
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
            var type = TypeOperation.Find(call, flow.Types, requested);
            return accepting.Any(t => t.ID == type.ID)
                ? type
                : throw AgentException.Usage($"{type.Name} does not accept {extension} files.", Alternatives(call, extension, naming, anyFile));
        }
        return naming.FirstOrDefault() ?? throw AgentException.Usage(
            $"No media type is registered for {extension} files.", Alternatives(call, extension, naming, anyFile));
    }

    private static string Alternatives(CmsCall call, string extension, IReadOnlyList<ContentType> naming, IReadOnlyList<ContentType> anyFile)
    {
        var hints = new List<string>();
        if (naming.Count > 0)
        {
            hints.Add($"Types for {extension}: {string.Join(", ", naming.Select(t => t.Name))}.");
        }
        if (anyFile.Count > 0)
        {
            hints.Add($"Types that accept any file (only when named as {call.ForCaller("--type", "type")}): {string.Join(", ", anyFile.Select(t => t.Name))}.");
        }
        if (naming.Count == 0)
        {
            hints.Add("Media types list their extensions in [MediaDescriptor(ExtensionString = \"...\")] on the class.");
        }
        return string.Join(" ", hints);
    }
}
