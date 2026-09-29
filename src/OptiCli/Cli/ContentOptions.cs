using System.CommandLine;
using OptiCli.Core.Content;
using OptiCli.Core.Refs;

namespace OptiCli.Cli;

/// <summary>The <c>&lt;ref&gt;</c> argument plus <c>--lang</c> and <c>--site</c>, shared by content commands.</summary>
internal sealed class ContentOptions
{
    public Argument<string> Ref { get; } = new("ref") { Description = ContentRefParser.Syntax };

    public Option<string?> Lang { get; } = new("--lang")
    {
        Description = "Language branch (code, e.g. en). Default: the item's master language, or the language a URL ref selects.",
        HelpName = "code",
    };

    public Option<string?> Site { get; } = new("--site")
    {
        Description = "Site (name or host) that a path ref like /en/about/ belongs to. Default: the only site, or the one with the '*' host.",
        HelpName = "name|host",
    };

    public void AddTo(Command command, bool withRef = true, bool withLang = true, bool withSite = true)
    {
        if (withRef)
        {
            command.Arguments.Add(Ref);
        }
        if (withLang)
        {
            command.Options.Add(Lang);
        }
        if (withSite)
        {
            command.Options.Add(Site);
        }
    }

    public Task<LocatedContent> LocateAsync(CliContext context, ContentSession session, CancellationToken cancellationToken) =>
        session.LocateAsync(context.Parse.GetValue(Ref)!, context.Parse.GetValue(Site), cancellationToken);

    /// <summary><c>--lang</c> if given, else the language a URL ref selected, else null (master).</summary>
    public LanguageBranch? Language(CliContext context, ContentSession session, LocatedContent? located = null) =>
        session.Language(context.Parse.GetValue(Lang)) ?? located?.Url?.Language;
}
