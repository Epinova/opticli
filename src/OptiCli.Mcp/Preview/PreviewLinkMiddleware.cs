using System.Security.Claims;
using EPiServer.Core;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using OptiCli.Mcp.OAuth;
using OptiCli.Mcp.Tools;

namespace OptiCli.Mcp.Preview;

/// <summary>
/// Puts <see cref="PreviewLinkMiddleware"/> first in the site's pipeline when the site allows preview links. A preview
/// needs everything after it (routing, the CMS's preview, the site's controllers and views), which the module's own
/// endpoints, mapped after <c>MapContent()</c>, can't give it. With the option off, nothing is added, and the path is
/// the site's like any other.
/// </summary>
internal sealed class PreviewLinkStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        if (app.ApplicationServices.GetRequiredService<IOptions<OptiCliMcpOptions>>().Value.AllowPreviewLinks)
        {
            app.UseMiddleware<PreviewLinkMiddleware>();
        }
        next(app);
    };
}

/// <summary>
/// <c>{BasePath}/preview/{payload}</c> (<see cref="PreviewLinks"/>): the CMS's preview of the one version the link names,
/// as its editor sees it, for a client with no cookie and no header. Every other request passes through untouched.
/// </summary>
/// <remarks>
/// <para>
/// The link's payload (a link with a query or another path is refused), its connection and the editor's access to the
/// version are checked first. Then the request is turned into the CMS's preview of that version, in place: its path and
/// query are the preview URL's, its body is dropped and so is every header but <c>Host</c>, content negotiation and the
/// forwarded headers (<see cref="Keeps"/>): no cookie or credential, so no handler signs anyone in from them and the
/// CMS's preview token can't come along, and nothing else the client chose decides what is rendered. Only then is
/// <see cref="HttpContext.User"/> the editor: built from the grant as the bearer handler builds it, with the site's
/// claims transformation (which adds the CMS's virtual roles, such as CmsEditors, that its preview requires), but with
/// an authentication type of its own (<see cref="AuthenticationType"/>), so it is never an MCP editor
/// (<see cref="Tools.McpEditor"/>). The site's authentication finds no cookie and leaves it as it is. Once the rest of the
/// pipeline returns, the request is anonymous again.
/// </para>
/// <para>
/// The rest of the pipeline renders the preview into a buffer (at most <see cref="MaxBytes"/>). Only a success (2xx) is
/// sent on, without any <c>Set-Cookie</c> or <c>Location</c>; anything else (a redirect, a sign-in challenge, a refusal,
/// an error or an exception, and the site's own error page for it) becomes a plain 404 (500 for an error), so nothing
/// points anywhere, no cookie is set and no error details go out. Every response says <c>Cache-Control: no-store</c>,
/// <c>X-Robots-Tag: noindex, nofollow</c> and <c>Referrer-Policy: no-referrer</c>.
/// </para>
/// </remarks>
internal sealed class PreviewLinkMiddleware(
    RequestDelegate next,
    IOptions<OptiCliMcpOptions> options,
    PreviewLinks links,
    OAuthRateLimiter limiter,
    GrantCache cache,
    McpAudit audit,
    TimeProvider time,
    ILoggerFactory loggers)
{
    private readonly ILogger _logger = loggers.CreateLogger(ToolErrors.LogCategory);

    /// <summary>The preview's principal: not <see cref="TokenService.AuthenticationType"/>, so no tool ever runs as it.</summary>
    public const string AuthenticationType = "OptiCliMcpPreview";

    internal const string NotValid = "This preview link has expired or isn't valid; ask the assistant for a new one.";

    internal const string Failed = "The site couldn't show this preview.";

    /// <summary>The most a preview may be: a page, not a file download.</summary>
    internal const int MaxBytes = 10 * 1024 * 1024;

    /// <summary>The request headers a preview keeps: content negotiation, and what the site's forwarded headers read.</summary>
    private static readonly HashSet<string> KeptHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        HeaderNames.Host, HeaderNames.Accept, HeaderNames.AcceptEncoding, HeaderNames.AcceptLanguage, HeaderNames.UserAgent, "Forwarded",
    };

    public async Task InvokeAsync(HttpContext context)
    {
        if (!Handles(context.Request, out var token))
        {
            await next(context);
            return;
        }
        var request = context.Request;
        if (!HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method))
        {
            await RespondAsync(context, StatusCodes.Status405MethodNotAllowed, "A preview link only answers GET and HEAD.");
            return;
        }
        if (limiter.Check(OAuthRateLimiter.Preview, context) is { } slowDown)
        {
            Secure(context.Response);
            await slowDown.ExecuteAsync(context);
            return;
        }

        // A link the module made has no query: one with any (a preview token, epieditmode) isn't one of them.
        if (request.QueryString.HasValue || links.Read(token) is not { } link)
        {
            audit.Preview("-", "", "-", "-", "refused: not a valid link");
            await RespondAsync(context, StatusCodes.Status404NotFound, NotValid);
            return;
        }
        var grant = await cache.GetAsync(link.GrantId, context.RequestServices.GetRequiredService<IOAuthStore>(), context.RequestAborted);
        var now = time.GetUtcNow();
        if (grant is null || grant.Expires <= now || options.Value.ConnectionEnds(grant.Created) <= now
            || !string.Equals(grant.UserName, link.UserName, StringComparison.Ordinal) || !Scopes.Split(grant.Scope).Contains(Scopes.Read))
        {
            audit.Preview(link.Version, link.Language, link.UserName, grant?.ClientId ?? "-", "refused: connection revoked or ended");
            await RespondAsync(context, StatusCodes.Status404NotFound, NotValid);
            return;
        }

        string? target;
        ClaimsPrincipal editor;
        // The CMS caches what it works out for a request in its items (the context mode for this path, say), and the
        // preview must start as a request for its own path would: forget what the calls below leave there.
        var items = context.Items.Keys.ToHashSet();
        try
        {
            editor = await EditorAsync(context, grant);
            target = context.RequestServices.GetRequiredService<IPreviewVersions>()
                .PreviewUrl(ContentReference.Parse(link.Version), link.Language.Length == 0 ? null : link.Language, editor);
        }
        finally
        {
            foreach (var key in context.Items.Keys.Where(k => !items.Contains(k)).ToList())
            {
                context.Items.Remove(key);
            }
        }
        if (target is null || !Rewrite(request, target))
        {
            audit.Preview(link.Version, link.Language, link.UserName, grant.ClientId, "refused: version not readable");
            await RespondAsync(context, StatusCodes.Status404NotFound, NotValid);
            return;
        }

        // Only now, with the request the preview's own and its credentials gone, is it the editor's; and only until the
        // preview is rendered, so that nothing outside the module (the CMS's own error handling, say) runs as them.
        context.User = editor;
        string outcome;
        try
        {
            outcome = await RenderAsync(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            audit.Preview(link.Version, link.Language, link.UserName, grant.ClientId, "cancelled");
            throw;
        }
        finally
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity());
        }
        audit.Preview(link.Version, link.Language, link.UserName, grant.ClientId, outcome);
    }

    /// <summary>Whether the request is for a preview link, on the host the module answers on; <paramref name="token"/> is the payload.</summary>
    private bool Handles(HttpRequest request, out string token)
    {
        token = "";
        var site = options.Value;
        if (!request.Path.StartsWithSegments(PreviewLinks.Path(site), StringComparison.OrdinalIgnoreCase, out var rest)
            || (site.RequireHost is { Length: > 0 } host && !HostMatches(request.Host, host)))
        {
            return false;
        }
        // One segment: a payload is base64url, so a link with more segments (or none) is no link at all.
        token = rest.Value is { Length: > 1 } value ? value[1..] : "";
        return true;
    }

    /// <summary>Whether <paramref name="actual"/> is <paramref name="required"/>, as endpoint routing's RequireHost matches: any port unless one is given.</summary>
    internal static bool HostMatches(HostString actual, string required)
    {
        var expected = new HostString(required);
        return string.Equals(actual.Host, expected.Host, StringComparison.OrdinalIgnoreCase) && (expected.Port is null || actual.Port == expected.Port);
    }

    /// <summary>The editor, as the bearer handler and the site's authentication would make them, but not an MCP editor.</summary>
    private static async Task<ClaimsPrincipal> EditorAsync(HttpContext context, Grant grant)
    {
        var editor = TokenService.Principal(grant, authenticationType: AuthenticationType);
        return context.RequestServices.GetService<IClaimsTransformation>() is { } transformation
            ? await transformation.TransformAsync(editor)
            : editor;
    }

    /// <summary>
    /// Makes <paramref name="request"/> the preview of <paramref name="url"/>: its path and query (the request's own query
    /// is dropped), on the request's own host, with no body and only the headers <see cref="Keeps"/> allows: no cookie, no
    /// credential (so no CMS preview token either), nothing else the client chose that the site might read.
    /// </summary>
    /// <returns>False for no URL.</returns>
    internal static bool Rewrite(HttpRequest request, string url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return false;
        }
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            if (!Uri.TryCreate(new Uri("http://localhost/"), url, out uri))
            {
                return false;
            }
        }
        var path = PathString.FromUriComponent(uri);
        // The CMS's URL includes the application's own base path, which the request has already.
        if (request.PathBase.HasValue && path.StartsWithSegments(request.PathBase, StringComparison.OrdinalIgnoreCase, out var below))
        {
            path = below;
        }
        if (!path.HasValue)
        {
            return false;
        }
        foreach (var header in request.Headers.Keys.Where(h => !Keeps(h)).ToList())
        {
            request.Headers.Remove(header);
        }
        request.Body = Stream.Null;
        request.Path = path;
        request.QueryString = QueryString.FromUriComponent(uri);
        if (request.HttpContext.Features.Get<IHttpRequestFeature>() is { } feature)
        {
            feature.RawTarget = request.PathBase.Add(path).ToUriComponent() + request.QueryString.ToUriComponent();
        }
        return true;
    }

    /// <summary>
    /// Whether a preview keeps request header <paramref name="name"/>: <c>Host</c>, content negotiation, and the
    /// forwarded headers a proxy adds (<c>Forwarded</c>, <c>X-Forwarded-*</c>), which the site reads after the module.
    /// </summary>
    internal static bool Keeps(string name) => KeptHeaders.Contains(name) || name.StartsWith("X-Forwarded-", StringComparison.OrdinalIgnoreCase);

    /// <summary>Runs the rest of the pipeline into a buffer, and sends on what it made only if it is a success.</summary>
    /// <returns>The outcome, for the audit.</returns>
    private async Task<string> RenderAsync(HttpContext context)
    {
        var response = context.Response;
        // Registered first, so it runs last, after whatever the site added on the way out.
        response.OnStarting(() =>
        {
            Secure(response);
            return Task.CompletedTask;
        });
        var body = context.Features.Get<IHttpResponseBodyFeature>()!;
        using var buffer = new LimitedBuffer(MaxBytes);
        var buffered = new StreamResponseBodyFeature(buffer);
        context.Features.Set<IHttpResponseBodyFeature>(buffered);
        try
        {
            await next(context);
            await buffered.CompleteAsync();
        }
        catch (Exception e) when (!(e is OperationCanceledException && context.RequestAborted.IsCancellationRequested))
        {
            // Answered here, not passed on: the site's (or the CMS's) error handling outside the module would show the
            // exception, or its own error page, to whoever has the link.
            context.Features.Set(body);
            if (e is LimitedBuffer.FullException)
            {
                await RespondAsync(context, StatusCodes.Status500InternalServerError, Failed);
                return $"failed: larger than {MaxBytes / (1024 * 1024)} MB";
            }
            _logger.LogError(e, "Rendering the preview of a draft for an assistant's preview link failed.");
            await RespondAsync(context, StatusCodes.Status500InternalServerError, Failed);
            return $"failed: {e.GetType().Name}";
        }
        finally
        {
            context.Features.Set(body);
        }

        var status = response.StatusCode;
        if (status is < 200 or >= 300)
        {
            await RespondAsync(context, status >= 500 ? StatusCodes.Status500InternalServerError : StatusCodes.Status404NotFound, status >= 500 ? Failed : NotValid);
            return $"refused: the site answered {status}";
        }
        Secure(response);
        if (status is not (StatusCodes.Status204NoContent or StatusCodes.Status205ResetContent))
        {
            // Also for HEAD, which the site rendered all the same: the length a GET gets.
            response.ContentLength = buffer.Length;
        }
        if (!HttpMethods.IsHead(context.Request.Method))
        {
            buffer.Position = 0;
            await buffer.CopyToAsync(response.Body, context.RequestAborted);
        }
        return $"ok {status}";
    }

    /// <summary>A short plain-text answer of the module's own, with nothing of what the site may have set before.</summary>
    private static Task RespondAsync(HttpContext context, int status, string message)
    {
        var response = context.Response;
        // The site's status code pages, where they wrap the module (the CMS UI's own do), leave the answer alone.
        StatusCodePages.Skip(context);
        response.Headers.Clear();
        response.StatusCode = status;
        if (context.Features.Get<IHttpResponseFeature>() is { } feature)
        {
            feature.ReasonPhrase = null;
        }
        Secure(response);
        if (status == StatusCodes.Status405MethodNotAllowed)
        {
            response.Headers.Allow = "GET, HEAD";
        }
        response.ContentType = "text/plain; charset=utf-8";
        return response.WriteAsync(message, context.RequestAborted);
    }

    /// <summary>What every answer to a preview link has: no cookie, no redirect, not stored, not indexed, no referrer.</summary>
    internal static void Secure(HttpResponse response)
    {
        response.Headers.Remove(HeaderNames.SetCookie);
        response.Headers.Remove(HeaderNames.Location);
        response.Headers.CacheControl = "no-store";
        response.Headers["X-Robots-Tag"] = "noindex, nofollow";
        response.Headers["Referrer-Policy"] = "no-referrer";
    }

    /// <summary>A buffer that refuses to grow past <paramref name="limit"/> bytes.</summary>
    private sealed class LimitedBuffer(int limit) : MemoryStream
    {
        public sealed class FullException() : IOException("The preview is too large.");

        public override void Write(byte[] buffer, int offset, int count)
        {
            Check(count);
            base.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Check(buffer.Length);
            base.Write(buffer);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            Check(count);
            return base.WriteAsync(buffer, offset, count, cancellationToken);
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Check(buffer.Length);
            return base.WriteAsync(buffer, cancellationToken);
        }

        public override void WriteByte(byte value)
        {
            Check(1);
            base.WriteByte(value);
        }

        private void Check(int count)
        {
            if (Length + count > limit)
            {
                throw new FullException();
            }
        }
    }
}
