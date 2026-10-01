using OptiCli.Core.Configuration;
using OptiCli.Core.Errors;
using OptiCli.Core.Output;

namespace OptiCli.Core.Tests.Configuration;

public class ConnectionResolverTests : IDisposable
{
    private const string SecretsDb = "Server=localhost;Database=FromSecrets;User Id=app;Password=hunter2-one";
    private const string ProfileDb = "Server=(localdb)\\\\MSSQLLocalDB;Database=FromProfile;Integrated Security=True";
    private const string DevDb = "Server=.;Database=FromDevSettings;Integrated Security=True";
    private const string BaseDb = "Server=127.0.0.1;Database=FromAppSettings;Integrated Security=True";
    private const string RemoteDb = "Server=tcp:sql.example.com,1433;Database=Shared;User Id=app;Password=hunter2-two";

    private readonly SiteFixture _site = new();

    public void Dispose() => _site.Dispose();

    [Fact]
    public void Explicit_flag_wins_over_everything()
    {
        WriteAllSources();

        var result = Resolve(new ConnectionRequest(Explicit: "Server=localhost;Database=FromFlag"), new() { ["OPTICLI_DB"] = "Server=localhost;Database=FromEnv" });

        AssertChosen(result, ConnectionSource.Flag, "FromFlag");
        Assert.All(result.Candidates.Where(c => c != result.Chosen && c.IsLocal == true), c => Assert.Equal(CandidateStatus.Shadowed, c.Status));
    }

    [Fact]
    public void Environment_variable_is_used_when_no_flag()
    {
        WriteAllSources();

        var result = Resolve(new ConnectionRequest(), new() { ["OPTICLI_DB"] = "Server=localhost;Database=FromEnv" });

        AssertChosen(result, ConnectionSource.OptiCliDb, "FromEnv");
    }

    [Fact]
    public void Remote_explicit_string_is_used_for_the_run_with_a_warning()
    {
        WriteAllSources();

        var result = Resolve(new ConnectionRequest(Explicit: RemoteDb));

        AssertChosen(result, ConnectionSource.Flag, "Shared");
        Assert.Equal(SelectionMode.Explicit, result.Mode);
        Assert.False(result.Require().IsLocal);
        Assert.False(result.IsDevelopment);
        Assert.Contains("not this project's development database", Assert.Single(result.Warnings()), StringComparison.Ordinal);
    }

    [Fact]
    public void Remote_environment_variable_is_used_with_a_warning()
    {
        WriteAllSources();

        var result = Resolve(new ConnectionRequest(), new() { ["OPTICLI_DB"] = RemoteDb });

        AssertChosen(result, ConnectionSource.OptiCliDb, "Shared");
        Assert.Single(result.Warnings());
    }

    [Fact]
    public void Invalid_explicit_string_is_refused_without_falling_back()
    {
        WriteAllSources();

        var result = Resolve(new ConnectionRequest(Explicit: "not a connection string"));

        Assert.Null(result.Chosen);
        Assert.IsType<RefusedException>(result.Failure);
    }

    [Fact]
    public void Local_development_default_is_used_without_asking_and_without_warnings()
    {
        WriteAllSources();

        var result = Resolve(new ConnectionRequest());

        Assert.Equal(SelectionMode.Automatic, result.Mode);
        Assert.Same(result.Chosen, result.Development);
        Assert.True(result.IsDevelopment);
        Assert.Empty(result.Warnings());
    }

    [Fact]
    public void User_config_wins_over_user_secrets()
    {
        WriteAllSources();
        _site.WriteUserConfig($$"""
            {
              // Comments are allowed.
              "projects": {
                "{{_site.ProjectPath.Replace("\\", "\\\\")}}/": { "connection": "Server=localhost;Database=FromUserConfig", "port": 5199 }
              }
            }
            """);

        var result = Resolve(new ConnectionRequest());

        AssertChosen(result, ConnectionSource.UserConfig, "FromUserConfig");
    }

    [Fact]
    public void Launch_profiles_win_over_user_secrets_and_appsettings()
    {
        WriteAllSources();

        var result = Resolve(new ConnectionRequest());

        AssertChosen(result, ConnectionSource.LaunchProfile, "FromProfile");
        Assert.Contains(result.Candidates, c => c.Source == ConnectionSource.UserSecrets && c.Status == CandidateStatus.Shadowed);
        Assert.Contains(result.Candidates, c => c.Source == ConnectionSource.AppSettings && c.Status == CandidateStatus.Shadowed);
    }

    [Fact]
    public void Without_a_launch_profile_user_secrets_win_over_appsettings()
    {
        WriteAllSources();
        WriteLaunchSettings();

        AssertChosen(Resolve(new ConnectionRequest()), ConnectionSource.UserSecrets, "FromSecrets");
    }

    [Fact]
    public void User_secrets_may_be_nested_json_with_bom_and_comments()
    {
        _site.WriteSecrets("""
            {
              /* nested form, as written by hand */
              "ConnectionStrings": { "EPiServerDB": "Server=localhost;Database=Nested", },
            }
            """, withBom: true);

        var result = Resolve(new ConnectionRequest());

        AssertChosen(result, ConnectionSource.UserSecrets, "Nested");
    }

    [Fact]
    public void Launch_profile_accepts_double_underscore_keys()
    {
        WriteLaunchSettings(("Web", "ConnectionStrings__EPiServerDB", ProfileDb));

        var result = Resolve(new ConnectionRequest());

        AssertChosen(result, ConnectionSource.LaunchProfile, "FromProfile");
        Assert.Equal("Web", result.Chosen!.Profile);
        Assert.Equal("ConnectionStrings__EPiServerDB", result.Chosen.Key);
    }

    [Fact]
    public void An_exported_connection_string_wins_over_user_secrets_and_appsettings()
    {
        WriteAllSources();
        WriteLaunchSettings();

        var result = Resolve(new ConnectionRequest(), new() { ["ConnectionStrings__EPiServerDB"] = "Server=localhost;Database=FromShell" });

        AssertChosen(result, ConnectionSource.Environment, "FromShell");
        Assert.Equal(SelectionMode.Automatic, result.Mode);
        Assert.Equal("ConnectionStrings__EPiServerDB", result.Chosen!.Location);
        Assert.Equal("environment (ConnectionStrings__EPiServerDB)", result.Describe(result.Chosen));
        Assert.Contains(result.Candidates, c => c.Source == ConnectionSource.UserSecrets && c.Status == CandidateStatus.Shadowed);
        Assert.Contains("\"source\":\"environment\"", JsonOutput.Serialize(result.Chosen), StringComparison.Ordinal);
    }

    [Fact]
    public void A_launch_profile_that_sets_the_variable_wins_over_the_shell()
    {
        WriteAllSources();

        var result = Resolve(new ConnectionRequest(), new() { ["ConnectionStrings__EPiServerDB"] = "Server=localhost;Database=FromShell" });

        AssertChosen(result, ConnectionSource.LaunchProfile, "FromProfile");
        Assert.Contains(result.Candidates, c => c.Source == ConnectionSource.Environment && c.Status == CandidateStatus.Shadowed);
    }

    [Fact]
    public void The_colon_form_any_case_and_the_connection_name_count()
    {
        var colon = Resolve(new ConnectionRequest(), new() { ["ConnectionStrings:EPiServerDB"] = "Server=localhost;Database=Colon" });
        var lower = Resolve(new ConnectionRequest(), new() { ["connectionstrings__episerverdb"] = "Server=localhost;Database=Lower" });
        var named = Resolve(new ConnectionRequest(Name: "CmsDb"), new()
        {
            ["ConnectionStrings__EPiServerDB"] = "Server=localhost;Database=Other",
            ["ConnectionStrings__CmsDb"] = "Server=localhost;Database=Named",
        });

        AssertChosen(colon, ConnectionSource.Environment, "Colon");
        AssertChosen(lower, ConnectionSource.Environment, "Lower");
        AssertChosen(named, ConnectionSource.Environment, "Named");
        Assert.Single(named.Candidates);
    }

    [Fact]
    public void A_remote_exported_connection_string_needs_selection_and_can_be_chosen()
    {
        WriteAllSources();
        WriteLaunchSettings();
        var variables = new Dictionary<string, string> { ["ConnectionStrings__EPiServerDB"] = RemoteDb };

        var result = Resolve(new ConnectionRequest(), variables);

        var failure = Assert.IsType<NeedsSelectionException>(result.Failure);
        Assert.Contains("remote database 'Shared'", failure.Message, StringComparison.Ordinal);
        var details = Assert.IsType<SelectionDetails>(failure.Details);
        Assert.Equal("environment (ConnectionStrings__EPiServerDB)", details.Choices[0].From);

        Save(result.Candidates.Single(c => c.Source == ConnectionSource.Environment));
        var saved = Resolve(new ConnectionRequest(), variables);

        AssertChosen(saved, ConnectionSource.Environment, "Shared");
        Assert.Equal(SelectionMode.Saved, saved.Mode);
        Assert.IsType<NeedsSelectionException>(Resolve(new ConnectionRequest()).Failure);
    }

    [Fact]
    public void Exported_spellings_pointing_at_different_databases_need_selection()
    {
        var result = Resolve(new ConnectionRequest(), new()
        {
            ["ConnectionStrings__EPiServerDB"] = "Server=localhost;Database=One",
            ["ConnectionStrings:EPiServerDB"] = "Server=localhost;Database=Two",
        });

        var failure = Assert.IsType<NeedsSelectionException>(result.Failure);
        Assert.Contains("2 environment variables", failure.Message, StringComparison.Ordinal);
        Assert.Equal(2, result.Candidates.Count(c => c.Status == CandidateStatus.Ambiguous));
    }

    [Fact]
    public void Opticli_db_is_its_own_source_and_not_selectable()
    {
        var result = Resolve(new ConnectionRequest(), new() { ["OPTICLI_DB"] = "Server=localhost;Database=FromEnv" });

        AssertChosen(result, ConnectionSource.OptiCliDb, "FromEnv");
        Assert.Empty(result.Selectable);
    }

    [Fact]
    public void User_secrets_id_from_directory_build_props_is_used()
    {
        _site.WriteProjectFile("Web.csproj", SiteFixture.Csproj(userSecretsId: null));
        _site.Write("repo/Directory.Build.props", $"""
            <Project>
              <PropertyGroup><UserSecretsId>{SiteFixture.SecretsId}</UserSecretsId></PropertyGroup>
            </Project>
            """);
        _site.WriteSecrets($$"""{ "ConnectionStrings:EPiServerDB": "{{SecretsDb}}" }""");
        _site.WriteProjectFile("appsettings.json", $$"""{ "ConnectionStrings": { "EPiServerDB": "{{BaseDb}}" } }""");

        AssertChosen(Resolve(new ConnectionRequest()), ConnectionSource.UserSecrets, "FromSecrets");
    }

    [Fact]
    public void An_unexpandable_user_secrets_id_is_not_read()
    {
        _site.WriteProjectFile("Web.csproj", SiteFixture.Csproj(userSecretsId: "$(SolutionName)-secrets"));
        _site.WriteProjectFile("appsettings.json", $$"""{ "ConnectionStrings": { "EPiServerDB": "{{BaseDb}}" } }""");

        var result = Resolve(new ConnectionRequest());

        AssertChosen(result, ConnectionSource.AppSettings, "FromAppSettings");
        Assert.DoesNotContain(result.Candidates, c => c.Source == ConnectionSource.UserSecrets);
    }

    [Fact]
    public void Remote_development_default_needs_selection()
    {
        _site.WriteProjectFile("appsettings.Development.json", $$"""{ "ConnectionStrings": { "EPiServerDB": "{{RemoteDb}}" } }""");
        _site.WriteProjectFile("appsettings.json", $$"""{ "ConnectionStrings": { "EPiServerDB": "{{BaseDb}}" } }""");

        var result = Resolve(new ConnectionRequest());

        Assert.Null(result.Chosen);
        var failure = Assert.IsType<NeedsSelectionException>(result.Failure);
        Assert.Contains("remote database 'Shared'", failure.Message, StringComparison.Ordinal);
        Assert.Contains("db use", failure.Hint, StringComparison.Ordinal);
        var details = Assert.IsType<SelectionDetails>(failure.Details);
        Assert.Equal([1, 2], details.Choices.Select(c => c.N));
        Assert.Equal(["Shared", "FromAppSettings"], details.Choices.Select(c => c.Database));
        Assert.Equal("appsettings.Development.json", details.Choices[0].From);
        Assert.False(details.Choices[0].Local);
    }

    [Fact]
    public void Several_databases_in_launch_profiles_need_selection()
    {
        WriteLaunchSettings(("One", "ConnectionStrings:EPiServerDB", ProfileDb), ("Two", "ConnectionStrings:EPiServerDB", "Server=localhost;Database=Other"));
        _site.WriteProjectFile("appsettings.json", $$"""{ "ConnectionStrings": { "EPiServerDB": "{{BaseDb}}" } }""");

        var result = Resolve(new ConnectionRequest());

        Assert.Null(result.Chosen);
        Assert.IsType<NeedsSelectionException>(result.Failure);
        Assert.Equal(2, result.Candidates.Count(c => c.Status == CandidateStatus.Ambiguous));
    }

    [Fact]
    public void Launch_profiles_pointing_at_the_same_database_are_not_ambiguous()
    {
        WriteLaunchSettings(("One", "ConnectionStrings:EPiServerDB", ProfileDb), ("Two", "ConnectionStrings__EPiServerDB", ProfileDb));

        var result = Resolve(new ConnectionRequest());

        AssertChosen(result, ConnectionSource.LaunchProfile, "FromProfile");
        Assert.Equal("One", result.Chosen!.Profile);
    }

    [Fact]
    public void Profile_flag_selects_a_profile_and_beats_user_secrets()
    {
        WriteAllSources();
        WriteLaunchSettings(("One", "ConnectionStrings:EPiServerDB", ProfileDb), ("Two", "ConnectionStrings:EPiServerDB", "Server=localhost;Database=Second"));

        var result = Resolve(new ConnectionRequest(Profile: "two"));

        AssertChosen(result, ConnectionSource.LaunchProfile, "Second");
        Assert.Equal(CandidateStatus.Unselected, Assert.Single(result.Candidates, c => c.Profile == "One").Status);
        Assert.Equal(CandidateStatus.Shadowed, Assert.Single(result.Candidates, c => c.Source == ConnectionSource.UserSecrets).Status);
    }

    [Fact]
    public void Unknown_profile_is_not_found_with_suggestion()
    {
        WriteLaunchSettings(("Development", "ConnectionStrings:EPiServerDB", ProfileDb));

        var result = Resolve(new ConnectionRequest(Profile: "Developmnet"));

        var failure = Assert.IsType<NotFoundException>(result.Failure);
        Assert.Equal("Did you mean Development?", failure.Hint);
    }

    [Fact]
    public void Remote_selected_profile_is_used_with_a_warning()
    {
        WriteAllSources();
        WriteLaunchSettings(("Shared", "ConnectionStrings:EPiServerDB", RemoteDb));

        var result = Resolve(new ConnectionRequest(Profile: "Shared"));

        AssertChosen(result, ConnectionSource.LaunchProfile, "Shared");
        Assert.Single(result.Warnings());
    }

    [Fact]
    public void Development_appsettings_win_over_base_appsettings_and_empty_values_are_skipped()
    {
        _site.WriteProjectFile("appsettings.Development.json", $$"""{ "ConnectionStrings": { "EPiServerDB": "{{DevDb}}" } }""");
        _site.WriteProjectFile("appsettings.json", $$"""{ "ConnectionStrings": { "EPiServerDB": "{{BaseDb}}" } }""");
        _site.WriteProjectFile("appsettings.Production.json", """{ "ConnectionStrings": { "EPiServerDB": "Server=localhost;Database=Never" } }""");
        _site.WriteSecrets("""{ "ConnectionStrings:EPiServerDB": "" }""");

        var result = Resolve(new ConnectionRequest());

        AssertChosen(result, ConnectionSource.AppSettingsDevelopment, "FromDevSettings");
        Assert.Equal(CandidateStatus.Empty, Assert.Single(result.Candidates, c => c.Source == ConnectionSource.UserSecrets).Status);
        var production = Assert.Single(result.Candidates, c => c.Location.EndsWith("appsettings.Production.json", StringComparison.Ordinal));
        Assert.Equal(CandidateStatus.Available, production.Status);
        Assert.Equal(ConnectionSource.AppSettingsEnvironment, production.Source);
    }

    [Fact]
    public void Connection_name_is_configurable()
    {
        _site.WriteSecrets("""{ "ConnectionStrings:EPiServerDB": "Server=localhost;Database=Default", "ConnectionStrings:Other": "Server=localhost;Database=Named" }""");

        var result = Resolve(new ConnectionRequest(Name: "Other"));

        AssertChosen(result, ConnectionSource.UserSecrets, "Named");
    }

    [Fact]
    public void Nothing_found_is_not_found()
    {
        var result = Resolve(new ConnectionRequest());

        Assert.Null(result.Chosen);
        Assert.IsType<NotFoundException>(result.Failure);
    }

    [Fact]
    public void Only_remote_strings_found_needs_selection()
    {
        _site.WriteProjectFile("appsettings.json", $$"""{ "ConnectionStrings": { "EPiServerDB": "{{RemoteDb}}" } }""");

        var result = Resolve(new ConnectionRequest());

        Assert.IsType<NeedsSelectionException>(result.Failure);
    }

    [Fact]
    public void Only_other_environments_having_a_connection_string_needs_selection()
    {
        _site.WriteProjectFile("appsettings.Dev.json", $$"""{ "ConnectionStrings": { "EPiServerDB": "{{RemoteDb}}" } }""");
        _site.WriteProjectFile("appsettings.Production.json", """{ "ConnectionStrings": { "EPiServerDB": "Server=tcp:prod.example.com;Database=Live" } }""");

        var result = Resolve(new ConnectionRequest());

        var details = Assert.IsType<SelectionDetails>(Assert.IsType<NeedsSelectionException>(result.Failure).Details);
        Assert.Equal(["appsettings.Dev.json", "appsettings.Production.json"], details.Choices.Select(c => c.From));
    }

    [Fact]
    public void A_saved_remote_choice_is_the_development_database()
    {
        _site.WriteProjectFile("appsettings.Dev.json", $$"""{ "ConnectionStrings": { "EPiServerDB": "{{RemoteDb}}" } }""");
        _site.WriteProjectFile("appsettings.json", $$"""{ "ConnectionStrings": { "EPiServerDB": "{{BaseDb}}" } }""");
        Save(Resolve(new ConnectionRequest()).Candidates.Single(c => c.Database == "Shared"));

        var result = Resolve(new ConnectionRequest());

        AssertChosen(result, ConnectionSource.AppSettingsEnvironment, "Shared");
        Assert.Equal(SelectionMode.Saved, result.Mode);
        Assert.True(result.IsDevelopment);
        Assert.Empty(result.Warnings());
        Assert.Equal("Shared", result.Saved!.Database);
    }

    [Fact]
    public void A_saved_choice_beats_the_local_default()
    {
        WriteAllSources();
        Save(Resolve(new ConnectionRequest()).Candidates.Single(c => c.Source == ConnectionSource.AppSettings));

        AssertChosen(Resolve(new ConnectionRequest()), ConnectionSource.AppSettings, "FromAppSettings");
    }

    [Fact]
    public void A_saved_choice_whose_setting_changed_needs_selection_again()
    {
        _site.WriteProjectFile("appsettings.Development.json", $$"""{ "ConnectionStrings": { "EPiServerDB": "{{RemoteDb}}" } }""");
        Save(Assert.Single(Resolve(new ConnectionRequest()).Selectable));
        _site.WriteProjectFile("appsettings.Development.json", """{ "ConnectionStrings": { "EPiServerDB": "Server=tcp:other.example.com;Database=Shared" } }""");

        var result = Resolve(new ConnectionRequest());

        var failure = Assert.IsType<NeedsSelectionException>(result.Failure);
        Assert.Contains("no longer", failure.Message, StringComparison.Ordinal);
        Assert.NotNull(result.Saved);
    }

    [Fact]
    public void Db_option_picks_another_candidate_by_id_or_database_name_with_a_warning_when_remote()
    {
        WriteAllSources();
        _site.WriteProjectFile("appsettings.Test.json", $$"""{ "ConnectionStrings": { "EPiServerDB": "{{RemoteDb}}" } }""");
        var test = Resolve(new ConnectionRequest()).Candidates.Single(c => c.Database == "Shared");

        var byId = Resolve(new ConnectionRequest(Database: test.Id!.ToUpperInvariant()));
        var byName = Resolve(new ConnectionRequest(Database: "shared"));

        AssertChosen(byId, ConnectionSource.AppSettingsEnvironment, "Shared");
        AssertChosen(byName, ConnectionSource.AppSettingsEnvironment, "Shared");
        Assert.Equal("FromProfile", byId.Development!.Database);
        Assert.Single(byId.Warnings());
        Assert.Empty(Resolve(new ConnectionRequest(Database: "FromAppSettings")).Warnings());
    }

    [Fact]
    public void Db_option_that_matches_nothing_lists_the_choices()
    {
        WriteAllSources();

        var failure = Assert.IsType<NotFoundException>(Resolve(new ConnectionRequest(Database: "nope")).Failure);

        Assert.Equal(4, Assert.IsType<SelectionDetails>(failure.Details).Choices.Count);
    }

    [Fact]
    public void Two_ways_of_picking_the_database_at_once_is_a_usage_error()
    {
        WriteAllSources();

        Assert.IsType<UsageException>(Resolve(new ConnectionRequest(Explicit: BaseDb, Database: "FromSecrets")).Failure);
        Assert.IsType<UsageException>(Resolve(new ConnectionRequest(Profile: "Web", Database: "FromSecrets")).Failure);
    }

    [Fact]
    public void Ids_are_stable_and_change_with_the_target()
    {
        WriteAllSources();
        var first = Resolve(new ConnectionRequest()).Selectable.Select(c => c.Id).ToList();
        var again = Resolve(new ConnectionRequest()).Selectable.Select(c => c.Id).ToList();
        _site.WriteProjectFile("appsettings.json", """{ "ConnectionStrings": { "EPiServerDB": "Server=127.0.0.1;Database=Changed" } }""");
        var changed = Resolve(new ConnectionRequest()).Selectable.Select(c => c.Id).ToList();

        Assert.Equal(first, again);
        Assert.Equal(first.Count, first.Distinct().Count());
        Assert.All(first, id => Assert.Matches("^[0-9a-f]{6}$", id!));
        Assert.Equal(first.Take(3), changed.Take(3));
        Assert.NotEqual(first[3], changed[3]);
    }

    [Fact]
    public void Saving_a_choice_keeps_the_rest_of_the_user_config()
    {
        WriteAllSources();
        _site.WriteUserConfig($$"""
            {
              "projects": {
                "{{_site.ProjectPath.Replace("\\", "\\\\")}}/": { "port": 5200 },
                "/somewhere/else": { "port": 5300 }
              },
              "other": true
            }
            """);
        var candidate = Resolve(new ConnectionRequest()).Candidates.Single(c => c.Source == ConnectionSource.AppSettings);

        Save(candidate);
        var settings = UserConfig.ForProject(_site.Environment().UserConfigFile, _site.ProjectPath);
        UserConfig.SaveDatabase(_site.Environment().UserConfigFile, _site.ProjectPath, null);
        var cleared = UserConfig.ForProject(_site.Environment().UserConfigFile, _site.ProjectPath);

        Assert.Equal(5200, settings!.Port);
        Assert.Equal(candidate.Id, settings.Database!.Id);
        Assert.Equal("FromAppSettings", settings.Database.Database);
        Assert.Equal(SavedDatabase.ViaCommand, settings.Database.ChosenVia);
        Assert.Null(cleared!.Database);
        Assert.Equal(5200, cleared.Port);
        Assert.Equal(5300, UserConfig.ForProject(_site.Environment().UserConfigFile, "/somewhere/else")!.Port);
    }

    [Fact]
    public void Unreadable_files_are_reported_as_invalid_and_skipped()
    {
        _site.WriteSecrets("{ not json");
        _site.WriteProjectFile("appsettings.json", $$"""{ "ConnectionStrings": { "EPiServerDB": "{{BaseDb}}" } }""");

        var result = Resolve(new ConnectionRequest());

        AssertChosen(result, ConnectionSource.AppSettings, "FromAppSettings");
        Assert.Equal(CandidateStatus.Invalid, Assert.Single(result.Candidates, c => c.Source == ConnectionSource.UserSecrets).Status);
    }

    [Fact]
    public void Works_without_a_project_when_explicit()
    {
        var result = ConnectionResolver.Resolve(new ConnectionRequest(Explicit: "Server=localhost;Database=Only"), null, _site.Environment());

        AssertChosen(result, ConnectionSource.Flag, "Only");
    }

    [Fact]
    public void Candidates_never_serialise_the_connection_string()
    {
        WriteAllSources();

        var json = JsonOutput.Serialize(Resolve(new ConnectionRequest(Explicit: RemoteDb)).Candidates);

        Assert.DoesNotContain("hunter2", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Password", json, StringComparison.OrdinalIgnoreCase);
    }

    private void Save(ConnectionCandidate candidate) =>
        UserConfig.SaveDatabase(_site.Environment().UserConfigFile, _site.ProjectPath, SavedDatabase.From(candidate, SavedDatabase.ViaCommand, DateTimeOffset.UtcNow));

    private ConnectionResolution Resolve(ConnectionRequest request, Dictionary<string, string>? variables = null) =>
        ConnectionResolver.Resolve(request, _site.Project(), _site.Environment(variables));

    private static void AssertChosen(ConnectionResolution result, ConnectionSource source, string database)
    {
        Assert.Null(result.Failure);
        Assert.NotNull(result.Chosen);
        Assert.Equal(source, result.Chosen.Source);
        Assert.Equal(CandidateStatus.Chosen, result.Chosen.Status);
        Assert.Equal(database, result.Chosen.Database);
        Assert.Equal(database, result.Require().Database);
        Assert.Single(result.Candidates, c => c.Status == CandidateStatus.Chosen);
    }

    private void WriteAllSources()
    {
        _site.WriteSecrets($$"""{ "ConnectionStrings:EPiServerDB": "{{SecretsDb}}" }""");
        WriteLaunchSettings(("Web", "ConnectionStrings:EPiServerDB", ProfileDb));
        _site.WriteProjectFile("appsettings.Development.json", $$"""{ "ConnectionStrings": { "EPiServerDB": "{{DevDb}}" } }""");
        _site.WriteProjectFile("appsettings.json", $$"""{ "ConnectionStrings": { "EPiServerDB": "{{BaseDb}}" } }""");
    }

    private void WriteLaunchSettings(params (string Profile, string Key, string Value)[] profiles)
    {
        var body = string.Join(",\n", profiles.Select(p => $$"""
                "{{p.Profile}}": {
                  "commandName": "Project",
                  // A comment, as real launchSettings files often have.
                  "environmentVariables": { "ASPNETCORE_ENVIRONMENT": "Development", "{{p.Key}}": "{{p.Value}}" }
                }
            """));
        _site.WriteProjectFile("Properties/launchSettings.json", $$"""{ "profiles": { {{body}} } }""", withBom: true);
    }
}
