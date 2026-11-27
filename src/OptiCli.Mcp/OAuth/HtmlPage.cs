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
/// The module's few pages (consent, errors, connections), in opticli's look: a coloured panel, the decision on a dark
/// bar below it. Plain HTML, every value encoded; they can't be framed
/// (clickjacking), load nothing, and forms post only to the module itself or, for consent, to the client's redirect.
/// </summary>
/// <remarks>
/// <para>
/// A site's own security-header middleware usually sets its headers as the response starts, after the module set its
/// own, and so replaces them; a callback of the module's can't come later than the site's. So the page carries its
/// policy and referrer policy in <c>meta</c> elements as well, which the browser applies besides any header: the
/// stricter of the two wins. <c>frame-ancestors</c> and <c>X-Frame-Options</c> only work as headers (see docs/reference/mcp/security.md).
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
        Render(context, status, title, Panel($"<h1>{H(title)}</h1><p>{H(message)}</p>"));

    /// <summary>The page's coloured field; a form puts its buttons on the dark bar below it (<c>class="shelf"</c>).</summary>
    public static string Panel(string content) => $"<div class=\"panel\">{content}</div>";

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
                Panel($"<h1>Not in a frame</h1><p>{H("This page can't be shown inside another page. Open it in a tab of its own.")}</p>"), null);
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

    /// <summary>
    /// A 400 page for a request that carries a CMS preview token (<see cref="PreviewTokens"/>), which the site would
    /// take as the editor's sign-in; null for any other request. The consent and connections pages check this first.
    /// </summary>
    public static IResult? PreviewToken(HttpContext context) =>
        PreviewTokens.InRequest(context.Request)
            ? Message(context, HttpStatusCode.BadRequest, "Not with a preview link", "A CMS preview link can't be used to sign in here. Open the page without its preview_token.")
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
            :root{color-scheme:light dark;--ground:#f3f3ef;--ink:#1d2126;--soft:#56606a;--panel:#dde6f2;--panel-ink:#234a8a;--line:#234a8a38;--shelf:#1d2126;--shelf-ink:#f3f3ef;--shelf-soft:#b8c0c8;--mark:#1c5a35;--band:#f9dcd8;--band-ink:#7d1c15}
            @media(prefers-color-scheme:dark){:root{--ground:#15181b;--ink:#e9ecee;--soft:#a3abb3;--panel:#1d2636;--panel-ink:#a9c3f5;--line:#a9c3f538;--shelf:#e9ecee;--shelf-ink:#15181b;--shelf-soft:#4a535c;--mark:#9fdcb3;--band:#3f1f1c;--band-ink:#ffb4ab} }
            *{box-sizing:border-box}
            body{margin:0;padding:clamp(1rem,6vh,4rem) 16px 3rem;background:var(--ground);color:var(--ink);font:17px/1.5 system-ui,-apple-system,"Segoe UI",Roboto,sans-serif}
            header,main{max-width:34rem;margin:0 auto}main:has(table){max-width:60rem}
            header{font-weight:800;font-size:1.3rem;letter-spacing:-.02em;margin-bottom:1rem}header span{color:var(--mark)}
            .panel{background:var(--panel);border-radius:22px;padding:clamp(1.5rem,6vw,2.5rem)}form>.panel{border-radius:22px 22px 0 0}
            h1{font-size:clamp(2rem,7vw,2.6rem);font-weight:800;line-height:1.05;letter-spacing:-.03em;margin:0 0 1rem;text-wrap:balance}
            p{margin:0 0 1rem}.panel>:last-child{margin-bottom:0}b{overflow-wrap:anywhere}
            .warning{background:var(--band);color:var(--band-ink);border-radius:12px;padding:.8rem 1rem}
            .scopes{list-style:none;margin:1.25rem 0;padding:0}.scopes li{border-top:1px solid var(--line)}
            .scopes label{display:flex;gap:.75rem;padding:.75rem 0;font-weight:600;cursor:pointer}
            .scopes input{accent-color:var(--panel-ink);width:1.1rem;height:1.1rem;margin:.25rem 0 0;flex:none}
            .scopes small{display:block;color:var(--panel-ink);font-size:.85rem;font-weight:700}
            .muted{color:var(--soft);font-size:.9rem}main>.muted{margin:1rem .25rem 0}
            .shelf{background:var(--shelf);color:var(--shelf-ink);border-radius:0 0 22px 22px;padding:1rem clamp(1.5rem,6vw,2.5rem);display:flex;flex-wrap:wrap;align-items:center;gap:.75rem 1rem}
            .shelf p{flex:1 1 14rem;margin:0;font-size:.9rem;color:var(--shelf-soft)}.shelf b{color:var(--shelf-ink)}
            button{font:inherit;font-weight:700;padding:.6rem 1.4rem;border-radius:999px;cursor:pointer;border:1.5px solid var(--panel-ink);background:none;color:var(--panel-ink)}
            button:hover{background:var(--ground)}
            .shelf button{border-color:var(--shelf-soft);color:var(--shelf-ink)}.shelf button:hover{border-color:var(--shelf-ink);background:none}
            .shelf button[value=allow]{background:var(--shelf-ink);border-color:var(--shelf-ink);color:var(--shelf)}.shelf button[value=allow]:hover{background:var(--panel)}
            .table{overflow-x:auto;margin-top:1.25rem}
            table{border-collapse:collapse;width:100%;font-size:.9rem}td,th{text-align:left;padding:.6rem .75rem .6rem 0;border-top:1px solid var(--line);vertical-align:top}
            th{color:var(--panel-ink);font-weight:700;border-top:0}td form{margin:0}td button{padding:.3rem .9rem;font-size:.85rem}
            code{font-size:.8rem;color:var(--soft);word-break:break-all}
            @media(max-width:40rem){.table :is(table,tbody,tr,td){display:block}.table tr:first-child{display:none}.table tr{border-top:1px solid var(--line);padding:.6rem 0}.table td{border:0;padding:.1rem 0}td[data-label]::before{content:attr(data-label) ": ";color:var(--panel-ink);font-weight:700}td form{margin-top:.4rem} }
            a{color:inherit;text-underline-offset:.2em}
            :focus-visible{outline:2px solid var(--panel-ink);outline-offset:3px}.shelf :focus-visible{outline-color:var(--shelf-ink)}
            ::selection{background:var(--panel)}
            </style></head><body>
            <header>opticli<span>.</span></header>
            <main>
            {{body}}
            </main>
            </body></html>
            """,
            "text/html; charset=utf-8",
            Encoding.UTF8,
            (int)status);
    }
}
