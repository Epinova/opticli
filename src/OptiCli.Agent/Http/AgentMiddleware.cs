using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using EPiServer.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using OptiCli.Agent.Drift;
using OptiCli.Agent.Endpoints;
using OptiCli.Agent.Hosting;
using OptiCli.Agent.Jobs;
using OptiCli.Agent.Orphans;
using OptiCli.Agent.Safety;
using OptiCli.Agent.Sites;
using OptiCli.Agent.Users;
using OptiCli.Cms;
using OptiCli.Cms.Content;
using OptiCli.Cms.Operations;
using OptiCli.Protocol;

namespace OptiCli.Agent.Http;

/// <summary>
/// The whole agent: a terminal middleware branch at <see cref="AgentProtocol.BasePath"/> that guards,
/// routes, runs the endpoint as the opticli principal, and writes the envelope.
/// </summary>
/// <remarks>
/// The content endpoints are <c>OptiCli.Cms</c> operations, which the MCP module runs too; this class adds only what is
/// HTTP: the body, the status code, and the request's <see cref="AgentRequest.Call"/> as the developer. Site hosts
/// (<see cref="SiteHostsOperation"/>), scheduled jobs (<see cref="JobsOperation"/>), users (<see cref="UsersOperation"/>) and orphaned content types
/// (<see cref="OrphanRemovalOperation"/>) are the agent's own, never the module's.
/// </remarks>
internal static class AgentMiddleware
{
    private static readonly AgentMeta Meta = new("agent", AgentInfo.Version, AgentProtocol.Version);

    /// <summary>An upload's body limit: base64 grows the file by a third; the rest of the JSON is small.</summary>
    internal const int UploadBodyBytes = UploadRequest.MaxBytes / 3 * 4 + 1024 * 1024;

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
        [AgentEndpoint.Type] = Read(request => TypeOperation.Run(request.Call, request.Argument)),
        [AgentEndpoint.TypesWithoutCode] = Read(request => TypesWithoutCodeEndpoint.Handle(request)),
        [AgentEndpoint.Read] = Read(request => ReadEndpoint.Handle(request)),
        [AgentEndpoint.RestoreParents] = Read(request => RestoreParentsEndpoint.Handle(request)),
        [AgentEndpoint.Create] = Write(r => r.ReadBodyAsync<CreateRequest>(), b => b.DryRun, (r, b) => Created(CreateOperation.Run(r.Call, b))),
        [AgentEndpoint.Upload] = Write(r => r.ReadBodyAsync<UploadRequest>(UploadBodyBytes), b => b.DryRun, (r, b) => Created(UploadOperation.Run(r.Call, b))),
        [AgentEndpoint.Draft] = Write(r => r.ReadBodyAsync<DraftRequest>(), b => b.DryRun, (r, b) => Ok(DraftOperation.Run(r.Call, r.Argument, b))),
        [AgentEndpoint.Languages] = Write(r => r.ReadBodyAsync<LanguageBranchRequest>(), b => b.DryRun, (r, b) => Created(LanguagesOperation.Run(r.Call, r.Argument, b))),
        [AgentEndpoint.Publish] = Write(async r => await r.ReadOptionalBodyAsync<PublishRequest>() ?? new PublishRequest(), _ => false, (r, b) => Ok(PublishOperation.Run(r.Call, r.Argument, b))),
        [AgentEndpoint.RemoveLanguage] = Write(r => r.ReadBodyAsync<RemoveLanguageRequest>(), b => b.DryRun, (r, b) => Ok(RemoveLanguageOperation.Run(r.Call, r.Argument, b))),
        [AgentEndpoint.Unpublish] = Write(async r => await r.ReadOptionalBodyAsync<UnpublishRequest>() ?? new UnpublishRequest(), b => b.DryRun, (r, b) => Ok(UnpublishOperation.Run(r.Call, r.Argument, b))),
        [AgentEndpoint.Discard] = Write(async r => await r.ReadOptionalBodyAsync<DiscardRequest>() ?? new DiscardRequest(), b => b.DryRun, (r, b) => Ok(DiscardOperation.Run(r.Call, r.Argument, b))),
        [AgentEndpoint.Move] = Write(r => r.ReadBodyAsync<MoveRequest>(), b => b.DryRun, (r, b) => Ok(MoveOperation.Run(r.Call, r.Argument, b))),
        [AgentEndpoint.Restore] = Write(r => r.ReadBodyAsync<RestoreRequest>(), b => b.DryRun, (r, b) => Ok(RestoreOperation.Run(r.Call, r.Argument, b))),
        [AgentEndpoint.Access] = Write(r => r.ReadBodyAsync<AccessRequest>(), b => b.DryRun, async (r, b) => Ok(await AccessOperation.RunAsync(r.Call, r.Argument, b))),
        // Not a content operation: site definitions are the developer's only, so this one isn't in OptiCli.Cms.
        [AgentEndpoint.SiteHosts] = Write(r => r.ReadBodyAsync<SiteHostsRequest>(), b => b.DryRun, async (r, b) => Ok(await SiteHostsOperation.RunAsync(r, b))),
        // Scheduled jobs are the developer's only too. A job writes whatever its code writes, so a run is a write.
        [AgentEndpoint.JobRun] = Write(r => r.ReadBodyAsync<JobRunRequest>(), b => b.DryRun, (r, b) => Ok(JobsOperation.Run(r, b))),
        [AgentEndpoint.JobStop] = Write(r => r.ReadBodyAsync<JobStopRequest>(), b => b.DryRun, (r, b) => Ok(JobsOperation.Stop(r, b))),
        [AgentEndpoint.JobSet] = Write(r => r.ReadBodyAsync<JobSetRequest>(), b => b.DryRun, (r, b) => Ok(JobsOperation.Set(r, b))),
        // Users are the developer's only too: a local login for a restored database.
        [AgentEndpoint.UserAdd] = Write(r => r.ReadBodyAsync<UserAddRequest>(), b => b.DryRun, async (r, b) => Ok(await UsersOperation.AddAsync(r, b))),
        [AgentEndpoint.UserRemove] = Write(r => r.ReadBodyAsync<UserRemoveRequest>(), b => b.DryRun, async (r, b) => Ok(await UsersOperation.RemoveAsync(r, b))),
        [AgentEndpoint.UserRoles] = new(false, async request => (200, (object)await UsersOperation.RolesAsync(request))),
        // The content model is the developer's only too: content types and properties that removed code left behind.
        [AgentEndpoint.TypesRemove] = Write(r => r.ReadBodyAsync<OrphanRemovalRequest>(), b => b.DryRun, (r, b) => Ok(OrphanRemovalOperation.Run(r, b))),
        // No body and no dry run: a delete is always real.
        [AgentEndpoint.Delete] = Write(Task.FromResult, _ => false, (r, _) => Ok(DeleteOperation.Run(r.Call, r.Argument))),
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
