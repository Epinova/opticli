using EPiServer.Validation;
#if CMS13
using System.Reflection;
using System.Runtime.ExceptionServices;
#else
using EPiServer.Validation.Internal;
#endif

namespace OptiCli.Cms.Compat;

/// <summary>One validator the CMS's validation service runs: the type it validates, and the call (which may return null).</summary>
/// <param name="Validate">The instance, and the validation context (null when the service was called without one).</param>
internal sealed record RegisteredValidator(Type TypeToValidate, Func<object, object?, IEnumerable<ValidationError>?> Validate);

/// <summary>The validators of the CMS's own validation service, for <see cref="Content.TolerantValidation"/>.</summary>
internal static class RegisteredValidators
{
    /// <summary>
    /// The validators the CMS's own <see cref="IValidationService"/> runs; null when the site replaced or decorated the
    /// service, whose own handling the fallback mustn't bypass.
    /// </summary>
    public static IReadOnlyList<RegisteredValidator>? Of(IValidationService validation, CmsCall call)
    {
#if CMS13
        // The service is internal on CMS 13. It runs every IValidate registered in the container, wrapped as below.
        if (validation.GetType().Assembly != typeof(IValidationService).Assembly)
        {
            return null;
        }
        return call.Service<IEnumerable<IValidate>>().Select(Wrap).OfType<RegisteredValidator>().ToList();
#else
        _ = call;
        return validation is ValidationService service
            ? service.RegisteredValidators.Select(v => new RegisteredValidator(v.TypeToValidate, (instance, context) => context is null ? v.Validate(instance) : v.Validate(instance, context))).ToList()
            : null;
#endif
    }

#if CMS13
    /// <summary>
    /// As the CMS wraps a validator: it validates the first type argument of its <see cref="IContextValidate{TInstance, TContext}"/>
    /// (called when the context fits) or <see cref="IValidate{T}"/>.
    /// </summary>
    private static RegisteredValidator? Wrap(IValidate validator)
    {
        var interfaces = validator.GetType().GetInterfaces().Where(i => i.IsGenericType).ToList();
        var contextual = interfaces.FirstOrDefault(i => i.GetGenericTypeDefinition() == typeof(IContextValidate<,>));
        var plain = interfaces.FirstOrDefault(i => i.GetGenericTypeDefinition() == typeof(IValidate<>));
        if ((contextual ?? plain)?.GetGenericArguments()[0] is not { } validated)
        {
            return null;
        }
        return new RegisteredValidator(validated, (instance, context) =>
        {
            if (contextual is not null && contextual.GetGenericArguments()[1].IsInstanceOfType(context))
            {
                return Invoke(contextual, validator, [instance, context]);
            }
            return plain is null ? null : Invoke(plain, validator, [instance]);
        });
    }

    private static IEnumerable<ValidationError>? Invoke(Type contract, IValidate validator, object?[] arguments)
    {
        try
        {
            return (IEnumerable<ValidationError>?)contract.GetMethod(nameof(IValidate<object>.Validate))!.Invoke(validator, arguments);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Throw(ex.InnerException);
            throw;
        }
    }
#endif
}
