using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using EPiServer.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using OptiCli.Agent.Content;
using OptiCli.Agent.Drift;
using OptiCli.Agent.Endpoints;
using OptiCli.Agent.Hosting;
using OptiCli.Agent.Safety;
using OptiCli.Protocol;

namespace OptiCli.Agent.Http;

/// <summary>
/// The whole agent: a terminal middleware branch at <see cref="AgentProtocol.BasePath"/> that guards,
/// routes, runs the endpoint as the opticli principal, and writes the envelope.
/// </summary>
internal static class AgentMiddleware
{
    private static readonly AgentMeta Meta = new("agent", AgentInfo.Version, AgentProtocol.Version);

    public static async Task HandleAsync(HttpContext context)
    {
        try
        {
            RequestGuard.Check(context, context.RequestServices.GetRequiredService<AgentSettings>());
            var route = AgentRouter.Match(context.Request.Method, context.Request.Path.Value);

            using (OptiCliPrincipal.Enter(context))
            {
                var (status, data) = await DispatchAsync(new AgentRequest(context, route.Argument), route.Endpoint);
                await WriteAsync(context, status, new AgentResponse<object>(true, data, Meta));
            }
        }
        catch (Exception ex)
        {
            var failure = Translate(ex);
            if (failure.Code == AgentErrorCodes.Internal)
            {
                // Full detail goes to the site's log only; the caller gets the message and type.
                Console.Error.WriteLine($"[opticli] {context.Request.Method} {context.Request.Path} failed: {ex}");
            }
            await WriteAsync(context, failure.Status, new AgentResponse<object>(false, null, Meta, failure.ToError()));
        }
    }

    private static async Task<(int Status, object Data)> DispatchAsync(AgentRequest request, AgentEndpoint endpoint) =>
        Handlers.TryGetValue(endpoint, out var handler) ? await handler.Run(request) : throw new InvalidOperationException($"Unhandled endpoint {endpoint}.");

    /// <summary>
    /// What runs each endpoint. Every write is made with <see cref="Write{T}"/>, which passes <see cref="DriftGate"/> once
    /// the body says whether it is a dry run; the tests check that each route that changes something is one.
    /// </summary>
    internal static readonly IReadOnlyDictionary<AgentEndpoint, EndpointHandler> Handlers = new Dictionary<AgentEndpoint, EndpointHandler>
    {
        [AgentEndpoint.Ping] = Read(request => PingEndpoint.Handle(request)),
        [AgentEndpoint.Shutdown] = Read(request => ShutdownEndpoint.Handle(request)),
        [AgentEndpoint.Drift] = Read(request => DriftEndpoint.Handle(request)),
        [AgentEndpoint.Type] = Read(request => TypeEndpoint.Handle(request)),
        [AgentEndpoint.Read] = Read(request => ReadEndpoint.Handle(request)),
        [AgentEndpoint.Create] = Write(r => r.ReadBodyAsync<CreateRequest>(), b => b.DryRun, (r, b) => Created(CreateEndpoint.Handle(r, b))),
        [AgentEndpoint.Upload] = Write(r => r.ReadBodyAsync<UploadRequest>(UploadEndpoint.MaxBodyBytes), b => b.DryRun, (r, b) => Created(UploadEndpoint.Handle(r, b))),
        [AgentEndpoint.Draft] = Write(r => r.ReadBodyAsync<DraftRequest>(), b => b.DryRun, (r, b) => Ok(DraftEndpoint.Handle(r, b))),
        [AgentEndpoint.Languages] = Write(r => r.ReadBodyAsync<LanguageBranchRequest>(), b => b.DryRun, (r, b) => Created(LanguageEndpoint.Handle(r, b))),
        [AgentEndpoint.Publish] = Write(async r => await r.ReadOptionalBodyAsync<PublishRequest>() ?? new PublishRequest(), _ => false, (r, b) => Ok(PublishEndpoint.Handle(r, b))),
        [AgentEndpoint.RemoveLanguage] = Write(r => r.ReadBodyAsync<RemoveLanguageRequest>(), b => b.DryRun, (r, b) => Ok(LanguageEndpoint.Remove(r, b))),
        [AgentEndpoint.Unpublish] = Write(async r => await r.ReadOptionalBodyAsync<UnpublishRequest>() ?? new UnpublishRequest(), b => b.DryRun, (r, b) => Ok(VersionEndpoints.Unpublish(r, b))),
        [AgentEndpoint.Discard] = Write(async r => await r.ReadOptionalBodyAsync<DiscardRequest>() ?? new DiscardRequest(), b => b.DryRun, (r, b) => Ok(VersionEndpoints.Discard(r, b))),
        [AgentEndpoint.Move] = Write(r => r.ReadBodyAsync<MoveRequest>(), b => b.DryRun, (r, b) => Ok(MoveEndpoint.Move(r, b))),
        [AgentEndpoint.Access] = Write(r => r.ReadBodyAsync<AccessRequest>(), b => b.DryRun, async (r, b) => Ok(await AccessEndpoint.HandleAsync(r, b))),
        // No body and no dry run: a delete is always real.
        [AgentEndpoint.Delete] = Write(Task.FromResult, _ => false, (r, _) => Ok(MoveEndpoint.Delete(r))),
    };

    /// <param name="Gated">The endpoint is a write, and passes <see cref="DriftGate"/> before it runs.</param>
    internal sealed record EndpointHandler(bool Gated, Func<AgentRequest, Task<(int Status, object Data)>> Run);

    /// <summary>An endpoint that changes nothing in the content (or, for shutdown, in the database).</summary>
    private static EndpointHandler Read(Func<AgentRequest, object> handle) => new(false, request => Task.FromResult((200, handle(request))));

    /// <summary>A write: its body is read, <see cref="DriftGate"/> checked (dry runs pass), then it runs.</summary>
    private static EndpointHandler Write<T>(Func<AgentRequest, Task<T>> read, Func<T, bool> dryRun, Func<AgentRequest, T, (int, object)> handle) =>
        Write(read, dryRun, (request, body) => Task.FromResult(handle(request, body)));

    private static EndpointHandler Write<T>(Func<AgentRequest, Task<T>> read, Func<T, bool> dryRun, Func<AgentRequest, T, Task<(int, object)>> handle) =>
        new(true, async request =>
        {
            var body = await read(request);
            DriftGate.Check(request, dryRun(body));
            return await handle(request, body);
        });

    private static (int, object) Ok(object result) => (200, result);

    private static (int, object) Created(WriteResult result) => (result.Saved ? 201 : 200, result);

    private static AgentException Translate(Exception ex) => ex switch
    {
        AgentException agent => agent,
        JsonException json => AgentException.Usage($"The request body is not valid: {json.Message}", "Send a JSON object matching the endpoint's request type."),
        BadHttpRequestException bad => AgentException.Usage(bad.Message),
        ContentNotFoundException notFound => AgentException.NotFound(notFound.Message),
        ValidationException validation => AgentException.Invalid(ValidationErrors.From(validation)),
        EPiServer.Core.AccessDeniedException denied => AgentException.Refused(denied.Message),
        _ => new AgentException(AgentErrorCodes.Internal, ex.Message, ex.GetType().FullName),
    };

    private static async Task WriteAsync(HttpContext context, int status, AgentResponse<object> body)
    {
        if (context.Response.HasStarted)
        {
            return;
        }
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        await JsonSerializer.SerializeAsync(context.Response.Body, body, AgentJson.Options);
    }
}
