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

    /// <summary>A save into a project marked ready, under an approval sequence.</summary>
    public const string ReadyRefusedHint =
        "Ask the user whether to send it for review instead; if so, run again with --request-approval (in a plan: \"requestApproval\": true on the step): it goes in the project for review, and counts as ready once a reviewer approves it. Without --ready it is saved in the project as a draft. Nothing was saved.";

    public const string NoSequenceHint = "Publish it with --publish instead, or leave both out to save a draft.";

    /// <summary>The agent's errors about approvals, with hints that name the options instead of request fields.</summary>
    /// <param name="ready">The write was a save into a project marked ready (<c>--ready</c>), not a publish.</param>
    public static OptiCliException? Translate(OptiCliException ex, bool ready = false) => (ex.Details as AgentErrorDetails)?.Reason switch
    {
        AgentErrorReasons.ApprovalSequence => new RefusedException(ex.Message, ready ? ReadyRefusedHint : RefusedHint) { Details = ex.Details },
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
    /// <remarks>
    /// A save into a project with <see cref="WriteOperation.Ready"/> counts as a publish here, as the site has it: under a
    /// sequence it needs <see cref="WriteOperation.RequestApproval"/> (a review instead of ready), and without one the
    /// review request is no error (the version is marked ready).
    /// </remarks>
    public static bool Check(ApprovalSequence? sequence, WriteOperation operation, string what)
    {
        var ready = operation is { Ready: true, Project: not null };
        if (sequence is null)
        {
            return operation.RequestApproval && !operation.Publishes && !ready
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
        if (ready)
        {
            throw new RefusedException($"{what} has an approval sequence ({sequence.Describe()}), so it isn't marked Ready to publish: that would let it past its reviewers.", ReadyRefusedHint)
            {
                Details = new AgentErrorDetails(null, null) { Reason = AgentErrorReasons.ApprovalSequence },
            };
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
