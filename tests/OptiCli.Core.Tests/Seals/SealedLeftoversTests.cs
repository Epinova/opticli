using OptiCli.Core.Cms;
using OptiCli.Core.Content;
using OptiCli.Core.Errors;
using OptiCli.Core.Seals;
using OptiCli.Protocol;

namespace OptiCli.Core.Tests.Seals;

/// <summary>
/// Content types and properties only production's database has (removed code): a seal accepts them by name, and the run's
/// site may report exactly those; every other difference still stops the run before it writes.
/// </summary>
public class SealedLeftoversTests
{
    private static DriftItem Leftover(string name) => new(name, DriftAhead.Database, DriftItem.OnlyInDatabase);

    private static DriftReport Report(IEnumerable<DriftItem>? types = null, IEnumerable<DriftItem>? properties = null, IEnumerable<DriftItem>? migrations = null) =>
        DriftReport.Create(types ?? [], properties ?? [], migrations ?? [], [], [], []);

    [Fact]
    public void Leftovers_are_told_apart_from_every_other_difference()
    {
        var report = Report(
            [Leftover("FooterSettings"), new DriftItem("NewsPage", DriftAhead.Local, "only in the code"), new DriftItem("LegacyType", DriftAhead.Unknown, DriftItem.OnlyInDatabase + ", unknown origin") { Informational = true }],
            [Leftover("StartPage.LegacyFooter"), new DriftItem("ArticlePage.Heading", DriftAhead.Unknown, "type: XhtmlString in the code, String in the database")],
            [new DriftItem("20261026_AddTable", DriftAhead.Local, "only in the code")]);

        var (leftovers, other) = SealedLeftovers.Split(report);

        Assert.Equal(["FooterSettings", "StartPage.LegacyFooter"], leftovers);
        Assert.Equal(["NewsPage", "ArticlePage.Heading", "20261026_AddTable"], other.Select(o => o.Item.Name));
    }

    [Fact]
    public void The_seal_expects_the_local_leftovers_production_has_and_what_only_production_has()
    {
        var model = new CmsModel(
            [new ContentTypeInfo(1, Guid.NewGuid(), "FooterSettings", null, null, ContentKind.Other, "Other", null, null),
             new ContentTypeInfo(2, Guid.NewGuid(), "StartPage", null, null, ContentKind.Page, "Page", null, null)],
            [new PropertyDefinition(10, 2, "LegacyFooter", "ContentReference", PropertyBaseType.ContentReference, null, false, false)],
            [], [], null, null);

        var leftovers = SealedLeftovers.Expected(["FooterSettings", "SearchFolder", "StartPage.LegacyFooter", "StartPage.Gone"], ["TagFilterBlock", "StartPage.Promo"], model);

        // Local leftovers production doesn't have aren't expected there; what only production has is.
        Assert.Equal(["FooterSettings", "StartPage.LegacyFooter", "StartPage.Promo", "TagFilterBlock"], leftovers);
    }

    [Fact]
    public void The_run_accepts_the_sealed_leftovers_and_confirms_with_the_fingerprint()
    {
        var report = Report([Leftover("FooterSettings"), Leftover("HeaderSettings")], [Leftover("StartPage.LegacyFooter")]);

        Assert.Equal(report.Fingerprint, SealedLeftovers.Accept(report, ["footersettings", "HeaderSettings", "StartPage.LegacyFooter", "TagFilterBlock"]));
        Assert.Null(SealedLeftovers.Accept(Report(), null));
    }

    [Fact]
    public void A_leftover_the_seal_didnt_list_stops_the_run()
    {
        var report = Report([Leftover("FooterSettings"), Leftover("PromoBlock")]);

        var refused = Assert.Throws<ConflictException>(() => SealedLeftovers.Accept(report, ["FooterSettings"]));

        Assert.Equal(SealedLeftovers.Reason, SealStoreTests.Reason(refused));
        Assert.Contains("PromoBlock (only in the database, not in the seal)", refused.Message, StringComparison.Ordinal);
        Assert.Throws<ConflictException>(() => SealedLeftovers.Accept(report, null));
    }

    [Fact]
    public void Any_other_difference_stops_the_run_whatever_was_sealed()
    {
        var report = Report([Leftover("FooterSettings"), new DriftItem("NewsPage", DriftAhead.Local, "only in the code")]);

        var refused = Assert.Throws<ConflictException>(() => SealedLeftovers.Accept(report, ["FooterSettings", "NewsPage"]));

        Assert.Contains("NewsPage (only in the code)", refused.Message, StringComparison.Ordinal);
    }
}
