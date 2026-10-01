using System.Text.Json;
using OptiCli.Agent.Http;
using OptiCli.Protocol;

namespace OptiCli.Agent.Tests.Protocol;

public class ProtocolJsonTests
{
    private static T RoundTrip<T>(T value, out string json)
    {
        json = JsonSerializer.Serialize(value, AgentJson.Options);
        return JsonSerializer.Deserialize<T>(json, AgentJson.Options)!;
    }

    [Fact]
    public void Draft_request_round_trips_with_structured_values()
    {
        var request = JsonSerializer.Deserialize<DraftRequest>("""
            {
              "lang": "en",
              "properties": {
                "Heading": "Hello",
                "ShowTeaser": true,
                "MainArea": [{ "ref": "123", "displayOption": "wide" }, { "guid": "0b1c2d3e-0000-4000-8000-000000000001" }],
                "Teaser": { "Heading": "Nested" }
              },
              "areaOps": [{ "op": "move", "property": "MainArea", "index": 1, "at": 0 }],
              "dryRun": true,
              "baseVersion": 456
            }
            """, AgentRequest.RequestOptions)!;

        var copy = RoundTrip(request, out var json);

        Assert.Equal("en", copy.Lang);
        Assert.True(copy.DryRun);
        Assert.False(copy.Publish);
        Assert.Equal(456, copy.BaseVersion);
        Assert.Equal(JsonValueKind.Array, copy.Properties!["MainArea"].ValueKind);
        Assert.Equal("wide", copy.Properties["MainArea"].Deserialize<List<AreaItemValue>>(AgentJson.Options)![0].DisplayOption);
        Assert.Equal(new AreaOperation { Op = AreaOps.Move, Property = "MainArea", Index = 1, At = 0 }, copy.AreaOps![0]);
        Assert.Contains("\"baseVersion\":456", json);
        Assert.DoesNotContain("\"name\"", json);
    }

    [Fact]
    public void Requests_reject_unknown_fields_so_typos_never_turn_a_dry_run_into_a_save()
    {
        var error = Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<DraftRequest>("""{"properties":{},"dry_run":true}""", AgentRequest.RequestOptions));

        Assert.Contains("dry_run", error.Message);
    }

    [Fact]
    public void Required_request_fields_are_enforced()
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<CreateRequest>("""{"parent":"123","name":"x"}""", AgentRequest.RequestOptions));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<MoveRequest>("{}", AgentRequest.RequestOptions));
    }

    [Fact]
    public void Create_language_publish_and_move_requests_round_trip()
    {
        var create = new CreateRequest { ForContent = "123", Type = "TeaserBlock", Name = "Teaser", Lang = "en", Publish = true };
        var language = new LanguageBranchRequest { Lang = "sv", Name = "Om oss" };
        var publish = new PublishRequest { Version = 456 };
        var move = new MoveRequest { Parent = "789" };

        Assert.Equal(create, RoundTrip(create, out _));
        Assert.Equal(language, RoundTrip(language, out _));
        Assert.Equal(publish, RoundTrip(publish, out _));
        Assert.Equal(move, RoundTrip(move, out var json));
        Assert.Equal("""{"parent":"789","dryRun":false}""", json);

        var confirmed = new PublishRequest { IncludeDraft = true };
        Assert.Equal(confirmed, RoundTrip(confirmed, out json));
        Assert.Equal("""{"includeDraft":true}""", json);

        // Left out unless set, so an agent from before it was added still takes every other request.
        var review = new PublishRequest { RequestApproval = true };
        Assert.Equal(review, RoundTrip(review, out json));
        Assert.Equal("""{"includeDraft":false,"requestApproval":true}""", json);
        Assert.True(RoundTrip(new DraftRequest { Publish = true, IncludeDraft = true }, out _).IncludeDraft);
        Assert.True(RoundTrip(create with { Guid = Guid.NewGuid(), UpdateExisting = true, IncludeDraft = true }, out _).IncludeDraft);
    }

    [Fact]
    public void A_publishing_write_result_names_the_previously_published_version()
    {
        var copy = RoundTrip(new WriteResult { Saved = true, Published = true, BaseVersion = 457, PreviouslyPublished = 450 }, out var json);

        Assert.Equal(450, copy.PreviouslyPublished);
        Assert.Contains("\"previouslyPublished\":450", json);
        Assert.DoesNotContain("pendingDraft", json);
    }

    [Fact]
    public void Write_result_envelope_round_trips()
    {
        var result = new WriteResult
        {
            Content = new ContentSummary
            {
                Ref = "123_457", Id = 123, Version = 457, Guid = Guid.Parse("0b1c2d3e-0000-4000-8000-000000000001"),
                Name = "About", Type = "ArticlePage", Language = "en", Status = "checkedOut", Parent = "5",
            },
            Saved = true,
            BaseVersion = 456,
            Changes = [new PropertyChange("Heading", JsonSerializer.SerializeToElement("Old"), JsonSerializer.SerializeToElement("New"))],
        };
        var envelope = new AgentResponse<WriteResult>(true, result, new AgentMeta("agent", "0.1.0", AgentProtocol.Version));

        var copy = RoundTrip(envelope, out var json);

        Assert.True(copy.Ok);
        Assert.Null(copy.Error);
        Assert.Equal(result.Content, copy.Data!.Content);
        Assert.Equal("New", copy.Data.Changes[0].After!.Value.GetString());
        Assert.True(copy.Data.Valid);
        Assert.StartsWith("""{"ok":true,"data":{"content":{"ref":"123_457","id":123""", json);
        Assert.Contains("\"meta\":{\"source\":\"agent\",\"version\":\"0.1.0\",\"protocol\":1}", json);
    }

    [Fact]
    public void Error_envelope_carries_every_validation_issue_and_the_current_version()
    {
        var error = new AgentError(AgentErrorCodes.Validation, "Validation failed with 2 errors.", "Fix them.")
        {
            Validation = [new ValidationIssue("Heading", "Heading is required."), new ValidationIssue(null, "Name is too long.", "warning")],
            CurrentVersion = 457,
        };
        var envelope = new AgentResponse<object>(false, null, new AgentMeta("agent", "0.1.0", 1), error);

        var json = JsonSerializer.Serialize(envelope, AgentJson.Options);
        var copy = JsonSerializer.Deserialize<AgentResponse<JsonElement>>(json, AgentJson.Options)!;

        Assert.False(copy.Ok);
        Assert.Equal(error.Validation, copy.Error!.Validation);
        Assert.Equal(457, copy.Error.CurrentVersion);
        Assert.DoesNotContain("\"data\"", json);
        Assert.Contains("""{"property":"Heading","message":"Heading is required.","severity":"error"}""", json);
    }

    [Fact]
    public void Ping_and_type_model_round_trip()
    {
        var ping = new PingResponse
        {
            Protocol = 1, AgentVersion = "0.1.0", CmsVersion = "12.23.1", Runtime = "10.0.0", Environment = "Development",
            Principal = AgentProtocol.PrincipalName, Database = new DatabaseTarget("EPiServerDB", "localhost,1433", "Cms", true, true),
        };
        var type = new ContentTypeModel
        {
            Name = "ArticlePage", Guid = Guid.Parse("0b1c2d3e-0000-4000-8000-000000000002"), Id = 7, Kind = "page",
            Children = new ChildTypeRules("specific", ["ArticlePage"]),
            Properties =
            [
                new PropertyModel
                {
                    Name = "MainArea", PropertyType = "PropertyContentArea", ClrType = "EPiServer.Core.ContentArea",
                    AllowedTypes = ["TeaserBlock"], Validation = [new AttributeModel("MaxItems", new Dictionary<string, string> { ["max"] = "3" })],
                },
            ],
        };

        Assert.Equal(ping, RoundTrip(ping, out _));
        var copy = RoundTrip(type, out var json);
        Assert.Equal("TeaserBlock", copy.Properties[0].AllowedTypes![0]);
        Assert.Equal("3", copy.Properties[0].Validation![0].Args!["max"]);
        Assert.Contains("\"children\":{\"availability\":\"specific\",\"allowed\":[\"ArticlePage\"]}", json);
    }

    [Fact]
    public void Content_item_round_trips_with_nested_blocks_and_area_items()
    {
        var teaser = new Dictionary<string, ContentItemProperty>
        {
            ["Heading"] = new("String", JsonSerializer.SerializeToElement("Inline heading")),
        };
        var item = new ContentItem
        {
            Ref = "123", Id = 123, Version = 456, Guid = Guid.Parse("0b1c2d3e-0000-4000-8000-000000000003"), Name = "About",
            Type = "ArticlePage", Kind = "page", Language = "en", MasterLanguage = "en", Languages = ["en", "sv"], Status = "published",
            Parent = "5", Url = "/en/about/",
            Properties = new Dictionary<string, ContentItemProperty>
            {
                ["Heading"] = new("String", JsonSerializer.SerializeToElement("About us")),
                ["Summary"] = new("XhtmlString"),
                ["Hero"] = new("Block", JsonSerializer.SerializeToElement(teaser, AgentJson.Options)) { BlockType = "HeroBlock" },
                ["MainArea"] = new("ContentArea", JsonSerializer.SerializeToElement(new[]
                {
                    new ContentItemAreaEntry("789") { DisplayOption = "wide" },
                    new ContentItemAreaEntry(null) { Inline = true, Type = "TeaserBlock", Properties = teaser },
                }, AgentJson.Options)),
            },
        };

        var copy = RoundTrip(item, out var json);

        Assert.Equal(["en", "sv"], copy.Languages);
        Assert.Null(copy.Properties["Summary"].Value);
        Assert.Contains("\"Summary\":{\"type\":\"XhtmlString\"}", json);
        Assert.Contains("\"Hero\":{\"type\":\"Block\",\"value\":{\"Heading\":{\"type\":\"String\",\"value\":\"Inline heading\"}},\"blockType\":\"HeroBlock\"}", json);
        var area = copy.Properties["MainArea"].Value!.Value.Deserialize<List<ContentItemAreaEntry>>(AgentJson.Options)!;
        Assert.Equal("wide", area[0].DisplayOption);
        Assert.True(area[1].Inline);
        Assert.Equal("Inline heading", area[1].Properties!["Heading"].Value!.Value.GetString());
    }
}
