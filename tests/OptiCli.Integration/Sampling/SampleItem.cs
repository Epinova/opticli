using OptiCli.Core.Cms;

namespace OptiCli.Integration.Sampling;

/// <summary>One thing to compare: a content item in one language branch, optionally at a specific version.</summary>
/// <param name="LanguageId"><c>tblLanguageBranch.pkID</c> of the branch.</param>
/// <param name="VersionId">A version to compare instead of the primary one (drafts).</param>
internal sealed record SampleItem(int ContentId, int TypeId, ContentKind Kind, int LanguageId, int? VersionId = null);
