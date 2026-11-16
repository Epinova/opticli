using System.Text.Json;
using OptiCli.Agent.Http;
using OptiCli.Protocol;

namespace OptiCli.Agent.Tests.Protocol;

/// <summary>The project fields on the save requests, and the project routes' requests and results, as JSON.</summary>
public class ProjectProtocolTests
{
    [Fact]
    public void Saves_without_a_project_send_no_project_fields_which_an_older_agent_would_refuse()
    {
        foreach (var request in new object[]
        {
            new DraftRequest(),
            new CreateRequest { Type = "StandardPage", Name = "x" },
            new UploadRequest { FileName = "a.pdf" },
            new LanguageBranchRequest { Lang = "sv" },
        })
        {
            var json = JsonSerializer.Serialize(request, request.GetType(), AgentJson.Options);
            Assert.DoesNotContain("\"project\"", json);
            Assert.DoesNotContain("\"ready\"", json);
        }
    }

    [Fact]
    public void A_save_into_a_project_round_trips()
    {
        var json = JsonSerializer.Serialize(new DraftRequest { Project = "Spring campaign", Ready = true, RequestApproval = true }, AgentJson.Options);
        var copy = JsonSerializer.Deserialize<DraftRequest>(json, AgentRequest.RequestOptions)!;

        Assert.Contains("\"project\":\"Spring campaign\"", json);
        Assert.Equal(("Spring campaign", true, true), (copy.Project, copy.Ready, copy.RequestApproval));
        Assert.Equal("12", JsonSerializer.Deserialize<CreateRequest>("""{"type":"StandardPage","name":"x","project":"12","ready":true}""", AgentRequest.RequestOptions)!.Project);
        Assert.True(JsonSerializer.Deserialize<UploadRequest>("""{"fileName":"a.pdf","project":"12","ready":true}""", AgentRequest.RequestOptions)!.Ready);
        Assert.Equal("12", JsonSerializer.Deserialize<LanguageBranchRequest>("""{"lang":"sv","project":"12"}""", AgentRequest.RequestOptions)!.Project);
    }

    [Fact]
    public void Project_requests_leave_out_what_wasnt_given_and_refuse_unknown_fields()
    {
        Assert.Equal("""{"all":true,"dryRun":false}""", JsonSerializer.Serialize(new ProjectReadyRequest { All = true }, AgentJson.Options));
        Assert.Equal("""{"items":["123","456_7"],"lang":"sv","all":false,"requestApproval":true,"comment":"Please check","dryRun":true}""",
            JsonSerializer.Serialize(new ProjectReadyRequest { Items = ["123", "456_7"], Lang = "sv", RequestApproval = true, Comment = "Please check", DryRun = true }, AgentJson.Options));
        Assert.Equal("""{"includeDraft":false,"dryRun":false}""", JsonSerializer.Serialize(new ProjectPublishRequest(), AgentJson.Options));
        var at = JsonSerializer.Deserialize<ProjectPublishRequest>("""{"publishAt":"2030-01-02T03:04:05Z","includeDraft":true}""", AgentRequest.RequestOptions)!;
        Assert.Equal((new DateTime(2030, 1, 2, 3, 4, 5, DateTimeKind.Utc), true), (at.PublishAt!.Value.ToUniversalTime(), at.IncludeDraft));

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ProjectRemoveRequest>("""{"items":["1"],"dryrun":true,"force":true}""", AgentRequest.RequestOptions));
    }

    [Fact]
    public void A_publish_result_has_only_the_lists_it_filled()
    {
        var result = new ProjectResult
        {
            Project = new ProjectSummary { Id = 3, Name = "Launch", Status = ProjectStatuses.Published, Items = 2, Ready = 0 },
            EditorUi = ProjectEditorUi.Gadget,
            Published = [new ProjectItemResult { Content = new ContentSummary { Ref = "10_100", Id = 10, Guid = Guid.Empty, Name = "News", Status = "published" }, Outcome = ProjectItemOutcomes.Published, PreviouslyPublished = 98 }],
        };

        var json = JsonSerializer.Serialize(result, AgentJson.Options);
        var element = JsonDocument.Parse(json).RootElement;

        Assert.Equal("gadget", element.GetProperty("editorUi").GetString());
        Assert.True(element.GetProperty("valid").GetBoolean());
        Assert.Equal(98, element.GetProperty("published")[0].GetProperty("previouslyPublished").GetInt32());
        foreach (var absent in new[] { "notReady", "failed", "scheduled", "alreadyPublished", "items", "siteChanges", "siteError", "created", "deleted", "reactivated" })
        {
            Assert.False(element.TryGetProperty(absent, out _), absent);
        }
        Assert.False(element.GetProperty("published")[0].TryGetProperty("problem", out _));
    }

    [Fact]
    public void A_refusal_over_items_carries_the_project_in_the_error()
    {
        var error = new AgentError(AgentErrorCodes.Conflict, "Can't publish") with
        {
            Reason = AgentErrorReasons.ProjectItems,
            Project = new ProjectResult
            {
                Project = new ProjectSummary { Id = 3, Name = "Launch", Status = ProjectStatuses.Active },
                Valid = false,
                Published = [new ProjectItemResult
                {
                    Content = new ContentSummary { Ref = "10_100", Id = 10, Guid = Guid.Empty, Name = "News" },
                    Outcome = ProjectItemOutcomes.Published,
                    Problem = new ProjectItemProblem(AgentErrorCodes.Validation, "10 ('News') doesn't pass validation."),
                }],
            },
        };

        var element = JsonDocument.Parse(JsonSerializer.Serialize(error, AgentJson.Options)).RootElement;

        Assert.Equal("projectItems", element.GetProperty("reason").GetString());
        Assert.Equal("validation", element.GetProperty("project").GetProperty("published")[0].GetProperty("problem").GetProperty("code").GetString());
        Assert.False(element.GetProperty("project").GetProperty("valid").GetBoolean());
    }

    [Fact]
    public void A_save_result_says_what_it_did_to_the_project()
    {
        var json = JsonSerializer.Serialize(new WriteResult { Saved = true, Project = new ProjectSaveResult(3, "Launch") { Item = ProjectSaveItems.Replaced, Replaced = "10_99", Ready = true } }, AgentJson.Options);

        Assert.Contains("\"project\":{\"id\":3,\"name\":\"Launch\",\"item\":\"replaced\",\"replaced\":\"10_99\",\"ready\":true}", json);
        Assert.DoesNotContain("\"project\"", JsonSerializer.Serialize(new WriteResult(), AgentJson.Options));
    }
}
