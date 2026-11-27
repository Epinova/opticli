# Databases and environments

## Which database

opticli works with the project's **development database**: the one the site uses when you run it locally in
`Development`.

For one run, `--connection` (or `OPTICLI_DB`), `--db` or `--profile` picks the database, only one of them. Otherwise
opticli uses the development database chosen with `db use`, else `"connection"` for the project in the user config
(see [Files](output.md#files)), else:

1. opticli reads the connection string (`EPiServerDB` unless `--connection-name` says otherwise) from the site's
   Development configuration, in ASP.NET Core's order. The first source that has it wins:
   - launch profiles;
   - `ConnectionStrings__EPiServerDB` (or `ConnectionStrings:EPiServerDB`, in any case, or an App Service
     `SQLCONNSTR_EPiServerDB` and the like) exported in the shell, shown as source
     `environment`. A launch profile that sets the same variable replaces it when the site runs with that profile,
     so profiles come first;
   - user secrets;
   - `appsettings.Development.json`;
   - `appsettings.json`.

   If that database is **local**, opticli uses it without asking.

   User secrets are found by the project's `UserSecretsId`. opticli reads it, like `TargetFramework` and
   `AssemblyName`, from the project file and the nearest `Directory.Build.props` (and the files that one imports).
   The project file wins. `$(Name)` references to properties set earlier are expanded. Conditions are evaluated
   when they are simple comparisons or `Exists(...)`, with `Configuration` taken as `Debug`. A value opticli can't
   expand is left as written (such a `UserSecretsId` reads no user secrets), a condition it can't evaluate is
   skipped, and `doctor` warns about both.
2. Otherwise opticli asks once. That happens when:
   - the connection string points at a remote server such as Azure SQL;
   - several launch profiles (or exported variables) disagree;
   - only other environments' `appsettings.{Env}.json` files have one.

   On a terminal it shows a numbered list. Anywhere else, commands fail with `needs_selection` (exit 6), and
   `error.details.choices` lists the options, so a coding agent can ask the user and run `opticli db use <id>`.
3. The choice is saved in the user config file (see [Files](output.md#files)) and holds until that setting changes. If the
   server or database at that source changes, opticli asks again.

```sh
opticli db list               # every connection string: id, name, server, database, local?, role, where it was found
opticli db use a71c3f         # make it the development database (no id on a terminal: pick from a list)
opticli db forget             # back to the default
opticli get 123 --db e02d9b   # another database, for one run (a remote one is flagged in meta.warnings)
```

Other environments' databases can be added by name, and given a role, so production is only ever read: see
[Environments](#environments).

**LocalDB.** The CMS templates (`epi-alloy-mvc`, `epi-cms-empty`, CMS 12 and 13 alike) connect to SQL Server Express
LocalDB with `AttachDbFilename=|DataDirectory|\<Name>.mdf`, and the site points `DataDirectory` at its `App_Data`
folder. On Windows opticli resolves `|DataDirectory|` to the project's `App_Data` the same way; the site started by
`serve` keeps its own value. LocalDB counts as local. Linux and macOS have no LocalDB, so neither the site nor opticli
can use that string there: opticli refuses it (`refused`, exit 3) with a hint to point the connection string at a SQL
Server database by name, e.g. SQL Server in a container (`doctor` shows which file it comes from).

When the database in use is remote, or has a role other than `local`, every response carries `meta.database`
(`server`, `name`, `local`, `development`, `role`). A remote database that isn't the development one also adds a
warning, and so does a production database.

`serve` runs the site only against a local database or the chosen development database. Any other remote
database is refused (exit 3), also one passed with `--connection` or `OPTICLI_DB`, and such a string can't be chosen:
one in the site's configuration, or one added with `db add`, can. To run the site against it, add it to a launch profile in
`Properties/launchSettings.json` (`environmentVariables`, `ConnectionStrings__EPiServerDB`; mind the password in a
committed file), user secrets or `appsettings.Development.json`, then choose it from `opticli db list` with
`opticli db use <id>`, or add it with `opticli db add <name> --role dev` and run `opticli db use <name>`. `serve` then
runs against it without `--connection`.

`serve` sets `ConnectionStrings__<Name>` to the database it runs against and removes other spellings of it inherited
from the shell, so an exported one can't win over the pin. It turns the site's scheduler off for every run (see
[Scheduled jobs](commands.md#scheduled-jobs)), and against a remote one it also turns off, for that run:
- automatic schema updates;
- content type sync;
- the remapping of Dynamic Data Store types whose properties changed.

So the CMS doesn't change a shared database just because a local build starts. Against a remote database, `serve`
also checks before it starts the site:
- EF Core migrations. A migration in the build that the database's `__EFMigrationsHistory` lacks would be applied
  by a site that migrates at startup, so `serve` refuses (exit 3, `details.reason: "pendingMigrations"`) unless
  `--allow-pending-migrations`.
- The CMS schema version. When the database's doesn't fit the build's EPiServer packages, the CMS won't start, and
  `serve` refuses (exit 3, `details.reason: "schemaVersion"`) and says which side to update. EPiServer.Framework 12.17 and later accept a schema one version newer; earlier
  ones (and a version opticli can't read) are taken not to.

Once the site answers, `serve` reports drift: what differs between the build and the database (see
[Shared databases](safety.md#shared-databases)).

## Environments

opticli knows which databases are production, and keeps them read-only and never the default. Add the databases of
other environments by name, with a role:

```sh
opticli db add prod --role prod    # asks for the connection string, and its password without showing it
opticli db add test --role test --connection "Server=...;Database=...;User Id=...;Password=..."
opticli db role e02d9b prod        # give a database the project already has (from db list) a role
opticli db remove test             # forget the entry; its role stays
opticli get /en/about/ --db prod   # read production for one run
```

A database added by name is listed by `db list` and used only when a command names it (`--db <name>`, `sync pull
--from`, `--seal-for`), or once `db use <name>` makes it the development database (never a `prod` one). The
connection string is kept in the user config (see [Files](output.md#files)), with its password, so **save only a read-only
login for production** (`db_datareader`): `opticli doctor` warns when a production entry's login can write (it checks
`db_owner`, `db_datawriter` and INSERT/UPDATE on `tblContent`, and says nothing when it may not ask or can't connect).
Run `db add` at your own terminal rather than having an agent pass `--connection`, which puts the password in the
command line.

The roles are `local`, `dev`, `test` and `prod`. A database on this machine is `local` until it is given another (and only one on this
machine can be `local`); a remote one has none until it is given one, and behaves as remote databases always have (`dev` and `test` do too:
shared mode and the drift gate, see [Shared databases](safety.md#shared-databases)). A role belongs to the **server and
database**, not to an entry: once a database is `prod`, every entry, setting and connection string that points at it
is production, in every project. The server is compared as SqlClient reads it, ignoring case, a `tcp:` (or `np:`, `lpc:`,
`admin:`) prefix, a trailing dot, the default port 1433, leading zeros in a port, an instance name beside a port (SqlClient ignores it), the default
instance's name `MSSQLSERVER` and the loopback names (`localhost`, `127.0.0.1`, `.`, `(local)`, `::1`, `[::1]` are one
server); another name for the same server (an IP address, an alias) is another server. A connection string whose
`Failover Partner` is a production database is production too. Giving a database a weaker role than it has
(`prod` to `test`, say, by `db role` or by a second `db add`) needs a terminal, where you confirm by typing the
database's name; elsewhere it is `refused` (exit 3, `details.reason: "roleLowered"`), so an agent can't make
production writable. `db remove` keeps the role. A local database may be given `prod`; the tests use that.

What `prod` means:
- It is never the development database. `db use` refuses it (exit 3), and a saved choice, a configured `connection` or
  a Development default that turns out to be production is refused with a hint to choose another: a command without
  `--db` never reaches it.
- Reads work when a run picks it (`--db`, `--connection`, `--profile`, `OPTICLI_DB`). Each response has a warning in
  `meta.warnings` and `meta.database.role: "prod"`, and opticli connects with `ApplicationIntent=ReadOnly` unless the
  connection string sets the intent itself. `sql` works too (it is read-only and keeps personal data out).
- `serve`, `env` and every write command (including `--dry-run` and `apply`) are `refused` (exit 3,
  `details.reason: "prodDatabase"`), whichever way the database was picked. The one way to write to production is a
  sealed apply, which only the user runs, in their own terminal: see [Writing to production](#writing-to-production).

### Exporting content

`opticli export` writes content of any database as a plan folder that `apply --update-existing` brings into another
database, with the same GUIDs. It only reads the database (over SQL, never through a site), so it works on production:

```sh
opticli export /en/about-us/ /globalassets/about-us/ --descendants --db prod --blobs ../prod-blobs --out about-us
opticli apply about-us/plan.json --update-existing --dry-run    # against the development database; then without --dry-run
```

- **What**: the refs (one or more), with `--descendants` everything below them; `--site <name>` a site's start page with
  everything below it, the site's assets and the global assets; `--all` every site. Blocks, media and folders in a "For
  this page" folder go with the content they belong to (a folder there is made with `create --for`), and what is in such
  a folder with that folder. Never the root, the recycle bin, the asset roots themselves or content in the recycle bin.
  Every language branch (`--all-languages`, the default), or `--languages en,sv` (an item's master branch always).
- **The folder** (`--out`, default `export-<database>-<time>`; an earlier export's folder is written over, any other
  that isn't empty is refused, also one with a plan of its own): `plan.json`, `files/` with the media files, and `texts/` with rich text
  longer than 4000 characters, which the plan reads as `@file` values.
- **The plan**: each item is a `create`, `block` or `upload` step with the source's GUID and its values, including the
  built-in settings (publish dates, URL segment, menu visibility, simple address, shortcut, sort order and index,
  category); parents come first, and content before what refers to it where references allow it. References to what
  the plan creates are `$id`s, to anything else GUIDs (never ids, which differ between databases), also inside local
  and inline blocks, block lists, rich text and CMS 13 compositions (which keep their node keys, so a second run finds
  them). Values that refer to content created later in the plan (a cycle: A links to B and B to A) are written by a
  `set` once both exist. Other language branches are `translate` steps with their culture-specific values. A branch
  published in the source is published by the last step that writes it; an unpublished one stays a draft, and one
  scheduled for publishing gets a `publish` step with its `publishAt` (a time that has passed leaves it a draft, as the
  source has it); a newer draft than the published version is left out. A top-level `"source"` says where and when the
  plan was read, and the version written of each branch; `apply` doesn't read it.
- **Media**: the files come from `--blobs`, a folder holding the blob containers (a copy of the site's
  `App_Data/blobs`) or an Azure blob container's `https://` URL with a SAS token (read with a plain GET; the token is
  never shown). On the command line the token is visible to other processes (`ps`) and in the shell's history: give
  `--blobs -` to paste the URL at a prompt that doesn't show it, or to pipe it in (`... | opticli export ... --blobs -`). Or, for a local database, from the project's file blob folder (`EPiServer:Cms:FileBlobProvider:Path`,
  also from the environment). Media whose file isn't found is left out and listed; `--no-media` leaves all media out.
  Applied again, an upload whose file changed gets the new file.
- **The result** counts items, steps, kinds, types, languages, files and `texts`, and `heldBack` (values a later `set`
  writes, where content refers to content the plan creates after it), and lists `skipped` items, newer `drafts` left
  out, `externalRefs` (content the plan refers to but doesn't create, with its name and whether it is missing or in
  the source's recycle bin: the target must have it, or the dry run fails with `not_found`), `visitorGroups`
  personalized values show to, `dropped` values that can't be written to another database (a value of a property the
  source's code no longer has, a reference to content that doesn't exist (in a ContentArea only that item), rich text
  with an inline block (whose values the CMS keeps apart from the text, and no write takes), an empty CMS 13
  composition on a branch other than the master branch, which the CMS can't save safely), references
  to a content provider's content (`providerRefs`, kept as they are), and `notExported`: access rights, other versions
  and who saved them when, and everything that isn't content (Forms submissions, visitor groups, categories, users).

### Pulling content from another environment

`opticli sync pull` keeps the local database's content in step with another environment's. It is an export of the
source (read over SQL only, so production is fine), a diff against the local database by GUID, and an `apply
--update-existing` of only what changed, through the local site:

```sh
opticli db add prod --role prod                    # once, at your terminal: a read-only login
opticli serve                                      # the local site, on the current build
opticli sync pull --from prod --all --blobs ../prod-blobs --dry-run
opticli sync pull --from prod --all --blobs ../prod-blobs --delete
opticli sync pull --from prod /en/news/            # a ref (or several) and everything below it; --site <name> for one site
```

- **What it does with each item**, matched by GUID: what the local database lacks is created; what differs is updated
  (a new version, published when the source's branch is; a newer draft in the source is left out and listed in
  `drafts`); what the source moved is moved (a `move`, then the update); what is in the local recycle bin and live in
  the source is restored. With `--delete`, content synced before that the source no longer has, or has in its recycle
  bin, is moved to the local recycle bin (never deleted for good; content the source has in its recycle bin counts as
  synced before even on the first pull, since its GUID says it came from there); without it, it is listed in `gone`.
  Content only in the local database, never synced, is left alone, and so are language branches only it has.
- **The sync state** (in the user config folder, see [Files](output.md#files)) records per item and language the source's version
  that was read and the local version it became (with when it was saved: a local database restored from a backup reuses
  version ids), so the next pull reads only what changed in either. The first pull of
  a source takes the **baseline**: every item is compared, what differs is updated. The baseline can't tell a local
  change from the source's, so a published local change made before it is written over (it stays in the history; a
  local draft newer than the published version is a conflict instead): look at `updated` in its `--dry-run` first. The
  same goes for a pull without its state (another project folder, another user config, a deleted state file). A **local
  change** since the last pull (a branch saved, a language branch made locally that the source has too, the item moved
  or moved to the recycle bin) to content the source changed too is a **conflict**:
  listed in `conflicts` and left as it is; `--overwrite-local` writes the source's content over it (the local versions
  stay in the history, and other people's drafts are included). Content changed only locally is listed in `kept`. Before
  the baseline exists, a local draft newer than the published version counts as a local change. A local trash of
  synced content is a conflict too; `--overwrite-local` restores it.
- **The content model**: the source database's content types and properties are compared with the local database's,
  which holds the build's once its site has started on that build (types by GUID, then name; properties by name; a
  property the code no longer has isn't in the build). Content of a type the build lacks is skipped, a property it
  lacks or has as another type is left out of the values, each listed (`model`, `skipped`). `--strict` fails instead
  (exit 5, `details.reason: "modelDiffers"`).
- **What the local database lacks**: a value that refers to content the local database doesn't have (and the pull doesn't
  create), to a visitor group or a category it doesn't have, is left out and listed in `dropped` (in a ContentArea, only
  that item); content whose parent isn't there is skipped.
- **Media** comes from `--blobs` as for `export` (for a local source, the project's file blob folder by default). A media
  item's file is compared with the local site's (`EPiServer:Cms:FileBlobProvider:Path`, also from the environment);
  where that can't be read, the upload goes to the site, which compares the bytes.
- **Output**: `counts` and lists per action (`created`, `updated`, `moved`, `restored`, `trashed`, `conflicts`, `kept`,
  `gone`, `skipped` with reasons), `dropped`, `drafts` (refs are the source's), `model`, the media `files`, the number of
  plan `steps` and `run`, `apply`'s report with each step's status and undo hints. An empty diff applies nothing:
  `steps` is 0 and `run` null. `baseline` is true on the first pull from a source, and `state` is the sync state's
  file. `--languages en,sv` limits a pull as it does `export`. Progress goes to stderr on a terminal. `--out <dir>` keeps the plan the pull applied.
- **Ctrl+C** (or a failed step) stops the plan; the sync state records only what was saved, so the next pull carries on
  where it stopped, and the error's `details.run` lists what was saved with undo hints.
- **Where it may write**: only a database with the role `local` (refused for `dev`, `test`, `prod` and a remote
  database without a role, exit 3, `details.reason: "syncTarget"`): a shared database is other people's, and in shared mode its database doesn't hold
  the build's model. `--from` is picked like `--db` (a name from `db add`, an id or a database name); `--connection` and
  `OPTICLI_DB` name the local database.
- **Not synced**: access rights, other versions and who saved them when (the local history says the sync wrote it),
  Forms submissions, visitor groups, categories, users, the schema and content types. A bacpac (or a database backup)
  is still the tool for an empty local database, for the schema and what isn't content, and where only a bacpac of
  production can be had; `sync pull` is for keeping a restored copy, or part of one, current afterwards.

How long it takes, on a local copy of the edge-case site with 16 000 pages and blocks: the first pull into a database
that has none of them about 6 minutes (a minute to read, compare and check the plan, then about 20 ms a step through the
site); the baseline of a copy that has them all, 20 seconds; a pull after 50 edits in the source, 7 seconds; one with
nothing changed, 3–5 seconds (it compares versions only). A site whose saves run event handlers (search indexing, cache
warmers) writes slower.

### Writing to production

Content gets to production in one command that the user runs in their own terminal. A coding agent prepares it and
checks it, and *seals* it; it can't run it, and opticli doesn't let it:

```sh
opticli sync pull --from prod --all                                 # first: the local database is a recent pull of production
opticli sync push --to prod /en/campaign/                           # what changed locally, as a plan, sealed; or:
opticli apply campaign.plan.json --dry-run --seal-for prod --update-existing
# both print the command for the user (data.userCommand):
opticli apply /path/to/plan.json --db prod --seal 7f3a9c1e0b7d4e55  # the user, at their terminal
```

**Sealing** (`apply <plan> --dry-run --seal-for <name>`, an agent may do it) checks the plan against the local site (a
dry run, as `apply --dry-run` does; the development database must be `local`, with its site running on this build),
reads production over SQL with its saved read-only login, and writes nothing there:
- the plan may only create, change, move, translate and publish content, and upload media files, by GUID (or `$id` of
  what it creates): every other op is refused, among them `delete`, `unpublish`, `access`, `discard`, `site`,
  `language` and the Commerce ops (`relation`, `price`, `warehouse`, `inventory`, `market`), and so are a translation's
  `remove`, `includeDraft`, `force`, `"project"`, version ids and a blueprint not named by GUID are refused (`refused`, exit 3, `details.reason: "sealedPlan"`, every problem listed), and so are content ids
  and URLs (in refs, a ContentArea item's `ref`, the value of a reference property), which name other content in another
  database. A create or upload needs a fixed GUID (`guid`, or `guidNamespace`), so a second run can't make a copy (an
  upload with `replace` keeps the media's own GUID). An
  `upload` (or `replace`) is only sealed for a site whose build has the CMS's Azure blob provider (`EPiServer.Azure`, on
  CMS 13 `EPiServer.Blobs.Azure`): the run writes files only to production's blob storage, which the user gives it;
- production's content model must be the build's: compared with the local database's (which holds the build's model)
  both ways, by GUID and name, and the local site's code with its own database. One kind of difference is accepted:
  content types and properties only the database has (classes or properties removed from code that the CMS kept for
  their content, or newer code in production), since a sealed plan doesn't write them: a step that creates or changes
  content of such a type, or sets such a property, is refused (`sealedPlan`). The seal records them by name
  (`leftovers`), the run shows them before you type anything, and the run's site may find those and nothing else.
  Any other difference (what only the build has, a property of another type, stores, migrations) is a `conflict`
  (exit 5, `details.reason: "modelDiffers"`);
- the seal records the plan's SHA-256 (plan.json and every file it reads), production's server and database (as roles
  key them), the project, a fingerprint of the build output (every file in its folder, the project's `appsettings*.json`
  and user secrets), production's model fingerprint, and the newest and published version (with when it was saved) of
  every item the plan touches, and whether the items it only names are there. It is `seals/<id>.json` in the user config
  folder, with a MAC (HMAC-SHA256, keyed with `seal.key` there), and is good for 24 hours, once.

Sealing takes no plan from stdin, `--allow-outside`, `--accept-drift` or `--version-per-step`. A development database
that isn't `local` is `refused` (`details.reason: "sealValidation"`), and so is a `--seal-for` database that isn't
`prod` (`"sealTarget"`).

`sync push --to <name> (<ref>... | --site <name> | --all)` builds such a plan from the local database and seals it: an
export of the local content, compared with production by GUID, with only what differs (created, updated, moved, or
restored from production's recycle bin). It leaves out media (listed, unless production has it as it is here: in the same
place, with the same values and stored file; upload or change it in production's edit UI), unless `--media` is given: then
media production lacks, or has with another file or other values, is uploaded with its file from the local site's blob
folder, and the run asks for production's blob storage. It also leaves out content
production already has as it is, content with someone's unpublished changes in production, and content production
changed since the last pull, or that was never pulled from it (`conflicts`: the pull's sync state says what production had
then; pull first). It never deletes. The plan folder (`--out`, default `push-<database>-<time>`) must stay until the
command has run.

**The run** (`apply <plan> --db <name> --seal <id>`, the user only) refuses without a terminal on stdin, stdout and stderr
(`refused`, exit 3, `details.reason: "sealNeedsTerminal"`, with the command in `details.userCommand`), takes no other
option (the seal's are used), and:
1. checks the seal: its MAC and that it wasn't used, the database `--db` names (production, the same server and database),
   the project, the plan file and its hash (read once: the plan runs from those bytes, so a file swapped afterwards isn't
   what runs), the build, and its age. Another target, project or plan file is `refused` (`details.reason`: `sealTarget`,
   `sealProject`, `sealPlan`; also a saved production string with a `Failover Partner`); an expired seal, a changed
   plan or build is a `conflict` (exit 5; `sealExpired`, `planChanged`, `buildChanged`): seal again;
2. shows what the plan does (counted from the plan it read: steps, types, new and existing content, publishes, and steps
   that set an expiry that has passed), the target, the build (and its git commit), and asks for the database's name.
   From here on the run names the target as `--db` does (a staging database with the role `prod`, say), not as
   "production";
3. reads production again with the saved login: its model and every touched item must be as sealed (`conflict`, exit 5,
   `details.reason: "productionChanged"`, `details.changes` says what changed: seal again);
4. asks for the write login (user, and the password without showing it), used for this run only and never written
   anywhere (not the seal, the log, the output or the environment). It replaces the saved login's user and password; a
   connection that signs in as the user (integrated security, Microsoft Entra ID) asks for nothing and writes as them.
   With Microsoft Entra ID the run's site gets the access token the run signed in with (on its stdin line, like a write
   password) instead of fetching its own, so it writes as exactly the identity the run checked (a starting site fetching
   its own token could stall until Azure SQL dropped the login). Should the token expire during a long run, the site
   fetches its own from then on;
5. when the site's build has the CMS's Azure providers, asks for **production's event bus** (optional) and, when the plan
   uploads, **production's blob storage** (required then; see below), and checks them before the seal is used;
6. marks the seal used, starts the site against production for this run only and stops it again afterwards (also on a
   failure or Ctrl+C): in shared mode (scheduler, schema updates, content type sync and store remapping off), refusing
   pending EF Core migrations and any drift (nothing is accepted), on `127.0.0.1` only (a site that also listens elsewhere is
   stopped before anything is written), with no `serve` state (no other command finds it), the token and the connection
   string handed to it on stdin rather than in its environment. It serves opticli's agent and nothing else: the site's
   pages, edit UI, login and other endpoints answer 404 while it runs, so no other program on the machine reaches the
   site while it runs in `Development` with the write login;
7. checks the plan against production (a dry run of every step), checks production once more, right before the first
   save, and applies it. A publish that would put someone else's unpublished changes live is refused, as everywhere
   (no `--include-draft`). Where the site makes new area blocks shared, a block a `publish` step names (`withBlocks`)
   whose latest version is a draft someone else saved stays a draft too, with a `warning`: in a sealed run naming a
   block takes along only the run's own drafts of it, never a production editor's (sealing's dry run does the same).
   When that leaves a publish of content published already with nothing to publish, the step is `unchanged` rather
   than a `conflict`, so the run doesn't stop half-way.

The result says whether production still needs a restart (`restartNeeded`; see the event bus below), and lists every
uploaded file as found in production's blob storage (`media`).

**Production's event bus and blob storage.** A site on Azure (DXP, or its own Azure setup) usually registers the CMS's
Azure event and blob providers only outside `Development`, so the run's site, which runs in `Development`, would neither
tell production's servers about its changes nor write files where production keeps them. When the build has the providers
(`EPiServer.Azure` on CMS 12; `EPiServer.Events.Azure` / `EPiServer.Blobs.Azure` on CMS 13), the run asks for them at the
terminal, like the write login: for this run only, never written to disk, logs, output or the seal, and never from an
option, environment variable or configuration entry. opticli checks Service Bus and Blob Storage with the Azure SDKs
(`Azure.Messaging.ServiceBus`, `Azure.Storage.Blobs`), which ship with the CLI only; the site agent brings no Azure package
into the site: it registers the site's own CMS Azure providers, with the values typed winning
over anything in the site's configuration (its Key Vault may hold another environment's). A site without them is asked
nothing.
- **Event bus** (optional): a Service Bus connection string with a shared access key that may manage (the CMS's provider
  makes its own subscription), and the topic (offered from `EPiServer:Cms:AzureEventProvider:TopicName` in the project's
  `appsettings*.json` when they agree, else the CMS's default `MySiteEvents`). Before the seal is used, the topic must exist
  and have subscriptions (one per server of production; none means the wrong topic or bus); their names (the servers'
  machine names) are shown, so check they are production's. Once the site runs, its event
  provider must be the Azure one with that connection and topic, and right before the first save its own subscription must
  be on the topic (otherwise nothing is written). After the run, the site sends what its provider still has queued
  (the CMS would drop it when the site stops), once the site has raised no cache removal for a few seconds (a save's own
  publish handlers keep raising them for a while), and a probe subscription counts the run's messages on the topic. Only
  then, and when the site's log has no "Failed to send event" before the host began to stop, is `restartNeeded` false
  (removals the site raises while it stops are listed apart, `events.notSentWhileStopping`) (`events.sent` says how many); anything
  uncertain leaves it true, with the restart warning and why (`events.problem`). Enter: no event bus, and the restart warning as before. A bus the site can't reach makes it fail to start (nothing
  written; the seal is used).
- **Blob storage** (when the plan uploads): an Azure Storage connection string (account name and key, or a blob endpoint
  with a SAS token) and the container (offered from `EPiServer:Cms:AzureBlobProvider:ContainerName`, else `mysitemedia`).
  The container must exist before the seal is used (else `refused`, `details.reason: "blobStorage"`; the event bus's
  problems are `"eventBus"`); Enter refuses the uploads (`sealedPlan`) with the seal unused. The site
  must use the Azure provider with that connection and container as its default before anything is written; uploads send
  the bytes the run read and hashed, never the file again. After the run every uploaded file is looked up where
  production's database now points (`<content GUID>/<file>` in the container); one that isn't there with its size, or
  can't be checked (`problem`), is a `conflict` (exit 5, `details.reason: "blobMissing"`) that still carries the run.
  A run that stops part way checks the files it saved too, and its error's details have `events`, `media` and
  `restartNeeded` beside what was saved.

What a seal doesn't cover:
- **The summary counts; it doesn't show the values.** It says how many steps of which kind and type, what is new and
  what production has, not what the steps write. Read the plan (`plan.json` and the files beside it) before you type the
  name. A plan can change any value of the content it touches, its publish dates included: an expiry (`StopPublish`) that
  has passed takes the content offline once it is published (the summary counts those steps).
- **Production's servers keep their cache, unless the run had the event bus.** Without it (or when the run's messages
  couldn't be confirmed on the topic), nothing tells production's servers: changed and moved content stays as it was
  there (a moved page keeps answering at its old URL), and new content is missing from menus and listings, until the site
  restarts or an item goes unread for the content cache's sliding expiration (12 hours by default; content read more
  often never refreshes). Restart the site (every instance; on DXP the environment's web app) after the run. The output
  says so (`restartNeeded`).
- **Your build runs against production, with the write login.** Its startup code, initialization modules, event handlers
  (search indexing, webhooks, emails, cache warmers) and configuration run as they do locally, in `Development`, with your
  local settings (search index and webhook endpoints included). Of your shell's environment, the site gets only what the
  system and .NET need (`PATH`, `HOME`, temp folders, locale, `DOTNET_ROOT`, TLS certificate and proxy settings), and it
  runs on the .NET host opticli runs on (when that has the site's runtime): configuration in environment variables (`EPiServer__...`), profilers and extra
  dependencies stay behind, so settings belong in the sealed `appsettings*.json` or user secrets. The seal makes sure the build is the one that was sealed
  (its output folder, the project's `appsettings*.json` and user secrets; other files the site reads from the project
  folder at run time, such as `App_Data` or views compiled at run time, aren't part of it, and `notChecked` says so);
  it can't say what that code does. Review what the agent changed in the code before you run a sealed apply.
- **Media** only with production's blob storage (above). **Deletes, unpublish steps, access rights and Commerce data**
  (prices, stock, markets, warehouses, catalog relations): never (an expiry that has passed still takes content offline,
  see above). Do those in production's edit UI, or in Commerce's own UI. The run's site refuses them too, whatever it is
  sent: in a sealed run the agent answers only reads and the writes a sealed plan has, never with `includeDraft`.
- **With the event bus, the run's site is one more server of production** for the run's minutes: it receives production's
  cache events too and sends its own, as a deployed instance does.
- Content an editor changes after the last check, while the plan runs, is caught by each step's own checks (a newer
  version is a conflict, someone's draft isn't published), not by the seal.
- Types and properties only production's database has are accepted as they are, by name, as sealed; the run stops before
  writing (`conflict`, `details.reason: "driftNotSealed"`) if its site finds any other difference, or a leftover the seal
  didn't list.

Why an agent can't: the run needs a person at a terminal and the write login, which only the user has (save only a
read-only login for production, see [Environments](#environments)). The seal ties what the user sees to what runs. An agent
can still fake a terminal (`script`, `expect`); that gets it past the terminal check but not the write login. With
integrated security or Microsoft Entra ID, the agent runs as the user and could write to production with any tool, sealed
apply or not: give production a read-only SQL login for reads and a separate login for writes. See also
[Things these rules can't see](safety.md#safety-model).
