using System.Text.Json;
using OptiCli.Cms;
using OptiCli.Cms.Content;
using OptiCli.Protocol;

namespace OptiCli.Agent.Tests.Content;

public class PendingDraftsTests
{
    private static readonly DateTime Monday = new(2025, 1, 27, 9, 0, 0, DateTimeKind.Utc);

    /// <summary>The agent's saves are attributed to the opticli principal.</summary>
    private const string Agent = AgentProtocol.PrincipalName;

    private static VersionStamp Version(int id, string? savedBy, bool published = false) => new(id, published, Monday.AddHours(id), savedBy);

    private static PendingDraft Draft(string? savedBy = "editor@example.com") => new("123_12", savedBy, Monday,
        [new PropertyChange("Heading", JsonSerializer.SerializeToElement("Old"), JsonSerializer.SerializeToElement("New"))]);

    [Fact]
    public void A_draft_by_another_user_after_the_published_version_is_pending()
    {
        VersionStamp[] versions = [Version(10, "editor@example.com", published: true), Version(11, "editor@example.com"), Version(12, "opticli")];

        // opticli's own draft on top of it still carries the editor's change.
        Assert.Equal(11, PendingDrafts.NewestByOthers(versions, baseVersion: 12, Agent)?.Id);
        Assert.Equal(11, PendingDrafts.NewestByOthers(versions, baseVersion: 11, Agent)?.Id);
    }

    [Fact]
    public void Drafts_saved_by_opticli_are_not()
    {
        VersionStamp[] versions = [Version(10, "editor@example.com", published: true), Version(11, "opticli"), Version(12, "OptiCli ")];

        Assert.Null(PendingDrafts.NewestByOthers(versions, baseVersion: 12, Agent));
    }

    [Fact]
    public void Versions_before_the_published_one_or_after_the_base_do_not_count()
    {
        VersionStamp[] versions = [Version(8, "editor@example.com"), Version(10, "editor@example.com", published: true), Version(11, "opticli"), Version(13, "editor@example.com")];

        Assert.Null(PendingDrafts.NewestByOthers(versions, baseVersion: 11, Agent));
        // A change based on the published version itself publishes nothing else.
        Assert.Null(PendingDrafts.NewestByOthers(versions, baseVersion: 10, Agent));
        Assert.Equal(13, PendingDrafts.NewestByOthers(versions, baseVersion: 13, Agent)?.Id);
    }

    [Fact]
    public void Every_draft_by_others_up_to_the_base_is_a_candidate_newest_first()
    {
        VersionStamp[] versions = [Version(10, "opticli", published: true), Version(11, "editor@example.com"), Version(12, "opticli"), Version(13, ""), Version(14, "editor@example.com")];

        Assert.Equal([13, 11], PendingDrafts.ByOthers(versions, baseVersion: 13, Agent).Select(v => v.Id));
        Assert.Empty(PendingDrafts.ByOthers(versions, baseVersion: 10, Agent));
    }

    [Fact]
    public void Whoever_marked_a_project_item_ready_consented_to_their_own_drafts_in_it_not_to_another_persons()
    {
        // 11 by bob, 12 by alice, 13 by alice: the version alice marked ready (the CMS records her as its saver too).
        VersionStamp[] versions = [Version(10, "opticli", published: true), Version(11, "bob@example.com"), Version(12, "alice@example.com"), Version(13, "alice@example.com")];

        Assert.Equal([11], PendingDrafts.ByOthers(versions, baseVersion: 13, Agent, readyBy: "Alice@example.com ").Select(v => v.Id));
        Assert.Equal([13, 12, 11], PendingDrafts.ByOthers(versions, baseVersion: 13, Agent).Select(v => v.Id));
        // Only alice's: nothing to confirm.
        VersionStamp[] hers = [Version(10, "opticli", published: true), Version(12, "alice@example.com"), Version(13, "alice@example.com")];
        Assert.Empty(PendingDrafts.ByOthers(hers, baseVersion: 13, Agent, readyBy: "alice@example.com"));
        // A version without a user name is nobody's consent.
        VersionStamp[] job = [Version(10, "opticli", published: true), Version(11, ""), Version(12, "alice@example.com")];
        Assert.Equal([11], PendingDrafts.ByOthers(job, baseVersion: 12, Agent, readyBy: "alice@example.com").Select(v => v.Id));
    }

    [Fact]
    public void Who_marked_a_version_ready_is_who_checked_it_in_unless_that_is_the_caller_or_nobody()
    {
        Assert.Equal("alice@example.com", PendingDrafts.ReadyBy(checkedIn: true, " alice@example.com ", Agent));
        Assert.Null(PendingDrafts.ReadyBy(checkedIn: true, "OptiCli", Agent));
        Assert.Null(PendingDrafts.ReadyBy(checkedIn: true, "", Agent));
        Assert.Null(PendingDrafts.ReadyBy(checkedIn: true, null, Agent));
        // A draft, or a version in review: nobody marked it ready.
        Assert.Null(PendingDrafts.ReadyBy(checkedIn: false, "alice@example.com", Agent));
    }

    private static PropertyChange Change(string property, string? before, string? after) => new(property,
        before is null ? null : JsonSerializer.SerializeToElement(before), after is null ? null : JsonSerializer.SerializeToElement(after));

    [Fact]
    public void A_base_with_the_drafts_value_for_a_property_it_changed_carries_the_draft()
    {
        // The draft changed Heading and Teaser; the base differs from it only in Teaser, which opticli changed again.
        List<PropertyChange> draft = [Change("Heading", "Old", "Theirs"), Change("Teaser", "Old", "Theirs")];

        Assert.True(PendingDrafts.Carries(draft, [Change("teaser", "Theirs", "Ours")]));
        Assert.True(PendingDrafts.Carries(draft, []));
    }

    [Fact]
    public void A_base_from_the_published_version_does_not_carry_a_later_draft()
    {
        // Based on the published version, the base has none of the draft's values; its own change is elsewhere.
        List<PropertyChange> draft = [Change("Heading", "Old", "Theirs")];

        Assert.False(PendingDrafts.Carries(draft, [Change("Heading", "Theirs", "Old"), Change("MainBody", "Text", "Ours")]));
        // A draft that changed nothing puts nothing live.
        Assert.False(PendingDrafts.Carries([], [Change("MainBody", "Text", "Ours")]));
    }

    [Fact]
    public void Without_a_published_version_every_version_counts()
    {
        Assert.Equal(1, PendingDrafts.NewestByOthers([Version(1, "editor@example.com"), Version(2, "opticli")], baseVersion: 2, Agent)?.Id);
        Assert.Null(PendingDrafts.NewestByOthers([Version(1, "opticli"), Version(2, "opticli")], baseVersion: 2, Agent));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void A_save_without_a_user_name_is_someone_elses(string? savedBy)
    {
        Assert.Equal(11, PendingDrafts.NewestByOthers([Version(10, "opticli", published: true), Version(11, savedBy)], baseVersion: 11, Agent)?.Id);
    }

    [Fact]
    public void For_an_editor_their_own_drafts_are_theirs_and_opticlis_are_someone_elses()
    {
        VersionStamp[] versions = [Version(10, "admin@example.com", published: true), Version(11, "Editor@Example.com "), Version(12, "opticli")];

        Assert.Equal(12, PendingDrafts.NewestByOthers(versions, baseVersion: 12, "editor@example.com")?.Id);
        Assert.Null(PendingDrafts.NewestByOthers(versions, baseVersion: 11, "editor@example.com"));
        Assert.Equal(11, PendingDrafts.NewestByOthers(versions, baseVersion: 11, "colleague@example.com")?.Id);
    }

    [Fact]
    public void A_caller_without_a_name_owns_no_version()
    {
        Assert.Equal(11, PendingDrafts.NewestByOthers([Version(10, "", published: true), Version(11, "")], baseVersion: 11, "")?.Id);
        Assert.False(PendingDrafts.SavedBy(null, ""));
    }

    /// <summary>
    /// Commerce 14's own copy of catalog content in another language of its catalog: the branch's first version, saved by
    /// system (or nobody, in its cached list) with the item's last modified time, which the master language's publish
    /// (version 7) set.
    /// </summary>
    [Theory]
    [InlineData("system")]
    [InlineData("")]
    [InlineData(null)]
    public void Commerces_own_copy_of_a_catalog_language_is_nobodys_draft(string? savedBy)
    {
        VersionStamp[] others = [Version(7, "opticli", published: true)];
        var copy = new VersionStamp(8, false, others[0].Saved, savedBy);

        Assert.True(PendingDrafts.CommerceLanguageCopy(copy, [copy, Version(9, "opticli")], others));
        // Also once the master language is published again: the copy keeps the time it was made with.
        Assert.True(PendingDrafts.CommerceLanguageCopy(copy, [copy], [others[0] with { Published = false }, Version(10, "opticli", published: true)]));
        // A copy made after a publish in another language (version 9) has that publish's time, a few ms apart.
        var after = new VersionStamp(10, false, Version(9, "opticli").Saved.AddMilliseconds(4), savedBy);
        Assert.True(PendingDrafts.CommerceLanguageCopy(after, [after], [Version(7, "opticli", published: true), Version(9, "opticli", published: true)]));
    }

    [Fact]
    public void A_draft_in_that_language_is_not_Commerces_copy()
    {
        VersionStamp[] others = [Version(7, "opticli", published: true)];
        var copy = new VersionStamp(8, false, others[0].Saved, "system");

        // An editor's save into the copy records the editor and the time of the save.
        Assert.False(PendingDrafts.CommerceLanguageCopy(copy with { SavedBy = "editor@example.com" }, [copy], others));
        Assert.False(PendingDrafts.CommerceLanguageCopy(copy with { SavedBy = "", Saved = others[0].Saved.AddMinutes(5) }, [copy], others));
        // A user called System isn't Commerce.
        Assert.False(PendingDrafts.CommerceLanguageCopy(copy with { SavedBy = "System" }, [copy], others));
        // A version after the branch's first is someone's, a job's too, whatever its time.
        var later = new VersionStamp(9, false, others[0].Saved, "");
        Assert.False(PendingDrafts.CommerceLanguageCopy(later, [copy, later], others));
        // Published, it is no draft; a time no version before it has is not Commerce's copy, nor is a later version's.
        Assert.False(PendingDrafts.CommerceLanguageCopy(copy with { Published = true }, [copy], others));
        Assert.False(PendingDrafts.CommerceLanguageCopy(copy, [copy], [Version(10, "opticli", published: true)]));
        Assert.False(PendingDrafts.CommerceLanguageCopy(copy, [copy], [new VersionStamp(12, true, copy.Saved, "opticli")]));
        Assert.False(PendingDrafts.CommerceLanguageCopy(copy, [copy], []));
    }

    /// <summary>
    /// Commerce's copy holds no value of its language: every culture-specific property the type's default for a new item
    /// (empty where it has none) but the SeoUri Commerce makes for it. A draft that gives the language a value of its own,
    /// a job's too, isn't the copy.
    /// </summary>
    [Fact]
    public void Commerces_copy_has_the_types_defaults_and_the_address_Commerce_makes()
    {
        static KeyValuePair<string, object?> Value(string name, object? value) => new(name, value);
        KeyValuePair<string, object?>[] copy =
        [
            Value("DisplayName", null), Value("Teaser", " "), Value("Description", new EPiServer.Core.XhtmlString("")),
            Value("SeoInformation.Title", null), Value("Tags", Array.Empty<string>()), Value("SeoUri", "Women-sv.aspx"),
        ];
        var none = new Dictionary<string, object?>();

        Assert.True(PendingDrafts.CommerceCopyValues(copy, none, "sv"));
        Assert.True(PendingDrafts.CommerceCopyValues(copy, null, "sv"));
        Assert.True(PendingDrafts.CommerceCopyValues([Value("SeoUri", "Women-2-SV.aspx")], none, "sv"));
        Assert.True(PendingDrafts.CommerceCopyValues([Value("SeoUri", null)], none, "sv"));
        Assert.True(PendingDrafts.CommerceCopyValues([], none, "sv"));

        Assert.False(PendingDrafts.CommerceCopyValues([.. copy, Value("DisplayName", "Dam")], none, "sv"));
        Assert.False(PendingDrafts.CommerceCopyValues([Value("SeoInformation.Title", "Dam")], none, "sv"));
        Assert.False(PendingDrafts.CommerceCopyValues([Value("Description", new EPiServer.Core.XhtmlString("<p>Dam</p>"))], none, "sv"));
        Assert.False(PendingDrafts.CommerceCopyValues([Value("SeoUri", "dam.aspx")], none, "sv"));
        Assert.False(PendingDrafts.CommerceCopyValues([Value("SeoUri", "Women-en.aspx")], none, "sv"));
        Assert.False(PendingDrafts.CommerceCopyValues([Value("InStock", false)], none, "sv"));
    }

    /// <summary>
    /// Commerce builds its copy from the type's defaults (SetDefaultValues), so a culture-specific default is in the copy
    /// whatever the master language has; a value other than the default is someone's.
    /// </summary>
    [Fact]
    public void A_culture_specific_default_of_the_type_is_Commerces_not_a_draft()
    {
        static KeyValuePair<string, object?> Value(string name, object? value) => new(name, value);
        var defaults = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Ribbon"] = "New", ["Featured"] = true, ["Rank"] = 3, ["Intro"] = new EPiServer.Core.XhtmlString("<p>Hi</p>"),
            ["Tags"] = new[] { "a", "b" }, ["DisplayName"] = null,
        };
        KeyValuePair<string, object?>[] copy =
        [
            Value("Ribbon", "New"), Value("Featured", true), Value("Rank", 3), Value("Intro", new EPiServer.Core.XhtmlString("<p>Hi</p>")),
            Value("Tags", new List<string> { "a", "b" }), Value("DisplayName", null), Value("SeoUri", "Women-sv.aspx"),
        ];

        Assert.True(PendingDrafts.CommerceCopyValues(copy, defaults, "sv"));

        Assert.False(PendingDrafts.CommerceCopyValues([Value("Ribbon", "Sale")], defaults, "sv"));
        Assert.False(PendingDrafts.CommerceCopyValues([Value("Ribbon", null)], defaults, "sv"));
        Assert.False(PendingDrafts.CommerceCopyValues([Value("Featured", false)], defaults, "sv"));
        Assert.False(PendingDrafts.CommerceCopyValues([Value("Rank", 4)], defaults, "sv"));
        Assert.False(PendingDrafts.CommerceCopyValues([Value("Intro", new EPiServer.Core.XhtmlString("<p>Hej</p>"))], defaults, "sv"));
        Assert.False(PendingDrafts.CommerceCopyValues([Value("Tags", new[] { "a" })], defaults, "sv"));
        Assert.False(PendingDrafts.CommerceCopyValues([Value("DisplayName", "Dam")], defaults, "sv"));
        // A default it can't compare (an object without value equality) is no default: the value counts as given.
        Assert.False(PendingDrafts.CommerceCopyValues([Value("Link", new object())], new Dictionary<string, object?> { ["Link"] = new object() }, "sv"));
        // Without the defaults (a new item couldn't be made), only empty values pass.
        Assert.False(PendingDrafts.CommerceCopyValues([Value("Ribbon", "New")], null, "sv"));
    }

    /// <summary>Only Commerce 14 (CMS 12) makes the copies; on CMS 13 (Commerce 15) the rule never applies.</summary>
    [Fact]
    public void Only_Commerce_14_on_CMS_12_makes_language_copies()
    {
#if CMS13
        Assert.False(OptiCli.Cms.Compat.CmsApi.CommerceLanguageCopies);
#else
        Assert.True(OptiCli.Cms.Compat.CmsApi.CommerceLanguageCopies);
#endif
    }

    [Fact]
    public void An_unconfirmed_publish_is_a_conflict_carrying_the_draft()
    {
        var draft = Draft();

        var error = Assert.Throws<AgentException>(() => PendingDrafts.Require(draft, confirmed: false, dryRun: false, "123 ('News')", "en"));

        Assert.Equal((AgentErrorCodes.Conflict, 409), (error.Code, error.Status));
        Assert.Same(draft, error.ToError().PendingDraft);
        Assert.Equal(
            "Publishing 123 ('News') in 'en' would also put live changes saved by editor@example.com in 123_12 (2025-01-27 09:00:00Z) that aren't published yet (Heading).",
            error.Message);
        Assert.Contains("includeDraft", error.Hint);
    }

    [Fact]
    public void Confirming_or_a_dry_run_reports_the_draft_without_failing()
    {
        var draft = Draft(savedBy: "");

        Assert.Same(draft, PendingDrafts.Require(draft, confirmed: true, dryRun: false, "123", null));
        Assert.Same(draft, PendingDrafts.Require(draft, confirmed: false, dryRun: true, "123", null));
        Assert.Null(PendingDrafts.Require(null, confirmed: false, dryRun: false, "123", null));
        Assert.Contains("without a user name", draft.Describe());
    }

    [Fact]
    public void The_draft_round_trips_in_the_error_envelope()
    {
        var error = PendingDrafts.Unconfirmed("123", null, Draft()).ToError();
        var envelope = new AgentResponse<object>(false, null, new AgentMeta("agent", "0.1.0", AgentProtocol.Version), error);

        var json = JsonSerializer.Serialize(envelope, AgentJson.Options);
        var copy = JsonSerializer.Deserialize<AgentResponse<JsonElement>>(json, AgentJson.Options)!;

        Assert.Equal("123_12", copy.Error!.PendingDraft!.Version);
        Assert.Equal(Monday, copy.Error.PendingDraft.Saved);
        Assert.Equal("New", copy.Error.PendingDraft.Changes[0].After!.Value.GetString());
        Assert.Contains("\"pendingDraft\":{\"version\":\"123_12\",\"savedBy\":\"editor@example.com\",\"saved\":\"2025-01-27T09:00:00Z\"", json);
    }
}
