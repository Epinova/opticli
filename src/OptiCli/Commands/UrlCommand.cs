using System.CommandLine;
using OptiCli.Cli;
using OptiCli.Core.Cms;
using OptiCli.Core.Content;

namespace OptiCli.Commands;

internal static class UrlCommand
{
    private sealed record Urls(string Ref, Guid Guid, string Type, IReadOnlyList<LanguageUrl> Languages, string? Note);

    /// <param name="Path">As served on <paramref name="Url"/>'s host (no language prefix when the host is mapped to the language).</param>
    private sealed record LanguageUrl(string? Language, string? Name, string Status, string? Path, string? Url, string? Site);

    public static Command Create(GlobalOptions options)
    {
        var content = new ContentOptions();
        var command = new Command("url", """
            Show the URL of a page or media item in each of its languages (or only --lang).
            Gives the site-relative path and the absolute URL on the site's host for that language. Blocks and folders have no URL.
            Example: opticli url 123
            """);
        content.AddTo(command);

        CommandRunner.SetHandler(command, options, async (context, cancellationToken) =>
        {
            await using var session = await context.OpenContentAsync(cancellationToken);
            var located = await content.LocateAsync(context, session, cancellationToken);
            var header = await session.HeaderAsync(located.Id, cancellationToken);
            var only = session.Language(context.Parse.GetValue(content.Lang));

            var languages = new List<LanguageUrl>();
            foreach (var row in header.Languages.Values.OrderBy(r => r.LanguageId == header.MasterLanguageId ? 0 : 1).ThenBy(r => r.LanguageId))
            {
                var language = session.Model.Language(row.LanguageId);
                if (only is not null && language?.Id != only.Id)
                {
                    continue;
                }
                var url = session.Identities.Urls.UrlOf(header, language is { IsInvariant: false } ? language : null);
                languages.Add(new LanguageUrl(language?.DisplayCode, row.Name, VersionStatuses.Name(row.Status), url?.Path, url?.Absolute, url?.Site));
            }

            var kind = session.Model.Kind(header.TypeId);
            var note = languages.All(l => l.Path is null)
                ? header.Deleted ? "The item is in the recycle bin."
                    : header.Blueprint ? "A blueprint is a template new content is made from (CMS 13's Visual Builder), not content visitors see: it has no URL."
                    : $"{(kind is ContentKind.Experience or ContentKind.Element ? "An" : "A")} {kind.Name()} has no URL of its own; only pages{(session.Model.Schema.Compositions ? ", experiences" : "")} and media do."
                : null;
            return new CommandResult(new Urls(ContentIdentity.RefFor(header.Id), header.Guid, session.Model.TypeName(header.TypeId), languages, note));
        });
        return command;
    }
}
