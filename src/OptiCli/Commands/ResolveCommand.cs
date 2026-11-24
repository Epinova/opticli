using System.CommandLine;
using OptiCli.Cli;
using OptiCli.Core.Urls;

namespace OptiCli.Commands;

internal static class ResolveCommand
{
    /// <param name="LanguageSource">How the language was chosen: path prefix, host mapping, site default or simple address.</param>
    private sealed record ResolveOutput(
        string Ref, Guid Guid, string Type, string? Name, string? Language, Core.Content.VersionStatus Status, string? Url,
        string? Site, string? Host, string? LanguageSource, string MatchedBy);

    public static Command Create(GlobalOptions options)
    {
        var url = new Argument<string>("url") { Description = "Full URL (https://host/en/about/) or site-relative path (/en/about/)." };
        var content = new ContentOptions();
        var command = new Command("resolve", """
            Find the content item a URL shows, and in which language.
            The host picks the site (a bare path uses --site, else the '*' site), a leading language segment picks the language
            (else the host's language, else the site's master), then the segments are walked from the start page. Media paths
            (/globalassets/, /siteassets/, /contentassets/) work too. Every <ref> argument also accepts a URL directly.
            On an Optimizely Commerce site, a catalog URL (which the site's catalog routing makes, not the database) is
            asked of the running site: matchedBy is catalog, and ref the catalog ref (1__CatalogContent).
            Example: opticli resolve https://www.example.com/en/about/team/
            """);
        command.Arguments.Add(url);
        content.AddTo(command, withRef: false, withLang: false);

        CommandRunner.SetHandler<ResolveOutput>(command, options, async (context, cancellationToken) =>
        {
            await using var session = await context.OpenContentAsync(cancellationToken);
            ResolvedUrl resolved;
            try
            {
                resolved = await new UrlResolver(session.Db, session.Model)
                    .ResolveAsync(context.Parse.GetValue(url)!, session.Site(context.Parse.GetValue(content.Site)), cancellationToken);
            }
            catch (Core.Errors.NotFoundException)
            {
                // Optimizely Commerce: a URL the site's catalog routing makes, which the CMS database knows nothing of.
                if (await CatalogAsync(context, context.Parse.GetValue(url)!, cancellationToken) is { } catalog)
                {
                    return new CommandResult<ResolveOutput>(catalog, Source: Core.Writes.WriteExecutor.AgentSource);
                }
                throw;
            }
            var header = await session.HeaderAsync(resolved.ContentId, cancellationToken);
            var identity = session.Identities.Describe(header, resolved.Language);
            return new CommandResult<ResolveOutput>(new ResolveOutput(
                identity.Ref!, identity.Guid, identity.Type!, identity.Name, identity.Language, identity.Status!.Value, identity.Url,
                resolved.Site?.Name, resolved.Host, resolved.LanguageSource, resolved.MatchedBy));
        });
        return command;
    }

    /// <summary>
    /// The catalog content a URL shows, asked of the running site's Commerce agent; null on a project without Commerce,
    /// with no site running, or where the site routes the URL to nothing (the database's not-found stands then).
    /// </summary>
    private static async Task<ResolveOutput?> CatalogAsync(CliContext context, string url, CancellationToken cancellationToken)
    {
        if (context.TryGetProject(out _) is not { } project || Core.Discovery.PackageVersions.FindCommerce(project.Project) is null)
        {
            return null;
        }
        try
        {
            var agent = await context.ConnectAgentAsync(cancellationToken);
            var found = await agent.SendAsync<Protocol.CatalogUrl>(HttpMethod.Get, $"{Protocol.AgentRoutes.CommerceResolve}?url={Uri.EscapeDataString(url)}", null, cancellationToken);
            var content = found.Content;
            var reference = Protocol.CatalogRefSyntax.Format(content.Id);
            var status = Enum.TryParse<Core.Content.VersionStatus>(content.Status, ignoreCase: true, out var parsed) ? parsed : Core.Content.VersionStatus.Published;
            return new ResolveOutput(reference, content.Guid, content.Type ?? "", content.Name, content.Language, status, found.Url,
                null, null, null, CatalogMatch);
        }
        catch (Core.Errors.OptiCliException)
        {
            return null;
        }
    }

    /// <summary><c>matchedBy</c> of a URL the site's catalog routing (Optimizely Commerce) made.</summary>
    internal const string CatalogMatch = "catalog";
}
