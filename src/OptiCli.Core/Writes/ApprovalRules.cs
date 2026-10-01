using OptiCli.Core.Errors;
using OptiCli.Core.Queries;
using OptiCli.Core.Serve;
using OptiCli.Protocol;

namespace OptiCli.Core.Writes;

/// <summary>
/// The site agent's approval rules in command-line terms, and the same rules checked from the database where a dry run
/// doesn't ask the site (publish) or can't (content a plan creates).
/// </summary>
public static class ApprovalRules
{
    public const string RefusedHint =
        "Ask the user whether to send it for review; if so, run again with --request-approval (in a plan: \"requestApproval\": true on the step), which saves it and starts the approval sequence, as the edit UI's Ready for Review does. A reviewer then approves it in the CMS. Without --publish the change is saved as a draft and nothing goes live.";

    public const string NoSequenceHint = "Publish it with --publish instead, or leave both out to save a draft.";

    /// <summary>The agent's errors about approvals, with hints that name the options instead of request fields.</summary>
    public static OptiCliException? Translate(OptiCliException ex) => (ex.Details as AgentErrorDetails)?.Reason switch
    {
        AgentErrorReasons.ApprovalSequence => new RefusedException(ex.Message, RefusedHint) { Details = ex.Details },
        AgentErrorReasons.NoApprovalSequence => new UsageException(ex.Message, NoSequenceHint) { Details = ex.Details },
        AgentErrorReasons.InReview => new ConflictException(ex.Message, InReviewHint) { Details = ex.Details },
        _ => null,
    };

    public const string InReviewHint =
        "A reviewer must approve or reject it in the CMS edit UI first (`opticli drafts` lists what awaits review); tell the user. Nothing was saved.";

    /// <summary>The agent's decision for a write that publishes, from the database.</summary>
    /// <param name="sequence">The sequence that applies to the content (or to new content, its parent's).</param>
    /// <returns>True when the write requests approval rather than publishing.</returns>
    /// <exception cref="RefusedException">A publish under a sequence, without <see cref="WriteOperation.RequestApproval"/>.</exception>
    /// <exception cref="UsageException"><see cref="WriteOperation.RequestApproval"/> alone where no sequence applies.</exception>
    public static bool Check(ApprovalSequence? sequence, WriteOperation operation, string what)
    {
        if (sequence is null)
        {
            return operation.RequestApproval && !operation.Publishes
                ? throw new UsageException($"No approval sequence applies to {what}, so there is no review to request.", NoSequenceHint)
                {
                    Details = new AgentErrorDetails(null, null) { Reason = AgentErrorReasons.NoApprovalSequence },
                }
                : false;
        }
        if (operation.RequestApproval)
        {
            return true;
        }
        if (!operation.Publishes)
        {
            return false;
        }
        throw new RefusedException($"{what} has an approval sequence ({sequence.Describe()}), so it isn't published directly: that would skip its reviewers.", RefusedHint)
        {
            Details = new AgentErrorDetails(null, null) { Reason = AgentErrorReasons.ApprovalSequence },
        };
    }
}
