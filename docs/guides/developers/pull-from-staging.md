---
title: Pull content from staging into your local database
summary: Register a staging or test database by name, bring its content into your local database with sync pull, and keep it current without another restore.
audience: developers
order: 4
written-for: 0.30.0
---

Your local database drifts away from staging the day you restore it: editors add pages, move things and publish new
campaigns there. `opticli sync pull` brings those changes into your local database, matched by GUID, writing only what
changed and leaving your own local work alone. This guide sets up the staging database as a named, read-only source,
runs a first pull and the pulls after it, and shows `opticli export` for a one-off copy of part of the tree. Staging
is only ever read; everything is written to your local database through your local site.

## Before you start

- opticli 0.30.0 or newer, run from inside the site's repository.
- A local database with content and the site's schema, usually a restored copy of staging or production (see
  [Work on a restored production database](restored-production-database.md)). `sync pull` doesn't make an empty
  database usable; a backup or bacpac does that.
- A login to the staging database that can read it. A read-only one is enough.
- The staging site's media: a copy of its `App_Data/blobs` folder, or a SAS URL for its Azure blob container.

## Steps

### 1. Add staging by name, with a role

Run this at your own terminal. It asks for the connection string, and the password without showing it:

```bash
opticli db add staging --role test
opticli db list
opticli get /en/ --db staging --fields name
```

`db list` shows `staging` with the role `test`, next to your local database with the role `local`. The roles are
`local`, `dev`, `test` and `prod`. A role belongs to the server and database, so it holds in every project. The `get`
checks that reads work; its response warns that it isn't the development database. For Microsoft Entra ID
(`Authentication=Active Directory Default`), sign in with `az login` first.

### 2. Start the local site on the current build

```bash
git pull && dotnet build
opticli serve
```

`sync pull` writes through the local site, so versions, caches and events behave as in the edit UI. It also compares
staging's content model with your local database's, which holds your build's model once the site has started on it.
So build what staging runs, or newer, and restart `serve` after each build. The local database must have the role
`local`: a pull never writes to a shared `dev` or `test` database.

### 3. Dry-run the first pull

```bash
opticli sync pull --from staging --all --blobs ../staging-blobs --dry-run
```

```json
{"dryRun":true,"baseline":true,"counts":{"created":42,"updated":7,"moved":2,"restored":0,"trashed":0,
 "unchanged":311,"kept":0,"conflicts":0,"gone":0,"skipped":1}}
```

The first pull of a source takes the baseline: every item is compared, and what differs is updated. A baseline
can't tell your local change from staging's, so a page you published locally gets staging's content (your version
stays in the history). Read `updated` before you go on. Also look at `skipped` (with reasons), `dropped` (values that
point at content, visitor groups or categories your database lacks) and `model`.

For an Azure container, pass its URL with a SAS token instead of a folder, quoted:
`--blobs "https://<account>.blob.core.windows.net/mysitemedia?<sas>"`. Without `--blobs`, media whose file can't be
found is left out and listed.

### 4. Check the content model

`model` lists where staging's content types and properties differ from your build's. Content of a type your build
lacks is skipped, and a property it lacks, or has as another type, is left out of the values. That usually means
staging runs code you haven't pulled: check out what staging runs, build, `opticli serve --stop` and `opticli serve`,
and dry-run again. `--strict` turns any difference into a failure (`conflict`, exit 5, `details.reason:
"modelDiffers"`), which suits a script.

### 5. Run it

```bash
opticli sync pull --from staging --all --blobs ../staging-blobs
```

Missing content is created, changed content gets a new version (published when staging's is), moved content is
moved, and content in your local recycle bin that is live on staging is restored. A newer unpublished draft on staging
is left out and listed in `drafts`. Content you made locally and never synced is left alone. Ctrl+C stops it; the next
pull carries on where it stopped.

To pull only part of the tree, give refs (each with everything below it) or one site, and optionally languages:

```bash
opticli sync pull --from staging /en/alloy-plan/ /en/about-us/ --languages en,sv
opticli sync pull --from staging --site Alloy
```

### 6. Pull again later

```bash
opticli sync pull --from staging --all --blobs ../staging-blobs --dry-run
opticli sync pull --from staging --all --blobs ../staging-blobs --delete
```

The sync state in your user config remembers what each item was, so later pulls read only what changed and take
seconds. Content you changed locally since the last pull is listed in `kept` and stays. Content changed on both
sides is a conflict: listed in `conflicts` and left as it is, unless you pass `--overwrite-local`, which writes
staging's content over yours (your versions stay in the history). Without `--delete`, content staging deleted is only
listed in `gone`; with it, it goes to your local recycle bin, never deleted for good.

> **Note:** Don't delete the sync state file to get rid of conflicts. The next pull is then a new baseline, which
> writes over your published local changes.

### 7. Or export a part as a plan

For a one-off copy you can keep, review or commit, export instead. `export` only reads the source and writes a plan
folder with the content's GUIDs, media files and long texts:

```bash
opticli export /en/about-us/ --descendants --db staging --blobs ../staging-blobs --out about-us
opticli apply about-us/plan.json --update-existing --dry-run
opticli apply about-us/plan.json --update-existing
```

Check the export's `externalRefs` (content the plan points at but doesn't create, which your database must have),
`skipped`, `drafts` and `dropped` before you apply. Running the same plan again updates what changed.

## What a pull brings, and what it doesn't

| Brings | Doesn't bring |
|---|---|
| Pages, blocks, media and folders, by GUID | Access rights |
| Every language branch, or those you name | Other versions and who saved them when |
| Published state and built-in settings: publish dates, URL segment, menu visibility, sort order, category selections | Forms submissions, visitor groups, the categories themselves, users |
| Moves, and with `--delete` deletions (to the recycle bin) | The schema and content types: those come from your build |

## Production

`--from prod` works the same way: a pull only reads its source, over a read-only connection. Everything else about
production is read-only too: `serve`, every write and every dry run against it are refused (exit 3,
`details.reason: "prodDatabase"`). The one way to get content there is a sealed apply that you run yourself at your
terminal; see [Writing to production](https://github.com/Epinova/opticli#writing-to-production).

## If something goes wrong

| What you see | What to do |
|---|---|
| `refused` (exit 3), `details.reason: "syncTarget"` | Your development database isn't `local`. Pull into a local copy; `opticli db list` shows the roles. |
| `unreachable` (exit 4), "Start it with `opticli serve`" | The local site isn't running. Start it, on the current build. |
| `login_failed` (exit 4) | Staging refused the login. Run `opticli db add staging --role test` again with the right one. |
| `conflict` (exit 5), `modelDiffers` | You passed `--strict` and the models differ. See step 4. |
| `refused` (exit 3), `roleLowered` | You gave a database a weaker role than it has (`prod` to `test`). That needs your own terminal, where you confirm by typing the database's name. |
| Media missing after the pull | The files weren't found under `--blobs`. They are listed in the output; point `--blobs` at the folder holding the blob containers. |

## Related

- [Environments](https://github.com/Epinova/opticli#environments)
- [Pulling content from another environment](https://github.com/Epinova/opticli#pulling-content-from-another-environment)
- [Exporting content](https://github.com/Epinova/opticli#exporting-content)
- [Writing to production](https://github.com/Epinova/opticli#writing-to-production)
- [Work on a restored production database](restored-production-database.md)
- [Seed content for Playwright tests](playwright-seed-content.md)
