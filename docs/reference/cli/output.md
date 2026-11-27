# Output and files

## Output and exit codes

- Redirected stdout gets compact JSON:
  `{"ok": true, "data": …, "meta": {"source", "version", "next", "warnings", "database"}}`.
  - `source` is `db` for reads, `agent` for writes and `cli` for local commands.
  - `database` is present when the database is remote, or has a role other than `local` (`server`, `name`, `local`,
    `development`, `role`).
- A terminal gets tables. `--json` and `--text` force either.
- `--jsonl` on list commands prints one item per line. A final `{"meta": {…}}` line follows only when there is a
  next page or warnings.
- Lists return 50 items by default (`jobs log`: 20 runs, `sql`: 100 rows). Pass `meta.next` as `--cursor` for the next page, or raise
  `--limit`. There is no total count.
- Strings over 300 characters are cut (`truncated: true, length: N`); `--full` or `--fields` gives them whole.
  Dates are UTC (`…Z`).
- Errors look like `{"ok": false, "error": {"code", "message", "hint", "details"}, "meta": {"version"}}`. The hint says
  what to do next.

| Exit | Code | Meaning |
|---|---|---|
| 0 | | ok |
| 1 | `usage` / `internal` | bad arguments (or a bug in opticli) |
| 2 | `not_found` | project, connection string, content, type or version not found |
| 3 | `refused` | a safety rule blocked it |
| 4 | `unreachable` | database or site agent not reachable, or a query failed on the server (a write that timed out may still have been saved) |
| 4 | `login_failed` | the database server answered but refused the login: the identity has no user in the database (`error.details.identity` names it, also for Microsoft Entra), a wrong password, or a database that doesn't exist |
| 4 | `timeout` | `jobs run --timeout` ran out: the job goes on running |
| 5 | `conflict` / `validation` / `drift` | a newer version exists, a publish would include someone else's unpublished changes (`details.reason: "pendingDraft"`), the CMS rejected the values, or a write against a shared database that differs from the build wasn't confirmed (`details` is the drift report) |
| 6 | `needs_selection` | the user must choose the development database first (`error.details.choices`) |
| 7 | `job_failed` | a job `jobs run` ran didn't succeed: it failed, couldn't start, or was stopped or aborted (`error.details` has its status and message) |
| 130 | `cancelled` | interrupted (Ctrl+C); an `apply` reports what it saved until then |

## Files

| What | Linux / macOS | Windows |
|---|---|---|
| User config | `$XDG_CONFIG_HOME/opticli/config.json` (default `~/.config/opticli/config.json`), readable by you only (made so at every save, as it may hold connection strings with passwords) | `%APPDATA%\opticli\config.json` |
| `serve` state, start lock, logs (the last 3 runs) and, against a shared database, what `serve` compared before the start | `$XDG_STATE_HOME/opticli/` (default `~/.local/state/opticli/`) | `%LOCALAPPDATA%\opticli\` |
| IDE mode: where the IDE's site says it listens, with its token (`<project>.ide.json`, written and removed by the site), and opticli's copies of the agent that builds name as their startup hook (`agent/`) | `$XDG_STATE_HOME/opticli/`, readable by you only | `%LOCALAPPDATA%\opticli\` |
| Generated passwords of `users add` | `$XDG_STATE_HOME/opticli/users/<project>/<name>.txt`, readable by you only (the folders too) | `%LOCALAPPDATA%\opticli\users\<project>\<name>.txt`, in your user profile, which other users can't read by default (opticli sets no ACL of its own) |
| What `types remove`, `remove-property` and `prune` removed (one JSON line each, never rotated) | `$XDG_STATE_HOME/opticli/removals.jsonl`, readable by you only | `%LOCALAPPDATA%\opticli\removals.jsonl` |
| The sync state of `sync pull`, one file per project, source and local database (deleting one takes a new baseline, which writes the source's content over published local changes) | `$XDG_CONFIG_HOME/opticli/sync/<hash>.json`, readable by you only | `%APPDATA%\opticli\sync\<hash>.json` |
| Seals for production, their key, and the mark that one was run | `$XDG_CONFIG_HOME/opticli/seals/<id>.json` and `<id>.used`, `$XDG_CONFIG_HOME/opticli/seal.key`, readable by you only | `%APPDATA%\opticli\seals\`, `%APPDATA%\opticli\seal.key` |
| A sealed apply's site log and what was compared before it started | `$XDG_STATE_HOME/opticli/sealed/<id>.log`, `<id>.drift.json`, readable by you only | `%LOCALAPPDATA%\opticli\sealed\` |

You can set per-project defaults in the user config (`connection`, the development database; `output`, `port`, `https`, `httpsPort` and `scheduler` for `serve`).
`opticli db use` adds the chosen `database` there, `opticli db add` an entry to `databases` (and its role to `roles`,
which holds the roles of every project's databases), and `opticli sites primary --save` the `sites.primary` mapping.
opticli keeps the file's permissions when it rewrites it, and creates it readable by you only on Linux and macOS. A
`roles` entry opticli can't read stops it from using any database until it is fixed.

```json
{"projects": {"/abs/path/to/Site": {"connection": "...", "output": "bin/Debug/net8.0/Site.dll", "port": 5199, "https": true, "httpsPort": 5443,
  "databases": {"prod": {"connection": "Server=...;Database=...;User Id=reader;Password=..."}},
  "sites": {"primary": {"Site A": "localhost:5001", "Site A@nb": "https://localhost:5004",
    "Site B": {"host": "localhost:5002", "keepEdit": true}}}}},
 "roles": [{"server": "sql.example.com", "database": "site", "role": "prod", "setAt": "2026-10-15T09:00:00Z", "setVia": "add"}]}
```
