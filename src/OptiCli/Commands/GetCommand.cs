using System.CommandLine;
using OptiCli.Cli;
using OptiCli.Core.Content;
using OptiCli.Core.Errors;
using OptiCli.Core.Properties;

namespace OptiCli.Commands;

internal static class GetCommand
{
    public static Command Create(GlobalOptions options)
    {
        var content = new ContentOptions();
        var version = new Option<string?>("--version")
        {
            Description = "published (default: the primary version, which is the latest draft if never published), latest, or a version id. A 123_456 ref also selects a version.",
            HelpName = "published|latest|id",
        };
        var fields = new Option<string?>("--fields") { Description = "Only these properties (comma-separated), never truncated. Identity fields (name, status, saved, ...) are always shown, so --fields name gives just those.", HelpName = "a,b" };
        var full = new Option<bool>("--full") { Description = $"Do not cut strings longer than {TextValues.MaxLength} characters." };
        var all = new Option<bool>("--all-properties") { Description = "Include properties without a value (as null)." };
        var expand = new Option<bool>("--expand") { Description = "Inline the properties of ContentArea items and referenced content (one level)." };
        var variation = new Option<string?>("--variation")
        {
            Description = "CMS 13: show a content variation (its key, as versions lists it) instead of the content itself: its published version, or with --version latest its newest. Its unchanged properties are the published version's, as the CMS loads it.",
            HelpName = "key",
        };

        var command = new Command("get", $$"""
            Show one content item with every property decoded: identity, version, status, URL and typed values.
            Culture-specific values come from the requested branch, shared ones from the master branch; each top-level property
            says which ("culture"). ContentArea items, references and rich-text links resolve to {ref, guid, type, name, language,
            status, url}; local and inline blocks are nested objects. Empty properties are omitted (--all-properties shows them);
            strings over {{TextValues.MaxLength}} characters are cut (truncated: true, length: N) unless --full or --fields.
            CMS 13: a version of a content variation shows variation (versions lists them; --variation <key> picks one); a Visual
            Builder experience (or section) shows its composition instead of the Layout and UnstructuredData
            properties it is stored in: sections → rows → columns → elements, each with its key, name, type, displayTemplate and
            displaySettings, an inline one with its properties, a shared one as content {ref, ...}; --fields composition picks it.
            Example: opticli get /en/about/ --fields Heading,MainArea
            """);
        content.AddTo(command);
        command.Options.Add(version);
        command.Options.Add(fields);
        command.Options.Add(full);
        command.Options.Add(all);
        command.Options.Add(expand);
        command.Options.Add(variation);

        CommandRunner.SetHandler(command, options, async (context, cancellationToken) =>
        {
            await using var session = await context.OpenContentAsync(cancellationToken);
            var located = await content.LocateAsync(context, session, cancellationToken);
            var selector = VersionSelector.Parse(context.Parse.GetValue(version));
            if (located.VersionId is { } versionId)
            {
                if (selector.Kind != VersionKind.Published || context.Parse.GetValue(variation) is not null)
                {
                    throw new UsageException("Pass either a 123_456 ref or --version/--variation, not both.");
                }
                selector = new VersionSelector(VersionKind.Specific, versionId);
            }
            if (context.Parse.GetValue(variation) is { } key)
            {
                if (selector.Kind == VersionKind.Specific)
                {
                    throw new UsageException("--variation picks the variation's published (or, with --version latest, newest) version: pass it without a version id.");
                }
                if (!session.Model.Schema.Variations)
                {
                    throw new UsageException("Content variations are CMS 13's; this site's database has none.");
                }
                selector = selector with { Variation = key.Trim() };
            }

            var fieldNames = context.Parse.GetValue(fields)?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var decode = new DecodeOptions(
                context.Parse.GetValue(full),
                context.Parse.GetValue(all),
                fieldNames is { Length: > 0 } ? new HashSet<string>(fieldNames, StringComparer.OrdinalIgnoreCase) : null,
                context.Parse.GetValue(expand),
                Composition: true);

            var loader = new ContentLoader(session.Db, session.Identities);
            return new CommandResult(await loader.GetAsync(located.Id, selector, content.Language(context, session, located), decode, cancellationToken));
        });
        return command;
    }
}
