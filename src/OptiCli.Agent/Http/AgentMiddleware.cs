using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using EPiServer.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using OptiCli.Agent.Content;
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

    private static async Task<(int Status, object Data)> DispatchAsync(AgentRequest request, AgentEndpoint endpoint) => endpoint switch
    {
        AgentEndpoint.Ping => (200, PingEndpoint.Handle(request)),
        AgentEndpoint.Type => (200, TypeEndpoint.Handle(request)),
        AgentEndpoint.Create => Created(CreateEndpoint.Handle(request, await request.ReadBodyAsync<CreateRequest>())),
        AgentEndpoint.Draft => (200, DraftEndpoint.Handle(request, await request.ReadBodyAsync<DraftRequest>())),
        AgentEndpoint.Languages => Created(LanguageEndpoint.Handle(request, await request.ReadBodyAsync<LanguageBranchRequest>())),
        AgentEndpoint.Publish => (200, PublishEndpoint.Handle(request, await request.ReadOptionalBodyAsync<PublishRequest>() ?? new PublishRequest())),
        AgentEndpoint.Move => (200, MoveEndpoint.Move(request, await request.ReadBodyAsync<MoveRequest>())),
        AgentEndpoint.Access => (200, await AccessEndpoint.HandleAsync(request, await request.ReadBodyAsync<AccessRequest>())),
        AgentEndpoint.Delete => (200, MoveEndpoint.Delete(request)),
        AgentEndpoint.Read => (200, ReadEndpoint.Handle(request)),
        _ => throw new InvalidOperationException($"Unhandled endpoint {endpoint}."),
    };

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
