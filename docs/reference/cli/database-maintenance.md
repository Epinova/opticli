# Database maintenance

## Site hosts after a database restore

A restored copy of a production database has the production host names. Locally, every site but the one with the `*`
host is unreachable, and absolute URLs (canonical links, sitemaps, structured data, emails) point at production.
`sites primary` points each site at the port the site listens on, through the running site (`opticli serve`):

```sh
opticli sites primary "Site A=localhost:5001" "Site B=localhost:5002" "Site C=localhost:5003" --dry-run
opticli sites primary "Site A=localhost:5001" "Site B=localhost:5002" "Site C=localhost:5003" --save
```

For each pair the site gets the host if it doesn't have it, as its primary host. The previous primary host and the
site's Edit host become undefined: an Edit host on a production name sends the edit UI and preview there, and
without one the CMS edits on any host. The site's URL (SiteUrl) follows the new primary host, `https://` unless
`--https false`, keeping SiteUrl's path. The CMS builds a host's links on SiteUrl's scheme when the host has no https
setting of its own, and adds SiteUrl's host back to the site whenever it is saved. The production hosts stay, so
`opticli resolve https://site-b.example/...` still finds content.

A pair without `@lang` replaces the site's primary host: the one for every language, or, on a site whose only primary
host is bound to a language (a site whose only hosts are for `nb`, say), that one, and the new host is for `nb` too;
the `changes` line says so. With primary hosts for several languages only (or one in a language that isn't enabled),
the new host is for every language beside them, and `meta.warnings` gives the pairs for the rest. A host that is a
primary host already keeps its language, and the language is worked out on the site as it was before the command, so
the order of the pairs doesn't matter and running them again changes nothing. SiteUrl follows a pair without
`@lang`, and a `@lang` pair whose primary host SiteUrl was on. A pair without `@lang` whose host is already one
language's primary host, beside a primary host for every language, is that language's pair (`Site A@sv=...`): SiteUrl
stays, and `meta.warnings` says so. Every primary pair, with or without `@lang`, makes the Edit host undefined (unless
`--keep-edit`): the CMS has one Edit host per site, for every language. `doctor` and `--from-config` read a saved
entry without a language the same way.

Putting a site back: the commands only ever put SiteUrl on a primary host. Once it has moved, a SiteUrl that was on
a host that isn't primary (as on a site the CMS created on its first request) can't be put back with them alone.

A host is `name[:port]`. The default port of the host's scheme is left out, as browsers leave it out of the requests'
host: `localhost:443` is the host `localhost` with https, `localhost:80` with http, while `http://localhost:443` (or
`localhost:443 --https false`) keeps its port. A host a site already has under another spelling
(`www.site.example:443`, from admin mode) is found by the name `opticli sites` shows. IPv6 addresses can't be site
hosts in the CMS; use `localhost`.

| Option | Does |
|---|---|
| `"Site A@nb=localhost:5004"` | the primary host for one language (an enabled one); SiteUrl stays. Without it, the host is for every language, as most are. `meta.warnings` names the languages whose URLs still use a production host. |
| `https://localhost:5001/` | the same host; the scheme sets its https setting. `--https true\|false\|unset` overrides it. |
| `--keep-edit`, `--keep-site-url` | leave the Edit host, or SiteUrl, as they are |
| `--dry-run` | validates through the site and shows each site's end state, saving nothing |
| `--save` | after a real run, saves the pairs (with `--keep-edit` and `--keep-site-url`) in the opticli user config, replacing that project's entries for those sites. Entries are keyed `Site A` and `Site A@nb`, so with sites named both `Shop` and `Shop@en`, saving for `Shop` replaces the `Shop@en` entry too. |
| `--from-config` | runs the saved pairs: after the next restore, `opticli sites primary --from-config` is all it takes. A saved site the database doesn't have is skipped with a warning. |
| `--forget <site>` | drops a site's entries (or one entry, `"Site A@nb"`) from the saved mapping, e.g. for a site that was deleted or renamed |

All pairs are one batch: every pair is checked, and the end state of every site as the CMS would save it, before
any site is saved. Then nothing is saved if one fails, and the error names each failing pair. Should the CMS still
refuse a site while saving (or the database fail), the sites saved before it stay saved, and the message says
`Already saved: ...; not saved: ...`. A site that is already as asked is `unchanged` and isn't saved, so the command
is safe to run again, also after such a failure. `doctor` compares the saved mapping with the database and says when
to run `--from-config`.

| Problem | Error |
|---|---|
| a host another site has (in any spelling: `localhost:443` is `localhost`), two primary hosts for a site and language, `*` on two sites, `host add --lang` with a language that isn't enabled | `validation`, exit 5 |
| a host with a path, query, other scheme or a bad port, a pair without `=` | `usage`, exit 1 (read by the CLI) |
| a site that doesn't exist, also `"Site A@xx=..."` when `xx` isn't an enabled language (`@xx` is then read as part of the name; the hint says so) | `not_found`, exit 2 |
| adding a host the site has | `conflict`, exit 5 |
| removing the site's last host; anything but `host add --type undefined` against a shared database | `refused`, exit 3 |

`sites host add <site> <host>` adds one host (`--type undefined|primary|edit|redirect-permanent|redirect-temporary`,
`--lang`, `--https`); a host the site has is a `conflict`. `sites host remove <site> <host>` removes one, but never the
site's last host or SiteUrl's host. Both take `--dry-run`.

On CMS 13 the sites are applications, and a few of these rules differ: see
[Sites are applications](cms13.md#sites-are-applications).

Saving through the site clears its site definition cache, so the site `serve` runs uses the new hosts at once. Another
process running the site against the same database (your IDE's) keeps the old ones until it restarts, unless remote
events are set up, or unless it is the site that saved ([IDE mode](serve.md#running-the-site-from-your-ide-ide-mode)). Site
definitions aren't versioned: the output lists every change and the hosts after it.

## A database built from scratch

A database the CMS created itself, from an empty database (`CreateDatabaseSchema`, on by default), has no sites, so no
URL renders, and only `en` and `sv` enabled. A test or demo database built from code needs both before its content.
Both go through the running site (`opticli serve`), which clears the CMS's caches:

```sh
opticli languages enable no --dry-run
opticli sites add "Alloy" --start-page 5 --host alloy.localhost:5000 --host alloy-sv.localhost:5000@sv --dry-run
```

`languages enable <code>...` enables language branches. A code the database has no branch for (a fresh one has a
dozen, most disabled) gets one, which Everyone may edit, as admin mode's Add does. A branch that is enabled already is
`unchanged`. `languages disable <code>...` warns about content in the language and about a site whose start page has it
as master language, and refuses to disable every enabled language (exit 3).

`sites add <name> --start-page <ref> --host <host>...` creates a site definition (CMS 13: an application) with that
start page and hosts:

| Option | |
|---|---|
| `--start-page <ref>` | the start page: an id, GUID or URL (CMS 12: a page) |
| `--host <host>` | `name[:port][@lang]`, with an optional scheme (`https://alloy.localhost:5001`), or `*` for host names no other site has. Repeatable. In each language (and among the hosts for every language) the first is the primary host and the rest are undefined |
| `--https true\|false` | the https setting of hosts without a scheme. Default: unset on CMS 12 (links use SiteUrl's scheme); on CMS 13 http for `localhost`, `*.localhost` and loopback addresses, else https |
| `--update-existing` | a site of that name is made to match: the start page set, the hosts added or given the type, language and https setting asked for (a new primary or edit host makes the old one undefined), its other hosts kept. Without it, a site of that name is a `conflict` (exit 5) |
| `--site-assets own\|global` | the site's assets root. `own` (the default for a new site): a folder of its own, at `/siteassets/`, as admin mode makes it: the start page's `SysSiteAssets` folder, created below it when it has none. `global`: the global assets folder every site shares. An existing site's assets root changes only when this is given |
| `--dry-run` | what would be created or changed |

SiteUrl (CMS 12) is the site's primary host for every language, over http for `localhost`, `*.localhost` and
loopback addresses unless the host says https. A host another site has, a language that isn't enabled and a host type
the CMS version lacks are `validation` errors (exit 5), all of them listed. So is a `SysSiteAssets` folder that
another site has as its assets root already (two sites on the start page), since the CMS doesn't start with that.

A new site gets its own assets folder, as one made in admin mode does, so add-ons that keep data in a site's assets
folder find one, and its media is at `/siteassets/`. The folder is a published content folder named `SysSiteAssets`;
where the start page's type doesn't allow folders below it, they are allowed for that save only. The result's
`siteAssets` says which root the site has (`mode`: `own` or `global`), the folder's id (`folder`) and whether it was
created (`created`; on a dry run: would be). Changing an existing site's assets root (`--update-existing
--site-assets ...`) moves none of its media: the site's `/siteassets/` URLs then lead to the other folder, and a
warning says so. On CMS 13 the application's assets root is set through the CMS's own call, as admin mode's "Use
site-specific assets" does. opticli doesn't delete sites: admin mode does, and the folder stays (in a plan's
results the step's `undo` gives the command that deletes it).

In a plan they are the steps `language` and `site`, so one seed plan builds the whole database:

```json
{"guidNamespace": "3f6d2a9e-6c1b-4f7e-9a51-0d2b8c4e7f10", "operations": [
  {"op": "language", "lang": "no"},
  {"op": "create", "id": "home", "parent": "1", "type": "StartPage", "name": "Hjem", "lang": "no", "publish": true},
  {"op": "translate", "ref": "$home", "lang": "en", "name": "Home", "publish": true},
  {"op": "site", "name": "Alloy", "startPage": "$home",
   "hosts": ["alloy.localhost:5000", "alloy-en.localhost:5000@en", {"name": "edit.alloy.localhost:5000", "type": "edit"}]}]}
```

`lang` is one code or an array, `"enabled": false` disables. A host in `hosts` is a string as `--host` takes it, or
an object with `name`, `type`, `lang` and `https`. `"siteAssets": "own"` or `"global"` is `--site-assets`. A later
step reaches the site's own assets folder by its URL, such as `"parent": "http://alloy.localhost:5000/siteassets/"`. Steps in a language that an earlier `language` step enables are
checked when the plan runs (`deferred` in a dry run), since the database doesn't have it enabled yet, and so are steps
with a URL on a host that an earlier `site` step adds or changes. With
`apply --update-existing` the `site` step updates the site of that name, so the plan runs again without changes. Once a
`site` step saved, the plan's results give every step's `url` and `canonicalUrl` as the sites have them at the end
(a page whose shortcut is an external link keeps that link).
Neither step is allowed against a shared database or in a sealed plan.

Hosts with a port need the site on that port every run: `serve --port 5000 --https-port 5001`, or `port` and
`httpsPort` in the [user config](output.md#files).

## Local users after a database restore

A restored copy of a production database has production's users: often accounts from another identity provider, or
local ones whose passwords nobody knows here. `users add` makes a login to sign in with locally, through the site's own
ASP.NET Identity (`services.AddCmsAspNetIdentity<...>()`, the user class the CMS UI uses, found at runtime), as the
CMS's first-admin registration does:

```sh
opticli users roles                    # the roles, how many users each has, and what CmsAdmins, CmsEditors, ... map to
opticli users add dev --dry-run        # the name, the password against the site's rules, the roles
opticli users add dev                  # in WebAdmins (--role, repeatable, for others); a missing role is created
opticli users remove dev               # only a user opticli made
```

| Command | Does |
|---|---|
| `users add <name> [--role R]... [--password-stdin]` | makes the user, approved, with the address `<name>@opticli.localhost` (it reaches nobody) and the claim `opticli:created`, in `WebAdmins` (the role the first-admin registration uses) unless `--role` says otherwise; a role that doesn't exist is created (`createdRoles`). A warning says when the roles don't give `CmsAdmins` (admin mode) on this site. The password: `--password-stdin` reads one line; on a terminal opticli asks without showing it; otherwise it generates one and writes it to a file only you can read (`passwordFile`, see [Files](output.md#files)), never to the output. If the site's answer gets lost, the file is kept as `<name>.txt.<id>.pending` and the error names it (the site may have made the user); a later run for the name doesn't remove it, `users remove` does. The site's password rules apply (`validation`, exit 5). A name that exists is a `conflict`. |
| `users remove <name>` | removes a user `users add` made (the claim `opticli:created` with the value `true` and the `opticli.localhost` address; in a user store without claims, the address), and its password files. Any other user is `refused` (exit 3); roles stay. |
| `users roles` | role names with their member counts, the virtual roles each gives, the virtual roles (mapped ones with the roles that give them), and how many users opticli made. No user names or addresses. |

All three go through the site (`serve`) and take no part of an existing account. Against a shared database they are
refused (exit 3), even `users roles`: its users are real, and opticli doesn't even count them there. On a site whose
CMS users come from OpenID Connect, Opti ID or another identity provider (no ASP.NET Identity), they are refused (exit
3) with the user provider the site has: sign in through that provider. `doctor` doesn't hint at `users add`: whether
anyone can sign in locally depends on password hashes and external logins in the user tables, which are personal data.

## Orphaned content types and properties

When a content type's class or a property is removed from the code, the CMS deletes it from the database when the site
next starts, but only if nothing uses it. A type with content (also in the recycle bin), or a block type a property
still has, stays; so does a property with stored values, marked `existsOnModel: false`. Old databases collect years of
them, and admin mode removes them one at a time. `types --orphaned` and `type <name>` show them; these commands remove
them through the site (`opticli serve`), with the CMS's own `IContentTypeRepository.Delete` and
`IPropertyDefinitionRepository.Delete` (on CMS 13 the type saved without the property), as admin mode's Delete does.
On CMS 13, types of unknown origin go only with `--include-unknown-origin` (see
[Content types on CMS 13](cms13.md#content-types-on-cms-13)):

```sh
opticli types prune --dry-run                          # what would go and what stays (kept), with why
opticli types remove OldNewsPage OldTeaserBlock        # these types; all are checked first, or none goes
opticli types remove-property ArticlePage OldIntro --dry-run   # the values that would go with it
opticli types prune --properties --allow-destructive --dry-run
```

| Command | Does |
|---|---|
| `types remove <type>...` | removes content types whose class the running site can't load. Refused (exit 3): a type made in admin mode (no class on record), one of the CMS's own, one whose class the site loads. `conflict` (exit 5): content of the type, also in the recycle bin, inline blocks of it in ContentAreas, a property that has it as its block type (unless that property's type goes in the same run: it is removed first), or page-type properties whose values name it (the CMS would clear those values; the error lists the versions holding them, at most 20). Every named type is checked before any is removed; `details.validation` has each one that can't go. |
| `types remove-property <type> <property>...` | removes properties that aren't in their type's code (`existsOnModel: false`), on a type defined in code. The CMS deletes the values with them, in every version and language, values inside a block property and category selections included: a property with values needs `--allow-destructive` (refused, exit 3, otherwise), which nothing implies. A property a content provider (a catalog, a DAM) uses is a `conflict` (exit 5) with no override: opticli can't count or see those values, and the provider may keep them outside the CMS's tables. A property added in admin mode to a type that has a class looks the same in the database, so check before removing one; a property of a type made in admin mode is refused. |
| `types prune [--properties] [--allow-destructive]` | every orphaned type that can go, and with `--properties` every orphaned property (with values only with `--allow-destructive`); `kept` lists the rest with the reason and the counts. A block type whose only users are properties or types removed in the same run goes after them. |

The output records what was removed, to make it again by hand: each type's name, GUID, base, class, display name and
properties (data type, block type, culture-specific, required, tab, ...), the types it allowed below it and those that
allowed it, and each property's stored values (`content` items and `versions`); on a terminal every field is shown in
full. Content types and properties aren't versioned and there is no undo, so the site also writes each record, just
before the CMS removes it, as one JSON line to `removals.jsonl` in opticli's state directory (`$XDG_STATE_HOME/opticli/`,
`%LOCALAPPDATA%\opticli\` on Windows; never rotated; `recordFile` in the output), with the time, the site and the
database, and the same line to its output (`serve --logs`). A removal the CMS then refuses gets a line saying so. The
record is written by the site itself, so it is complete however the command ends (a timeout, Ctrl+C: the site removes
nothing more once the CLI stops waiting). A run that stops halfway fails with the records of what it did remove in
`error.details.removed`. Each item is checked once more just before it goes (its values, what uses it), and the run
stops there if that changed; one removal runs at a time per site. The CMS records a type's removal in its activity log (`tblActivityLog`, type
`ContentType`), not a property's. The site `serve` runs uses the new content model at once; another process running the
site against the same database (your IDE's) keeps its cached content types until it restarts. With
[IDE mode](serve.md#running-the-site-from-your-ide-ide-mode) the IDE's site is the one that removes them.

Without `serve` these commands are `unreachable` (exit 4): only the running site can tell which classes it loads, and
there is no database fallback for removing. Against a shared database they are refused (exit 3), with no override: the
deployed site uses the same content model. `apply` has no such steps, and the MCP module no such tools.
