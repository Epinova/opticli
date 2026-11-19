using System.Net;
using System.Text;
using System.Text.Json;
using OptiCli.Core.Errors;
using OptiCli.Core.Serve;
using OptiCli.Core.Writes;
using OptiCli.Protocol;

namespace OptiCli.Core.Tests.Writes;

/// <summary>
/// On a terminal, a write refused for someone else's draft of the content and for their draft of a block of its folder
/// asks about each on its own: a yes to one never confirms the other.
/// </summary>
public class BlockDraftPromptTests
{
    private static readonly PendingDraft ContentDraft = new("123_10", "someone", DateTime.UtcNow, []);

    private static readonly PendingDraft BlockDraft = new("456_20", "someone", DateTime.UtcNow, []) { Path = "MainContentArea[0]" };

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_yes_to_the_contents_draft_doesnt_confirm_a_blocks_and_each_is_asked_about_once(bool contentFirst)
    {
        var agent = new FakeAgent(contentFirst);
        var asked = new List<string>();
        var executor = Executor(agent, (_, draft, question) =>
        {
            asked.Add($"{(draft.Path is null ? "content" : "block")}: {question}");
            return true;
        });

        await executor.PostDraftAsync(AgentRoutes.Draft("123"), new DraftRequest { Publish = true }, CancellationToken.None);

        string[] content = ["content: Publish these changes too?"];
        string[] block = ["block: Save the block's new version over their draft?"];
        Assert.Equal(contentFirst ? [.. content, .. block] : [.. block, .. content], asked);
        // The write sent after the first yes still left the other unconfirmed: the agent refused it, and only the last is both.
        Assert.Equal(contentFirst ? (true, false) : (false, true), (agent.Requests[1].IncludeDraft, agent.Requests[1].IncludeBlockDraft ?? agent.Requests[1].IncludeDraft));
        Assert.Equal((true, true), (agent.Requests[2].IncludeDraft, agent.Requests[2].IncludeBlockDraft ?? agent.Requests[2].IncludeDraft));
    }

    [Fact]
    public async Task A_no_to_the_blocks_draft_after_a_yes_to_the_contents_saves_nothing()
    {
        var agent = new FakeAgent(contentFirst: true);
        var executor = Executor(agent, (_, draft, _) => draft.Path is null);

        var ex = await Assert.ThrowsAsync<ConflictException>(() => executor.PostDraftAsync(AgentRoutes.Draft("123"), new DraftRequest { Publish = true }, CancellationToken.None));

        Assert.Equal("Not saved, as answered: the block's draft stays as it is.", ex.Message);
        Assert.Contains("{\"ref\": \"456\"}", ex.Hint);
        Assert.Equal(2, agent.Requests.Count);
        Assert.Equal((true, (bool?)false), (agent.Requests[1].IncludeDraft, agent.Requests[1].IncludeBlockDraft));
    }

    [Fact]
    public async Task Without_a_terminal_a_blocks_draft_is_a_conflict_whose_hint_names_the_block()
    {
        var agent = new FakeAgent(contentFirst: false);

        var ex = await Assert.ThrowsAsync<ConflictException>(() => Executor(agent, null).PostDraftAsync(AgentRoutes.Draft("123"), new DraftRequest(), CancellationToken.None));

        Assert.Equal(WriteExecutor.BlockDraftHint(BlockDraft), ex.Hint);
        Assert.Equal("MainContentArea[0]", Assert.IsType<AgentErrorDetails>(ex.Details).Draft!.Path);
        Assert.Single(agent.Requests);
    }

    private static WriteExecutor Executor(FakeAgent agent, Func<string, PendingDraft, string, bool>? confirm) =>
        new(null!, _ => Task.FromResult(agent.Client), confirmDraft: confirm);

    /// <summary>
    /// An agent that makes shared area blocks and refuses a write for the content's draft and a block's, in the order given,
    /// until each is confirmed (the block's by <see cref="DraftRequest.IncludeBlockDraft"/>, or else <see cref="DraftRequest.IncludeDraft"/>).
    /// </summary>
    private sealed class FakeAgent : HttpMessageHandler
    {
        private readonly bool _contentFirst;

        public FakeAgent(bool contentFirst)
        {
            _contentFirst = contentFirst;
            Client = new AgentClient(new Uri("http://127.0.0.1:5199"), "token", this);
        }

        public AgentClient Client { get; }

        public List<DraftRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var meta = new AgentMeta("agent", "0.35.0", AgentProtocol.Version);
            if (request.Method == HttpMethod.Get)
            {
                var ping = new PingResponse
                {
                    Protocol = AgentProtocol.Version, AgentVersion = "0.35.0", CmsVersion = "12.21.2", Runtime = "8.0", Environment = "Development",
                    Principal = AgentProtocol.PrincipalName, Database = new DatabaseTarget("EPiServerDB", "localhost", "alloy", true, true),
                    InlineBlocks = InlineBlockModes.Shared,
                };
                return Answer(HttpStatusCode.OK, new AgentResponse<PingResponse>(true, ping, meta));
            }
            var body = JsonSerializer.Deserialize<DraftRequest>(await request.Content!.ReadAsStringAsync(cancellationToken), AgentJson.Options)!;
            Requests.Add(body);
            var content = !body.IncludeDraft;
            var block = !(body.IncludeBlockDraft ?? body.IncludeDraft);
            PendingDraft? refused = _contentFirst
                ? content ? ContentDraft : block ? BlockDraft : null
                : block ? BlockDraft : content ? ContentDraft : null;
            return refused is null
                ? Answer(HttpStatusCode.OK, new AgentResponse<WriteResult>(true, new WriteResult { Saved = true }, meta))
                : Answer(HttpStatusCode.Conflict, new AgentResponse<object>(false, null, meta,
                    new AgentError(AgentErrorCodes.Conflict, refused.Path is null ? "Someone else's draft would go live." : "The write would save over someone else's block draft.")
                    {
                        PendingDraft = refused,
                    }));
        }

        private static HttpResponseMessage Answer<T>(HttpStatusCode status, AgentResponse<T> response) =>
            new(status) { Content = new StringContent(JsonSerializer.Serialize(response, AgentJson.Options), Encoding.UTF8, "application/json") };
    }
}
