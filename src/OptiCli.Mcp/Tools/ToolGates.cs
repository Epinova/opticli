using System.Globalization;
using EPiServer.Cms.Shell.UI.Configurations;
using OptiCli.Cms.Content;
using OptiCli.Mcp.OAuth;

namespace OptiCli.Mcp.Tools;

/// <summary>
/// What a tool checks before it touches the CMS: the scopes the editor granted the connection and what the site allows.
/// Neither ever widens what the editor may do; the CMS's access rights still decide every call. Kept apart from the
/// tools, which need a CMS, so the gates can be tested without one.
/// </summary>
internal static class ToolGates
{
    /// <exception cref="ModelContextProtocol.McpException"><c>refused</c> when the connection wasn't granted <paramref name="scope"/>.</exception>
    public static void Scope(IReadOnlyCollection<string> granted, string scope)
    {
        if (!granted.Contains(scope, StringComparer.Ordinal))
        {
            throw ToolErrors.Refused(
                $"This connection wasn't granted {scope} ({Scopes.Describe(scope).ToLowerInvariant()}).",
                "Tell the user: they can connect the assistant again and allow it on the consent page. Nothing was changed.",
                McpErrorReasons.MissingScope);
        }
    }

    /// <summary>
    /// For a call that would publish, unpublish or schedule a publish, or otherwise change what visitors see (move content
    /// that may be live, discard a scheduled version, save content without versions): the site must allow it and the
    /// editor must have granted <see cref="Scopes.Publish"/>. Drafts and review requests need neither.
    /// </summary>
    /// <exception cref="ModelContextProtocol.McpException"><c>refused</c>, with a hint that says what still works.</exception>
    public static void Publishing(IReadOnlyCollection<string> granted, OptiCliMcpOptions site)
    {
        if (!site.AllowPublish)
        {
            throw ToolErrors.Refused(
                "This site doesn't let assistants publish, unpublish or schedule publishing, or change what visitors see in other ways (moving content that may be live, discarding a scheduled version).",
                "Save the change as a draft (leave out publish and publishAt) and give the user its editUrl to review and publish it in the CMS; where an approval sequence applies, requestApproval sends it for review. A move or discard that changes what visitors see is for the user to make in the CMS. Nothing was changed.",
                McpErrorReasons.PublishingOff);
        }
        Scope(granted, Scopes.Publish);
    }

    /// <summary>
    /// For a call that deletes: moves content to the recycle bin, or discards a draft someone else saved (which is gone for
    /// good). The site must allow it.
    /// </summary>
    /// <exception cref="ModelContextProtocol.McpException"><c>refused</c> unless the site allows deleting.</exception>
    public static void Deleting(OptiCliMcpOptions site)
    {
        if (!site.AllowDelete)
        {
            throw ToolErrors.Refused(
                "This site doesn't let assistants delete content, or discard what someone else saved.",
                "Tell the user to do it in the CMS edit UI. Nothing was changed.",
                McpErrorReasons.DeletingOff);
        }
    }

    /// <summary>
    /// An upload follows the site's own upload rules as well as the module's: its extension must be one the CMS UI's
    /// upload allows (<see cref="CmsUploadRules"/>, where the site limits them), and its file, base64, at most the lower
    /// of <see cref="OptiCliMcpOptions.MaxUploadBytes"/> and the CMS UI's upload limit once decoded. The size is checked
    /// from its length, before anything is decoded.
    /// </summary>
    /// <param name="cms">The CMS UI's upload rules; null where it has none (a test, or a site without the CMS UI).</param>
    /// <exception cref="ModelContextProtocol.McpException"><c>usage</c> for an extension the site doesn't allow, or a larger file.</exception>
    public static void Upload(string? fileName, string? data, OptiCliMcpOptions site, CmsUploadRules? cms = null)
    {
        if (cms is { AllowedExtensions.Count: > 0 } && MediaFileNames.Extension(fileName) is { } extension && !cms.Allows(extension))
        {
            throw ToolErrors.Usage(
                $"This site doesn't allow uploading {extension} files.",
                $"Its uploads allow {string.Join(", ", cms.AllowedExtensions)}. Convert the file to one of those, or ask the user to upload it in the CMS. Nothing was uploaded.");
        }
        var limit = Math.Min(site.MaxUploadBytes, cms?.FileSizeLimit is > 0 and var cmsLimit ? cmsLimit : long.MaxValue);
        if (data is not null && DecodedLength(data) > limit)
        {
            throw ToolErrors.Usage(
                $"The file is larger than this site lets assistants upload ({Megabytes(limit)}).",
                "Ask the user to upload it in the CMS edit UI, or to make it smaller.");
        }
    }

    /// <summary>The bytes base64 text decodes to, ignoring whitespace (as <see cref="Convert.FromBase64String"/> does); invalid text is left to the decoder.</summary>
    internal static long DecodedLength(string base64)
    {
        long characters = 0;
        var padding = 0;
        foreach (var c in base64)
        {
            if (char.IsWhiteSpace(c))
            {
                continue;
            }
            characters++;
            padding = c == '=' ? padding + 1 : 0;
        }
        return characters / 4 * 3 + (characters % 4 is var rest and > 1 ? rest - 1 : 0) - Math.Min(padding, 2);
    }

    internal static string Megabytes(long bytes) =>
        bytes % (1024 * 1024) == 0
            ? string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024 * 1024)} MB")
            : string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024):0.#} MB");
}

/// <summary>
/// The CMS UI's own upload rules, which its upload dialog applies (<c>EPiServer:CmsUI:Upload</c>): a size limit, and
/// from CMS UI 12.33 a list of allowed file extensions. The module is built against an older CMS UI, so the list is read
/// by name where the site's CMS UI has it.
/// </summary>
/// <param name="FileSizeLimit">The largest upload the edit UI takes, in bytes; null or 0 for none.</param>
/// <param name="AllowedExtensions">Extensions as configured (<c>.jpg</c> or <c>jpg</c>); empty when any is allowed.</param>
internal sealed record CmsUploadRules(long? FileSizeLimit, IReadOnlyList<string> AllowedExtensions)
{
    /// <summary>The rules of the CMS UI's <see cref="UploadOptions"/>; null without them.</summary>
    public static CmsUploadRules? From(UploadOptions? options)
    {
        if (options is null)
        {
            return null;
        }
        // As the edit UI reads it: comma-separated, and "*" allows any.
        var configured = options.GetType().GetProperty("AllowedFileExtensions")?.GetValue(options) as string;
        var extensions = (configured ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        return new CmsUploadRules(options.FileSizeLimit, extensions.Contains("*") ? [] : extensions);
    }

    /// <summary>Whether <paramref name="extension"/> (<c>.jpg</c>) is allowed, matched as the edit UI matches it: it ends with an allowed one.</summary>
    public bool Allows(string extension) =>
        AllowedExtensions.Count == 0 || AllowedExtensions.Any(a => extension.EndsWith(a, StringComparison.OrdinalIgnoreCase));
}
