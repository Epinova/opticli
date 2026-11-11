---
title: Work on a restored production database
summary: Point opticli at a restored copy of production, make its sites answer on localhost, get a local login and keep its scheduled jobs from running.
audience: developers
order: 1
written-for: 0.30.0
---

A restored copy of a production database is the closest you get to the real thing on your own machine, but it
arrives with production's host names, production's users and production's scheduled jobs, all overdue. This guide
takes you through the first hour: opticli reads the copy, the site answers on `localhost`, you can sign in to the edit
UI, and no import, email or recycle-bin purge starts behind your back. Everything here changes only the local copy.

## Before you start

- opticli 0.30.0 or newer (`opticli --version`; update with `dotnet tool update -g OptiCli`).
- The site's repository checked out and built (`dotnet build`), on a branch close to what production runs.
- The backup restored into SQL Server on your machine: a local instance or a container. LocalDB works on Windows only.
- A coding agent is optional. If you use one, run `opticli skill install` first so it knows these commands.

## Steps

### 1. Point the site at the restored database

opticli uses the database the site uses in `Development`, found in the same order as ASP.NET Core: launch profiles,
exported `ConnectionStrings__EPiServerDB`, user secrets, `appsettings.Development.json`, `appsettings.json`. Put the
copy's connection string in one of them, for example user secrets:

```bash
dotnet user-secrets set "ConnectionStrings:EPiServerDB" \
  "Server=localhost,1433;Database=alloy_restored;User Id=sa;Password=<password>;TrustServerCertificate=True"
opticli doctor
```

`doctor` names the database it found and the source it came from. A launch profile that sets the same connection
string wins over user secrets, so if `doctor` shows the old database, check `Properties/launchSettings.json`. A local
database is used without asking. When several sources disagree, opticli asks once: pick the copy from the list, or
run `opticli db list` and then `opticli db use <id>`.

### 2. Look at what you restored

Reads go straight to the database, so nothing needs to run yet:

```bash
opticli sites
opticli languages
opticli jobs
```

`sites` lists each site with its hosts: expect production names such as `alloy.example.com`. `jobs` lists every
scheduled job with `enabled`, `nextRun` and `overdue`. On a fresh restore most enabled jobs are overdue. Note the ones
that reach other systems (imports, syncs, emails) and the built-in Automatic Emptying of Trash.

### 3. Start the site with the scheduler off

```bash
opticli serve
opticli serve --status
```

`serve` runs your build in `Development` against the copy and returns once the site answers (30 to 60 seconds). It
turns the site's scheduler off for the run, so the overdue jobs stay put: `serve --status` says `scheduler: off`, and
the site's log (`opticli serve --logs`) has `[opticli] Scheduler is off for this run`. Don't pass `--scheduler` against
a restored copy. It leaves the scheduler as the site sets it, and every overdue job starts at once.

### 4. Make the sites answer on localhost

Point each site at the host and port you open it on, usually the one in your launch profile. Dry-run first:

```bash
opticli sites primary "Alloy=localhost:5000" --dry-run
opticli sites primary "Alloy=localhost:5000" --save
```

The dry run shows each site's `changes`: the new host is added as the primary host, the old primary host and the
Edit host become undefined, and the site's URL (SiteUrl) moves to the new host, `https://` unless you pass
`--https false` (on CMS 13, where sites are applications without a SiteUrl, the URL follows the hosts). The
production hosts stay, so `opticli resolve https://alloy.example.com/en/` still finds content.
`--save` keeps the mapping in your user config. After the next restore, one command puts it back:

```bash
opticli sites primary --from-config
```

With several sites, give one pair per site (`"Alloy=localhost:5000" "Alloy Intranet=localhost:5001"`); all pairs are
checked before any site is saved. To make one more address answer without changing the primary host, add it as a
plain host:

```bash
opticli sites host add "Alloy" alloy.localhost:5000 --dry-run
```

> **Note:** A site you also run from your IDE keeps the old hosts until you restart it, unless it runs in
> [IDE mode](https://github.com/Epinova/opticli#running-the-site-from-your-ide-ide-mode).

### 5. Get a local login

Production's users are there, but you don't know their passwords, or they sign in through another identity provider.
Add a login of your own to the site's ASP.NET Identity:

```bash
opticli users roles
opticli users add dev --dry-run
opticli users add dev
```

`users roles` shows which roles give `CmsAdmins` and `CmsEditors` on this site. `users add` puts the user in
`WebAdmins` unless you pass `--role`, and warns when the roles don't give admin mode. At a terminal it asks for the
password without showing it; from a script or an agent it generates one and gives you `passwordFile`, a file only you
can read. Sign in to the edit UI (`/EPiServer/CMS/` on CMS 12, `/Optimizely/CMS/` on CMS 13).
`opticli users remove dev` removes it again, and only ever removes users opticli made.

### 6. Keep the jobs off when you run the site yourself

`serve` keeps the scheduler off, but a site you start from your IDE, with `dotnet run` or with
[IDE mode](https://github.com/Epinova/opticli#running-the-site-from-your-ide-ide-mode), runs it as the site sets it.
Before you do that, disable the jobs you noted in step 2 while `serve` still runs:

```bash
opticli jobs --enabled
opticli jobs set "Automatic Emptying of Trash" --enabled false --dry-run
opticli jobs set "Automatic Emptying of Trash" --enabled false
```

The output shows the schedule before and after; schedules aren't versioned, so keep that output if you want to put a
job back. To run one job on purpose, use `opticli jobs run "<job>"`, which waits and ends with the job's status.

### 7. Check the result

```bash
opticli doctor
opticli resolve http://localhost:5000/en/
opticli types --orphaned
opticli serve --stop
```

`doctor` should now show no warning about the saved site mapping and count the overdue jobs. `resolve` on the local
address should find the start page. `types --orphaned` lists content types whose class is gone from your code, which
older production databases tend to collect; see
[Orphaned content types and properties](https://github.com/Epinova/opticli#orphaned-content-types-and-properties) if
you want to remove them. Stop the site when you are done, or leave it running for writes.

## If something goes wrong

| What you see | What to do |
|---|---|
| `needs_selection` (exit 6) | Several sources name a database. Run `opticli db list`, then `opticli db use <id>` for the copy. |
| `refused` (exit 3) about LocalDB | Linux and macOS have no LocalDB. Restore into SQL Server, for example in a container, and point the connection string there. |
| `refused` (exit 3) on `serve`, CMS 13 build and CMS 12 database | The site would upgrade the copy for good. Check out the CMS 12 branch, or upgrade the copy on purpose by starting the site once yourself. |
| `login_failed` (exit 4) | The server refused the login, or the database name is wrong. When the login has no user in the database, `error.details.identity` names it. |
| `unreachable` (exit 4) on `sites primary`, `users` or `jobs set` | These go through the site. Start it with `opticli serve`; `opticli serve --logs --tail 40` shows why it didn't start. |
| `validation` (exit 5) on `sites primary` | Another site already has that host. Give each site its own port or `*.localhost` name. |
| `not_found` (exit 2) on `sites primary` | The site name doesn't match. Use the name `opticli sites` shows. |
| `refused` (exit 3) on `users add` | The site signs CMS users in through OpenID Connect or Opti ID, not ASP.NET Identity. Sign in through that provider. |
| `validation` (exit 5) on `users add` | The password breaks the site's password rules. The message says which. |

## Related

- [Which database](https://github.com/Epinova/opticli#which-database)
- [Site hosts after a database restore](https://github.com/Epinova/opticli#site-hosts-after-a-database-restore)
- [Local users after a database restore](https://github.com/Epinova/opticli#local-users-after-a-database-restore)
- [Scheduled jobs](https://github.com/Epinova/opticli#scheduled-jobs)
- [serve and env](https://github.com/Epinova/opticli#serve-and-env)
- [Pull content from staging](pull-from-staging.md), to keep the copy current without another restore
- [Find content and the code behind it](find-content-and-code.md)
