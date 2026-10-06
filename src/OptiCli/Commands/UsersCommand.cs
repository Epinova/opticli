using System.CommandLine;
using System.Text;
using OptiCli.Cli;
using OptiCli.Core.Errors;
using OptiCli.Core.Serve;
using OptiCli.Core.Users;
using OptiCli.Core.Writes;
using OptiCli.Protocol;

namespace OptiCli.Commands;

/// <summary>
/// A local login for a restored database: <c>users add</c>, <c>users remove</c> and <c>users roles</c>, through the site's
/// ASP.NET Identity (needs <c>serve</c>). Never lists users: their names and addresses are personal data.
/// </summary>
internal static class UsersCommand
{
    public static Command Create(GlobalOptions options)
    {
        var command = new Command("users", """
            A local login for a restored database, through the site's own ASP.NET Identity (needs `opticli serve`): `users add`
            makes a user (tagged as made by opticli), `users remove` removes one opticli made, and `users roles` shows the
            roles with how many users each has and how the CMS's virtual roles (CmsAdmins, CmsEditors, ...) map to them.
            opticli never changes or removes a user it didn't make, and never lists users (their names and addresses are
            personal data). Refused against a shared database, and on a site whose CMS users come from elsewhere (OpenID
            Connect, Opti ID) (exit 3).
            Example: opticli users add dev --dry-run
            """);
        command.Subcommands.Add(Add(options));
        command.Subcommands.Add(Remove(options));
        command.Subcommands.Add(Roles(options));
        return command;
    }

    private static Command Add(GlobalOptions options)
    {
        var name = new Argument<string>("name") { Description = "The user name to sign in with." };
        var roles = new Option<string[]>("--role")
        {
            Description = $"A role to put the user in (repeat for more); one that doesn't exist is created. Default: {LocalUsers.DefaultRole}, the role the CMS's own first-admin registration uses (admin and edit mode on a default site). `opticli users roles` shows what the site's virtual roles map to.",
            HelpName = "role",
            AllowMultipleArgumentsPerToken = false,
        };
        var passwordStdin = new Option<bool>("--password-stdin") { Description = "Read the password from standard input (one line)." };
        var write = new WriteOptions();
        write.DryRun.Description = "Check the name, the password against the site's rules and the roles, without creating anything (or writing a password file).";
        var command = new Command("add", """
            Add a user to the site's ASP.NET Identity, as the CMS's first-admin registration does, so you can sign in to a
            restored database locally. Through the site (needs `opticli serve`). The user is tagged as made by opticli (a claim,
            and the address <name>@opticli.localhost), so `opticli users remove` can tell. The password: with --password-stdin
            from standard input; on a terminal, asked for without showing it; otherwise generated and written to a file only you
            can read, under opticli's state directory: the output gives its path (passwordFile), never the password. The site's
            password rules apply (validation, exit 5). A name that exists is a conflict (exit 5). Refused against a shared
            database and on a site without ASP.NET Identity (exit 3).
            Example: opticli users add dev
            Example: printf '%s\n' "$PASSWORD" | opticli users add dev --role WebAdmins --role WebEditors --password-stdin
            """);
        command.Arguments.Add(name);
        command.Options.Add(roles);
        command.Options.Add(passwordStdin);
        command.Options.Add(write.DryRun);

        CommandRunner.SetHandler(command, options, async (context, cancellationToken) =>
        {
            var parse = context.Parse;
            var user = parse.GetValue(name)!.Trim();
            if (user.Length == 0)
            {
                throw new UsageException("The user name is empty.");
            }
            var dryRun = parse.GetValue(write.DryRun);
            RequireLocal(context);
            var (password, source) = parse.GetValue(passwordStdin) ? (ReadStdin(), "stdin")
                : DatabasePrompt.CanAsk ? (Prompt(user), "prompt")
                : (PasswordFiles.Generate(), "generated");
            var file = source == "generated" && !dryRun ? PasswordFiles.PathFor(context.Environment.StateDirectory, context.Project.Directory, user) : null;
            var agent = await context.ConnectAgentAsync(cancellationToken);
            // Written before the site makes the user, so an answer lost on the way doesn't lose the password; beside the
            // file until then, under a name of its own, so neither the password file of a user that exists already nor a
            // file an earlier lost answer left is touched by a run the site refuses.
            var earlier = file is null ? [] : PasswordFiles.Pending(file);
            var pending = file is null ? null : PasswordFiles.NewPending(file);
            if (pending is not null)
            {
                PasswordFiles.Write(pending, password);
            }
            UserAddResult result;
            try
            {
                result = await Send<UserAddResult>(agent, AgentRoutes.UserAdd, new UserAddRequest
                {
                    Name = user,
                    Password = password,
                    Roles = parse.GetValue(roles) is { Length: > 0 } given ? given : null,
                    DryRun = dryRun,
                }, cancellationToken);
            }
            catch (OptiCliException ex) when (pending is not null && ex.Code is ErrorCode.Unreachable)
            {
                // The site may have made the user before the answer got lost: the password stays, and the error says where.
                throw OptiCliException.Create(ex.Code, ex.Message,
                    $"{ex.Hint} The site may have made '{user}' with the generated password, which is kept in {pending} (readable by you only): `opticli users roles` counts the users opticli made, and `opticli users remove {user}` removes it with that file.".Trim(),
                    ex.Details);
            }
            catch (OptiCliException ex) when (pending is not null)
            {
                PasswordFiles.Delete(pending);
                throw earlier.Count == 0 || ex.Code is not ErrorCode.Conflict
                    ? ex
                    : OptiCliException.Create(ex.Code, ex.Message,
                        $"{ex.Hint} An earlier run whose answer was lost kept a password for '{user}' in {string.Join(", ", earlier)}: the user may have been made with it.".Trim(),
                        ex.Details);
            }
            if (pending is not null)
            {
                File.Move(pending, file!, overwrite: true);
                foreach (var stale in earlier)
                {
                    // The user didn't exist after all, so no earlier run made it.
                    PasswordFiles.Delete(stale);
                }
            }
            var warnings = result.Warnings?.ToList() ?? [];
            if (result.CreatedRoles.Count > 0)
            {
                warnings.Add($"{(dryRun ? "Would create" : "Created")} the role{(result.CreatedRoles.Count == 1 ? "" : "s")} {string.Join(", ", result.CreatedRoles)}, which didn't exist; `opticli users remove` leaves {(result.CreatedRoles.Count == 1 ? "it" : "them")}.");
            }
            if (source == "generated" && dryRun)
            {
                warnings.Add("Not on a terminal and without --password-stdin, so the real run generates a password and writes it to a file only you can read (passwordFile); it is never printed.");
            }
            return new CommandResult(new UserAddOutput(result.Name, result.Email, result.Roles, result.CreatedRoles, source, file, result.UserType, result.Created, result.DryRun ? true : null),
                Warnings: warnings.Count > 0 ? warnings : null, Source: WriteExecutor.AgentSource);
        });
        return command;
    }

    private static Command Remove(GlobalOptions options)
    {
        var name = new Argument<string>("name") { Description = "The user to remove: one `opticli users add` made." };
        var write = new WriteOptions();
        write.DryRun.Description = "Check that the user exists and opticli made it, without removing it.";
        var command = new Command("remove", """
            Remove a user `opticli users add` made, through the site (needs `opticli serve`), and the file with its generated
            password if there is one. A user opticli didn't make is refused (exit 3): remove it in the CMS admin UI. Roles
            stay. Refused against a shared database (exit 3).
            Example: opticli users remove dev
            """);
        command.Arguments.Add(name);
        command.Options.Add(write.DryRun);

        CommandRunner.SetHandler(command, options, async (context, cancellationToken) =>
        {
            var user = context.Parse.GetValue(name)!.Trim();
            var dryRun = context.Parse.GetValue(write.DryRun);
            RequireLocal(context);
            var agent = await context.ConnectAgentAsync(cancellationToken);
            var result = await Send<UserRemoveResult>(agent, AgentRoutes.UserRemove, new UserRemoveRequest { Name = user, DryRun = dryRun }, cancellationToken);
            var file = PasswordFiles.PathFor(context.Environment.StateDirectory, context.Project.Directory, user);
            var fileRemoved = false;
            if (!dryRun && result.Removed)
            {
                foreach (var stale in PasswordFiles.Pending(file))
                {
                    PasswordFiles.Delete(stale);
                }
                fileRemoved = PasswordFiles.Delete(file);
            }
            return new CommandResult(new UserRemoveOutput(result.Name, result.Roles, result.Removed, fileRemoved ? file : null, dryRun ? true : null), Source: WriteExecutor.AgentSource);
        });
        return command;
    }

    private static Command Roles(GlobalOptions options)
    {
        var command = new Command("roles", """
            The site's roles with how many users are in each (members), and the CMS's virtual roles: mapped ones
            (EPiServer:Cms:MappedRoles, e.g. CmsAdmins: WebAdmins, Administrators) with the roles that give them, and built-in
            ones (Everyone, Authenticated, Creator, ...). No user names or addresses. optiCliUsers counts the users `opticli
            users add` made. Through the site (needs `opticli serve`).
            Example: opticli users roles
            """);

        CommandRunner.SetHandler(command, options, async (context, cancellationToken) =>
        {
            RequireLocal(context);
            var agent = await context.ConnectAgentAsync(cancellationToken);
            var result = await Send<UserRolesResult>(agent, AgentRoutes.UserRoles, null, cancellationToken, HttpMethod.Get);
            return new CommandResult(result, Source: WriteExecutor.AgentSource);
        });
        return command;
    }

    /// <summary>What <c>users add</c> prints. Never the password: where it came from, and for a generated one, its file.</summary>
    /// <param name="Password"><c>stdin</c>, <c>prompt</c> or <c>generated</c>.</param>
    private sealed record UserAddOutput(string Name, string Email, IReadOnlyList<string> Roles, IReadOnlyList<string> CreatedRoles, string Password,
        string? PasswordFile, string UserType, bool Created, bool? DryRun);

    private sealed record UserRemoveOutput(string Name, IReadOnlyList<string> Roles, bool Removed, string? PasswordFileRemoved, bool? DryRun);

    /// <exception cref="RefusedException">A shared (remote) database: checked before anything is asked or sent.</exception>
    private static void RequireLocal(CliContext context)
    {
        if (!context.UseConnection().IsLocal)
        {
            throw new RefusedException(LocalUsers.SharedRefusal, LocalUsers.SharedHint);
        }
    }

    private static async Task<T> Send<T>(AgentClient agent, string route, object? body, CancellationToken cancellationToken, HttpMethod? method = null)
    {
        try
        {
            return await agent.SendAsync<T>(method ?? HttpMethod.Post, route, body, cancellationToken);
        }
        catch (NotFoundException ex) when (ex.Message.StartsWith("No agent route", StringComparison.Ordinal))
        {
            throw new NotFoundException("The site's agent is older than this opticli and can't add, remove or list users.", AgentErrors.OutOfDateHint);
        }
    }

    /// <summary>The first line of standard input.</summary>
    private static string ReadStdin()
    {
        if (!Console.IsInputRedirected)
        {
            throw new UsageException("--password-stdin reads the password from standard input, which is a terminal here.", "Pipe it in (printf '%s\\n' \"$PASSWORD\" | opticli users add ...), or leave --password-stdin out to be asked.");
        }
        var password = Console.In.ReadLine()?.TrimEnd('\r', '\n') ?? "";
        return password.Length > 0 ? password : throw new UsageException("Standard input held no password.");
    }

    /// <summary>Asks twice, without showing what is typed.</summary>
    private static string Prompt(string user)
    {
        while (true)
        {
            var first = ReadHidden($"Password for {user}: ");
            if (first.Length == 0)
            {
                throw new UsageException("No password given; nothing was created.");
            }
            if (ReadHidden("Again: ") == first)
            {
                return first;
            }
            Console.Error.WriteLine("They differ; try again.");
        }
    }

    private static string ReadHidden(string question)
    {
        Console.Error.Write(question);
        var text = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.Error.WriteLine();
                return text.ToString();
            }
            if (key.Key == ConsoleKey.Backspace)
            {
                if (text.Length > 0)
                {
                    text.Length--;
                }
            }
            else if (!char.IsControl(key.KeyChar))
            {
                text.Append(key.KeyChar);
            }
        }
    }
}
