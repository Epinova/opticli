using OptiCli.Core.Errors;
using OptiCli.Core.Serve;
using OptiCli.Protocol;

namespace OptiCli.Core.Writes;

/// <summary>
/// The CMS publishes a language branch other than the master only once the master branch has been published: while the
/// master is "pending publish" (never published), <c>IContentRepository.Save</c> refuses the publish
/// (<c>/validation/mustpublishmasterfirst</c>). It is no validator, so no dry run sees it; and a scheduled publish or a
/// review request is saved, and fails only when it comes due or is approved. opticli checks it from the database, before
/// anything is saved. A master branch that was published once and is offline now (unpublished, expired) passes.
/// </summary>
public static class MasterLanguageRule
{
    /// <summary>What the CMS's own refusal says to do, when opticli's check didn't catch it first.</summary>
    public const string Hint =
        "The CMS publishes a language branch only once its master language branch is published: publish the master branch first (opticli publish <ref>), or save this branch as a draft (leave out --publish).";

    /// <param name="id">The content.</param>
    /// <param name="what">The content as messages name it: <c>123 ('News')</c>.</param>
    /// <param name="language">The branch the operation publishes.</param>
    /// <param name="master">The content's master language, which isn't published yet.</param>
    /// <param name="deferred">The CMS saves the publish for later (scheduled, or sent for review), so it fails only then.</param>
    /// <returns>For a deferred publish: a warning.</returns>
    /// <exception cref="ContentValidationException">A publish now, which the CMS refuses.</exception>
    public static string Check(int id, string what, string language, string master, WriteOperation operation, bool deferred, bool dryRun)
    {
        if (deferred)
        {
            var then = operation.PublishAt is not null ? "this scheduled publish fails when it comes due" : "the version can't be published once it is approved";
            return $"The master language ('{master}') of {what} isn't published yet, and the CMS won't publish the '{language}' branch before it: {then}, unless the master branch is published first (opticli publish {id}).";
        }
        throw new ContentValidationException(
            $"{(dryRun ? "Dry run: the" : "The")} '{language}' branch of {what} can't be published before its master language ('{master}') is published; the CMS refuses that. Nothing was saved.",
            operation is PublishOperation
                ? $"Publish the master branch first (opticli publish {id}), then this branch."
                : $"Publish the master branch first (opticli publish {id}), or save this branch as a draft (leave out --publish).")
        {
            Details = Details,
        };
    }

    /// <summary><c>details</c> of a refusal: its reason, as the agent sends it when the CMS refuses the publish itself.</summary>
    public static AgentErrorDetails Details => new(null, null) { Reason = AgentErrorReasons.MasterNotPublished };
}
