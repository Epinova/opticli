using System.Globalization;
using System.Reflection;
using EPiServer.DataAnnotations;
using EPiServer.Validation;
using OptiCli.Cms.Compat;

namespace OptiCli.Cms.Content;

/// <summary>
/// The CMS's recursive validation walk (<c>ValidationService.ValidateRecursively</c>), minus its one
/// fragility: a site validator that returns null instead of an empty list makes the CMS's own loop
/// throw, which would turn every valid save into an internal error. CMS 13 still has it: with a save context, a
/// validator's null goes to <c>ToList()</c>.
/// </summary>
/// <remarks>
/// Only used as a fallback, because it bypasses any post-processing a site's own service subclass adds.
/// </remarks>
internal static class TolerantValidation
{
    private static readonly HashSet<Type> Leaves =
    [
        typeof(string), typeof(DateTime), typeof(DateTimeOffset), typeof(TimeSpan), typeof(Guid), typeof(decimal), typeof(CultureInfo),
    ];

    public static List<ValidationError> Validate(IReadOnlyList<RegisteredValidator> validators, object instance, object? context)
    {
        var errors = new List<ValidationError>();
        Walk(validators, instance, context, new HashSet<object>(ReferenceEqualityComparer.Instance), "", errors);
        return errors
            .GroupBy(e => (e.PropertyName, e.ErrorMessage))
            .Select(g => g.First())
            .ToList();
    }

    private static void Walk(IReadOnlyList<RegisteredValidator> validators, object instance, object? context, HashSet<object> visited, string prefix, List<ValidationError> errors)
    {
        if (!visited.Add(instance))
        {
            return;
        }

        foreach (var validator in validators.Where(v => v.TypeToValidate.IsInstanceOfType(instance)))
        {
            var found = validator.Validate(instance, context);
            foreach (var error in found ?? [])
            {
                error.PropertyName = prefix + error.PropertyName;
                errors.Add(error);
            }
        }

        var type = instance.GetType();
        if (type.IsPrimitive || type.IsEnum || Leaves.Contains(type) || instance is Type)
        {
            return;
        }
        foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (!property.CanRead || !property.CanWrite || property.GetIndexParameters().Length > 0 || property.IsDefined(typeof(IgnoreAttribute), inherit: true))
            {
                continue;
            }
            object? value;
            try
            {
                value = property.GetValue(instance);
            }
            catch (TargetInvocationException)
            {
                continue;
            }
            if (value is not null)
            {
                Walk(validators, value, context, visited, $"{prefix}{property.Name}.", errors);
            }
        }
    }
}
