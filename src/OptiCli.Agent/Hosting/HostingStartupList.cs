namespace OptiCli.Agent.Hosting;

internal static class HostingStartupList
{
    /// <summary>
    /// Adds <paramref name="assemblyName"/> to a <c>;</c>-separated hosting startup list without
    /// dropping or duplicating entries: sites already use the variable (e.g. Razor runtime compilation).
    /// </summary>
    public static string Append(string? existing, string assemblyName)
    {
        var names = (existing ?? "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (!names.Contains(assemblyName, StringComparer.OrdinalIgnoreCase))
        {
            names.Add(assemblyName);
        }
        return string.Join(';', names);
    }
}
