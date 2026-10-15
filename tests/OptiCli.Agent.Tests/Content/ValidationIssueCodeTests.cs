using EPiServer.Validation;
using OptiCli.Cms.Content;
using OptiCli.Protocol;

namespace OptiCli.Agent.Tests.Content;

/// <summary>
/// CMS 13's refusal of a reference to content that doesn't exist is marked, so a plan's dry run can tell it from other
/// errors: also for a value inside a block, where the CMS's validation service names the block's validator as the source.
/// </summary>
public class ValidationIssueCodeTests
{
    private sealed class BlockValidator;

    [Fact]
    public void An_unresolved_reference_inside_a_block_is_marked_like_one_in_the_content_itself()
    {
        var nested = new ValidationError
        {
            Source = new BlockValidator(),
            PropertyName = "SiteLogotype.Url",
            ErrorMessage = "Property 'Url' has an unresolved reference to content with ID 155cba62-ae10-561d-b0c6-831fbd91606f.",
            Severity = ValidationErrorSeverity.Error,
        };
        var other = new ValidationError { Source = new BlockValidator(), PropertyName = "SiteLogotype.Title", ErrorMessage = "Title is required.", Severity = ValidationErrorSeverity.Error };

        var issue = ValidationErrors.ToIssue(nested);

        Assert.Equal((ValidationIssueCodes.UnresolvedReference, "SiteLogotype.Url", "error"), (issue.Code, issue.Property, issue.Severity));
        Assert.Null(ValidationErrors.ToIssue(other).Code);
    }
}
