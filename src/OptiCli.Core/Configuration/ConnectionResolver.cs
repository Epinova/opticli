using System.Text.Json;
using OptiCli.Core.Discovery;
using OptiCli.Core.Errors;
using OptiCli.Core.Text;

namespace OptiCli.Core.Configuration;

/// <summary>
/// Decides which database opticli uses. For this run only: <c>--connection</c> / <c>OPTICLI_DB</c>, <c>--db</c>
/// or <c>--profile</c>. Otherwise the project's development database: the one the user chose (<c>opticli db
/// use</c>), else the user config's <c>connection</c>, else what the site's Development configuration uses
/// (launch profiles, user secrets, <c>appsettings.Development.json</c>, <c>appsettings.json</c>, first hit wins),
/// provided it is local.
/// </summary>
/// <remarks>
/// opticli never guesses a remote database: when the Development configuration points at one, several launch
/// profiles disagree, or only other environments' settings have a connection string, it fails with
/// <see cref="NeedsSelectionException"/> listing the choices, and the user picks once. Other environments'
/// <c>appsettings.{Environment}.json</c> are listed but only used when chosen.
/// </remarks>
public static class ConnectionResolver
{
    private const string LaunchSettingsPath = "Properties/launchSettings.json";

    /// <summary>What to run once the user has chosen.</summary>
    public const string UseCommand = "opticli db use <id>";

    private const string SelectionHint =
        "Ask the user which of these is their development database (show server, database and from), then run `"
        + UseCommand + "` with its id. Don't choose for them. A person at a terminal can run `opticli db use` to pick from a list.";

    private sealed record Step(IReadOnlyList<ConnectionCandidate> Candidates);

    private sealed record Profiles(Step Step, string File, bool Exists, IReadOnlyList<string> Names);

    private sealed record Default(ConnectionCandidate? Candidate, SelectionMode? Mode, OptiCliException? Failure);

    public static ConnectionResolution Resolve(ConnectionRequest request, ProjectInfo? project, OptiCliEnvironment environment)
    {
        var explicitStep = ExplicitStep(request, environment);
        var userConfig = new Step([]);
        var chain = new List<Step>();
        var others = new Step([]);
        SavedDatabase? saved = null;
        Profiles? profiles = null;

        if (project is not null)
        {
            (userConfig, saved) = UserConfigStep(project, environment);
            profiles = LaunchProfileStep(request, project);
            // ASP.NET Core's precedence: a launch profile's environment variables override user secrets.
            chain.Add(profiles.Step);
            chain.Add(UserSecretsStep(request, project, environment));
            chain.Add(AppSettingsStep(request, project, "appsettings.Development.json", ConnectionSource.AppSettingsDevelopment));
            chain.Add(AppSettingsStep(request, project, "appsettings.json", ConnectionSource.AppSettings));
            others = OtherEnvironmentsStep(request, project);
        }

        foreach (var candidate in others.Candidates.Where(c => c.Status == CandidateStatus.Shadowed))
        {
            candidate.Status = CandidateStatus.Available;
        }
        var candidates = new[] { explicitStep, userConfig }.Concat(chain).Append(others).SelectMany(s => s.Candidates).ToList();
        var projectDirectory = project?.Directory;
        var development = FindDevelopment(request, saved, userConfig, chain, candidates, projectDirectory);

        ConnectionCandidate? chosen = null;
        SelectionMode? mode = SelectionMode.Explicit;
        OptiCliException? failure = null;
        var pickers = (explicitStep.Candidates.Count > 0 ? 1 : 0) + (request.Database is null ? 0 : 1) + (request.Profile is null ? 0 : 1);

        if (pickers > 1)
        {
            failure = new UsageException("--connection (or OPTICLI_DB), --db and --profile each pick the database; use only one.");
        }
        else if (explicitStep.Candidates.Count > 0)
        {
            (chosen, failure) = PickExplicit(explicitStep.Candidates[0]);
        }
        else if (request.Database is { } wanted)
        {
            (chosen, failure) = PickDatabase(candidates, wanted, projectDirectory, development.Candidate);
        }
        else if (request.Profile is { } profile)
        {
            (chosen, failure) = PickProfile(profiles, request, profile);
        }
        else
        {
            (chosen, mode, failure) = (development.Candidate, development.Mode, development.Failure);
            failure ??= chosen is null ? NothingFound(request, candidates, project) : null;
        }

        if (chosen is not null)
        {
            chosen.Status = CandidateStatus.Chosen;
        }
        return new ConnectionResolution(
            request.Name,
            projectDirectory,
            candidates,
            chosen,
            chosen is null ? null : mode,
            development.Candidate,
            development.Mode,
            saved,
            chosen is null ? failure : null);
    }

    /// <summary>The project's development database, or why it isn't known yet.</summary>
    private static Default FindDevelopment(
        ConnectionRequest request,
        SavedDatabase? saved,
        Step userConfig,
        IReadOnlyList<Step> chain,
        IReadOnlyList<ConnectionCandidate> candidates,
        string? projectDirectory)
    {
        if (saved is not null)
        {
            if (candidates.FirstOrDefault(c => c.IsSelectable && string.Equals(c.Id, saved.Id, StringComparison.OrdinalIgnoreCase)) is { } match)
            {
                return new(match, SelectionMode.Saved, null);
            }
            return new(null, null, NeedsSelection(
                $"The development database chosen for this project ('{saved.Database}' on '{saved.Server}', from "
                    + $"{DatabaseChoice.Describe(saved.Source, saved.Location, saved.Profile, projectDirectory)}) is no longer in its "
                    + "configuration, or that setting now points at another database. It has to be chosen again.",
                candidates, projectDirectory));
        }

        if (userConfig.Candidates.FirstOrDefault(c => c.IsUsable) is { } configured)
        {
            return new(configured, SelectionMode.Configured, null);
        }

        foreach (var step in chain)
        {
            if (Ambiguity(step, request) is { } ambiguous)
            {
                return new(null, null, NeedsSelection(ambiguous, candidates, projectDirectory));
            }
            if (step.Candidates.FirstOrDefault(c => c.IsUsable) is { } first)
            {
                return first.IsLocal == true
                    ? new(first, SelectionMode.Automatic, null)
                    : new(null, null, NeedsSelection(
                        $"The site's Development configuration uses the remote database '{first.Database}' on '{first.Server}' "
                            + $"(from {DatabaseChoice.Describe(first.Source, first.Location, first.Profile, projectDirectory)}). opticli uses a remote "
                            + "database only once the user has confirmed it is this project's development database.",
                        candidates, projectDirectory));
            }
        }

        if (candidates.Any(c => c.IsSelectable))
        {
            return new(null, null, NeedsSelection(
                $"The site's Development configuration has no connection string '{request.Name}', but other settings files do. "
                    + "Choose which one, if any, is this project's development database.",
                candidates, projectDirectory));
        }
        return new(null, null, null);
    }

    private static NeedsSelectionException NeedsSelection(string message, IReadOnlyList<ConnectionCandidate> candidates, string? projectDirectory) =>
        new(message, SelectionHint) { Details = new SelectionDetails(DatabaseChoice.List(candidates, projectDirectory, null), UseCommand) };

    /// <summary>The first of <c>--connection</c> / <c>OPTICLI_DB</c> decides: if it is unusable, stop rather than fall back.</summary>
    private static (ConnectionCandidate?, OptiCliException?) PickExplicit(ConnectionCandidate candidate) =>
        candidate.IsUsable
            ? (candidate, null)
            : (null, new RefusedException($"Refusing the connection string from {candidate.Location}: {candidate.Reason ?? "it is empty."}"));

    private static (ConnectionCandidate?, OptiCliException?) PickDatabase(
        IReadOnlyList<ConnectionCandidate> candidates, string wanted, string? projectDirectory, ConnectionCandidate? development)
    {
        var selectable = candidates.Where(c => c.IsSelectable).ToList();
        if (selectable.FirstOrDefault(c => string.Equals(c.Id, wanted, StringComparison.OrdinalIgnoreCase)) is { } byId)
        {
            return (byId, null);
        }

        var byName = selectable.Where(c => string.Equals(c.Database, wanted, StringComparison.OrdinalIgnoreCase)).ToList();
        if (byName.Count > 0 && byName.All(c => c.SameTarget(byName[0].Server, byName[0].Database)))
        {
            return (byName[0], null);
        }

        var choices = DatabaseChoice.List(candidates, projectDirectory, development);
        if (byName.Count > 1)
        {
            return (null, new UsageException(
                $"--db '{wanted}' matches databases on several servers: "
                    + string.Join("; ", choices.Where(c => string.Equals(c.Database, wanted, StringComparison.OrdinalIgnoreCase)).Select(c => $"{c.Id} ({c.Server}, from {c.From})")) + ".",
                "Pass the id instead.") { Details = new SelectionDetails(choices, null) });
        }
        return (null, new NotFoundException(
            $"--db '{wanted}' is neither the id nor the database name of one of this project's connection strings.",
            "`opticli db list` shows them.") { Details = new SelectionDetails(choices, null) });
    }

    private static (ConnectionCandidate?, OptiCliException?) PickProfile(Profiles? profiles, ConnectionRequest request, string wanted)
    {
        if (profiles is null || !profiles.Exists)
        {
            return (null, new NotFoundException($"--profile '{wanted}' given, but {profiles?.File ?? LaunchSettingsPath} does not exist."));
        }
        if (!profiles.Names.Any(n => string.Equals(n, wanted, StringComparison.OrdinalIgnoreCase)))
        {
            return (null, new NotFoundException(
                $"Launch profile '{wanted}' not found in {profiles.File}.",
                Suggestions.DidYouMean(wanted, profiles.Names) ?? $"Profiles: {string.Join(", ", profiles.Names)}."));
        }

        foreach (var other in profiles.Step.Candidates.Where(c => !IsProfile(c, wanted)))
        {
            other.Status = CandidateStatus.Unselected;
        }
        return profiles.Step.Candidates.FirstOrDefault(c => IsProfile(c, wanted)) switch
        {
            null => (null, new NotFoundException($"Launch profile '{wanted}' does not set ConnectionStrings:{request.Name}.")),
            { IsUsable: false } unusable => (null, new RefusedException($"Refusing the connection string from launch profile '{wanted}': {unusable.Reason ?? "it is empty."}")),
            var found => (found, null),
        };
    }

    private static Step ExplicitStep(ConnectionRequest request, OptiCliEnvironment environment)
    {
        var candidates = new List<ConnectionCandidate>();
        if (request.Explicit is not null)
        {
            candidates.Add(new ConnectionCandidate(ConnectionSource.Flag, "--connection", null, null, request.Explicit));
        }
        if (environment.Variable(ConnectionRequest.EnvironmentVariable) is { } fromEnvironment)
        {
            candidates.Add(new ConnectionCandidate(ConnectionSource.Environment, ConnectionRequest.EnvironmentVariable, null, null, fromEnvironment));
        }
        return new Step(candidates);
    }

    private static (Step, SavedDatabase?) UserConfigStep(ProjectInfo project, OptiCliEnvironment environment)
    {
        var file = environment.UserConfigFile;
        try
        {
            var settings = UserConfig.ForProject(file, project.Directory);
            var step = settings?.Connection is null
                ? new Step([])
                : new Step([new ConnectionCandidate(ConnectionSource.UserConfig, file, $"projects:{project.Directory}:connection", null, settings.Connection)]);
            return (step, settings?.Database);
        }
        catch (Exception ex) when (ex is UsageException or IOException or UnauthorizedAccessException)
        {
            return (new Step([ConnectionCandidate.Unreadable(ConnectionSource.UserConfig, file, $"Could not read: {ex.Message}")]), null);
        }
    }

    private static Step UserSecretsStep(ConnectionRequest request, ProjectInfo project, OptiCliEnvironment environment)
    {
        if (project.Project.UserSecretsId is not { } id)
        {
            return new Step([]);
        }
        var file = environment.UserSecretsFile(id);
        return FromFlatJson(file, ConnectionSource.UserSecrets, $"ConnectionStrings:{request.Name}");
    }

    private static Step AppSettingsStep(ConnectionRequest request, ProjectInfo project, string fileName, ConnectionSource source) =>
        FromFlatJson(Path.Combine(project.Directory, fileName), source, $"ConnectionStrings:{request.Name}");

    /// <summary><c>appsettings.{Environment}.json</c> for every environment but Development, by file name.</summary>
    private static Step OtherEnvironmentsStep(ConnectionRequest request, ProjectInfo project)
    {
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(project.Directory, "appsettings.*.json").ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new Step([]);
        }

        var candidates = files
            .Where(f => !string.Equals(Path.GetFileName(f), "appsettings.Development.json", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase)
            .SelectMany(f => FromFlatJson(f, ConnectionSource.AppSettingsEnvironment, $"ConnectionStrings:{request.Name}").Candidates)
            .ToList();
        return new Step(candidates);
    }

    private static Step FromFlatJson(string file, ConnectionSource source, string key)
    {
        if (!File.Exists(file))
        {
            return new Step([]);
        }
        try
        {
            using var document = JsonConfigFile.Parse(file);
            var values = JsonConfigFile.Flatten(document.RootElement);
            return values.TryGetValue(key, out var value)
                ? new Step([new ConnectionCandidate(source, file, key, null, value)])
                : new Step([]);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new Step([ConnectionCandidate.Unreadable(source, file, $"Could not read: {ex.Message}")]);
        }
    }

    private static Profiles LaunchProfileStep(ConnectionRequest request, ProjectInfo project)
    {
        var file = Path.Combine(project.Directory, LaunchSettingsPath);
        if (!File.Exists(file))
        {
            return new Profiles(new Step([]), file, false, []);
        }

        var profileNames = new List<string>();
        var candidates = new List<ConnectionCandidate>();
        try
        {
            using var document = JsonConfigFile.Parse(file);
            if (document.RootElement.TryGetProperty("profiles", out var profiles) && profiles.ValueKind == JsonValueKind.Object)
            {
                foreach (var profile in profiles.EnumerateObject())
                {
                    profileNames.Add(profile.Name);
                    if (FindConnectionVariable(profile.Value, request.Name) is { } variable)
                    {
                        candidates.Add(new ConnectionCandidate(ConnectionSource.LaunchProfile, file, variable.Key, profile.Name, variable.Value));
                    }
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new Profiles(new Step([ConnectionCandidate.Unreadable(ConnectionSource.LaunchProfile, file, $"Could not read: {ex.Message}")]), file, true, []);
        }
        return new Profiles(new Step(candidates), file, true, profileNames);
    }

    private static bool IsProfile(ConnectionCandidate candidate, string profile) =>
        string.Equals(candidate.Profile, profile, StringComparison.OrdinalIgnoreCase);

    private static KeyValuePair<string, string?>? FindConnectionVariable(JsonElement profile, string name)
    {
        if (!profile.TryGetProperty("environmentVariables", out var variables) || variables.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        foreach (var variable in variables.EnumerateObject())
        {
            if (string.Equals(variable.Name, $"ConnectionStrings:{name}", StringComparison.OrdinalIgnoreCase)
                || string.Equals(variable.Name, $"ConnectionStrings__{name}", StringComparison.OrdinalIgnoreCase))
            {
                return new(variable.Name, variable.Value.ValueKind == JsonValueKind.String ? variable.Value.GetString() : null);
            }
        }
        return null;
    }

    /// <summary>Several launch profiles pointing at different databases: ask, never guess.</summary>
    private static string? Ambiguity(Step step, ConnectionRequest request)
    {
        var usable = step.Candidates.Where(c => c.Source == ConnectionSource.LaunchProfile && c.IsUsable).ToList();
        var distinct = usable
            .Select(c => $"{c.Server}|{c.Database}".ToLowerInvariant())
            .Distinct()
            .Count();
        if (distinct <= 1)
        {
            return null;
        }

        foreach (var candidate in usable)
        {
            candidate.Status = CandidateStatus.Ambiguous;
        }
        return $"{usable.Count} launch profiles set ConnectionStrings:{request.Name} to different databases: "
            + string.Join("; ", usable.Select(c => $"'{c.Profile}' ({c.Server}/{c.Database})")) + ".";
    }

    private static OptiCliException NothingFound(ConnectionRequest request, IReadOnlyList<ConnectionCandidate> candidates, ProjectInfo? project)
    {
        var skipped = candidates.Where(c => c.Status == CandidateStatus.Invalid).ToList();
        if (skipped.Count > 0)
        {
            return new RefusedException(
                $"Every connection string found for '{request.Name}' was unusable: "
                    + string.Join("; ", skipped.Select(c => $"{c.Location}{(c.Profile is null ? "" : $" [{c.Profile}]")}: {c.Reason}")),
                "Fix the site's user secrets, or pass --connection \"Server=localhost;Database=...;...\".");
        }

        var where = project is null
            ? "no CMS project was found and neither --connection nor OPTICLI_DB is set"
            : "checked --connection, OPTICLI_DB, the opticli user config, launch profiles, user secrets and appsettings";
        return new NotFoundException(
            $"No connection string '{request.Name}' found ({where}).",
            $"Pass --connection \"Server=localhost;Database=...;...\", or set it in user secrets: dotnet user-secrets set \"ConnectionStrings:{request.Name}\" \"...\"");
    }
}

/// <summary><c>error.details</c> of a <c>needs_selection</c> error (and of a <c>--db</c> that matched nothing).</summary>
/// <param name="Command">What to run once the user has chosen.</param>
public sealed record SelectionDetails(IReadOnlyList<DatabaseChoice> Choices, string? Command);
