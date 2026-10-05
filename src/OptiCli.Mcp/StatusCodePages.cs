using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;

namespace OptiCli.Mcp;

/// <summary>
/// Keeps the site's status code pages (<c>UseStatusCodePages</c>, <c>UseStatusCodePagesWithReExecute</c>, common for
/// custom error pages) off the module's responses. Those rewrite an error without a body: a 401 from the MCP endpoint,
/// re-executed as a POST to a site's GET-only error page, reaches the client as a 405 without the
/// <c>WWW-Authenticate</c> header that tells it where to sign in, and OAuth discovery breaks. The module's errors are
/// for programs, which read the status, the headers and the JSON body, never a page meant for people.
/// </summary>
/// <remarks>
/// Both halves, because either alone can be undone: the module's errors all have a body (the challenge's too), which
/// the status code pages leave alone, and on the module's own requests they are switched off as well.
/// </remarks>
internal static class StatusCodePages
{
    /// <summary>Switches the site's status code pages off for this request, if it uses them.</summary>
    public static void Skip(HttpContext context)
    {
        if (context.Features.Get<IStatusCodePagesFeature>() is { } feature)
        {
            feature.Enabled = false;
        }
    }

    /// <summary><paramref name="next"/>, a module endpoint, with the site's status code pages off for its requests.</summary>
    public static RequestDelegate Skipping(RequestDelegate next) => context =>
    {
        Skip(context);
        return next(context);
    };
}
