# Safety model

| Rule | Enforced by |
|---|---|
| A remote database is used only when the user chose it as the development database, or for one run with `--db`, `--connection` (`OPTICLI_DB`), `--profile` or `sync pull --from`, with a warning on every response. Only literal loopback names (`localhost`, `127.0.0.1`, `::1`, `.`, `(local)`) and LocalDB count as local. A host name that resolves to 127.0.0.1 counts as remote. | CLI, before connecting |
| `export` only reads (over SQL, never through a site), so it works on production; it writes nothing but its own folder. | CLI |
| `sync pull` only reads its source (over SQL, read-only intent for production) and writes only to a database with the role `local` (refused otherwise, exit 3, `details.reason: "syncTarget"`), through its site, as `apply --update-existing` does. It never deletes for good: `--delete` moves content to the recycle bin. A local change to content the source changed too is a conflict, left alone unless `--overwrite-local`. | CLI, then the site agent |
| A database with the role `prod` is only read, and only when a run picks it: it is never the development database (`db use` and a saved choice that became production are refused), and `serve`, `env` and every write are refused (exit 3, `details.reason: "prodDatabase"`), whether it was picked by name, id, database name, `--connection`, `--profile`, `OPTICLI_DB` or an exported connection string. Roles belong to a server and database, and a weaker one needs a person at a terminal. Reads connect with `ApplicationIntent=ReadOnly`. The only exception is a sealed apply (next row). | CLI before anything is read, again on the way to the site, and when starting it |
| A sealed apply writes to production only at a terminal (stdin, stdout and stderr), after the user typed the database's name and the write login, once per seal (24 hours), and only if the plan and its files, the build, production's content model and every item the plan touches are as sealed (checked before the login is asked, and again right before the first save). Sealed plans create, change, move, translate, publish and upload by GUID; never delete, unpublish, change access, discard, or include someone else's draft. Files go only to production's blob storage, and the run joins production's event bus only, when the user types them at the run's terminal (never an option, variable or setting); what the site runs with is checked before anything is written. The site for the run listens on loopback only, serves nothing but the agent (its own pages and edit UI answer 404), gets its token and the write login on stdin (not in its environment or a state file), runs in shared mode, accepts no drift but the sealed leftovers (types and properties only in production's database, by name), and is stopped afterwards. The write login is never saved. | CLI (seal, terminal, checks), the run's own site launcher, site agent |
| The site started by `serve` is pinned to the database the CLI reads. It refuses to start against any remote database except the approved development one. There it runs without scheduler, schema updates, content type sync or store remapping, and `serve` refuses a build with EF Core migrations the database lacks. If the site turns hosting startups off in its code, so the pin can't run, it refuses to start. | CLI + site agent at startup |
| The site agent answers only loopback callers that send the per-run token, and only in `Development`. The token is kept in a state file only your user can read. | Site agent, per request |
| Writes create drafts; publishing needs `--publish` (or `publish`). Saves are attributed to the user `opticli`. | CLI + site agent |
| A publish that would also put live changes someone else saved after the published version stops: on a terminal it shows who saved what and asks, elsewhere it fails with `conflict` (exit 5) listing them. `--include-draft` (a plan step's `"includeDraft": true`) confirms; `publish --version <id>` publishes that version as it is (an editor's assistant naming a version through the MCP module still needs `includeDraft`), also when it is older than the published version, with a warning naming whose published version it replaced (`code: "newerPublished"`; a `projects publish` of an item older than a version someone else published is a `conflict` until `--include-draft`); `--from published` bases the change on the published version, leaving them out. Where the site makes shared area blocks, `--include-draft` never takes someone else's drafts of the content's blocks along: only naming a block (`--with-block`) does, and in a sealed apply not even that. | Site agent |
| A write joins a CMS project only when it names it (`--in-project`, a plan's `"project"`), and never publishes while it does. Publishing a project puts live only its items marked Ready to publish, after every one passed `publish`'s checks; one problem stops it with nothing published. Marking an item ready counts as its marker's consent to publish their changes, so marking someone else's draft ready needs `--include-draft`. Sealed production runs refuse projects. | Site agent, CLI first |
| Against a shared database, writes stop while the build and the database differ (drift): on a terminal it shows the differences and asks, elsewhere it fails with `drift` (exit 5). `--accept-drift <fingerprint>` confirms; the fingerprint stops counting when the differences change. | Site agent, CLI first |
| Against a shared database, `sites add`, `sites primary`, `sites host remove` and `languages enable\|disable` are refused (exit 3), and `sites host add` only adds hosts of type undefined: the deployed site uses the same site definitions and language branches. Sealed plans never have `site` or `language` steps. There is no override; change those sites in their own admin UI. Site definitions are never reachable from the MCP module. | Site agent, CLI first |
| `delete` moves content to the recycle bin; nothing empties it, and `restore` brings it back. Site roots, start pages, asset roots and anything above them can't be moved or deleted. The MCP module doesn't restore. | Site agent |
| `serve` turns the site's scheduler off, so a restored database's overdue jobs don't all start (`--scheduler` leaves it on). `jobs run` refuses jobs that delete for good (emptying the recycle bin, trimming versions, truncating the change log, Commerce's expired carts and archived items, ...) or move content across the site, and `jobs set` refuses to let the scheduler run one (sooner), without `--allow-destructive`. Against a shared database jobs aren't run, stopped or changed, and the scheduler stays off. | Site agent, CLI first |
| `users add` only adds users to the site's ASP.NET Identity, tagged as made by opticli, and `users remove` only removes those: an existing account's password, address and roles are never touched. A generated password is written to a file only you can read and never printed. No command lists users; `users roles` counts them per role. Refused against a shared database, and on a site whose CMS users come from elsewhere (OpenID Connect). | Site agent, CLI first |
| `types remove`, `types remove-property` and `types prune` only remove what removed code left in the database, as the running site judges it: a content type whose class the site can't load (never one made in admin mode, nor one of the CMS's own) and a property that isn't in its type's code. A type stays while content of it exists, also in the recycle bin (opticli doesn't delete content), while a property has it as its block type, and while page-type values name it. A property's stored values go with it only with `--allow-destructive`, and never one a content provider uses. The site writes each removal's record to `removals.jsonl` before it removes it. Only through the site, refused against a shared database, and never reachable from the MCP module. | Site agent, CLI first |
| Reads use fixed queries. `sql` accepts a single SELECT, refuses anything that writes, runs code, reaches another database or reads server-wide views, logs and traces (in `sys`, only the views that describe the database's own schema), and always runs in a rolled-back transaction. Personal-data tables (form submissions, users) need `--include-personal-data`. Where another command's fixed query reads the Dynamic Data Store's tables, it reads only one store's rows and only what it shows (the parents the CMS stored for `trash`, visitor group names), nothing that names a person. | CLI |
| Passwords are never printed; `doctor` redacts connection strings. The exception is `opticli env`: it prints the per-run token, and with `--include-connection` the connection string too. | CLI |

Things these rules can't see:
- A port-forward or tunnel on `localhost,<port>` to a remote server looks local to opticli.
- So does a hosts-file entry or SQL client alias that maps a loopback name elsewhere.
- A production database reached by another name than the one it was given the role under (an IP address, an alias)
  isn't known to be production. Neither is one whose role was edited out of the user config, or a run with another
  user config (`XDG_CONFIG_HOME`, `APPDATA`): the user config is what holds the roles.
- Lowering a role checks that stdin, stdout and stderr are a terminal; a pseudo-terminal (`script`, `expect`) passes
  that check. So does a sealed apply's; it then needs the write login, which an agent doesn't have, unless production
  signs in with the user's own identity (integrated security, Microsoft Entra ID).
- Seals are MAC'd with a key in the user config folder: anything running as you can read it, and so forge a seal or
  remove the mark that one was used. A forged seal still shows the user the plan as it is, checks production, and needs
  the name and the write login typed.
- A sealed apply's write login is in the memory of opticli and of the site for the run: a process running as you that
  can read other processes' memory (Windows; Linux without Yama's `ptrace_scope`, which Ubuntu sets), makes one of them
  dump core where you can read the dump (a raised core limit, or `systemd-coredump`'s `coredumpctl`), or opens the site's
  stdin in its first moment, can read it. If opticli is killed hard (SIGKILL), the site it started for the run keeps
  running until stopped by hand; the run prints its pid.

Don't point opticli at such a connection unless you mean to write to what is behind it.

## Shared databases

A remote development database is often shared: with other developers, and with the environment it belongs to. The
database doesn't say which build or commit is deployed against it. So opticli checks whether the database matches
what the local build expects, which is what matters before a write:
- content types and properties in the code but not in the database, and the reverse (properties added in admin
  mode don't count);
- a property's type or culture-specific setting, and renames a migration step hasn't applied yet;
- EF Core migrations, Dynamic Data Store types and the CMS schema version.

Each difference says which side is ahead. `local`: your branch has changes that aren't deployed there (check out what
is deployed, or deploy first). `database`: the database has what your build lacks. Either the environment runs newer
code than your checkout (pull and build), or a class or property was removed from code and the CMS kept its content
type or property for the content that uses it (`opticli types --orphaned` lists such types); pulling doesn't change
those. When only EF Core migrations or the CMS schema version are on the database's side, that is newer code (pull and
build). `unknown`: they differ, and the database doesn't say which side changed. The report's `ahead` is `both` when
differences go each way. Required, display names, sort order,
tabs and `[AllowedTypes]` aren't stored for a model: the running code decides them, so they never show as drift. On
CMS 13, content types of unknown origin (see [CMS 13](cms13.md#cms-13)) are listed with `informational: true` and don't stop
writes.

For a content type or property only in the database, opticli looks in your checkout's git history for the commit that
removed it from code, and names it on the item in `opticli drift`, `doctor`, `serve`'s start message, the `drift`
error and the prompt (`removedFromCode`: commit, date, subject; in text "its class was removed from code in abc1234
(2026-09-14)"). A type is looked up by its GUID (the `[ContentType(GUID = ...)]` string), then by `class <Name>` (not
in a comment), in `*.cs` files; by name only when the removed class had no GUID or the type's. A property is looked up by its declaration
(not a comment) in its type's class files, then in those of the base classes they name, as far as the repository has
them; a same-named property of another class doesn't count. Only what isn't in `HEAD` counts, and when opticli can't
tie a removal to the type it names none. When every difference on the database's side is such a removal, the advice
says so instead of "pull and build": the CMS kept them for their content, or that environment doesn't run the removal
yet. The lookup runs only below a `.git` (in the project's folder or above) with `git` on the path, for up to 50 items
within 5 seconds; without git, or when it takes longer, drift is reported without it. It is the CLI's addition: the
fingerprint and what stops writes stay the site agent's.

`opticli drift` lists the differences and `doctor` shows them. Once `serve` has reported drift, a short warning comes
with every command that uses the database, and with `serve --status`. A site you start yourself with `opticli env`
reports drift only through `opticli drift` and the write errors. While there are any differences, every write stops, not only those to a type that differs: the risk is the build as a whole (its event
handlers and validators), not only its models. Reads keep working, since they don't run the site's code. Restart
`serve` to compare again after a deploy or a pull.

What opticli can't turn off is the site's own startup code. Against a shared database, a local build still:
- runs `Database.Migrate()` if the site calls it at startup (the reason for the migration check);
- registers its scheduled jobs (new jobs get rows, changed ones are updated), even with the scheduler off. That isn't
  reported as drift: the deployed site registers the same jobs once the code is deployed;
- creates the content root folders that add-ons register at startup (`IContentRootService`), and Dynamic Data Store
  stores when they are first used, if they don't exist yet;
- runs initialization modules and other startup code that writes, such as a site that creates its own content.

So use a SQL login without DDL rights (no `db_ddladmin` or `db_owner`) for a shared database where you can. Schema
changes then fail instead of reaching everyone. Some add-ons create or alter their own tables, views or stored
procedures every time the site starts, and a site with those may not start with such a login. Check what your
add-ons need first.
