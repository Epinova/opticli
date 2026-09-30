using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using OptiCli.Protocol;

namespace OptiCli.Agent.Http;

/// <summary>One matched request: the HTTP context plus the route's <c>{ref}</c>/<c>{name}</c> argument.</summary>
internal sealed class AgentRequest(HttpContext context, string? argument)
{
    private const int MaxBodyBytes = 16 * 1024 * 1024;

    /// <summary>
    /// Unknown fields are errors: a misspelt <c>dryRun</c> silently ignored would turn a dry run into a
    /// real save. (Responses stay lenient, so a newer agent can add fields without breaking older CLIs.)
    /// </summary>
    internal static readonly JsonSerializerOptions RequestOptions = new(AgentJson.Options)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public HttpContext Context { get; } = context;

    /// <summary>The route argument; only null for routes without one.</summary>
    public string Argument => argument ?? throw new InvalidOperationException("This route has no argument.");

    public T Service<T>() where T : notnull => Context.RequestServices.GetRequiredService<T>();

    /// <param name="maxBytes">Limit for this route; default 16 MB.</param>
    /// <exception cref="AgentException">The body is empty.</exception>
    public async Task<T> ReadBodyAsync<T>(int maxBytes = MaxBodyBytes) where T : class =>
        await ReadOptionalBodyAsync<T>(maxBytes) ?? throw AgentException.Usage($"This route needs a JSON body ({typeof(T).Name}).");

    public async Task<T?> ReadOptionalBodyAsync<T>(int maxBytes = MaxBodyBytes) where T : class
    {
        if (Context.Request.ContentLength > maxBytes)
        {
            throw AgentException.Usage($"The request body is larger than {maxBytes / (1024 * 1024)} MB.");
        }
        // The site's own server limit (Kestrel: 30 MB by default) would cut a large upload off first.
        if (maxBytes > MaxBodyBytes && Context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            limit.MaxRequestBodySize = maxBytes;
        }

        using var buffer = new MemoryStream();
        await Context.Request.Body.CopyToAsync(buffer, Context.RequestAborted);
        if (buffer.Length == 0)
        {
            return null;
        }
        if (buffer.Length > maxBytes)
        {
            throw AgentException.Usage($"The request body is larger than {maxBytes / (1024 * 1024)} MB.");
        }
        buffer.Position = 0;
        return JsonSerializer.Deserialize<T>(buffer, RequestOptions);
    }
}
