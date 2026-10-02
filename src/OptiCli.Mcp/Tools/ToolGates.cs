using System.Globalization;
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
    /// For a call that would publish, unpublish or schedule a publish: the site must allow it and the editor must have
    /// granted <see cref="Scopes.Publish"/>. Drafts and review requests need neither.
    /// </summary>
    /// <exception cref="ModelContextProtocol.McpException"><c>refused</c>, with a hint that says what still works.</exception>
    public static void Publishing(IReadOnlyCollection<string> granted, OptiCliMcpOptions site)
    {
        if (!site.AllowPublish)
        {
            throw ToolErrors.Refused(
                "This site doesn't let assistants publish, unpublish or schedule publishing.",
                "Save the change as a draft (leave out publish and publishAt) and give the user its editUrl to review and publish it in the CMS; where an approval sequence applies, requestApproval sends it for review. Nothing was changed.",
                McpErrorReasons.PublishingOff);
        }
        Scope(granted, Scopes.Publish);
    }

    /// <exception cref="ModelContextProtocol.McpException"><c>refused</c> unless the site allows deleting.</exception>
    public static void Deleting(OptiCliMcpOptions site)
    {
        if (!site.AllowDelete)
        {
            throw ToolErrors.Refused(
                "This site doesn't let assistants delete content.",
                "Tell the user to delete it in the CMS edit UI. Nothing was changed.",
                McpErrorReasons.DeletingOff);
        }
    }

    /// <summary>
    /// An upload's file, base64, must be at most <see cref="OptiCliMcpOptions.MaxUploadBytes"/> once decoded. Checked
    /// from its length, before anything is decoded.
    /// </summary>
    /// <exception cref="ModelContextProtocol.McpException"><c>usage</c> for a larger file.</exception>
    public static void Upload(string? data, OptiCliMcpOptions site)
    {
        if (data is not null && DecodedLength(data) > site.MaxUploadBytes)
        {
            throw ToolErrors.Usage(
                $"The file is larger than this site lets assistants upload ({Megabytes(site.MaxUploadBytes)}).",
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
