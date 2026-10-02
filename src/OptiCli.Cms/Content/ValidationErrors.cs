using EPiServer;
using EPiServer.Core;
using EPiServer.DataAccess;
using EPiServer.Validation;
using EPiServer.Validation.Internal;
using OptiCli.Protocol;
using DataAnnotationsValidationException = System.ComponentModel.DataAnnotations.ValidationException;

namespace OptiCli.Cms.Content;

/// <summary>Runs the CMS's own save validation and reports every issue, not just the first.</summary>
internal static class ValidationErrors
{
    /// <summary>
    /// The validators the CMS runs on save (required properties, [AllowedTypes] and other data
    /// annotations, custom IValidate implementations), with the save context when the service takes one.
    /// </summary>
    /// <remarks>
    /// Resolve <see cref="IValidationService"/>, not <see cref="IContextValidationService"/>: sites
    /// decorate or replace the former, and the CMS only registers its validators on whatever instance
    /// that resolves to. A site that replaces it leaves the context service registered but empty.
    /// </remarks>
    public static List<ValidationIssue> Validate(IValidationService validation, IContent content, SaveAction action)
    {
        var context = new ContentSaveValidationContext(action, newVersionRequired: true);
        List<ValidationError> errors;
        try
        {
            errors = (validation is IContextValidationService contextual ? contextual.Validate(content, context) : validation.Validate(content)).ToList();
        }
        catch (ArgumentNullException) when (validation is ValidationService service)
        {
            // A site validator returned null rather than an empty list; walk the validators ourselves.
            errors = TolerantValidation.Validate(service, content, validation is IContextValidationService ? context : null);
        }
        return errors.Where(e => e.Severity != ValidationErrorSeverity.None).Select(ToIssue).ToList();
    }

    public static bool HasErrors(IEnumerable<ValidationIssue> issues) => issues.Any(i => i.Severity == "error");

    /// <summary>
    /// Save can still reject what pre-validation passed (e.g. publish-only rules); the CMS throws the
    /// first message and puts per-property errors in <c>Data</c>.
    /// </summary>
    public static List<ValidationIssue> From(DataAnnotationsValidationException exception)
    {
        var issues = exception.Data.Values.OfType<ValidationError>().Select(ToIssue).ToList();
        return issues.Count > 0 ? issues : [new ValidationIssue(null, exception.Message)];
    }

    /// <summary>
    /// Whether a publish the CMS refused on save broke its "master language first" rule: a branch other than the master
    /// can't be published while the master branch has never been published. That rule is no validator, so a dry run
    /// can't see it (the CLI checks it from the database first), and "use dryRun" is no help.
    /// </summary>
    public static bool MasterNotPublished(IContent content, SaveAction action, IContentLoader loader) =>
        WriteFlow.Kind(action) == SaveAction.Publish
        && content is ILocalizable { Language: { } language, MasterLanguage: { } master } && !language.Equals(master)
        && !ContentReference.IsNullOrEmpty(content.ContentLink)
        && loader.TryGet<IContent>(content.ContentLink.ToReferenceWithoutVersion(), master, out var masterBranch)
        && masterBranch is IVersionable { IsPendingPublish: true };

    /// <summary>The refusal of <see cref="MasterNotPublished"/>, with the CMS's message.</summary>
    public static AgentException MasterFirst(DataAnnotationsValidationException exception) =>
        AgentException.Invalid(From(exception), "Publish the master language branch first, or save this branch without publishing it.", AgentErrorReasons.MasterNotPublished);

    private static ValidationIssue ToIssue(ValidationError error) => new(
        string.IsNullOrEmpty(error.PropertyName) ? null : error.PropertyName,
        error.ErrorMessage,
        error.Severity switch
        {
            ValidationErrorSeverity.Error => "error",
            ValidationErrorSeverity.Warning => "warning",
            _ => "info",
        });
}
