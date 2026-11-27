using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace OptiCli.Mcp.OAuth;

/// <summary>
/// The largest form the module's form posts take (the token endpoint, the consent post, a revoke on the connections
/// page): <see cref="Bytes"/>, far more than any of them sends. They are reached without a token, and the server's own
/// limit (30 MB on Kestrel and IIS) is no measure of them.
/// </summary>
/// <remarks>
/// A body that says it is larger is refused before it is read; any other is read up to the limit, where the server's
/// limit is set to the same size where it can be, and then served from memory to the handler and to antiforgery, which
/// read it as a form.
/// </remarks>
internal static class FormLimit
{
    public const int Bytes = 64 * 1024;

    /// <summary><paramref name="next"/> (a form post), for requests no larger than <see cref="Bytes"/>; a 413 for others.</summary>
    /// <param name="json">Whether the endpoint answers in OAuth's JSON (the token endpoint) rather than with a page.</param>
    public static RequestDelegate Wrap(RequestDelegate next, bool json) => async context =>
    {
        if (await ReadAsync(context) is null)
        {
            await TooLarge(context, json).ExecuteAsync(context);
            return;
        }
        await next(context);
    };

    /// <returns>The body, now also the request's body again; null when it is larger than <see cref="Bytes"/>.</returns>
    private static async Task<byte[]?> ReadAsync(HttpContext context)
    {
        if (context.Request.ContentLength > Bytes)
        {
            return null;
        }
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } size)
        {
            size.MaxRequestBodySize = Bytes;
        }
        var buffer = new byte[Bytes + 1];
        var length = 0;
        try
        {
            int read;
            while (length < buffer.Length && (read = await context.Request.Body.ReadAsync(buffer.AsMemory(length), context.RequestAborted)) > 0)
            {
                length += read;
            }
        }
        catch (BadHttpRequestException e) when (e.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return null;
        }
        if (length > Bytes)
        {
            return null;
        }
        var body = buffer[..length];
        context.Request.Body = new MemoryStream(body, writable: false);
        return body;
    }

    private static IResult TooLarge(HttpContext context, bool json) => json
        ? OAuthJson.Error(context, "invalid_request", $"The request is larger than this endpoint takes ({Bytes / 1024} KB).", StatusCodes.Status413PayloadTooLarge)
        : HtmlPage.Message(context, HttpStatusCode.RequestEntityTooLarge, "Too large", "The form sent was larger than this page takes. Open the page again and send it from there.");
}
