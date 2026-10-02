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
internal static class HtmlPage
{
    public static string H(string? value) => WebUtility.HtmlEncode(value ?? "");

    public static IResult Message(HttpContext context, HttpStatusCode status, string title, string message) =>
        Render(context, status, title, $"<h1>{H(title)}</h1><p>{H(message)}</p>");

    /// <param name="formTarget">An origin besides the module's own that a form's redirect may go to (the client's).</param>
    public static IResult Render(HttpContext context, HttpStatusCode status, string title, string body, string? formTarget = null)
    {
        var headers = context.Response.Headers;
        headers.XFrameOptions = "DENY";
        headers.ContentSecurityPolicy =
            $"default-src 'none'; style-src 'unsafe-inline'; img-src 'none'; base-uri 'none'; frame-ancestors 'none'; form-action 'self'{(formTarget is null ? "" : " " + formTarget)}";
        headers.CacheControl = "no-store";
        headers.Pragma = "no-cache";
        headers["Referrer-Policy"] = "no-referrer";
        headers.XContentTypeOptions = "nosniff";
        return Results.Content(
            $$"""
            <!doctype html>
            <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
            <title>{{H(title)}}</title>
            <style>
            body{font:16px/1.5 system-ui,sans-serif;max-width:40rem;margin:3rem auto;padding:0 1rem;color:#1b1b1b}
            button{font:inherit;padding:.4rem 1.1rem;margin-right:.5rem;cursor:pointer}
            table{border-collapse:collapse;width:100%;font-size:.9rem}td,th{text-align:left;padding:.35rem .5rem;border-bottom:1px solid #ddd;vertical-align:top}
            .muted{color:#666;font-size:.9rem}code{font-size:.85rem;word-break:break-all}
            .scopes{list-style:none;padding-left:0}.scopes li{margin:.3rem 0}
            </style></head><body>
            {{body}}
            </body></html>
            """,
            "text/html; charset=utf-8",
            Encoding.UTF8,
            (int)status);
    }
}
