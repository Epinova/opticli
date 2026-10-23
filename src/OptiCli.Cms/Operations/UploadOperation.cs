using System.Security.Cryptography;
using EPiServer.Core;
using EPiServer.DataAbstraction;
using EPiServer.DataAccess;
using EPiServer.Framework.Blobs;
using EPiServer.Web;
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
            RequireNoScript(call, extension, data);
        }
        if (body.Replace is { } replaced)
        {
            flow.NewFile = (extension, data);
            return Replace(call, flow, body, replaced, extension, data);
        }

        var type = MediaType(call, flow, extension, body.Type);
        var (parent, _) = CreateOperation.Parent(flow, body.Parent, body.ForContent, body.DryRun);

        if (body.Guid is { } guid && ExistingContent.Find(flow, guid) is { } existing)
        {
            return UpdateExisting(call, flow, body, existing, type, parent, extension, data);
        }

        var media = flow.Repository.GetDefault<MediaData>(parent.ContentLink, type.ID);
        if (body.Guid is { } fixedGuid)
        {
            media.ContentGuid = fixedGuid;
        }
        var before = PropertyValues.Snapshot(media);
        flow.NewFile = (extension, data);
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

    /// <summary>
    /// Media with the request's GUID exists (<c>updateExisting</c>, a plan run again): updated like other content, and its
    /// file replaced in the new version when it differs from the one it has, compared by SHA-256 (a dry run sends only the
    /// hash). The same file is no change.
    /// </summary>
    private static WriteResult UpdateExisting(CmsCall call, WriteFlow flow, UploadRequest body, IContent existing, ContentType type, IContent parent, string extension, byte[]? data)
    {
        var name = string.IsNullOrWhiteSpace(body.Name) ? body.FileName.Trim() : body.Name;
        var hash = data is not null ? Convert.ToHexString(SHA256.HashData(data)) : body.Sha256;
        if (hash is null)
        {
            // Nothing to compare with (a dry run of a request without the hash): the file is left as it is.
            var kept = ExistingContent.Update(flow, existing, body.UpdateExisting, type, parent, null, name, body.Properties, body.Publish, body.RequestApproval, body.IncludeDraft, body.DryRun);
            return kept with
            {
                MediaType = type.Name,
                Validation = [.. kept.Validation ?? [], new ValidationIssue(null, "The media item exists, so its file was kept; replace it in the CMS edit UI if it changed.", "warning")],
            };
        }

        Blob? blob = null;
        var replaced = false;
        bool Replace(IContent writable)
        {
            if (writable is not MediaData media || SameFile(media.BinaryData, hash))
            {
                return false;
            }
            replaced = true;
            flow.NewFile = (extension, data);
            if (!body.DryRun)
            {
                blob = call.Service<IBlobFactory>().CreateBlob(media.BinaryDataContainer, extension);
                using var stream = new MemoryStream(data!, writable: false);
                blob.Write(stream);
                media.BinaryData = blob;
            }
            return true;
        }

        WriteResult updated;
        try
        {
            updated = ExistingContent.Update(flow, existing, body.UpdateExisting, type, parent, null, name, body.Properties, body.Publish, body.RequestApproval, body.IncludeDraft, body.DryRun, alter: Replace);
        }
        catch when (blob is not null)
        {
            // The save failed (or was refused), so nothing references the new file.
            call.Service<IBlobFactory>().Delete(blob.ID);
            throw;
        }
        return updated with
        {
            MediaType = type.Name,
            Validation = replaced
                ? [.. updated.Validation ?? [], new ValidationIssue(null, $"The file differs from the media item's, so {(body.DryRun ? "it would be" : "it was")} replaced in the new version.", "warning")]
                : updated.Validation,
        };
    }

    /// <summary>Whether the stored file has the SHA-256 <paramref name="hash"/> (hex); false when it can't be read.</summary>
    private static bool SameFile(Blob? stored, string hash)
    {
        if (stored is null)
        {
            return false;
        }
        try
        {
            using var stream = stored.OpenRead();
            return Convert.ToHexString(SHA256.HashData(stream)).Equals(hash.Trim(), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
        {
            // A file the provider can't find (a copied database without its blobs): replacing it is what helps.
            return false;
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
    /// Besides the obvious ones, the extensions a default site serves as HTML (<c>.hxt</c>, <c>.stm</c>), as XML
    /// (<c>.xsd</c>, <c>.dtd</c>, <c>.wsdl</c>, ...), as script (<c>.jsx</c>) or as types only old browsers ran: also
    /// refused where the site can't say what it serves them as (<see cref="ServedAs"/> and <see cref="RunsScript"/>
    /// decide otherwise).
    /// </summary>
    private static readonly HashSet<string> ScriptFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        ".html", ".htm", ".xhtml", ".xht", ".shtml", ".mht", ".mhtml", ".js", ".mjs", ".xml", ".xsl", ".xslt", ".svgz",
        ".hxt", ".stm", ".htt", ".hta", ".htc", ".sct", ".dtd", ".xsd", ".xsf", ".wsdl", ".disco", ".mno", ".vml", ".atom",
        ".rss", ".rdf", ".xaml", ".osdx", ".jsx", ".vbs",
    };

    /// <summary>
    /// Whether a browser runs script in a file it gets as <paramref name="mimeType"/> (parameters ignored): HTML, XHTML, XML
    /// in any of its types but SVG (which <see cref="MarkupSafety.SvgFile"/> reads), JavaScript, MHTML, and the scriptlet and
    /// HTML application types of older browsers.
    /// </summary>
    internal static bool RunsScript(string mimeType)
    {
        var type = mimeType.Split(';')[0].Trim().ToLowerInvariant();
        return type is "text/html" or "application/xhtml+xml" or "text/xml" or "application/xml" or "text/xsl" or "text/x-component"
                or "text/scriptlet" or "application/hta" or "text/webviewhtml" or "message/rfc822" or "multipart/related"
            || type.Contains("javascript", StringComparison.Ordinal) || type.Contains("ecmascript", StringComparison.Ordinal)
            || type.Contains("jscript", StringComparison.Ordinal) || type.Contains("livescript", StringComparison.Ordinal) || type.Contains("vbscript", StringComparison.Ordinal)
            || (type.EndsWith("+xml", StringComparison.Ordinal) && type != Svg);
    }

    private const string Svg = "image/svg+xml";

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

    /// <summary>
    /// The MIME types the site may serve a file with <paramref name="extension"/> as: the one its media URLs get (the
    /// static file content types, <see cref="IServedContentTypes"/>, where the MCP module provides them), and the one the
    /// CMS's MIME registry gives the stored file (<c>MediaData.MimeType</c>, what it serves when the URL's extension is
    /// unknown). Either may know what a site added.
    /// </summary>
    internal static IReadOnlyList<string> ServedAs(CmsCall call, string extension)
    {
        var name = "file" + extension;
#pragma warning disable CS0618 // Obsolete on CMS 13; the extension list still applies there.
        var registry = call.OptionalService<IMimeTypeResolver>()?.GetMimeMapping(name);
#pragma warning restore CS0618
        return new[] { call.OptionalService<IServedContentTypes>()?.For(name), registry }
            .OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary><see cref="RequireNoScript(string, byte[], string)"/> for every type the site may serve the file as (<see cref="ServedAs"/>).</summary>
    internal static void RequireNoScript(CmsCall call, string extension, byte[]? data)
    {
        var types = ServedAs(call, extension);
        foreach (var type in types.Count == 0 ? [null] : types.Cast<string?>())
        {
            RequireNoScript(extension, data, type);
        }
    }

    /// <summary>
    /// For an editor's save of media: the extension its URL ends in (its route segment, which the CMS makes from its name
    /// when it has none, or again when it must), when it isn't the file's own, is checked as if it were, since that is
    /// what the site serves the file as. The file is the upload's (<paramref name="newFile"/>), or the one stored. Only
    /// when something of that changes: an upload, or a name or route segment other than <paramref name="unchanged"/>'s.
    /// </summary>
    /// <param name="unchanged">The version whose name, segment and file are what visitors get, or would get anyway: the one saved over, or the published one; null for new media.</param>
    /// <exception cref="AgentException"><c>usage</c>.</exception>
    internal static void RequireServedSafely(CmsCall call, MediaData media, (string Extension, byte[]? Data)? newFile, IContent? unchanged)
    {
        if (newFile is null && unchanged is MediaData before && before.Name == media.Name && before.RouteSegment == media.RouteSegment
            && before.BinaryData?.ID == media.BinaryData?.ID)
        {
            return;
        }
        var fileExtension = newFile?.Extension ?? (media.BinaryData is { } stored ? Path.GetExtension(stored.ID.AbsolutePath) : "");
        // Where each extension comes from, for the message.
        var names = new List<(string? Value, string Source)>
        {
            (media.RouteSegment, $"its URL segment '{media.RouteSegment}'"),
            (media.Name?.Trim().TrimEnd('.'), $"its name '{media.Name}'"),
        };
        if (!string.IsNullOrWhiteSpace(media.Name) && call.OptionalService<IUrlSegmentGenerator>() is { } segments)
        {
            // What the CMS will make of the name: it drops characters a URL can't have, and trailing dots.
            names.Add((segments.Create(media.Name), $"its name '{media.Name}'"));
        }
        var served = names.Where(n => !string.IsNullOrEmpty(n.Value)).Select(n => (Extension: Path.GetExtension(n.Value!).ToLowerInvariant(), n.Source))
            .Where(e => e.Extension.Length > 1 && !e.Extension.Equals(fileExtension, StringComparison.OrdinalIgnoreCase))
            .DistinctBy(e => e.Extension).ToList();
        if (served.Count == 0)
        {
            return;
        }
        var hint = $"Name it with the file's extension ({fileExtension}) or without one. Nothing was saved.";
        var data = newFile?.Data;
        var read = newFile is not null;
        foreach (var (extension, source) in served)
        {
            // Whether the file's content decides (an SVG drawing, an image), and so must be read.
            var content = extension == ".svg" || RasterImages.Contains(extension)
                || ServedAs(call, extension).Any(t => t.StartsWith(Svg, StringComparison.OrdinalIgnoreCase));
            if (content && !read)
            {
                data = Stored(media.BinaryData);
                read = true;
            }
            try
            {
                RequireNoScript(call, extension, content ? data : null);
            }
            catch (AgentException refused)
            {
                throw AgentException.Usage($"The media's address ends in {extension} (from {source}), and the site serves the file as that: {refused.Message}", hint);
            }
            if (content && data is null)
            {
                // Whose content would decide, but can't be read here.
                throw AgentException.Usage($"The media's address ends in {extension} (from {source}), which its file ({fileExtension}) isn't, and the file can't be checked.", hint);
            }
        }
    }

    /// <summary>The most of a stored file read to check it as another type; a larger one isn't checked, and so refused.</summary>
    private const int LargestChecked = 10 * 1024 * 1024;

    /// <summary>The stored file's bytes; null when there is none, it is larger than <see cref="LargestChecked"/>, or it can't be read.</summary>
    private static byte[]? Stored(Blob? blob)
    {
        if (blob is null)
        {
            return null;
        }
        try
        {
            using var stream = blob.OpenRead();
            using var copy = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (copy.Length + read > LargestChecked)
                {
                    return null;
                }
                copy.Write(buffer, 0, read);
            }
            return copy.ToArray();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Not there (a copied database without its blobs), or a provider's own failure: it can't be checked.
            return null;
        }
    }

    /// <summary>For an editor (<see cref="CmsCall.MayWriteScript"/>): no file that could run script on the site's origin.</summary>
    /// <param name="servedAs">A MIME type the site serves a file with this extension as (<see cref="ServedAs"/>); null where it can't say.</param>
    /// <exception cref="AgentException"><c>usage</c>.</exception>
    internal static void RequireNoScript(string extension, byte[]? data, string? servedAs = null)
    {
        if (ScriptFiles.Contains(extension) || (servedAs is not null && RunsScript(servedAs)))
        {
            throw AgentException.Usage($"{extension} files can run script when they are opened from the site, so they aren't uploaded here.",
                "Ask the user to upload it in the CMS edit UI, if the site should have it. Nothing was uploaded.");
        }
        // Whatever it is named: the site serves it as an SVG image.
        if (string.Equals(servedAs?.Split(';')[0].Trim(), Svg, StringComparison.OrdinalIgnoreCase) && !extension.Equals(".svg", StringComparison.OrdinalIgnoreCase)
            && data is not null && MarkupSafety.SvgFile(data) is { } served)
        {
            throw AgentException.Usage($"The file is served as an SVG image, and has {served}, which could run script when it is opened from the site.",
                "Upload a drawing without it, or ask the user to upload it in the CMS edit UI. Nothing was uploaded.");
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
