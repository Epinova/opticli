using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using OptiCli.Mcp.Tools;

namespace OptiCli.Mcp;

/// <summary>
/// The largest request the MCP endpoint takes: an upload of <see cref="OptiCliMcpOptions.MaxUploadBytes"/> as base64,
/// plus room for the rest of the call. The server's own default (30 MB on Kestrel and IIS) is no measure of what the
/// tools accept: it would cut off a large allowed upload with a bare connection error, and let anything else through
/// up to that size.
/// </summary>
/// <remarks>
/// A body that says it is larger is refused before it is read, with a 413 and a JSON-RPC error saying why. For one
/// without a length (chunked), the server's limit is set to the same size where it can be, so reading it fails at that
/// point. IIS has a request filtering limit of its own (<c>maxAllowedContentLength</c>, 30 MB by default), which a site
/// that raises <see cref="OptiCliMcpOptions.MaxUploadBytes"/> past about 20 MB must raise too.
/// </remarks>
internal static class McpRequestLimit
{
    /// <summary>Room for the JSON-RPC envelope, the file name and the item's other properties.</summary>
    public const long Overhead = 1024 * 1024;

    public static long Bytes(OptiCliMcpOptions options) => (options.MaxUploadBytes + 2) / 3 * 4 + Overhead;

    /// <summary><paramref name="next"/> (the MCP endpoint), for requests no larger than <paramref name="limit"/>.</summary>
    public static RequestDelegate Wrap(RequestDelegate next, long limit, long maxUploadBytes) => async context =>
    {
        if (context.Request.ContentLength > limit)
        {
            await TooLarge(context, limit, maxUploadBytes);
            return;
        }
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } size)
        {
            size.MaxRequestBodySize = limit;
        }
        try
        {
            await next(context);
        }
        catch (BadHttpRequestException e) when (e.StatusCode == StatusCodes.Status413PayloadTooLarge && !context.Response.HasStarted)
        {
            await TooLarge(context, limit, maxUploadBytes);
        }
    };

    private static Task TooLarge(HttpContext context, long limit, long maxUploadBytes)
    {
        context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
        context.Response.ContentType = "application/json";
        return context.Response.WriteAsync(JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = (object?)null,
            error = new
            {
                code = -32600,
                message = $"The request is larger than this site accepts ({ToolGates.Megabytes(limit)}). Files of up to {ToolGates.Megabytes(maxUploadBytes)} can be uploaded; ask the user to upload larger ones in the CMS.",
            },
        }));
    }
}
