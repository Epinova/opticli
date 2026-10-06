using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace OptiCli.Mcp.OAuth;

/// <summary>JSON for the OAuth documents and errors: the property names are the RFCs', whatever the site's own JSON settings.</summary>
internal static class OAuthJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    /// <summary>An OAuth error response (RFC 6749 5.2), never cached.</summary>
    public static IResult Error(HttpContext context, string error, string description, int status = StatusCodes.Status400BadRequest)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Pragma = "no-cache";
        return Results.Json(new Dictionary<string, string> { ["error"] = error, ["error_description"] = description }, Options, statusCode: status);
    }
}

/// <summary>
/// The module's few pages (consent, errors, connections). Plain HTML, every value encoded; they can't be framed
/// (clickjacking), load nothing, and forms post only to the module itself or, for consent, to the client's redirect.
/// </summary>
/// <remarks>
/// <para>
/// A site's own security-header middleware usually sets its headers as the response starts, after the module set its
/// own, and so replaces them; a callback of the module's can't come later than the site's. So the page carries its
/// policy and referrer policy in <c>meta</c> elements as well, which the browser applies besides any header: the
/// stricter of the two wins. <c>frame-ancestors</c> and <c>X-Frame-Options</c> only work as headers (see the README).
/// </para>
/// <para>
/// What doesn't depend on headers the site may replace: a page a browser asks for to show in a frame
/// (<c>Sec-Fetch-Dest</c>) is never rendered, and a form post the browser says came from another site
/// (<c>Sec-Fetch-Site</c>, <see cref="CrossSite"/>) is refused. A browser too old to send these headers gets the page.
/// </para>
/// </remarks>
internal static class HtmlPage
{
    /// <summary>What <c>Sec-Fetch-Dest</c> says when a page is to be shown inside another one.</summary>
    private static readonly string[] Embedded = ["iframe", "frame", "fencedframe", "object", "embed"];

    public static string H(string? value) => WebUtility.HtmlEncode(value ?? "");

    public static IResult Message(HttpContext context, HttpStatusCode status, string title, string message) =>
        Render(context, status, title, $"<h1>{H(title)}</h1><p>{H(message)}</p>");

    /// <summary>
    /// The page; or, when the browser says it is for a frame (<c>Sec-Fetch-Dest</c>), a 403 page instead, whatever
    /// headers the site sends: a consent page in someone else's frame is how an Allow click is stolen.
    /// </summary>
    /// <param name="formTarget">An origin besides the module's own that a form's redirect may go to (the client's).</param>
    public static IResult Render(HttpContext context, HttpStatusCode status, string title, string body, string? formTarget = null)
    {
        if (context.Request.Headers["Sec-Fetch-Dest"].ToString() is { Length: > 0 } destination
            && Embedded.Contains(destination.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            return Page(context, HttpStatusCode.Forbidden, "Not in a frame",
                $"<h1>Not in a frame</h1><p>{H("This page can't be shown inside another page. Open it in a tab of its own.")}</p>", null);
        }
        return Page(context, status, title, body, formTarget);
    }

    /// <summary>
    /// For a form post that changes something (consent, revoking): a 403 page when the browser says the post came from
    /// another site or origin (<c>Sec-Fetch-Site</c> other than <c>same-origin</c>); null when it came from the module's
    /// own page, or the browser doesn't say.
    /// </summary>
    public static IResult? CrossSite(HttpContext context) =>
        context.Request.Headers["Sec-Fetch-Site"].ToString() is { Length: > 0 } site && !site.Trim().Equals("same-origin", StringComparison.OrdinalIgnoreCase)
            ? Message(context, HttpStatusCode.Forbidden, "Not sent from this site", "The form wasn't sent from this site's own page, so nothing was changed. Open the page on the site and try again.")
            : null;

    private static IResult Page(HttpContext context, HttpStatusCode status, string title, string body, string? formTarget)
    {
        var headers = context.Response.Headers;
        var policy = $"default-src 'none'; style-src 'unsafe-inline'; img-src 'none'; base-uri 'none'; form-action 'self'{(formTarget is null ? "" : " " + formTarget)}";
        headers.XFrameOptions = "DENY";
        headers.ContentSecurityPolicy = policy + "; frame-ancestors 'none'";
        headers.CacheControl = "no-store";
        headers.Pragma = "no-cache";
        headers["Referrer-Policy"] = "no-referrer";
        headers.XContentTypeOptions = "nosniff";
        return Results.Content(
            $$"""
            <!doctype html>
            <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
            <meta http-equiv="Content-Security-Policy" content="{{H(policy)}}"><meta name="referrer" content="no-referrer">
            <title>{{H(title)}}</title>
            <style>
            body{font:16px/1.5 system-ui,sans-serif;max-width:40rem;margin:3rem auto;padding:0 1rem;color:#1b1b1b}
            button{font:inherit;padding:.4rem 1.1rem;margin-right:.5rem;cursor:pointer}
            table{border-collapse:collapse;width:100%;font-size:.9rem}td,th{text-align:left;padding:.35rem .5rem;border-bottom:1px solid #ddd;vertical-align:top}
            .muted{color:#666;font-size:.9rem}code{font-size:.85rem;word-break:break-all}
            .scopes{list-style:none;padding-left:0}.scopes li{margin:.3rem 0}
            .warning{background:#fff4e5;border:1px solid #e8a33d;border-radius:4px;padding:.6rem .8rem}
            </style></head><body>
            {{body}}
            </body></html>
            """,
            "text/html; charset=utf-8",
            Encoding.UTF8,
            (int)status);
    }
}
