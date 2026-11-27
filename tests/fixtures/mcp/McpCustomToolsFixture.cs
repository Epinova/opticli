// Copied into the MCP test site by setup.sh, for the MCP module's end-to-end tests (tests/OptiCli.Mcp.Integration,
// CustomToolsTests): the site's own tools on the module's endpoint, as a site adds them with AddOptiCliMcp()
// .WithTools<T>() / .WithWriteTool<T>() / .WithActionTool<T>() / .WithInstructions(...) (Startup.cs, patched by setup.sh).
// Test-only and generic: they work on Alloy's pages and blocks, by property and type names both Alloy templates (CMS 12
// and 13) share. The action tools work on notes on pages, kept outside the CMS's content in App_Data/mcp-fixture-notes.json.
// Some load content with IContentLoader, which checks no access rights, on purpose: the module must keep content the
// editor can't read out of their results and plans all the same.
//
// Also a server of the site's own at /site-mcp (anonymous), which registers the read tools' class too: the module must
// keep its custom tools off every other MCP endpoint. And a clock a test can move forward for one request (FixtureClock),
// so a plan token's expiry can be tested without waiting for it.
#nullable enable
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using EPiServer;
using EPiServer.Core;
using EPiServer.Security;
using ModelContextProtocol.Server;
using OptiCli.Mcp;

namespace OptiCliMcpFixture;

/// <summary>What setup.sh's Startup patch registers.</summary>
public static class McpCustomToolsFixture
{
    public const string Instructions = "Test-only tools of the opticli MCP fixture start with fixture_; they work on Alloy's pages.";

    /// <summary>The site's clock: <see cref="FixtureClock"/>, before the module's own (the system clock) is added.</summary>
    public static IServiceCollection AddFixtureClock(this IServiceCollection services)
    {
        services.AddHttpContextAccessor();
        services.AddSingleton<TimeProvider>(sp => new FixtureClock(sp.GetRequiredService<IHttpContextAccessor>(), sp.GetRequiredService<IWebHostEnvironment>()));
        return services;
    }

    /// <summary>The module's custom tools.</summary>
    public static IOptiCliMcpBuilder WithFixtureTools(this IOptiCliMcpBuilder builder)
    {
        builder.Services.AddSingleton<FixtureNotes>();
        return builder.WithFixtureToolTypes();
    }

    private static IOptiCliMcpBuilder WithFixtureToolTypes(this IOptiCliMcpBuilder builder) => builder
        .WithTools<McpFixtureReadTools>()
        .WithWriteTool<FixtureSetTeaserTool>()
        .WithWriteTool<FixtureBuildSectionTool>()
        .WithWriteTool<FixtureCreateTool>()
        .WithWriteTool<FixtureMoveTool>()
        .WithWriteTool<FixtureTrashTool>()
        .WithActionTool<FixtureSaveNoteTool>()
        .WithActionTool<FixturePublishNoteTool>()
        .WithInstructions(Instructions);

    /// <summary>
    /// The site's own MCP server, at /site-mcp: its own tool, and the module's custom read tools' class registered again,
    /// which the module removes there.
    /// </summary>
    public static IServiceCollection AddFixtureSiteMcpServer(this IServiceCollection services)
    {
        services.AddMcpServer()
            .WithHttpTransport(t => t.Stateless = true)
            .WithTools<McpFixtureSiteTools>()
            .WithTools<McpFixtureReadTools>();
        return services;
    }

    /// <summary>Content references, comma-separated: ids, or ids with a version (<c>123_456</c>).</summary>
    internal static IEnumerable<ContentReference> References(string ids) =>
        ids.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(id => ContentReference.TryParse(id, out var link)
                ? link
                : throw new OptiCliToolException("usage", $"'{id}' isn't a content id.", "Give content ids, comma-separated."));
}

/// <summary>
/// The system clock, moved forward for one request that asks for it with <see cref="Header"/> (seconds, at most two
/// hours), in Development only: what a test of something that expires (a plan token) needs, without waiting.
/// </summary>
public sealed class FixtureClock(IHttpContextAccessor http, IWebHostEnvironment environment) : TimeProvider
{
    public const string Header = "X-Opticli-Fixture-Clock";

    public override DateTimeOffset GetUtcNow()
    {
        var now = TimeProvider.System.GetUtcNow();
        return environment.IsDevelopment()
            && http.HttpContext?.Request.Headers[Header] is { Count: 1 } value
            && int.TryParse(value[0], NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            && seconds <= 7200
                ? now.AddSeconds(seconds)
                : now;
    }
}

/// <summary>The site's own tool on its own MCP server (/site-mcp).</summary>
[McpServerToolType]
public sealed class McpFixtureSiteTools
{
    [McpServerTool(Name = "site_ping", ReadOnly = true)]
    [Description("The site's own MCP server answers.")]
    public static string Ping() => "pong";
}

/// <summary>Read tools of the site's, as a search over the site's own index would be: candidates found unchecked, then made safe.</summary>
public sealed class McpFixtureReadTools(IOptiCliEditor editor, IContentLoader loader)
{
    [McpServerTool(Name = "fixture_find_pages", Title = "Fixture: find pages", ReadOnly = true, OpenWorld = false)]
    [Description("Test-only: pages below a page whose name or teaser text contains the text.")]
    public IReadOnlyList<ContentDescription> FindPages(
        [Description("The text to look for, any case.")] string text,
        [Description("The id of the page to look below.")] int root)
    {
        // As an index finds them: without access checks.
        var candidates = loader.GetDescendents(new ContentReference(root))
            .Select(link => loader.TryGet<PageData>(link, out var page) ? page : null)
            .Where(page => page is not null && Matches(page, text))
            .Select(page => page!.ContentLink.ToReferenceWithoutVersion());
        return editor.Readable(candidates).Select(editor.Describe).ToList();
    }

    [McpServerTool(Name = "fixture_describe", Title = "Fixture: describe", ReadOnly = true, OpenWorld = false)]
    [Description("Test-only: content as IOptiCliEditor.Load and Describe give it.")]
    public ContentDescription Describe(
        [Description("A content id, or an id with a version (123_456).")] string reference,
        [Description("A language branch.")] string? lang = null,
        [Description("The newest version rather than the published one.")] bool latest = false) =>
        editor.Describe(editor.Load<IContent>(McpCustomToolsFixture.References(reference).Single(), lang, latest));

    [McpServerTool(Name = "fixture_describe_unchecked", Title = "Fixture: describe what the site loaded", ReadOnly = true, OpenWorld = false)]
    [Description("Test-only: content the site's code loaded without an access check, described by IOptiCliEditor.Describe.")]
    public ContentDescription DescribeUnchecked([Description("A content id.")] int id) =>
        editor.Describe(loader.Get<IContent>(new ContentReference(id)));

    [McpServerTool(Name = "fixture_readable", Title = "Fixture: readable", ReadOnly = true, OpenWorld = false)]
    [Description("Test-only: the references IOptiCliEditor.Readable keeps of the ones given, in order.")]
    public IReadOnlyList<string> Readable(
        [Description("Content ids, comma-separated.")] string ids,
        [Description("A language.")] string? lang = null) =>
        editor.Readable(McpCustomToolsFixture.References(ids), lang).Select(c => c.ContentLink.ToString()).ToList();

    private static bool Matches(PageData page, string text) =>
        page.Name.Contains(text, StringComparison.OrdinalIgnoreCase)
        || (page.Property["TeaserText"]?.Value as string)?.Contains(text, StringComparison.OrdinalIgnoreCase) == true;
}

public sealed record SetTeaserArgs(
    [property: Description("Page ids, comma-separated (an id with a version plans from that version).")] string Ids,
    [property: Description("The new teaser text.")] string Text,
    [property: Description("What to plan from: latest (editor.Load, latest), published (editor.Load), or loader (IContentLoader, unchecked).")] string From = "latest",
    [property: Description("The language branch; the master language when left out.")] string? Lang = null,
    [property: Description("Test-only: also add this content id to each page's MainContentArea (an area edit).")] int? AddToArea = null,
    [property: Description("Test-only: also set each page's MainContentArea to this content id alone (a ContentArea value).")] int? AreaItem = null,
    [property: Description("Test-only: also set each page's MainContentArea to one new EditorialBlock with this text (a {type, properties} item).")] string? NewBlock = null);

/// <summary>Update steps: the teaser text of pages, planned from the version <see cref="SetTeaserArgs.From"/> says.</summary>
[OptiCliWriteTool("fixture_set_teaser", Title = "Fixture: set teaser text", Kinds = ContentPlanKinds.Update)]
[Description("Test-only: sets the teaser text of the pages given.")]
public sealed class FixtureSetTeaserTool(IOptiCliEditor editor, IContentLoader loader) : IOptiCliWriteTool<SetTeaserArgs>
{
    public Task<ContentPlan> PlanAsync(SetTeaserArgs args, CancellationToken cancellationToken)
    {
        var plan = new ContentPlan();
        foreach (var link in McpCustomToolsFixture.References(args.Ids))
        {
            IContent page = args.From switch
            {
                "latest" => editor.Load<IContent>(link, args.Lang, latest: true),
                "published" => editor.Load<IContent>(link, args.Lang),
                "loader" => args.Lang is null ? loader.Get<IContent>(link) : loader.Get<IContent>(link, new LoaderOptions { LanguageLoaderOption.Specific(CultureInfo.GetCultureInfo(args.Lang)) }),
                _ => throw new OptiCliToolException("usage", $"from '{args.From}' isn't latest, published or loader."),
            };
            plan.Update(page, p =>
            {
                p.Set("TeaserText", args.Text);
                if (args.AreaItem is { } item)
                {
                    p.Set("MainContentArea", new[] { new { @ref = new ContentReference(item) } });
                }
                if (args.NewBlock is { } text)
                {
                    p.Set("MainContentArea", new[] { new { type = "EditorialBlock", name = text, properties = new { MainBody = $"<p>{text}</p>" } } });
                }
                if (args.AddToArea is { } added)
                {
                    p.AddToArea("MainContentArea", new ContentReference(added));
                }
            });
        }
        return Task.FromResult(plan);
    }
}

public sealed record BuildSectionArgs(
    [property: Description("The id of the page to build the section below (its newest version gets the new block in its MainContentArea).")] int Page,
    [property: Description("The new section page's name; its child, folder and block are named after it.")] string Name,
    [property: Description("The id of a page to move below the new section.")] int? MovePage = null,
    [property: Description("Test-only: give the child page a value for a property it doesn't have, so the run fails at that step.")] bool Fail = false,
    [property: Description("A CMS project (id or exact name) for the drafts the run saves; the new folder stays out of it.")] string? Project = null,
    [property: Description("With project: mark the drafts Ready to publish.")] bool Ready = false);

/// <summary>
/// Create steps with handles, a ContentArea value and an area edit with planned content, a "For this page" folder, and a
/// move to planned content.
/// </summary>
[OptiCliWriteTool("fixture_build_section", Title = "Fixture: build a section", Kinds = ContentPlanKinds.Create | ContentPlanKinds.Update | ContentPlanKinds.Move)]
[Description("Test-only: below a page, makes a section page with a child page, a block in the page's own folder that both show, and optionally moves a page below the section.")]
public sealed class FixtureBuildSectionTool(IOptiCliEditor editor) : IOptiCliWriteTool<BuildSectionArgs>
{
    public Task<ContentPlan> PlanAsync(BuildSectionArgs args, CancellationToken cancellationToken)
    {
        var page = editor.Load<PageData>(new ContentReference(args.Page), latest: true);
        var plan = new ContentPlan();
        var section = plan.Create(page, "StandardPage", args.Name, v => v.Set("TeaserText", "Built by fixture_build_section."));
        var folder = plan.CreateFor(page, "SysContentFolder", args.Name + " blocks");
        var block = plan.Create(folder, "EditorialBlock", args.Name + " block", v => v.Set("MainBody", "<p>Built by fixture_build_section.</p>"));
        plan.Update(page, p => p.AddToArea("MainContentArea", block));
        plan.Create(section, "StandardPage", args.Name + " child", v =>
        {
            v.Set("MainContentArea", new[] { new { @ref = block } });
            if (args.Fail)
            {
                v.Set("NoSuchProperty", block);
            }
        });
        if (args.MovePage is { } move)
        {
            plan.Move(editor.Load<IContent>(new ContentReference(move), latest: true), section);
        }
        if (!string.IsNullOrWhiteSpace(args.Project))
        {
            plan.InProject(args.Project, args.Ready);
        }
        return Task.FromResult(plan);
    }
}

public sealed record CreateArgs(
    [property: Description("The id of the content to create below, or whose \"For this page\" folder to create in.")] int Parent,
    [property: Description("The new items' name; a number is added to each.")] string Name,
    [property: Description("Blocks in the content's \"For this page\" folder rather than pages below it.")] bool ForContent = false,
    [property: Description("How many items.")] int Count = 1);

/// <summary>Create steps only: pages below a page, or blocks in its "For this page" folder.</summary>
[OptiCliWriteTool("fixture_create", Title = "Fixture: create", Kinds = ContentPlanKinds.Create)]
[Description("Test-only: creates pages below a page, or blocks in its \"For this page\" folder.")]
public sealed class FixtureCreateTool(IOptiCliEditor editor) : IOptiCliWriteTool<CreateArgs>
{
    public Task<ContentPlan> PlanAsync(CreateArgs args, CancellationToken cancellationToken)
    {
        var parent = editor.Load<IContent>(new ContentReference(args.Parent), latest: true);
        var plan = new ContentPlan();
        for (var i = 1; i <= args.Count; i++)
        {
            var name = $"{args.Name} {i}";
            _ = args.ForContent
                ? plan.CreateFor(parent, "EditorialBlock", name)
                : plan.Create(parent, "StandardPage", name);
        }
        return Task.FromResult(plan);
    }
}

public sealed record MoveArgs(
    [property: Description("Content ids, comma-separated.")] string Ids,
    [property: Description("The id of the content to move them below.")] int Destination,
    [property: Description("Test-only: load the content with IContentLoader, without an access check.")] bool Unchecked = false);

/// <summary>Move steps.</summary>
[OptiCliWriteTool("fixture_move", Title = "Fixture: move", Kinds = ContentPlanKinds.Move)]
[Description("Test-only: moves the content given below another item.")]
public sealed class FixtureMoveTool(IOptiCliEditor editor, IContentLoader loader) : IOptiCliWriteTool<MoveArgs>
{
    public Task<ContentPlan> PlanAsync(MoveArgs args, CancellationToken cancellationToken)
    {
        IContent Load(ContentReference link) => args.Unchecked ? loader.Get<IContent>(link) : editor.Load<IContent>(link, latest: true);
        var plan = new ContentPlan();
        var destination = Load(new ContentReference(args.Destination));
        foreach (var link in McpCustomToolsFixture.References(args.Ids))
        {
            plan.Move(Load(link), destination);
        }
        return Task.FromResult(plan);
    }
}

public sealed record TrashArgs(
    [property: Description("Content ids, comma-separated.")] string Ids,
    [property: Description("Test-only: load the content with IContentLoader, without an access check.")] bool Unchecked = false);

/// <summary>Trash steps: offered only where the site allows deleting.</summary>
[OptiCliWriteTool("fixture_trash", Title = "Fixture: trash", Kinds = ContentPlanKinds.Trash)]
[Description("Test-only: moves the content given to the recycle bin.")]
public sealed class FixtureTrashTool(IOptiCliEditor editor, IContentLoader loader) : IOptiCliWriteTool<TrashArgs>
{
    public Task<ContentPlan> PlanAsync(TrashArgs args, CancellationToken cancellationToken)
    {
        var plan = new ContentPlan();
        foreach (var link in McpCustomToolsFixture.References(args.Ids))
        {
            plan.Trash(args.Unchecked ? loader.Get<IContent>(link) : editor.Load<IContent>(link));
        }
        return Task.FromResult(plan);
    }
}

/// <summary>
/// Notes on pages, kept outside the CMS's content as a site's external system would keep its own things: a draft and a
/// published text per page, and a version that goes up on every change. In App_Data/mcp-fixture-notes.json, so they
/// survive a restart; one lock for the file.
/// </summary>
public sealed class FixtureNotes(IWebHostEnvironment environment)
{
    public sealed record Note(string? Draft, string? Published, int Version);

    private readonly object _lock = new();

    private string File => Path.Combine(environment.ContentRootPath, "App_Data", "mcp-fixture-notes.json");

    public Note Get(int page)
    {
        lock (_lock)
        {
            return Read().GetValueOrDefault(page) ?? new Note(null, null, 0);
        }
    }

    public Note Set(int page, Func<Note, Note> change)
    {
        lock (_lock)
        {
            var notes = Read();
            var note = change(notes.GetValueOrDefault(page) ?? new Note(null, null, 0));
            notes[page] = note with { Version = note.Version + 1 };
            System.IO.File.WriteAllText(File, JsonSerializer.Serialize(notes));
            return notes[page];
        }
    }

    private Dictionary<int, Note> Read() =>
        System.IO.File.Exists(File) ? JsonSerializer.Deserialize<Dictionary<int, Note>>(System.IO.File.ReadAllText(File)) ?? [] : [];

    /// <summary>
    /// The page a note belongs to, for the editor, with the access <paramref name="level"/> to change its note:
    /// <c>not_found</c> for a page they can't read (as for one that doesn't exist), <c>refused</c> without that access.
    /// </summary>
    internal static IContent Page(IOptiCliEditor editor, int page, AccessLevel level)
    {
        var content = editor.Load<IContent>(new ContentReference(page));
        return editor.HasAccess(content, level)
            ? content
            : throw new OptiCliToolException("refused", $"You don't have {level} access to page {page}, so you can't change its note.", "Ask someone who has that access on the page.");
    }
}

public sealed record SaveNoteArgs(
    [property: Description("The id of the page the note is on.")] int Page,
    [property: Description("The note's new draft text, at most 200 characters.")] string Text);

/// <summary>An action tool that saves a draft outside the CMS: needs Edit access on the page.</summary>
[OptiCliActionTool("fixture_save_note", Title = "Fixture: save note", Effects = ActionEffects.Change)]
[Description("Test-only: saves the draft of a page's note, kept outside the CMS's content.")]
public sealed class FixtureSaveNoteTool(IOptiCliEditor editor, FixtureNotes notes) : IOptiCliActionTool<SaveNoteArgs>
{
    public Task<ActionPreview> PreviewAsync(SaveNoteArgs args, CancellationToken cancellationToken)
    {
        var page = FixtureNotes.Page(editor, args.Page, AccessLevel.Edit);
        var note = notes.Get(args.Page);
        var preview = new ActionPreview();
        var target = preview.Add("note", args.Page.ToString(CultureInfo.InvariantCulture), page.Name, ActionEffects.Change)
            .Version(note.Version.ToString(CultureInfo.InvariantCulture))
            .Links(editUrl: editor.Describe(page).EditUrl);
        if (note.Draft != args.Text)
        {
            target.Change("draft", note.Draft, args.Text);
        }
        if (args.Text.Length > 200)
        {
            target.Problem($"A note has at most 200 characters; this one has {args.Text.Length}.", "draft");
        }
        return Task.FromResult(preview);
    }

    public Task RunAsync(SaveNoteArgs args, ActionPreview preview, ActionRun run, CancellationToken cancellationToken)
    {
        notes.Set(args.Page, n => n with { Draft = args.Text });
        run.Done(preview.Targets[0], "saved");
        return Task.CompletedTask;
    }
}

public sealed record PublishNoteArgs(
    [property: Description("The ids of the pages whose notes to publish (or discard), comma-separated.")] string Pages,
    [property: Description("Discard the drafts rather than publish them.")] bool Discard = false);

/// <summary>
/// An action tool that changes what visitors see or deletes: publishes the drafts of pages' notes (Publish access), or
/// discards them (Edit access). A draft whose text is "fail on publish" makes its publish fail, after the notes before it.
/// </summary>
[OptiCliActionTool("fixture_publish_note", Title = "Fixture: publish note", Effects = ActionEffects.Live | ActionEffects.Delete)]
[Description("Test-only: publishes the drafts of pages' notes, or discards them.")]
public sealed class FixturePublishNoteTool(IOptiCliEditor editor, FixtureNotes notes) : IOptiCliActionTool<PublishNoteArgs>
{
    public const string FailOnPublish = "fail on publish";

    public Task<ActionPreview> PreviewAsync(PublishNoteArgs args, CancellationToken cancellationToken)
    {
        var preview = new ActionPreview();
        foreach (var id in args.Pages.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var number = int.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var n)
                ? n
                : throw new OptiCliToolException("usage", $"'{id}' isn't a page id.", "Give page ids, comma-separated.");
            var page = FixtureNotes.Page(editor, number, args.Discard ? AccessLevel.Edit : AccessLevel.Publish);
            var note = notes.Get(number);
            var target = preview.Add("note", id, page.Name, args.Discard ? ActionEffects.Delete : ActionEffects.Live)
                .Version(note.Version.ToString(CultureInfo.InvariantCulture))
                .Links(editUrl: editor.Describe(page).EditUrl);
            if (note.Draft is null)
            {
                target.Problem("The note has no draft.");
            }
            else if (args.Discard)
            {
                target.Change("draft", note.Draft, null);
            }
            else
            {
                target.Change("published", note.Published, note.Draft);
            }
        }
        if (!args.Discard)
        {
            preview.Note("Visitors see a published note under the page's heading.");
        }
        return Task.FromResult(preview);
    }

    public Task RunAsync(PublishNoteArgs args, ActionPreview preview, ActionRun run, CancellationToken cancellationToken)
    {
        foreach (var target in preview.Targets)
        {
            var page = int.Parse(target.Id!, CultureInfo.InvariantCulture);
            if (args.Discard)
            {
                notes.Set(page, n => n with { Draft = null });
                run.Done(target, "discarded");
                continue;
            }
            if (notes.Get(page).Draft == FailOnPublish)
            {
                throw new OptiCliToolException("refused", "The note service refused the note.", "Change the note's text.");
            }
            notes.Set(page, n => n with { Published = n.Draft, Draft = null });
            run.Done(target, "published");
        }
        return Task.CompletedTask;
    }
}
