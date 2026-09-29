using System.CommandLine;
using OptiCli.Cli;
using OptiCli.Core.Urls;

namespace OptiCli.Commands;

internal static class ResolveCommand
{
    /// <param name="LanguageSource">How the language was chosen: path prefix, host mapping, site default or simple address.</param>
    private sealed record Resolved(
        string? Ref, Guid Guid, string? Type, string? Name, string? Language, string? Status, string? Url,
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
            Example: opticli resolve https://www.example.com/en/about/team/
            """);
        command.Arguments.Add(url);
        content.AddTo(command, withRef: false, withLang: false);

        CommandRunner.SetHandler(command, options, async (context, cancellationToken) =>
        {
            await using var session = await context.OpenContentAsync(cancellationToken);
            var resolved = await new UrlResolver(session.Db, session.Model)
                .ResolveAsync(context.Parse.GetValue(url)!, session.Site(context.Parse.GetValue(content.Site)), cancellationToken);
            var header = await session.HeaderAsync(resolved.ContentId, cancellationToken);
            var identity = session.Identities.Describe(header, resolved.Language);
            return new CommandResult(new Resolved(
                identity.Ref, identity.Guid, identity.Type, identity.Name, identity.Language, identity.Status, identity.Url,
                resolved.Site?.Name, resolved.Host, resolved.LanguageSource, resolved.MatchedBy));
        });
        return command;
    }
}
