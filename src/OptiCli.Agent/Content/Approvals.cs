using EPiServer.Approvals;
using EPiServer.Approvals.ContentApprovals;
using EPiServer.Core;
using EPiServer.DataAbstraction;
using EPiServer.DataAccess;
using OptiCli.Agent.Http;
using OptiCli.Protocol;

namespace OptiCli.Agent.Content;

/// <summary>
/// Content approval sequences. The agent saves with <c>AccessLevel.NoAccess</c>, which also skips them: a publish of
/// content under a sequence would go live without its reviewers. So such a publish is refused, and
/// <c>requestApproval</c> saves with <see cref="SaveAction.RequestApproval"/> instead, which starts the sequence as the
/// edit UI's "Ready for Review" does.
/// </summary>
/// <remarks>
/// Saving as the opticli principal with access checks would not help: that principal has no roles, so it could save
/// nothing, and access rights are a different question from approvals. Hence <c>NoAccess</c>, with approvals checked here.
/// </remarks>
internal static class Approvals
{
    /// <summary>The enabled approval sequence for <paramref name="link"/> (its own or inherited), or for new content below it.</summary>
    public static ApprovalDefinition? Applying(AgentRequest request, ContentReference link)
    {
        var resolved = request.Service<IApprovalDefinitionRepository>().ResolveAsync(link.ToReferenceWithoutVersion()).GetAwaiter().GetResult();
        return resolved?.Definition is { IsEnabled: true } definition ? definition : null;
    }

    /// <summary>
    /// How a write saves: null for a draft, <see cref="SaveAction.Publish"/>, or <see cref="SaveAction.RequestApproval"/>
    /// where a sequence applies and the caller asked for that.
    /// </summary>
    /// <param name="link">The content, or the parent of new content.</param>
    /// <param name="what">The content for messages, e.g. <c>123 ('News')</c>.</param>
    /// <exception cref="AgentException">
    /// <c>refused</c> (<see cref="AgentErrorReasons.ApprovalSequence"/>) for a publish under a sequence without
    /// <paramref name="requestApproval"/>; <c>usage</c> for <paramref name="requestApproval"/> alone where none applies.
    /// </exception>
    public static SaveAction? Decide(AgentRequest request, ContentReference link, bool publish, bool requestApproval, string what) =>
        publish || requestApproval ? Choose(Applying(request, link), link, publish, requestApproval, what) : null;

    /// <summary><see cref="Decide"/> once the sequence that applies (<paramref name="definition"/>, or null) is known.</summary>
    internal static SaveAction? Choose(ApprovalDefinition? definition, ContentReference link, bool publish, bool requestApproval, string what)
    {
        if (!publish && !requestApproval)
        {
            return null;
        }
        if (definition is null)
        {
            return publish
                ? SaveAction.Publish
                : throw new AgentException(AgentErrorCodes.Usage, $"No approval sequence applies to {what}, so there is no review to request.",
                    "Publish it instead (publish), or save it as a draft.")
                {
                    Reason = AgentErrorReasons.NoApprovalSequence,
                };
        }
        if (requestApproval)
        {
            return SaveAction.RequestApproval;
        }
        throw new AgentException(
            AgentErrorCodes.Refused,
            $"{what} has an approval sequence ({Describe(definition, link)}), so it isn't published directly: that would skip its reviewers.",
            "Retry with requestApproval to save it and start the sequence (as the edit UI's Ready for Review); a reviewer then approves it. Without publish the change is saved as a draft.")
        {
            Reason = AgentErrorReasons.ApprovalSequence,
        };
    }

    /// <summary>
    /// The edit UI doesn't let content in review be changed until a reviewer approves or rejects it, and neither does
    /// opticli: the newest version of the branch must not be awaiting approval.
    /// </summary>
    /// <exception cref="AgentException"><c>conflict</c> with <see cref="AgentErrorReasons.InReview"/>.</exception>
    public static void RequireNotInReview(IReadOnlyList<ContentVersion> branch, string what, string? language)
    {
        var newest = branch.OrderByDescending(v => v.ContentLink.WorkID).FirstOrDefault();
        if (newest is { Status: VersionStatus.AwaitingApproval })
        {
            throw new AgentException(
                AgentErrorCodes.Conflict,
                $"{what}{(language is null ? "" : $" in '{language}'")} is in review: version {newest.ContentLink} awaits approval (requested by {newest.SavedBy}, {newest.Saved.ToUniversalTime():u}).",
                "A reviewer must approve or reject it in the CMS edit UI before it can be changed; tell the user. Nothing was saved.")
            {
                Reason = AgentErrorReasons.InReview,
            };
        }
    }

    /// <summary>"defined on 123, 1 step: Review (role WebAdmins)".</summary>
    private static string Describe(ApprovalDefinition definition, ContentReference link)
    {
        var where = definition is ContentApprovalDefinition { ContentLink: { } owner } && !owner.CompareToIgnoreWorkID(link)
            ? $"inherited from {owner.ID}"
            : $"defined on {link.ID}";
        var steps = definition.Steps.Select(step =>
            $"{step.Name} ({string.Join(", ", step.Reviewers.Select(r => $"{(r.ReviewerType == ApprovalDefinitionReviewerType.Role ? "role" : "user")} {r.Name}").Distinct())})");
        return $"{where}, {definition.Steps.Count} step(s): {string.Join(", ", steps)}";
    }
}
