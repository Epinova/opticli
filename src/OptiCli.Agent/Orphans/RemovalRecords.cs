using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using EPiServer.Data;
using OptiCli.Agent.Hosting;
using OptiCli.Agent.Safety;
using OptiCli.Protocol;

namespace OptiCli.Agent.Orphans;

/// <summary>
/// Where the agent writes what it is about to remove, before the CMS removes it: one JSON line per type or property
/// (<see cref="RemovalRecord"/>) in <see cref="OrphanRemoval.RecordFileName"/>, in opticli's state directory, which is never
/// rotated, and the same line in the site's output (<c>serve --logs</c>). Written from the site process, not the CLI,
/// so the record is there whatever happens to the CLI (a timeout, Ctrl+C) or to the answer.
/// </summary>
internal sealed class RemovalRecords(string path, string project, string database, TextWriter? echo)
{
    private const UnixFileMode UserOnlyFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode UserOnlyDirectory = UserOnlyFile | UnixFileMode.UserExecute;

    public string Path { get; } = path;

    public static RemovalRecords For(IServiceProvider services)
    {
        var used = DatabasePin.Resolve(services.GetRequiredService<IOptions<DataAccessOptions>>().Value);
        var verdict = ConnectionGuard.Check(used?.ConnectionString);
        return new RemovalRecords(
            DefaultPath(Environment.GetEnvironmentVariable),
            services.GetService<IWebHostEnvironment>()?.ContentRootPath ?? Environment.CurrentDirectory,
            $"{verdict.Server ?? "?"}/{verdict.Database ?? "?"}",
            Console.Out);
    }

    /// <summary>Overrides the record file's path; the edge-case test site sets it, so test runs stay out of the real one.</summary>
    public const string PathVariable = "OPTICLI_REMOVALS_FILE";

    /// <summary>
    /// The same state directory the CLI uses (<c>%LOCALAPPDATA%\opticli</c> on Windows, else
    /// <c>$XDG_STATE_HOME/opticli</c>, default <c>~/.local/state/opticli</c>): the site runs as the same user, with the
    /// environment <c>serve</c> gave it. <see cref="PathVariable"/> names another file.
    /// </summary>
    internal static string DefaultPath(Func<string, string?> variable)
    {
        static string? Set(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
        if (Set(variable(PathVariable)) is { } file)
        {
            return file;
        }
        var directory = Set(variable("LOCALAPPDATA")) is { } localAppData
            ? System.IO.Path.Combine(localAppData, "opticli")
            : System.IO.Path.Combine(Set(variable("XDG_STATE_HOME")) ?? System.IO.Path.Combine(Set(variable("HOME")) ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "state"), "opticli");
        return System.IO.Path.Combine(directory, OrphanRemoval.RecordFileName);
    }

    /// <summary>The record of a type about to be removed.</summary>
    public void Removing(RemovedContentType type) => Write(new RemovalRecord(DateTime.UtcNow, project, database, type, null), line => $"Removing the content type {type.Name} ({type.Guid}), recorded in {Path}: {line}");

    /// <summary>The record of a property about to be removed.</summary>
    public void Removing(RemovedProperty property) => Write(new RemovalRecord(DateTime.UtcNow, project, database, null, property), line => $"Removing the property {property.Type}.{property.Property.Name}, recorded in {Path}: {line}");

    /// <summary>A line saying the removal just recorded didn't happen, so the file stays true.</summary>
    public void Failed(string label, string error) =>
        Write(new RemovalRecord(DateTime.UtcNow, project, database, null, null) { Failed = $"{label}: {error}" }, _ => $"Not removed: {label} ({error}); recorded in {Path}.");

    /// <summary>Appends one line and flushes it to disk before returning; a failure here stops the removal.</summary>
    private void Write(RemovalRecord record, Func<string, string> echoLine)
    {
        var line = JsonSerializer.Serialize(record, AgentJson.Options);
        var directory = System.IO.Path.GetDirectoryName(Path)!;
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(directory);
        }
        else
        {
            Directory.CreateDirectory(directory, UserOnlyDirectory);
        }
        var options = new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.Write, Share = FileShare.Read };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UserOnlyFile;
        }
        using (var stream = new FileStream(Path, options))
        {
            stream.Seek(0, SeekOrigin.End);
            var bytes = System.Text.Encoding.UTF8.GetBytes(line + "\n");
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        echo?.WriteLine($"[opticli] {echoLine(line)}");
    }
}
