#if CMS13
using EPiServer.Core;
using EPiServer.DataAccess;
using EPiServer.DependencyInjection;
using EPiServer.Validation;
using Microsoft.Extensions.DependencyInjection;
using OptiCli.Cms;
using OptiCli.Cms.Content;
using OptiCli.Protocol;

namespace OptiCli.Agent.Tests.Cms;

/// <summary>
/// CMS 13's own (internal) validation service still throws when a site validator returns null with a save context; the
/// fallback walks the validators the container has registered.
/// </summary>
public class ValidationFallbackTests
{
    [Fact]
    public void A_site_validator_that_returns_null_doesnt_hide_the_other_errors()
    {
        using var services = Services()
            .AddCmsValidator<ReturnsNull>()
            .AddCmsValidator<RequiresName>()
            .BuildServiceProvider();
        var call = new CmsCall(services, CancellationToken.None, CmsCaller.Developer);

        var content = new CmsCallTests.Unsecured(new ContentReference(5)) { Name = "" };

        // What the CMS itself does with it.
        var service = (IContextValidationService)services.GetRequiredService<IValidationService>();
        Assert.Throws<ArgumentNullException>(() => service.Validate(content, new ContentSaveValidationContext(content, SaveAction.Publish, true)).ToList());
        Assert.Equal([new ValidationIssue("Name", "A name is required.")], ValidationErrors.Validate(call, content, SaveAction.Publish));
    }

    [Fact]
    public void The_context_reaches_a_context_validator()
    {
        using var services = Services()
            .AddCmsValidator<ReturnsNull>()
            .AddCmsValidator<RefusesPublish>()
            .BuildServiceProvider();
        var call = new CmsCall(services, CancellationToken.None, CmsCaller.Developer);
        var content = new CmsCallTests.Unsecured(new ContentReference(5));

        Assert.Equal([new ValidationIssue(null, "Not now.")], ValidationErrors.Validate(call, content, SaveAction.Publish));
        Assert.Empty(ValidationErrors.Validate(call, content, SaveAction.Save));
    }

    /// <summary>The CMS's validation service; its options read the (empty) configuration.</summary>
    private static IServiceCollection Services() => new ServiceCollection()
        .AddSingleton<Microsoft.Extensions.Configuration.IConfiguration>(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build())
        .AddCmsValidation();

    private sealed class ReturnsNull : IValidate<IContent>
    {
        public IEnumerable<ValidationError> Validate(IContent instance) => null!;
    }

    private sealed class RequiresName : IValidate<IContent>
    {
        public IEnumerable<ValidationError> Validate(IContent instance) =>
            string.IsNullOrEmpty(instance.Name) ? [new ValidationError { PropertyName = "Name", ErrorMessage = "A name is required.", Severity = ValidationErrorSeverity.Error }] : [];
    }

    private sealed class RefusesPublish : IContextValidate<IContent, ContentSaveValidationContext>
    {
        public IEnumerable<ValidationError> Validate(IContent instance, ContentSaveValidationContext context) =>
            (context.SaveAction & SaveAction.ActionMask) == SaveAction.Publish && context.CurrentContent == instance
                ? [new ValidationError { ErrorMessage = "Not now.", Severity = ValidationErrorSeverity.Error }]
                : [];
    }
}
#endif
