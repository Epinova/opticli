# serve and env

`opticli serve` runs the site's existing build output in `Development` on `http://127.0.0.1:<port>`, with the site
agent injected, and returns once the site agent answers.

| Option | Default |
|---|---|
| `--output <dll>` | `output` from the user config (relative to the project), else the newest `bin/{Debug,Release}/<tfm>/<AssemblyName>.dll` (also below a `<rid>/` folder, under the project's `OutputPath`/`BaseOutputPath`, or under `artifacts/` with `UseArtifactsOutput`) |
| `--port <n>` | `port` from the user config, else 5199, else the first free port up to 5299 |
| `--build` | off: opticli warns when sources (the site's or a referenced project's) are newer than the build, and `--build` runs `dotnet build` first |
| `--timeout <s>` | 180 seconds to wait for the site to answer |
| `--foreground` | off: the site runs in the background; with it, the site's output streams until Ctrl+C (to stderr when stdout is redirected, so stdout stays one JSON envelope) |
| `--https` | off (or `"https": true` in the user config, which `--https false` overrides): also listen on `https://localhost:<next free port>` with the development certificate, printed as `browseUrl`, for sites that redirect to HTTPS. `serve` warns when a site does |
| `--https-port <n>` | `httpsPort` from the user config, else the next free port: the HTTPS address's port, which turns `--https` on. A taken port is a `usage` error, never another one, so site hosts with that port (`alloy.localhost:5001`) stay right from run to run |
| `--profile <name>` | (the global option, which picks the launch profile's connection string) also gives the site the profile's other `environmentVariables`, as `dotnet run --launch-profile` would. Those `serve` sets itself are left out: the address (`ASPNETCORE_URLS`, `ASPNETCORE_HTTP(S)_PORTS`, `Kestrel__Endpoints__*`), `ASPNETCORE_ENVIRONMENT`/`DOTNET_ENVIRONMENT`, `DOTNET_STARTUP_HOOKS`, `OPTICLI_*` and the pinned connection string. A warning names what was applied and left out, never the values. `applicationUrl` and `commandLineArgs` are ignored |
| `--scheduler` | off (or `"scheduler": true` in the user config, which `--scheduler false` overrides): the site's scheduler stays as the site sets it, so jobs run on their schedule and overdue ones start at once. Without it the scheduler is off for the run. A `usage` error against a shared database, where it stays off |
| `--allow-pending-migrations` | off: against a shared database, `serve` refuses a build with EF Core migrations the database lacks |

`serve --status`, `serve --logs [--tail N]` and `serve --stop` manage the running site:
- `--stop` asks the site to shut down through the site agent, on every OS, and falls back to SIGTERM on Linux and
  macOS. A site that hasn't exited after 20 s is killed. On Windows that kill is all there is when the agent doesn't
  answer.
- `--status` (and `doctor`) also say whether the site's scheduler runs (`scheduler`) and how its edit UI makes a new
  block in a ContentArea, which writes follow (`inlineBlocks`: `inline` or `shared`; see
  [New blocks in a ContentArea](content-areas.md#new-blocks-in-a-contentarea)).
- Each start writes a new log. `--logs` reads the latest from its end, and `data.previous` lists the two before it.
- A state file opticli can't read is treated as stale: `--status` and `--stop` remove it, `doctor` reports it.
- Two `serve` runs for the same project don't start two sites: the second waits for the first and reports its site.
  `serve`s for different sites started at the same moment don't pick the same free port either (best effort: they
  share their claims through `$XDG_RUNTIME_DIR/opticli-ports`, else `~/.local/state/opticli/ports`, on Windows
  `%TEMP%\opticli-ports`, so `serve`s run with different
  values of those don't see each other's).
- Ctrl+C while `serve` waits for the site stops the site again.
- A site that configures `Kestrel:Endpoints` ignores `ASPNETCORE_URLS`. `serve` and `env` then add opticli's address
  as one more endpoint (`Kestrel__Endpoints__OptiCli__Url`) and warn. If the site sets its addresses in code, a
  timeout names the addresses it listens on instead.

To run the site yourself (IDE, `dotnet run`, hot reload), `opticli env` prints the variables `serve` would set:
- `--format shell|powershell|dotenv|json|launchSettings` picks the format. `--json` is the same as `--format json`.
- `--port` picks the port.
- `--profile <name>` adds that launch profile's variables, as `serve --profile` gives them to the site (not with
  `--format launchSettings`, whose profile has them already).
- The site's scheduler stays as the site sets it (`OPTICLI_SCHEDULER=on`): that run is yours.
- `OPTICLI_*` variables that this run leaves out but the shell still exports (from an earlier `opticli env`) are set
  to empty, so they can't pin or approve another database.
- Each run makes a new token, so a site started with an earlier run's variables stops taking writes: restart it.
  `env` is refused while `serve` runs the site, and `serve` while a site started with `env`'s variables runs.

The connection string is left out unless `--include-connection` is given. Without it the site uses its own
configuration, which is still checked to be local (or the approved development database) and the same database
the CLI reads.

Set the variables for the site process only, e.g. in a subshell:

```sh
(eval "$(opticli env)" && dotnet run --no-launch-profile)
```

`DOTNET_STARTUP_HOOKS` makes every .NET process started from a shell that exports it load the hook. Launch profiles
are often committed, so keep the token out of git.

| Variable | Set by `serve`/`env` for the site | Meaning |
|---|---|---|
| `ASPNETCORE_ENVIRONMENT` | always | `Development` |
| `ASPNETCORE_URLS` | always (not `--format launchSettings`) | `http://127.0.0.1:<port>`, and the HTTPS address with `--https` |
| `ASPNETCORE_HTTPS_PORT` | `--https` | where the site's HTTPS redirection sends browsers |
| `DOTNET_STARTUP_HOOKS` | always | path to `OptiCli.Agent.dll`, which loads the site agent |
| `OPTICLI_TOKEN` | always | the per-run token the site agent requires |
| `OPTICLI_DB` | `serve`, `env --include-connection` | the connection string to pin the site to (read by the CLI, it is the same as `--connection`) |
| `OPTICLI_CONNECTION_NAME` | `serve`, `env --include-connection`, when not `EPiServerDB` | which connection string to pin |
| `ConnectionStrings__<Name>` | `serve`, `env --include-connection` | the same connection string, for code that reads it before the agent's pin; `serve` also removes other spellings of it (`ConnectionStrings:<Name>`, another case, Azure's `SQLCONNSTR_<Name>` and the like) |
| `OPTICLI_REMOTE_DB` | against a remote development database | the one remote database the site agent accepts; turns on shared-database mode |
| `OPTICLI_DRIFT_FILE` | `serve`, against a remote development database | what `serve` compared before the start (EF Core migrations, CMS schema version), for the site agent's drift report |
| `OPTICLI_REMOVALS_FILE` | never (the edge-case test site sets it) | another file for the record of `types remove`, `remove-property` and `prune`, instead of `removals.jsonl` in opticli's state directory |
| `OPTICLI_SCHEDULER` | `serve --scheduler`, `env` (not against a remote database) | `on` leaves the site's scheduler as the site sets it; without it the site agent turns the scheduler off |

## Running the site from your IDE (IDE mode)

`serve` runs a second process of the site. The process you started from your IDE keeps its own content cache, and
nothing tells it that opticli changed the database, so it shows the old content until you restart it. `opticli ide on`
fixes that: the site you start from the IDE (Rider's run or debug, Visual Studio, `dotnet run`, `dotnet watch`) loads
the site agent from its own build, so writes go through that process and show there at once.

```sh
opticli ide on        # once per project (again after updating opticli)
# rebuild, then start the site from the IDE as usual
opticli ide status    # the block, the agent copy, whether the build has the hook, the running site
opticli ide off
```

- `ide on` adds a labelled `ItemGroup` to `<Project>.csproj.user`, which MSBuild imports on its own and repositories
  normally ignore. Its `RuntimeHostConfigurationOption` items put the agent into the build's `runtimeconfig.json` as a
  startup hook (only for `Debug`, `--configuration` picks another, and never when publishing). Nothing else in the file
  changes. A `.csproj.user` that git tracks is refused; one git doesn't ignore is added to `.git/info/exclude`, which is
  never committed (`ide off` removes that line again).
- The hook is a copy of the agent in opticli's state directory (`agent/<version>-<hash>/cms<major>/`): the tool's own
  path changes with every update, and a startup hook that is missing stops the site from starting. The block only adds
  the hook while the copy exists. `ide on` removes copies older than 30 days other than the current one.
- At startup the agent makes its own token. Once the site listens, it writes where it listens and the token to
  `<project>.ide.json` in the state directory (readable by you only), and removes it when the site stops. A file left by
  a site the IDE killed is ignored and removed. Nothing secret goes into the project, the build output or a launch
  profile.
- opticli reaches the agent on the site's own addresses on this machine (`localhost`, loopback or wildcard addresses,
  and names below `.localhost` such as `site.dev.localhost`): plain HTTP first, else HTTPS on `localhost`, accepting
  the ASP.NET Core development certificate there (also on Linux, where OpenSSL doesn't trust it).
- IDE mode never changes how the site starts. There's no connection pin, and the scheduler, schema updates and content
  type sync stay as the site sets them, also against a remote development database (`serve` turns those off there). If
  the site runs against a database other than a local one or the development database, or outside `Development`, the
  agent stays off for that run (an `[opticli] IDE mode: the agent is off` line in the site's output) instead of
  stopping the site. Writes to a remote development database still stop on drift.
- `serve --status`, `doctor` and every write use the IDE's site while it runs (`mode: ide`). `serve` then starts nothing
  and says so. When the IDE's site doesn't answer (paused at a breakpoint), writes say so; continue it.
- `serve` still works when the IDE's site isn't running: it runs the same build, and gives the site the agent copy that
  build names, so one agent loads. `opticli env` is refused while IDE mode is on.
- After updating opticli, or after `db use` chooses another development database, run `ide on` again and rebuild.
  `ide status` and `doctor` say when that's needed.
- The `runtimeconfig.json` is written at build time: after `ide on` or `ide off`, rebuild. In Rider (and Visual Studio)
  use Rebuild: the play button's up-to-date check doesn't notice a changed `.csproj.user` and starts the old build.
  `dotnet build` and `dotnet run` do notice it.
- Sealing a plan for production, and the sealed run, refuse a build from IDE mode (exit 3): a run with production's
  write login loads only this opticli's agent. `ide off`, rebuild, and seal again.

## How the injection works

`serve` starts `dotnet <Site>.dll` with `DOTNET_STARTUP_HOOKS` pointing at `OptiCli.Agent.dll`, which ships in the
tool package under `agent/`, one build per CMS major (`agent/cms12/`, `agent/cms13/`): `serve` and `env` take the one
for the CMS the project builds against (else the one its database's schema belongs to). The startup hook makes the site agent's assembly resolvable and appends it to
`ASPNETCORE_HOSTINGSTARTUPASSEMBLIES`. The hosting startup then:
1. pins the connection string, added as the last configuration source and as a `PostConfigure` of the CMS's data
   access options;
2. fails the start if the effective connection string is neither local nor the approved development database;
3. turns the scheduler off (`SchedulerOptions.Enabled`, as a `PostConfigure`) unless `OPTICLI_SCHEDULER=on`; a site
   that turns it on in a `PostConfigure` of its own wins, and `serve --status` then says `scheduler: on`. Against a
   shared database it also turns off automatic schema updates, content type sync and store remapping;
4. maps `/_opticli/v1/*` ahead of the site's own middleware.

A site run with a pinned connection string and `ASPNETCORE_PREVENTHOSTINGSTARTUP=true` fails to start, since there
would be no pin. In a .NET process that isn't an ASP.NET Core host (opticli itself, build tools), the hook does nothing.

The site agent is compiled against `EPiServer.CMS.Core` 12.0 (.NET 8) and 13.0 (.NET 10), one build each, and binds to
the site's own, newer CMS assemblies at runtime. It ships no copies of them.
