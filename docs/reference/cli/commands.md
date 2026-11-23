# Commands

`opticli <command> --help` lists every option with an example, and a bare `opticli` shows an overview.

## Reading

| Command | Answers |
|---|---|
| `doctor` | project, connection string candidates, database, schema version, site agent, drift (against a shared database), sites whose primary host differs from the saved `sites primary` mapping, installed skill |
| `db list`, `db use`, `db forget` | the development database (see [Which database](databases.md#which-database)) |
| `db add <name> --role R`, `db remove <name>`, `db role <id\|name> R` | other environments' databases by name, and roles: `prod` is only read (see [Environments](databases.md#environments)) |
| `sites`, `languages` | site definitions (CMS 13: applications) and hosts; language branches (`--all` with the disabled ones) |
| `types [--kind] [--unused] [--orphaned] [--sort]` | content types with instance counts and kinds (page, block, media, folder, other; on CMS 13 also experience, section, element, contract); `--orphaned`: types whose class is gone from the code, which the CMS keeps while content uses them (the running site checks every class, `meta.source: agent`; otherwise the site's sources are scanned for its own types, with a warning saying why); see [Orphaned content types](database-maintenance.md#orphaned-content-types-and-properties) for removing them |
| `type <name>` | properties (type, culture-specific, required, tab, order, source line, `[AllowedTypes]`; one that isn't in the code with how many values it has stored), C# class file, views |
| `allowed-in <type> [--kind] [--explicit]` | which ContentArea/reference properties accept a type, from `[AllowedTypes]` in code (`--explicit`: only those whose attribute names the type or a base type, not unrestricted ones) |
| `get <ref> [--lang] [--version] [--fields] [--expand] [--variation] [--all-properties]` | one item, typed and decoded (ContentAreas, local blocks, rich-text links; on CMS 13 a Visual Builder composition, and `--variation` for a content variation); properties without a value are left out unless `--all-properties` (as `null`) |
| `tree <ref> [--depth]`, `children`, `ancestors` | the content tree (`tree`: 2 levels down unless `--depth`, at most 5000 items, then `capped`) |
| `find --type T [--where Prop=value] [--under] [--status]` | items of a type, filtered; `--status scheduled` (a version waits to be published: `publishAt`) and `expired` (published, stop-publish date passed: `expiredAt`), in the master language unless `--lang` |
| `search <text> [--in names\|strings\|all]` | names and text properties containing a string |
| `where-used <ref> [--pages]`, `where-used --type T` | ContentAreas, references, links and rich text pointing at an item (`--pages`: through nested blocks up to pages); `--type`: for every instance of a type |
| `resolve <url>`, `url <ref>` | URL to content, and content to URL per language |
| `versions <ref>`, `drafts [--since] [--by] [--kind] [--type]` | version history; unpublished changes |
| `history <ref> [--since] [--by]` | the CMS's change log for one item: creates, publishes, moves (from, to), deletes and restores, with who and when; the only record of moves and deletes (drafts saved aren't in it) |
| `categories`, `visitor-groups` | the category tree (names `set` takes, selectable, how many items use each); visitor groups (ids ContentAreas store, names, how criteria combine), without their criteria or notes |
| `projects [<id\|name>]` | projects, with how many items each has and how many are marked Ready to publish (`ready`); with a project, its items and each version's status (see [CMS projects](cms-projects.md#cms-projects)) |
| `trash [--since] [--by] [--type]` | what is in the recycle bin: who deleted it and when, what came along (`descendants`), and `originalParent`, where `restore` puts it back (asked of the site while `serve` runs, `meta.source: agent`; otherwise read from the database, with a warning saying why) |
| `jobs [--failed] [--enabled\|--disabled] [--all]`, `jobs log [<job>] [--failed] [--since]` | scheduled jobs: schedule, next and last run, how it ended, overdue, running; their runs with status and message (see [Scheduled jobs](#scheduled-jobs)) |
| `display-templates [--type T]` | CMS 13: Visual Builder display templates and their settings, the styles a composition's nodes can use |
| `blob <ref>` | where a media file lives on disk |
| `export <ref>... [--descendants] [--languages]`, `export --site <name>\|--all` | content of any database, production too, as a plan folder that `apply --update-existing` brings into another one (see [Exporting content](databases.md#exporting-content)) |
| `drift` | what differs between the build and a shared database (needs `serve`; see [Shared databases](safety.md#shared-databases)) |
| `access <ref>` | who may read and edit an item: its access rights, and the ancestor they are inherited from |
| `sql "<SELECT …>"` | anything else, read-only |

A `<ref>` is a content id (`123`), a version (`123_456`), a content GUID, or a URL or path (`/en/about/`,
`https://host/en/about/`). Content from a content provider, such as images from a DAM, shows up as `63__provider`. You
can pass that form back in property values, ContentArea items and links. The id before `__` is local to one database;
the GUID is the same in every environment. `--lang <code>` picks the language branch. The default is the master
language, or the language the URL selects. On CMS 13, Visual Builder blueprints are left out of `tree`, `children`,
`find`, `search` and `drafts` unless `--blueprints`.

## Writing (needs `opticli serve`)

These commands write through the running site: the one `opticli serve` started, or the one you run from your IDE
with [IDE mode](serve.md#running-the-site-from-your-ide-ide-mode) on.
- `set`, `create`, `area` (add, remove or move ContentArea items, shared blocks or, on CMS 12.20+, inline blocks
  stored in the area; a new block is inline or shared as the site's edit UI makes it, see
  [New blocks in a ContentArea](content-areas.md#new-blocks-in-a-contentarea)), `block create`, `upload` and `translate` save a draft
  unless `--publish` is given. `upload <file>` adds a PDF, image or other file (up to 50 MB) as media, as the
  type the site maps its extension to, and prints where the file was stored. `create` doesn't take media types (that
  would be media without a file).
- `area <ref> <Prop> add <block-ref>|--type T [--at n] [--display <option>]`, `remove <n|ref:id>`, `move <n|ref:id>
  <to>` and `set <n>`: positions are zero-based. `--display` is a display option as the site registers it; an unknown
  one is refused.
- `block create --type T --name N` and `upload <file>` take `--for <ref>` (that content's "For this page" folder, made
  if missing) or `--parent <folder>`. Content that can't have such a folder (it isn't `IResourceable`, as a site's
  settings content often isn't) is a `usage` error for `--for`, in a dry run too: use `--parent`. `upload --replace <media>` gives existing media a new file instead: a new version
  of the same media type, which must accept the extension (no `--for`, `--parent` or `--type`).
- `translate <ref> --lang <code>` makes a language branch (`--with-blocks`: the blocks of its "For this page" folder
  too). `translate --remove` deletes the branch with all its versions instead (not the master language, nor a start
  page) and can't be undone: it asks on a terminal, and elsewhere is a `conflict` (exit 5) without `--confirm`.
- `publish`, `unpublish` (takes a published branch offline, as the edit UI's expiry does), `discard` (deletes one
  unpublished version; it can't be undone), `move` and `delete` (to the recycle bin). `delete` stops when other content
  references what it deletes, unless `--ignore-references`.
- `restore <ref>` brings deleted content back out of the recycle bin, as the edit UI's Restore does: below the parent
  it had before it was deleted, which the CMS stores for every move (`trash` shows it as `originalParent`), or below
  `--to <parent>`. It keeps its versions, so what was published is live again. Name it by id or GUID: content in the
  recycle bin has no URL. Something below deleted content can't be
  restored on its own (restore what was deleted), and a parent that is in the recycle bin too is a `conflict`. A
  warning says what is live again: the content when it was published, and how many items came back below it. `move`
  of content in the recycle bin also brings it back (as `sync pull` does), with the same warnings.
- `create`, `block create`, `upload` and `move` put content only where it can go: pages below pages, blocks, media and
  folders in asset folders, and only where the parent type allows the type (`[AvailableContentTypes]` and admin mode's
  settings, as the CMS answers it).
- `access <ref>` with `--grant Role=Levels`, `--user Name=Levels`, `--revoke Name`, `--break-inheritance` or
  `--inherit` changes one item's access rights. Children that inherit follow; nothing is applied to descendants.
  Access rights aren't versioned, so the output shows them before and after. The root, the recycle bin, start pages
  and asset roots are refused, and so is content that contains a start page or asset root (they may inherit from it)
  and a change that leaves no role with Administer. A "For this page" folder and
  the content in it have the access rights of the content that owns the folder (`assetsOf`), so they are refused as
  well, as in the edit UI: change the owner instead. A `--grant` role the site doesn't know is a `usage` error;
  `--allow-unknown-role` accepts one, such as a role an identity provider creates on first sign-in.
- `composition` (CMS 13) adds, moves, changes and removes the sections, rows, columns and elements of a Visual Builder
  experience; `set` and `create` also take a whole composition, `create --blueprint` makes content from a blueprint,
  and `--variation` writes a content variation. See [Visual Builder](cms13.md#visual-builder).
- `sites primary` and `sites host add|remove` change site definitions' host names; see
  [Site hosts after a database restore](database-maintenance.md#site-hosts-after-a-database-restore).
- `sites add` creates a site definition (CMS 13: an application), and `languages enable|disable` changes which language
  branches are enabled, for a database the CMS created from scratch; see
  [A database built from scratch](database-maintenance.md#a-database-built-from-scratch).
- `projects create`, `ready`, `remove`, `publish`, `reactivate` and `delete` work with the CMS's projects, and
  `--in-project <id|name>` (with `--ready`) on `set`, `create`, `area`, `composition`, `block create`, `upload` and
  `translate` puts the draft in one; see [CMS projects](cms-projects.md#cms-projects).
- `jobs run`, `jobs stop` and `jobs set` run, stop and reschedule scheduled jobs; see [Scheduled jobs](#scheduled-jobs).
- `users add` and `users remove` make and remove a local login for a restored database; see
  [Local users](database-maintenance.md#local-users-after-a-database-restore).
- `types remove`, `types remove-property` and `types prune` remove content types and properties that removed code left
  in the database; see [Orphaned content types](database-maintenance.md#orphaned-content-types-and-properties).
- `sync pull --from <name>` brings another environment's content into the local database, writing only what changed;
  see [Pulling content from another environment](databases.md#pulling-content-from-another-environment).
- `apply <plan> --dry-run --seal-for <prod>` and `sync push --to <prod>` prepare a write to production and seal it; only
  the user runs it, with `apply <plan> --db <prod> --seal <id>` in their own terminal. See
  [Writing to production](databases.md#writing-to-production).
- `apply plan.json` runs several operations validated together. Later operations can refer to an item an earlier
  one created as `$id`, in ref fields and as a whole property value: a reference property gets the content, a string
  property its id as text (`"TagId": "$tag"` stores `"456"`). Besides content steps, a plan can enable languages
  and create sites (`language` and `site`; see [A database built from scratch](database-maintenance.md#a-database-built-from-scratch)), and
  put its drafts in a CMS project (`"project"`; see [CMS projects](cms-projects.md#cms-projects)). A
  property value `"@texts/body.html"` is that file's text, as `Prop=@file` is on the command
  line. Files in a plan are relative to the plan file (the working directory for a plan read from stdin, `-`) and
  must stay inside its folder unless `--allow-outside` (which a sealed run refuses). With `"guidNamespace"`, what a
  plan creates gets the same GUIDs in every database, and `apply --update-existing` runs it again: existing content is
  updated (and moved back out of the recycle bin; an upload's file replaced when the bytes differ), and steps that are
  already done change nothing. A plan that stops
  halfway (a failure, Ctrl+C) still reports what it saved, with undo hints and `details.partial: true`.
  A plan leaves one draft per content and language, as the edit UI does: a `set`, `area` or `composition` step on
  content and a language that an earlier step of the run saved a draft of (a set, area or composition, or a create,
  block, upload or translate that didn't publish) saves into that draft. Such a step says `savedInto` (the version and
  the step that made it), and its undo points to that step's. So a later `discard` without a version discards the whole
  draft, with the changes of every step that saved into it. After a step that publishes, schedules, sends for review,
  unpublishes, discards or deletes, the next change starts a new draft (and the undos of the steps that saved into the
  draft point to that step's). Steps with `from`, `baseVersion` or `force`, or a ref that names a version, make a new
  version, and so does a step whose draft someone else saved or published in the meantime (opticli checks the
  database, the site checks again; the step's warning says so). `apply --version-per-step` saves a version per step,
  as earlier versions did; use it on a site whose own code asks for a new version on every save (a saving-event
  handler that sets `RequireNewVersion`), where saving into the draft fails with a `conflict` that says so.

Every write command takes `--dry-run` (`sync push` only ever prepares a sealed plan). Structured values use `--values`, e.g.
`--values '{"MainArea":[{"ref":"456"}]}'`; a value may also be given as `get` shows it (`{"type": ..., "value": ...}`;
a reference in it is written by its GUID, so `get` output from another database names the same content). A list
property is given as an array of its items, not as JSON text in a string: a property type that can't be set from text
(some of Optimizely Forms' lists) is a `usage` error that says so.
`Prop=@file.html` reads a property's value from a file (`@@` for a literal `@`); any other value that starts with `@`
is plain text (`--name @Home`, `opticli search @alloy`).
`set`, `area` and `composition` check that nobody saved a newer version in the meantime (exit 5 on a conflict). `--base-version <id>` pins the version the change is based on, and `--force` skips the check.
`--from published` (or `--from <version>`) bases the change on that version instead of the latest, and the check
stays: newer drafts are left out and stay as they are, and the output lists them (`leftOut`).
A publish puts the whole version live. When someone else saved unpublished changes in it, opticli shows them and asks on
a terminal; elsewhere it fails with `conflict` and `details.reason: "pendingDraft"`, and `--include-draft` confirms.
`set ... --from published --publish` publishes a change without them. Where the site makes shared area blocks, a write
that would save a block's new version over someone else's draft of it is refused the same way, with `details.draft.path`
naming the area item ([New blocks in a ContentArea](content-areas.md#new-blocks-in-a-contentarea)). `--include-draft` doesn't publish
someone else's drafts of the content's other blocks along: those stay drafts unless a publish names them
(`--with-block`).
Write output has the content's public `url` (host-relative, as `get` shows it; for a draft of content never published,
where it will be; none for blocks and folders), so a script can open the page without asking for it, and
`canonicalUrl`, the same URL with the host of the content's site for that language, as the CMS links to it: on a
solution with several sites, which site the content is on (a production host name doesn't answer locally). After a plan
whose `site` step saved, every step's URLs are as the sites have them at the end of the run, also those of content saved
before the site existed.
A save runs the site's own code that reacts to it (handlers of the CMS's content events), which a dry run can't. Write
output's `changes` are what was stored, and `siteChanges` says what that code did, each with a warning: other content it
created, saved, published, moved or deleted, the saved content put below another parent than asked, and, as `changed`,
values the write set that it changed (`properties`: each `property` with what the write `asked` for and what was
`saved`; an empty value is left out).
Output after a publish names `previouslyPublished`, the version to publish again to go back. Content with an approval
sequence isn't published directly (exit 3); `--request-approval` sends it for review instead, as the edit UI does.
Against a shared database that differs from the build, writes stop with `drift` (exit 5) until `--accept-drift
<fingerprint>` (`apply --accept-drift` for a whole plan); dry runs don't stop.
[skill/reference.md](../../../skill/reference.md) documents value syntax and the plan format.

## Scheduled jobs

A restored copy of a production database has its jobs enabled, with next runs in the past. A site started against it
would start every overdue job at once: imports and syncs reach external systems, emails go out, and the built-in
Automatic Emptying of Trash deletes the recycle bin for good. So `serve` turns the site's scheduler off for its run
(`[opticli] Scheduler is off for this run` in the site's output, `scheduler: off` in `serve --status` and `doctor`).
The site still registers its jobs at startup, and `jobs run` still runs one. `serve --scheduler` (or `"scheduler": true`
in the user config) leaves the scheduler as the site sets it, and then warns about the jobs that are overdue;
`doctor` counts them too. `opticli env` leaves it as the site sets it: a site you start yourself is yours.

```sh
opticli jobs                                   # every job: schedule, next run, overdue, last run and how it ended
opticli jobs log --failed --since 1d           # what failed since yesterday, with the jobs' messages
opticli jobs run "Publish Delayed Content Versions"   # run it now (needs serve) and wait for it
opticli jobs set "Content import" --every 1h --next now --dry-run
```

Reading (`jobs`, `jobs log`) comes from the database (`tblScheduledItem`, `tblScheduledItemLog`) and needs nothing
running. A job in the site's code (a `[ScheduledPlugIn]` or, on CMS 13, `[ScheduledJob]` class) has its file and line
as `source`, and one the database doesn't have yet is listed with `registered: false`: the site registers jobs when it
starts. `running: "stale"` means the job is marked as running but the process that ran it stopped pinging. A run's
`finished` is when the CMS logged it, and `started` that less its duration. A job of the site's own assemblies whose
class is gone has `inCode: false` (the CMS keeps a removed job's row). `--all` adds the jobs the CMS hides from the
admin UI (`hidden`).

A `<job>` is its id (GUID), its name, its class (`EPiServer.Util.BlobCleanupJob` or `BlobCleanupJob`), or a part of its
name only it has. Several matches are a `usage` error that lists them.

| Command | Does |
|---|---|
| `jobs run <job>` | runs it now through the site, as the admin UI's "Start manually" does, also while the scheduler is off, as the user `opticli`; waits for it by reading the job tables, shows its status messages on a terminal, and ends with its status, duration and message. Exit 0 when it succeeded, 7 (`job_failed`) when it failed, couldn't start, or was stopped or aborted. `--no-wait` returns once it has started, `--timeout <s>` stops waiting (exit 4, `timeout`) and Ctrl+C too: the job goes on running. Already running: `conflict`. Jobs other than the CMS's own (the site's, and add-ons' such as Commerce or Search & Navigation) run without asking, with a warning in `meta.warnings` (in `error.details.warnings` when the run fails): opticli doesn't know what they change or contact. A run of an overdue job moves its next run on, as in the admin UI. |
| `jobs run <job> --allow-destructive` | needed for the jobs that delete for good: the CMS's Automatic Emptying of Trash, Remove Abandoned BLOBs, Trim Content Versions, Remove Unrelated Content Assets, Change Log Auto Truncate, Notification Message Truncate, Monitored Tasks Auto Truncate and (CMS 13) Remove Unused Content Variations, and Commerce's Remove Expired Carts, Permanently Delete Archived and Remove Expired Lowest Price; and for Archive Function, which moves every expired page to its archive page (each move can be undone by hand). Matched by class name. Without it they are `refused` (exit 3), naming what they do; nothing implies it. |
| `jobs stop <job>` | asks the job the site runs to stop, as the admin UI's Stop does (the job's own code decides when), and waits up to 30 s for its run to end: normally `cancelled`. A job whose class can't be stopped is `refused`; one that isn't running is a `conflict`. |
| `jobs set <job> --enabled true\|false --every 30m\|1h\|1d\|1w\|1mo\|1y\|manual --next now\|<time>` | changes its schedule through the CMS's job repository, as the admin UI's form does; the output has it before and after (schedules aren't versioned). `--every` on a job without a next run needs `--next`; `manual` clears the next run. Units: `m` minutes, `h`, `d`, `w`, `mo` months, `y`, in any case except `M`, which is refused as ambiguous; intervals under a minute are refused. A next run in the past makes it overdue; with the scheduler off nothing starts it there. A change that lets the scheduler run one of the `--allow-destructive` jobs (enabled with a next run, an earlier next run or a shorter interval) needs `--allow-destructive` too; disabling one or making it manual never does. |

`run`, `stop` and `set` take `--dry-run`. Against a shared database they are refused (exit 3), with no override: the
deployed site's scheduler uses the same jobs, and a job writes to the content everyone shares. `jobs` and `jobs log`
work there.

## The coding agent skill

`skill install` installs the Claude Code skill bundled with this opticli (`SKILL.md` and `reference.md`) into
`~/.claude/skills/opticli/`, or with `--repo` into `<repository>/.claude/skills/opticli/` (the git repository around the
working directory, else the site's solution directory), to share it through git. It replaces a copy it installed
itself when its files are unedited; `--force` replaces one with local edits, or one installed by an opticli older than
0.4, which kept no record of it. `skill print [SKILL.md|reference.md]` writes a file of the skill to stdout as-is, for
other agents' instruction files (`opticli skill print >> AGENTS.md`).
`doctor` warns when an installed copy was written for an older opticli.
