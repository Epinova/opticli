# CMS 13

The same opticli reads and writes CMS 13 sites (`EPiServer.CMS` 13.x on .NET 10), both fresh installs and databases
upgraded from CMS 12. Reads choose their SQL from the database's schema. `serve` and `env` inject the site agent built
for the site's CMS major (`agent/cms12/`, `agent/cms13/`), taken from the project's packages, else from the database's
schema version; `doctor` shows both as `cmsMajor`. A CMS 13 build against a CMS 12 database is refused (exit 3): the
CMS would upgrade the database at startup, and CMS 12 can't use it afterwards. To upgrade it on purpose, start the site
once yourself. The reverse is refused too.

## Sites are applications

CMS 13 keeps its sites as applications. The old site definition tables are empty on a fresh install and stale after
an upgrade, and opticli doesn't read them there. `sites` lists the applications in the same shape, without `guid`,
plus `application` (the name that identifies it: e.g. `alloy13`, or `Site_<old GUID>` after an upgrade),
`applicationType` (`inProcessWebsite`, or `website` for a headless front end) and `isDefault`. Commands take a site by
its name, id or application name.

- The default application answers host names that no application has, as the `*` host did. `sites host add <site> "*"`
  makes an application the default and takes it from the previous one in the same save (the result lists both).
  `resolve` of a host nobody has shows `host: null`.
- An application has no SiteUrl: its URL is its first primary host (by name), else its first undefined one, and
  follows its hosts. `--keep-site-url` and `--https unset` are `usage` errors.
- A host is http or https. A new host without a scheme or `--https` gets http on `localhost`, `*.localhost` and
  loopback addresses (what `serve` listens on), else the scheme of the site's URL. The upgrade makes every host that
  had no https setting https, so an upgraded site's absolute URLs are https.
- `--type preview` and `--type media` add CMS 13's preview and media hosts (one of each per application, for every
  language; preview hosts only on a headless application). CMS 12 refuses them.
- A request's host matches only with its port. CMS 13 refuses an application whose start page is below another's, so
  there are no nested sites.
- `sites add` creates an in-process application, named from the site's name (`Alloy Site` is `AlloySite`, with a
  number when that is taken), which becomes its display name. `*` among its hosts makes it the default application.

## Scheduled jobs on CMS 13

The database has only the job's class name (`PageArchiveJob`). `jobs` shows the name admin mode shows: for the CMS's
own jobs from a table built into opticli (the names CMS 12 stored), for the site's own from
`[ScheduledJob(DisplayName = ...)]` in its code, else the class name. Either name works wherever a job is named.

## Content types on CMS 13

- Kinds: besides page, block, media, folder and other, `experience`, `section`, `element` and `contract` (an
  interface other types implement). `--kind page` includes experiences and `--kind block` sections and elements; the
  others match only themselves. `types` adds `compositionBehaviors`, `contracts`, `blueprints` and `inlineUses` (inline
  blocks of the type in content's current versions); `type` adds `implementedBy` for a contract and the
  `displayTemplates` the type's content can use.
- CMS 13 records no class for a type whose class has a GUID, only the version of the class's assembly when the model
  sync made or changed it. A type with neither, and with no class in the build that has its GUID, is of **unknown
  origin**: made in admin mode, or a code type a content import overwrote (the Alloy template's own import does that to
  all its types). Once such a type's class is gone, `types --orphaned` lists it with `originUnknown: true`, and `types
  remove`, `remove-property` and `prune` remove it only with `--include-unknown-origin`. Without `serve`, `types
  --orphaned` also reads the GUIDs of the classes in the site's build output, so build it first.
- Against a shared database, types of unknown origin are listed as drift with `informational: true` and don't stop
  writes.

## Visual Builder

An experience is a page made of sections. A section holds rows, a row columns, and a column elements (blocks with the
ElementEnabled composition behaviour). Sections and elements are mostly inline: stored in the experience, with no
content of their own. An element can also be a shared block, placed by reference. opticli reads compositions from the
database, and writes them through the CMS's own composition API, which keeps each node's key.

```sh
opticli get 104 --fields composition          # sections → rows → columns → elements: keys, names, types, styles, values
opticli get 104 --text                        # the same as a tree
opticli display-templates --type TextElement  # the styles (display templates and settings) a node of the type can use
opticli where-used 103                        # names the experience, section and element a shared block is in
```

`get` of an experience (or a section) shows `composition` instead of the two properties it is stored in, `Layout` and
`UnstructuredData` (`--fields Layout` shows the stored value). Each node has `key`, `name` and `type`, styles as
`displayTemplate` and `displaySettings`, and an inline block's `properties` or a shared block's `content`. A
section-enabled block in the outline is listed in `sections` with `"nodeType": "component"`. `search` and `where-used`
name the section and element a match is in, `where-used --type` adds a row (`inline: true`) for the type's inline
blocks, and `allowed-in` lists where a composition takes a type (`property: "composition"`).

```json
{"ref": "104", "type": "LandingExperience", "kind": "experience", "version": "104_105",
 "properties": {"Summary": {"type": "LongString", "value": "Spring campaign"}},
 "composition": {"layout": "outline", "culture": "en", "sections": [
   {"key": "1b1ef8e9-…", "name": "Body", "type": "TwoColumnSection", "inline": true, "displayTemplate": "wide", "rows": [
     {"key": "2715f4d1-…", "name": "Row", "columns": [
       {"key": "f7c9491b-…", "name": "Left", "elements": [
         {"key": "5faf72ec-…", "name": "Intro", "type": "TextElement", "inline": true,
          "properties": {"Heading": {"type": "LongString", "value": "Hello"}}},
         {"key": "7a05f524-…", "name": "Shared", "type": "TextElement",
          "content": {"ref": "103", "type": "TextElement", "kind": "element", "name": "Shared text", "status": "published"}}]}]}]}]}}
```

Writes need `serve`, save a draft unless `--publish`, and take `--dry-run`:

```sh
opticli composition 104 add section --type TwoColumnSection --name Hero --template wide --setting background=dark
opticli composition 104 add row --in Hero
opticli composition 104 add column --in <row key> --name Left
opticli composition 104 add element --in Left --type TextElement Heading="Hi" Body=@intro.html
opticli composition 104 add element --in Left --ref 103          # a shared block, by reference
opticli composition 104 set Hero --setting background=light      # --setting key= removes one, --template "" clears
opticli composition 104 move <element key> --in Right --at 0
opticli composition 104 remove Hero
opticli composition 104 add section --node @section.json         # a whole node as get shows it, children included
opticli set 104 composition=@composition.json --dry-run          # the whole composition, as get shows it
opticli create 6 --type LandingExperience --name Spring composition=@composition.json
```

- A node is named by its key, or by its name when only one node has it; `root` is the composition itself.
- The CMS's rules are checked first and explained in opticli's words: an outline holds sections and section-enabled
  blocks, a section rows, a row columns, a column element-enabled blocks. Display templates and settings are checked
  against the site's (`display-templates`), as errors where the CMS only warns.
- A whole composition (`set`, `create`, a plan) sets structure, order, names and styles as given. A node with a key
  keeps its block, and only the properties given change, as with `set`; a node without a key is new, and nodes left
  out are removed. So `get`'s output can be edited and written back; a value `get` cut short is refused (`--full`).
- Write results list what changed per node (`composition`: `added`, `removed`, `moved`, `changed`). Plans have an op
  `composition` with the command's fields (`opticli apply --help`).

**Content variations** are versions of their own, e.g. for a campaign, that store only what they change over the
published version. `versions` marks them with `variation`, `get --variation <key>` shows one as the CMS loads it
(`--version latest`: its newest), and `drafts` and `find --status draft` count a variation's drafts (`variation`,
`variationDrafts`). `set` and `composition` with `--variation <key>` save a version of that variation, made from the
published version when it has none yet; `publish <id>_<version>` publishes it.

**Blueprints** are templates new content is made from. `create <parent> --blueprint <ref|name> --name <name>` makes
content with the blueprint's type, values and composition (one language, one version; dry runs, `--guid` and plans
work), and `composition <ref> add section --blueprint <name>` adds a section from a section blueprint, with new keys.
opticli doesn't make or change blueprints or display templates.

## Also different on CMS 13

- A fresh CMS 13 database gives administrators no access rights on content. opticli writes all the same, but the edit
  UI shows such content read-only until a role has access (admin mode).
- A site has visitor groups only if it registers them (`services.AddVisitorGroupsMvc()`); without that, a ContentArea
  item with `visitorGroups` is refused ("No visitor group has the id ...").
- The CMS refuses a save whose rich text or references point at content that doesn't exist (a `validation` issue with
  `code: "unresolvedReference"`). So `apply` refuses up front a plan whose rich text links to content the same or a
  later step creates: reorder it, or add the link in a later `set`.
- The edit UI is at `/Optimizely/CMS/`.
- The MCP server for editors is the `OptiCli.Mcp.Cms13` package on CMS 13: see
  [MCP server for editors](../mcp/install.md#mcp-server-for-editors).
