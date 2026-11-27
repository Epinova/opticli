# Changelog

Every release is on [nuget.org](https://www.nuget.org/packages/OptiCli). After updating, run `opticli skill install`
again to update the skill.

## 0.38.0 (27 November 2026)

### MCP module

- **A site's own tools can change things outside the CMS's content.** An action tool
  (`WithActionTool<T>()`, `IOptiCliActionTool<TArgs>`) changes what a site keeps elsewhere: offers or events in a
  system of their own, a search index, a translation service. It previews the change, a target per thing it changes
  with the values before and after, and the module runs the same flow as for content: the dry run the assistant shows
  the editor, a token that runs only what the dry run showed, the publish gate for a change visitors see, the delete
  gate, and an audit line per target. A token runs once: the module keeps a record of each run, and the same token
  again answers with what the first call did, so an assistant can safely repeat a call that got no answer. See
  [Action tools](https://github.com/Epinova/opticli/blob/main/docs/reference/mcp/tools.md#action-tools) and the new
  guide [Let assistants change things outside the CMS](https://github.com/Epinova/opticli/blob/main/docs/guides/mcp/action-tools.md).
- **`IOptiCliEditor.HasAccess(content, level)`** tells a site's tool whether the editor has Edit, Publish or other
  access to content, as the CMS checks it. A site with its own implementation of `IOptiCliEditor` (a fake in its tests)
  adds the member.
- **The consent, connections and error pages have opticli's look.** The page an editor sees after signing in was
  plain text and two grey buttons. It is now a coloured panel with Allow and Deny on a dark bar below it, which also
  names who is signed in and on which site. It follows the browser's dark mode, and on a phone the connections page
  lists each connection as a block instead of a table too wide to read. The pages still load nothing (no images,
  fonts or scripts), so the security policy is unchanged.
- **On CMS 13 the module lives under `/optimizely/opticli`.** CMS 13 keeps its edit UI and login under
  `/Optimizely`, and a site whose login is chosen by path (Opti ID, for one) signs editors in only there, so the
  consent and connections pages at `/episerver/opticli` couldn't always ask who was signed in. CMS 12 keeps
  `/episerver/opticli`. On a CMS 13 site that already has connections, editors connect their assistant again with the
  new address, or the site keeps the old path with `options.BasePath = "/episerver/opticli"`. The site logs a warning
  at startup when `BasePath` isn't under the CMS's own path, and the Claude Desktop extension tries both paths when
  given only the site's address.
- **Roles come from the editor's login and the CMS together.** The module took an editor's roles from the CMS's role
  provider by user name; now it uses the roles that both the login and the role provider have, and a connection's
  roles can only narrow at each refresh. An editor given a new role signs in again before a connection uses it.
- **A publish that would replace a colleague's newer published version asks first.** `publish_project` with a ready
  item older than the version published since, or `publish_content` of an older version by name, is a `conflict`
  (`newerPublished`) naming who published it and when; `includeDraft` confirms it once the editor agrees, and the
  result says which version was replaced.
- **The edit UI's allowed types apply inside blocks too.** A ContentArea in a local, inline or shared block that a
  write changes now gets the same check as the content's own areas, and a shared block taken over through its page
  follows its own access rights for tabs that require them.
- **A site's own write tools describe what they do.** A tool built with `WithWriteTool` that moves content or puts it
  in the recycle bin now says so in its description, instead of "saves drafts only".
- **The consent page names apps more plainly.** Domains show in their ASCII form (punycode), app names lose invisible
  and direction-changing characters, and every app gets a short reminder to allow only a connection the editor just
  added.
- **The OAuth endpoints take smaller requests.** Token, consent and revoke requests are limited to 64 KB, and the
  token endpoint looks up a client's metadata document only for a code or refresh token of that client, a few at a
  time.

### CLI

- **A sealed apply runs only the steps it lists.** Creating, changing, moving, translating, publishing and uploading
  content by GUID, as before; any other step is refused when sealing, including the Commerce steps added in 0.36.0
  (prices, stock, markets, warehouses, relations), which a seal can't compare with production. The run's site
  enforces the same list itself. Its site also starts with only the environment variables the system and .NET need,
  and the seal lists which project files its build check doesn't cover.
- **Commerce removals on a shared database need `--allow-destructive`.** `relation remove`, `price remove` and
  `market remove` (a plan step's `"remove": true`) are for good, as Commerce has no recycle bin; on a shared database
  they now ask for `--allow-destructive` (a plan step's `"allowDestructive": true`). Local databases are unchanged.
- **`--blobs -`** of `export` and `sync pull` reads the folder or SAS URL from a prompt that doesn't show it, or from
  standard input, so a token stays off the command line.
- **Private files stay private.** The user config is saved readable by you only, also when it was made by hand with
  wider permissions, and `sync pull`'s temporary folder is too.
- **Export file names are cleaned.** The files of long rich text take their language and property names through the
  same cleaning as media file names.

### npm package

- **Arguments come after `--`.** Methods put a command's options first and its arguments last, after `--`, so an
  argument that starts with `-` (a name, a search) is never read as an option.

### Skill

- **Content is data.** The skill tells coding agents that what opticli reads from the CMS is the site's data, not
  instructions, and not to pass paths or text from it as files or shell arguments.

## 0.37.1 (27 November 2026)

### CLI and MCP module

- **A block list written again changes nothing when its items have a key the site makes.** A block list's item type
  can have a property the edit UI hides (`[ScaffoldColumn(false)]`) that the site fills in `SetDefaultValues`, such
  as a `Guid` to tell items apart. A write builds each item anew, so for the CLI and plans that value was new on every
  write: a plan run again with `--update-existing` saved the step again (`saved`, never `unchanged`), and the site lost
  the keys it pairs items by. Now an item keeps what the item of its type at the same position had of such
  properties, unless the item names them (`"Key": null` still clears it), as an edit in the CMS does. Editors already
  kept them.
- **Times in a list of the site's own items no longer count as a change.** A `PropertyList<T>` whose items have
  `DateTime` values (opening hours, say) came back `changed` on every run of the same plan: the CMS reads a list's
  times back as local times (`09:00:00+01:00`), so they never matched the value written (`08:00:00Z`). A value that is
  the current one with its times as the same instants is now left as it is.
- **An unknown option names the command's options.** `opticli get 17 --properties Heading` said only
  "Unrecognized command or argument". The hint now suggests the closest option (`--feilds` → `--fields`) and, for a
  command with a few options, lists them, so `--fields` is there.

### npm package

- **`new OptiCli({ timeoutMs })`** (and `OptiCliSync`) sets how long every call waits, unless the call gives its own
  `timeoutMs`; it was ignored before. `apply` keeps its 10 minutes unless one of them is given. The Playwright setup's
  `opticli` option takes it too, and the workers' clients get it.
- **A timeout says what was stopped.** "no answer within 120 s; it was stopped" read as if the site was stopped. The
  message now says that only that opticli process was stopped, that a site `opticli serve` runs keeps running, and that
  a write may have been saved anyway.
- **The Playwright setup says when it stops the site.** When setup fails, it stops a site it started (unless
  `keepSite`), and now writes a line saying so.

## 0.37.0 (26 November 2026)

### Optimizely Commerce

- **Price types of the site's own are no longer read as `all`.** A site can add price types to Commerce's three in
  its settings (`EPiServer:Commerce:CatalogOptions:SalePriceTypes`, members' prices, say). opticli read every such
  row as `"priceType": "all"`, told apart from the base price only by its amount, and `price set` of the base price
  next to them failed ("Give validFrom", which couldn't help) or updated such a row as if it were the base price. Now a
  site's own type is named by its key in lower case (`MEMBER` is `member`), the number of one the site no longer
  defines as a string (`"7"`), and rows are matched by their type: `all` never names a `member` row.
  `--price-type` (a plan's `priceType`) takes the name in any case or the number; one the site doesn't have is a
  `usage` error that lists the site's types. Such a type takes any `--price-code` or none, kept as given (a JSON rule
  too), up to Commerce's 256 characters. `opticli commerce` lists the site's types as `priceTypes: [{name, id,
  description}]`. `priceType` in the output schema and the npm types is open now: `'all' | 'user' | 'group' | (string &
  {})`.
- **`market set` on a market without lists.** A market written with SQL, with a default currency and language but no
  `MarketCurrencies` or `MarketLanguages` rows, failed every `market set` ("A market needs at least one currency and
  one language"), a rename too. An existing market now keeps its lists as they are unless the change gives them, and a
  default is checked against its list only when it has one. Commerce's save adds the default currency and language to
  the lists, which `changes` then says (`["name", "currencies", "languages"]`).
- **`commerce market remove <id>`** (a plan's `market` step with `"remove": true`) removes a market through
  `IMarketService.DeleteMarket`, with `--dry-run`; `removed`, or `absent` when there is none. Commerce removes the
  market's prices with it and takes it off payment and shipping methods, which a warning says; orders and carts keep
  naming it. Commerce's own `DEFAULT` is refused. The removal clears Commerce's price caches (on every server), so
  reads and price writes don't meet the removed prices. In a plan, a `price` step in a market an earlier step removes
  is a `usage` error in the dry run, naming that step, unless a step between them makes the market again. The undo hint of a new market is now `commerce market remove`. A
  removal that found none has no `market`, so the npm package's `MarketResult.market` is optional now (as
  `PriceResult.price` is): TypeScript asks for a check where code reads it.
- **Translating catalog content.** `translate` takes catalog content, into a language of its catalog: on Commerce 15 it
  makes the language branch, as the CMS does (opticli 0.36 refused it, so catalog content couldn't be translated
  there); on Commerce 14, which keeps catalog content in every language of its catalog, it saves into that language as
  `set --lang` does (`existing: true`). A plan's `translate` step runs again `unchanged` on either major. See
  [Languages and translations](https://github.com/Epinova/opticli/blob/main/docs/reference/cli/commerce.md#languages-and-translations).
- **Fix: a translation on Commerce 14 needed `--include-draft`.** Commerce 14 saves a copy of its own of catalog content
  in each language of its catalog (as `system`), which opticli counted as someone else's draft, so publishing a
  translation was a `conflict` and confirming it also waved through real drafts. Commerce's copy is left out now, only
  as long as it is what Commerce made: the first version in that language, saved by Commerce right after a save in
  another language, with no value of that language but the type's defaults and the address Commerce gives it. A draft someone else saved in
  that language, or a later one, still stops the publish. Commerce 15 makes no such copies, so there nothing is left
  out.
- **Fix: `commerce get --lang` on Commerce 15 showed a language never published without its draft's values** (the
  version was the draft's, the values what Commerce serves in that language). It shows the draft now.
- **A language the catalog doesn't have is refused.** `create`, `set` and `translate` of catalog content in a language
  that isn't its catalog's (`DefaultLanguage` or `CatalogLanguages`) are a `usage` error before anything is saved, with
  the `set catalog:... --values '{"CatalogLanguages": [...]}'` that adds it. Commerce 14 refused it with a hint to
  `translate`, Commerce 15 saved a version it then couldn't load, and `create` saved the content in the default
  language instead. In a plan, a step in a language an earlier step adds to its catalog is `deferred` in the dry run,
  so a seed plan can grow by a language.
- **`--name` in another language is refused.** Catalog content has one name in every language, the master language's:
  Commerce dropped a name given with `--lang` in another language, though the result listed it as changed. It is a
  `usage` error now, which says to translate `DisplayName`.
- **`catalogLanguages`** on `commerce children` items: the languages of the item's catalog.
- **Docs:** `CatalogLanguages` (a catalog's languages, and how to add one) in the reference, and a catalog in two
  languages in the guide [Seed a Commerce catalog for Playwright tests](https://github.com/Epinova/opticli/blob/main/docs/guides/developers/seed-a-commerce-catalog.md).

## 0.36.0 (24 November 2026)

opticli writes and reads Optimizely Commerce 14 (CMS 12) and 15 (CMS 13) sites, through the running site, so an E2E
suite can seed a Commerce site's catalog from code. See
[Optimizely Commerce](https://github.com/Epinova/opticli/blob/main/docs/reference/cli/commerce.md) and the guide
[Seed a Commerce catalog for Playwright tests](https://github.com/Epinova/opticli/blob/main/docs/guides/developers/seed-a-commerce-catalog.md).

### Optimizely Commerce

- **A Commerce agent.** `serve` gives a site whose build has `EPiServer.Commerce.Core` a second agent, shipped in the
  tool and built for Commerce 14 and 15, beside the site agent; a site without Commerce runs exactly as before.
  `serve`, `serve --status` and `doctor` show `commerce` (the site's version, and whether the agent loaded, or why
  not); `doctor` shows the version the project builds against (`project.commerceVersion`). `OPTICLI_COMMERCE=off` in
  the site's environment turns it off.
- **Catalog content with `create` and `set`.** Catalogs, nodes, products, variants, bundles and packages are content:
  `create` and `set` write them with the catalog model's types, by the site's own `IContentRepository`, with fixed
  GUIDs, dry runs and `unchanged` on a re-run, in plans too. Catalog refs: `code:<code>`, `catalog:<name>`,
  `catalog-root`, GUIDs and `id__CatalogContent`. What catalog content doesn't take (projects, approvals, schedules,
  ContentAreas) is refused before anything is saved.
- **Relations, prices, warehouses, stock and markets.** `commerce relation add|remove` (a product's variants, a
  bundle's or package's entries with a quantity, an entry's extra category), `commerce price set|remove` (a price keyed
  by market, currency, price type and code, minimum quantity and valid-from; without valid-from the base price, a
  campaign price with valid-from and valid-until beside it), `commerce warehouse set`, `commerce inventory set` and
  `commerce market set`, each with `--dry-run` and a status (`created`, `updated`, `unchanged`, `removed`, `absent`),
  and each a plan step (`relation`, `price`, `warehouse`, `inventory`, `market`). A plan's `$id` of catalog content
  works in those steps and in a CMS page's properties (a start page's `Catalog`).
- **Reads through the site.** `commerce` (version, catalog root, catalogs), `commerce get|children|tree` for catalog
  content, and `commerce relations|prices|stock|warehouses|markets`. `get`, `children` and `tree` read the CMS
  database, where catalog content isn't, and say so for a catalog ref.
- **The npm package** has a method for each `commerce` command (`commercePrices()`, `commercePriceSet()`, ...), and
  types the new plan steps and their results.
- **Catalog content's refs work.** Commerce's catalog content has ids that its `ReferenceConverter` encodes: a catalog is
  `0x80000000 | id`, so its ref is negative (`-2147483645__CatalogContent`), a node `0x40000000 | id`. opticli only took
  positive provider ids, so the ref a write or `get` printed for a catalog couldn't be passed back to any command. Every
  ref parser now takes negative provider ids, and what opticli prints for catalog content always parses back
  (`--base-version` takes the version a result prints, `1_33_CatalogContent`). A CMS property that points at catalog
  content now shows its ref (`1__CatalogContent`) and `"provider": "CatalogContent"`, not `"missing": true`, and so does
  a rich-text link to it in `get` while the site runs. In reference properties, `code:` and `catalog:` refs work as
  values, and in ContentAreas and reference lists, beside CMS content.
- **Product images.** A catalog item's `CommerceMediaCollection` (its Assets tab) takes media in `create`, `set` and
  plans: an array of media refs (ids, GUIDs, a plan's `$id` of an upload) or `{ref, groupName, sortOrder}`, the whole
  list, `unchanged` when it is as given; what isn't media, and the same item twice, are `usage` errors in the dry run
  too. `commerce get` and a change's before and after show it as `[{ref, groupName, sortOrder}]`.
- **Catalog URLs.** `commerce get` shows the `url` the site's catalog routing gives the item, and `resolve` takes a
  catalog URL back to the catalog ref: the CMS database knows nothing of catalog URLs, so `resolve` asks the running
  site when the database finds nothing (`matchedBy: "catalog"`).
- **Seed plans grow.** A plan's `price` step in a market that an earlier `market` step makes or gives new currencies,
  or an `inventory` step in a warehouse an earlier `warehouse` step makes, is `deferred` in the dry run: checked against
  the market as that step leaves it (its currencies) and in all else, with only the market's or warehouse's existence
  left for the site when the plan runs. So a plan that seeded the database before can grow by a market or warehouse with
  its prices or stock. A rename or any other change leaves the steps after it fully checked.
- **Catalog content is found through the site, not `tblMappedIdentity`.** Commerce's catalog provider doesn't register
  its items in `tblMappedIdentity` (it was empty on a Commerce site after content referenced the catalog): their ids and
  GUIDs live in Commerce's own tables, behind `ReferenceConverter`. opticli resolved provider refs and GUIDs only there,
  so a catalog item's ref or GUID was "not found". Catalog refs (`code:<code>`, `catalog:<name>`, `catalog-root`,
  `id__CatalogContent`, and GUIDs the CMS database doesn't have) are now resolved by the site's Commerce agent. Other
  content providers still go through `tblMappedIdentity`.
- **A version of catalog content is the catalog's.** The site agent looked up a version of provider content without its
  provider (`new ContentReference(id, version)`), so a draft of catalog entry 1 loaded the CMS's content 1 ("Content 1
  has no version 4") or failed on content that wasn't there. Versions keep their provider now. The code is shared, so the
  MCP module (`OptiCli.Mcp`, `OptiCli.Mcp.Cms13`) gets the fix too, for any content provider's content.
- **A plan's `$id` of catalog content is its ref.** A plan bound `$id` to the content's id, which for catalog content is
  the id of other, CMS content: a later step on `$product` wrote to the CMS's content of the same id. It binds to
  `1__CatalogContent` now, in refs and in property values (a start page's `Catalog`).
- **Checked before a dry run answers**, as `usage` errors: a catalog type below CMS content and a CMS type below catalog
  content (which saved a catalog row in the CMS's tables, or failed inside Commerce's save), a price whose end isn't
  after its start, a price in a currency its market doesn't have, a market currency Commerce doesn't have, a market
  language that isn't a culture, a market name another market has, and blocks and media "for" catalog content or below
  it (`upload --for`/`--parent`, `block create`, a plan's `create` with `for`; catalog content has no "For this page"
  folder): the hint says to upload to a media folder and attach the image with `CommerceMediaCollection`.

### CLI

- **`sql` reports SQL Server's error when `--limit` cuts a query that then fails.** If the rows past the limit ended in
  an error, for example a recursive CTE running past SQL Server's 100 levels, opticli dropped the error with the rest
  of the rows. The transaction it rolls back was then gone, and `sql` failed with an `internal` error saying the query
  may have kept its changes. It now reads the rest of the result and reports SQL Server's error as `usage`. For a CTE
  past the recursion limit, the hint names `OPTION (MAXRECURSION n)`.

## 0.35.2 (23 November 2026)

Fixes from a review of the write path, and of the rest of the writes, and from building an E2E suite with opticli. The first four apply where the
site's edit UI makes new blocks in a ContentArea shared blocks in the "For this page" folder
(`UIOptions.InlineBlocksInContentAreaEnabled` off).

### CLI and MCP module

- **`unpublish` no longer publishes block drafts.** Taking content offline (`opticli unpublish`, the MCP module's
  `unpublish_content`) published, along with it, the drafts of the blocks in its "For this page" folder that were never
  published, as a publish does. Taking content offline now puts nothing live, and its dry run lists no blocks.
- **A publish says when a block's draft in review, rejected or scheduled stays behind.** If the latest version of such a
  block was waiting for review, rejected by its reviewers, or scheduled on its own, a publish left the block out
  without a word, also when `--with-block` named it. Where the block would otherwise have gone along (it was never
  published, it was named, or the write or plan run saved it), a `warning` now says why it stayed behind.
- **A block published along with a passed stop date has the date cleared.** A block's draft saved after the block
  was taken offline carries the stop-publish date that has passed. Published along with the content, it went live
  already expired, so nothing of it showed, although `sharedBlocks` said `published`. The date is now cleared, with a
  `warning`, as it already was for the content itself.
- **A failed reschedule is no longer reported as saved.** `publish <version> --publish-at <time>` on a version that
  was scheduled already, where the save failed before storing anything (a site validator, the database), was reported
  as saved with a `siteError` and the new time, while the version kept its old schedule. Now the error is reported as
  it is. A site handler that fails after the save is still reported as a `siteError` on a save that stands.
- **A failed block save names the folder it made.** When saving a new block failed on existing content that had no
  "For this page" folder yet, the error didn't mention that the folder had been made and stays. Now it does, as it
  already did for new content.
- **`block create --for` on content that can't have a "For this page" folder is refused.** On content that isn't
  `IResourceable`, such as a site's settings content, the run failed with a `NullReferenceException` in the agent,
  although the dry run had passed. Both now give a `usage` error that says to use `--parent` (in a plan, `parent`;
  in the MCP module, `create_content` with `parent`).
- **A list property given as text it can't parse is a `usage` error.** A string for a list property whose type can't
  be set from text (`PropertyList<T>` without `ParseItem`, as in some of Optimizely Forms' lists) failed with a
  `NotImplementedException` from the CMS. It is now a `usage` error that asks for the items as a JSON array.

- **An access change on content that contains a start page or asset root is refused.** The root, start pages and
  asset roots themselves were refused, but not a container above a start page. Revoking `Everyone` there could take
  every site below it offline for visitors. It is now refused as a move of such content is.
- **A site tool with `[Authorize]` stops the site at startup.** The module's server runs none of the MCP SDK's
  filters, so `[Authorize]` on a tool registered with `WithTools` or `WithWriteTool` was ignored, and every connected
  editor could call a tool the site meant for some. Such a tool is now refused at registration, with a message saying
  why.
- **Publish results no longer name blocks the editor can't read.** A block in the page's "For this page" folder with
  access rights of its own showed, with its ref, name and who saved it, in a publish's warnings and hints, and could be
  named in `blocks`. It is now left out, and naming it is `not_found`, as for content that doesn't exist.
- **Renaming a folder with content below it needs publishing allowed.** The new name changes the URLs of the media
  below it at once, as moving the folder does, but it passed without the publishing gate (`AllowPublish`), also in a
  site tool's plan. It now passes the same gate as the move.
- **A site tool's plan that touches hidden content is refused whole.** Each step's own `not_found` said which ids of a
  plan were hidden from the editor, and a trash step's `moreDescendants` counted hidden items too. A plan with such a
  step is now one `not_found` that names no step or id, and `moreDescendants` only counts what the editor can read.
- **A save into a project with nothing to change needs Edit.** A write with a project and no changes puts the version
  it is based on in the project as it is, replacing the project's item for that content. That changed the project with
  no access check, so an editor who may only read a page (or an assistant steered by text it read) could swap which
  version a project publishes. Every save into a project, dry run too, now needs Edit on the content, as
  `remove_from_project` does.
- **A block's draft that holds a colleague's earlier changes goes along with a publish only when named.** Whether a
  block's draft was someone else's was told by who saved its latest version alone. A colleague's never-published changes
  in an earlier draft went live with the page once someone else saved the block again. Now they count as that
  colleague's, as a publish of the block itself asks about them, and the block stays a draft with a warning unless
  the publish names it.
- **An editor's move dry run checks the access the move needs.** `move_content` with `dryRun` (and a plan's dry run)
  passed for an editor without Delete on the content, or without Publish where published content goes, and the move
  then failed. Both now check what the CMS's move checks: Delete on the content, Create below the destination, and
  Publish there for published content.

### CLI

- **A plan can't read a file outside its folder through a link's `..`.** A link whose target climbs out through another
  link (`f -> s/../secret`, with `s` linking elsewhere) passed the folder check, because `..` was resolved as text. A
  sealed run (`apply --seal-for`, `sync push`) then read the file outside the folder. Links are now followed part by
  part, as the OS does, and a loop of links is a `usage` error instead of a crash.
- **A plan's dry run checks a later step's schedule and review request on content the plan creates.** A `set` or
  `publish` of planned content was dry-run with the creating step's `publishAt` and `requestApproval`, not its own. A
  `publishAt` in the past passed the dry run and failed the run after the create was saved, and a step sending the page
  for review was refused by the dry run where an approval sequence applies. The step's own are now checked.
- **A plan that saves the same content into two projects is refused before anything is saved.** Content is in one
  project at a time, so the run saved the first step and refused the second, with half the plan saved.
- **A `publish` after an `upload` with `replace` is dry-run with the new file.** The publish was dry-run alone, against
  the media's published version, and refused as "already the published version", although the run would publish the
  new file.
- **`move` of content in the recycle bin says that it brings it back.** `opticli move` takes deleted content out of the
  recycle bin (`sync pull` relies on that), but said nothing. Now it warns, as `restore` does, when the content is live
  again, and how many items below it came back.
- **`restore` of a folder says what comes back below it.** Content without versions, such as a block folder, has no
  status, so restoring it gave no warning, although its published blocks show again wherever they are used. A restore
  now says how many items came back with it.
- **`serve` warns when no site answers on its address.** With a database restored with production host names, or
  hosts on another port (a launch profile's `applicationUrl`, which `serve` doesn't use), every page on serve's address
  was a 404 with nothing to say why. `serve` now warns when no site has a host there (or the `*` host). The warning
  names the sites' hosts, and either the `--port`/`--https-port` of a `localhost` host a site has or the `sites
  primary` command that points a site at serve's address.

## 0.35.1 (19 November 2026)

### CLI

- **`env --connection-name` without `--include-connection` checks the right connection string.** `opticli env` left
  out `OPTICLI_CONNECTION_NAME` unless the connection was pinned, so the site agent checked `EPiServerDB` instead of the
  site's own connection string: a site without an `EPiServerDB` string refused to start, and one with both had the
  wrong string checked. The name is now always passed when it isn't `EPiServerDB`.

### Documentation

**The reference moved out of the README.** The README is now a quickstart with a documentation index (it is also the
NuGet packages' page). The reference is in [docs/reference/](https://github.com/Epinova/opticli/tree/main/docs/reference),
one page per area for the CLI (`cli/`) and the MCP module (`mcp/`), and building, testing and releasing are in
[docs/development.md](https://github.com/Epinova/opticli/blob/main/docs/development.md). The README keeps a short
section under each of its old top-level headings, so links to them still land.

Every section was checked against the code on the way:
- 13 options the README never named are documented: `tree --depth`, `allowed-in --explicit`, `jobs --enabled`,
  `--disabled` and `--all`, `export --all-languages`, `area --display`, `composition --node`, `block create --parent`,
  `upload --parent` and `--replace`, `translate --remove` and `--confirm`, `access --allow-unknown-role` and
  `apply --allow-outside`.
- `get --all-properties` and the `skill` commands (`skill install --repo` and `--force`, `skill print`) are in the
  CLI reference too, and a test now fails when an option of a command isn't named there next to its command.
- `area add --if-missing` never existed: an add counts a matching block as there already when a plan runs again with
  `apply --update-existing`, or with `ifMissing` in the MCP module's `areaOps` (the skill said so too).
- opticli runs on .NET 8, or on the next newer runtime when 8 isn't installed, not on the newest one.
- The MCP module's reference lists its own tools, and its security model gives the limits and refusals it left out.
- Smaller corrections to connection resolution, sealing, `serve` and `env`, the known limitations and the release
  steps.

## 0.35.0 (19 November 2026)

New blocks in a ContentArea are made as the site's edit UI makes them. **A change for sites whose edit UI makes no
inline blocks** (`UIOptions.InlineBlocksInContentAreaEnabled` off: Optimizely's default on CMS 12, and on CMS 13.0.0):
opticli and the MCP module made an inline block there with only a warning, which such a site may not render (a page
built so could fail with HTTP 500). There they now make a shared block in the page's "For this page"
folder, as the edit UI's "Create a new block" does. Sites with the setting on (CMS 13's default from 13.0.2) work as
before. See [New blocks in a ContentArea](https://github.com/Epinova/opticli/blob/main/docs/reference/cli/content-areas.md#new-blocks-in-a-contentarea).

### CLI

- **New area blocks follow the site.** Where the edit UI makes shared blocks, `area add --type`, a `{"type",
  "properties"}` item in a whole area and a plan's `area add` with `type` make a shared block of that type in the "For
  this page" folder of the content written (the folder is made when needed, for new content too), with the item's
  values, name and display option, in the language written, as a draft. Every block is validated before anything is
  saved, and one invalid block saves nothing. There is no option to make inline blocks there instead.
- **`sharedBlocks` in write results** lists the blocks of the content's own folder that a write made, changed, found or
  published along: `path` (`MainArea[2]`), `ref`, `name`, `type`, `folder`, `status` and `action` (`created`,
  `updated`, `published`, `existing`). A dry run lists them without a `ref`, and its `changes` show a planned block as
  `{"type", "name", "properties", "shared": "planned"}`. When the content's save fails after its blocks were saved,
  the error's `details.madeBlocks` lists them for a retry.
- **Inline blocks a page already has stay inline**, and are edited, moved and removed by position as before. A whole
  area written again pairs its items with the current inline blocks and the blocks of the content's folder (exact copy,
  then by name, then the only one of its type left), so a run again updates those blocks (`updated`) instead of making
  new ones; shared blocks from anywhere else are never changed that way. `MainArea[2].X=...` and `area set` on a shared
  block are refused with its ref, to change it by.
- **A publish takes the page's new blocks along.** `publish`, a write with `--publish` or `--publish-at`, and
  `projects publish` also publish (or schedule for the same time) the drafts of the blocks of the content's own folder
  that its areas show **and that were never published** in its language, and list them in `sharedBlocks`. A block
  that is live already keeps its newer draft, with a warning naming it, so an unfinished change to it doesn't go live
  with the page: `--with-block <ref>` (repeatable) takes it along, and `--without-block <ref>` keeps a new block back;
  a ref that isn't a block of the content's own folder that it shows is refused. A write that publishes takes the
  blocks it made or changed along by itself. A block whose draft someone else saved goes along only when
  `--with-block` names it (a plan's `withBlocks`): `--include-draft` doesn't take it along, so agreeing to one
  colleague's change never puts another's live unasked. A sealed apply to production leaves such a block a draft even
  when named (sealing's dry run alike); a publish left with nothing to publish there is `unchanged`, not a conflict,
  so the run goes on. A block under an approval sequence is left with a warning, and a project's own items follow the
  project's rules. Naming blocks for a publish that goes for review is refused. Of content published already,
  `publish` puts live just the block drafts it names with `--with-block` (never-published ones too) and, in a plan,
  those the run saved, so `publish <page> --with-block <block>` publishes a block's draft after the page went live
  without it; a never-published block it doesn't name stays back there (someone probably kept it back). With none it
  is a `conflict` whose hint lists the blocks with drafts it could name (and who saved a draft that is someone
  else's); a plan run again with `--update-existing` reports such a publish `unchanged`. A write with `--publish` that
  changes only blocks takes along only its own.
- **A write won't save a block's new version over someone else's draft unasked.** Where a whole area written again
  takes over a block of the content's own folder whose latest version is a draft someone else saved, the write would
  drop their changes from the block: it is a `conflict` (exit 5) with `details.reason: "pendingDraft"` and
  `details.draft` naming the block's draft, who saved it and the item (`path`), in a dry run too, and on a terminal
  opticli asks first. `--include-draft` (a plan step's `includeDraft`) confirms it, and a warning says whose draft was
  replaced; with `--publish` the block goes live as the write's own change. In a plan, the step that writes the area
  confirms it, not the `publish` step after it.
- **Plans: a `publish` step takes along the block drafts earlier steps of the run saved** on that content, as one write
  with `"publish": true` does, besides its `withBlocks` (and not its `withoutBlocks`); a block a later step took out of
  the area isn't published. When those blocks are all that changed, the step publishes just them instead of failing
  with `conflict`. Its dry run takes the same blocks along, with the step's `withBlocks`, `withoutBlocks`,
  `requestApproval` and `publishAt`, and reports the same warnings; its note says "sent for review" for a step with
  `requestApproval`.
- **A write that changes only a block of the content's folder says so**: a `set` or area step whose only change is a
  block's new draft reported "Nothing changed" (and a plan counted it as unchanged); it now says the content was left
  as it was and lists the block drafts it saved, and a plan counts the step as saved, with how to discard them.
- **`publish --dry-run` asks the site** where it makes shared blocks: the site validates the version and lists the
  blocks it would publish along and those it would leave behind, saving nothing. Elsewhere publish dry runs are
  answered from the database as before.
- **Review requests with never-published blocks are refused**, as the approval would publish the page alone (a live
  block with a newer draft doesn't count): the hint says to save into a CMS project, or to publish the blocks first.
  A write into a project with `--ready` or `--request-approval` takes its new and changed blocks along: marked ready,
  or sent for review where an approval sequence applies.
- **Plans: `id` on an `area add` with `type`** names the block it makes, so later steps change it as `$id` and
  `created` lists it; a run again with `--update-existing` finds it. The dry run refuses a later step that names a
  planned shared block by its position, an `id` a later step uses on a site that makes inline blocks, and one whose add
  finds an inline block like it on a run again.
- **`serve --status` and `doctor` show `inlineBlocks`** (`inline` or `shared`), how the site makes a new block in a
  ContentArea.
- **Fixed: a plan's dry run of a step that names a ContentArea item by its position** (`"MainArea[1].Heading"`, `area
  set`) ran against the stored area, without the items earlier steps of the plan add, unless the plan published,
  translated or used `from`. It follows the earlier steps in every plan now, on sites with inline blocks too.
- **Fixed: a plan's dry run of a `publish` after steps that change nothing** (a `set` of the values the content has
  already) passed, while the run failed with `conflict` at the publish ("already the published version"). The
  dry run now checks such a publish on its own, against the content as it is, and says so too.

### MCP server for editors

- `create_content`, `update_content` (`{type, properties}` items and `areaOps` adds) and custom tools' `ContentPlan`
  make shared blocks in the content's "For this page" folder where the site's edit UI does, and only where it would:
  the editor needs Edit and Create on the content (Create on its parent for new content) and a block type they may
  create there. The results (and custom tools' step results) have `sharedBlocks` with the blocks' refs.
- `publish_content` publishes the content's never-published block drafts along, as `publish` does, and takes
  `blocks` (live blocks whose newer drafts go along too, and blocks whose draft a colleague saved, which go along only
  when named: `includeDraft` doesn't take them) and `skipBlocks` (new blocks kept back); `publish_project` takes both
  too. On a site that makes shared blocks, the server's instructions tell the assistant that the CMS's own
  Publish doesn't (or, where assistants may not publish, that the editor publishes the blocks too), and to change a
  block later by its ref.
- **`publish_content` has `dryRun`**: what would go live, someone else's changes it carries, validation, and the blocks
  it would publish along or leave behind, saving nothing. It needs what the publish needs (`AllowPublish` and the
  `content:publish` scope, unless it is a review request). The server's instructions ask the assistant to dry-run a
  publish and show the editor what goes live first.
- A review request (`requestApproval`) with never-published blocks is refused with the same hint as the CLI's, and a
  save into a project with `ready` or `requestApproval` takes the blocks along. `blocks` and `skipBlocks` with
  `requestApproval` are refused.
- `publish_content` of content published already publishes just the block drafts `blocks` names (never-published ones
  too), and is a conflict when there are none, whose hint lists the blocks it could name.
- A whole-area `update_content` over a colleague's unpublished draft of a block of the content's folder is refused
  with a `pendingDraft` that has a `path`, dry run too; `includeDraft` confirms it, and the server's instructions tell
  the assistant to ask the editor first, as for a publish, and that `includeDraft` never puts a colleague's draft of
  another block live along (only naming it in `blocks` does).

### npm package

- `sharedBlocks` on write results, `inlineBlocks` on `AgentStatus`, `id` on a plan's `area` step, `withBlock` and
  `withoutBlock` on `publish` and `projectsPublish`, `withBlocks`/`withoutBlocks` on a plan's `publish` step,
  and `path` on `PendingDraft` are typed.

### Documentation

- New guide for editors: [Add blocks to a page with your assistant](https://epinova.github.io/opticli/guides/).

## 0.34.2 (18 November 2026)

### MCP server for editors

- **Fixed: the Claude Desktop extension stuck on "Unknown app".** The module deletes a registered app that nobody has
  signed in with after a day, but mcp-remote kept its copy of the registration, so after a sign-in that wasn't
  finished, every later sign-in page said "Unknown app" and Claude Desktop gave up with "Unable to connect to extension
  server". The extension now drops a saved registration that is over an hour old and never got a sign-in, and registers
  again.

## 0.34.1 (17 November 2026)

### CLI

- **Fixed on Windows: output and plans with characters outside ASCII.** When another program read opticli's output
  (the npm package, a script, an agent), Windows encoded it in the console's code page instead of UTF-8: `æøå` in a
  content name came out as other characters, and some (such as `→` in a command's help) as a control character that
  made the JSON invalid. A plan piped to `apply -` was read the same wrong way. Redirected stdin, stdout and stderr are
  UTF-8 now; output to a console window is as before.

## 0.34.0 (17 November 2026)

The npm package's client has a typed method for every command a test or script needs, generated from the CLI itself,
and a blocking twin for `playwright.config`. Its API changes where 0.33's no longer fit: see the breaking changes
below.

### CLI

- **A value that starts with `@` is plain text.** opticli read such a value as a response file and put that file's
  contents in its place, or failed with "Response file not found": `opticli search @alloy`, `get @5`, `create ...
  --name @Home` and `apply @plan.json` didn't do what they said. Now they search for `@alloy`, name the page `@Home`,
  and so on. `Prop=@file` values (a file's text as a property value) and `@@` (a literal `@`) work as before.
- **Fixed: plans that replace a file.** In a plan with a `guidNamespace`, an `upload` step with `replace` was refused
  for having no `id`, while `replace` takes no `id` or `guid`; it keeps the media's own GUID now, as without a
  namespace. And `"replace": "$id"` of media an earlier step of the same plan uploads failed validation (`$ids only
  work inside opticli apply`), in a dry run and a real run alike; it is now checked when the plan runs (`deferred`).

### npm package

- **Every command a test or script needs is a method on `OptiCli`**, generated from opticli's description of its
  commands, with the CLI's shape: arguments as parameters in the CLI's order, flags as an options object named after
  them, and the command's JSON result as the return type: `cli.languages({ all: true })`, `cli.projects('Spring
  campaign')`, `cli.whereUsed('123', { pages: true })`, `cli.jobsLog({ failed: true })`. Where the result depends on
  an option, the type follows it (`serve({ status: true })` gives `AgentStatus`, `find({ type, status: 'draft' })`
  `DraftItem[]`). Each method carries the command's help as documentation. Commands that list also have an iterator
  over every page (`for await (const item of cli.findAll({ type: 'ArticlePage' }))`). An optional argument may be
  `undefined` (`cli.whereUsed(maybeRef, { pages: true })`); run options (`timeoutMs`, `cwd`, `input`) come after the
  options object (`cli.whereUsed(undefined, undefined, { timeoutMs })`). Not generated: commands for a
  terminal or for setup (`db`, `sync`, `export`, `drift`, `doctor`, `ide`, ...); `run()` and `runSync()` still run
  any command.
- **`OptiCliSync`**: the same methods, blocking, for code that can't await, such as a `playwright.config` that makes a
  project per enabled language (`new OptiCliSync().languages()`). `runSync()` runs any other command.
- **`sql<Row>(query)`** types the rows of a read-only query as you say they are, for what no command answers.
- **CMS projects**: `create`, `set`, `cms.page()` and `cms.set()` take `inProject`, `ready` and `requestApproval`;
  plans take `project` and `ready`, and the result's `projects` lists them. `cms.project(name)` makes a project for the
  test, and a project a test made (`cms.project()`, or `cms.apply()` with a plan whose `project` didn't exist) is
  deleted after the test, after its content, unless `keep`. A project that was there before is never deleted.
- `PlanStep` has the `composition` and `access` steps and the step fields opticli takes (`variation`, `from`,
  `baseVersion` and `force` on `set`, `blueprint` on `create`, `replace` on `upload`, `publishAt`, ...).
- Every result type opticli prints is exported, under the name the CLI's schema gives it (`ContentDocument`,
  `WriteOutput`, `AgentStatus`, `ProjectInfo`, ...), with its known values as unions (`VersionStatus`,
  `PlanStepStatus`, `ProjectEditorUi`, ...), and so is each command's options interface (`FindOptions`, ...).

**Breaking changes.** Code that only calls the 0.33 methods and reads their usual fields mostly compiles unchanged;
the [npm README](https://github.com/Epinova/opticli/blob/main/npm/README.md#upgrading-from-033) shows what to change.

- Result types renamed to the generated names, with no aliases: `ContentItem` → `ContentDocument` (`language`, `parent`,
  `name`, `saved` and `changedBy` are optional, as opticli leaves them out for a folder, media or the root),
  `WriteResult` → `WriteOutput` (`set` returns `SavedContent`, a `create` that isn't a dry run `CreatedContent`, with
  `ref`, `version`, `guid`, `type` and `name` always there), `ChangedProperty` → `PropertyChange`, `DeleteResult` →
  `MoveOutput`, `UrlResult` → `ContentUrls`, `ApplyResult` → `AppliedPlan<P>`, `ServeResult` → `AgentStatus` (whose
  `state` has no `starting`, and `mode` has `external`), `ServeStopOutput` and `ServeLogs`.
- Same name, the generated shape: `PlanStepResult` (`result` is `unknown`; `apply`'s steps are `AppliedStep`, whose
  `result` is typed by `op`), `SiteChange` (`actions` is `string[]`), `SiteInfo`, `PropertyOut` (`value` is
  `JsonValue`).
- `serveStatus()` → `serve({ status: true })`, `serveStop()` → `serve({ stop: true })`. `serve()` returns the data
  (`AgentStatus`), not the envelope; `timeoutSeconds` is `timeout`, and `https: false` passes `--https false`. A
  start waits up to `timeout` + 600 seconds; status, stop and logs the usual 120.
- `WriteOptions` → `CreateCallOptions` and `SetCallOptions`, which take every flag of the command; `apply`'s options
  are `ApplyCallOptions` (every flag of `apply`, and `baseDir`), and `apply` also takes a plan file's path.
- An option a command doesn't have is a `TypeError`, before opticli runs; 0.33 ignored it.
- `apply()` and `cms.apply()` with a plan written inline type `created` by the plan's step ids: `result.created.page`
  compiles, `result.created[someString]` no longer does.
- `cms.page()` doesn't publish with `inProject` or `publishAt` (the project's or the schedule's publish puts it live;
  opticli refuses both with `publish`). `cms.publish()` returns `WriteOutput` and takes `publish`'s options;
  `cms.page()`'s options are `PageOptions`.
- `PlanStep` takes per op only the fields opticli takes for it: a field that opticli refused as unknown no longer
  compiles. An `area` step's display option is `display`, as opticli has always read it (`displayOption` was refused;
  it stays right on ContentArea items), and `version` on `publish` and `discard` steps is a number (a string was
  refused).

### MCP module

- **`get_content_type` gives the allowed types an editor descriptor sets.** Many sites set a ContentArea's block types
  with an editor descriptor for its `UIHint` instead of `[AllowedTypes]`, so one list serves an area many page types
  share. The edit UI follows that list, but `get_content_type` only read the attribute, and the assistant couldn't
  tell which blocks such an area takes. It now asks the CMS UI's own metadata for a property with a `UIHint` and
  without `[AllowedTypes]`, and gives what it finds as `allowedTypes` and `restrictedTypes`. The CMS's own default
  (any content) isn't listed, as before.
- **Writes keep to those types.** The CMS checks only `[AllowedTypes]` on save, so an assistant could put a block in
  such an area that the edit UI wouldn't offer an editor there. Content a write adds that the area's types leave out
  is now a validation error, as for `[AllowedTypes]`, also in a dry run. What the area already holds may stay.

### Documentation

- A new guide for developers, [Read the site from Node.js scripts and
  playwright.config](https://epinova.github.io/opticli/guides/), with a Playwright project per enabled language.
  [Seed CMS content for Playwright tests](https://epinova.github.io/opticli/guides/) shows a test's own CMS project.

## 0.33.0 (17 November 2026)

### CLI

- **Drafts go into the CMS's projects**, so editors review many related changes together and they go live in one go.
  `--in-project <id|name>` on `set`, `create`, `area`, `composition`, `block create`, `upload` and `translate` puts the
  draft in that project, in place of the content's earlier version there; the result's `project` says whether it was
  `added` or `replaced`. A write joins a project only when it names it, every time. Content in another unpublished
  project is a `conflict` (`inOtherProject`), and `--in-project` doesn't go with `--publish` or `--publish-at`. A save
  without `--in-project` of content that is in a project warns that the new version isn't in it. The flag is
  `--in-project` because `--project` already names the site's .csproj.
- **`projects create`, `ready`, `remove`, `publish`, `reactivate` and `delete`**, through the running site. `--ready` on
  a save, or `projects ready` (refs or `--all`), marks drafts Ready to publish; under an approval sequence that is
  refused, and `--request-approval` (with `--comment` on `projects ready`) sends them for review instead. Marking
  someone else's draft ready needs `--include-draft`, since the CMS then records opticli as the one who saved it.
- **Publishing a project puts only its items marked Ready to publish live**, now or `--at` a set time, through the
  site's own `ProjectPublisher`, as the edit UI does in project mode. The rest stay drafts and are listed in
  `notReady`. This also holds where editors use the Projects gadget (CMS 13's default), whose own Publish puts every
  unpublished item live. Every ready item gets `publish`'s checks first, and one problem stops the publish with nothing
  published. Marking an item ready counts as consent: the changes of whoever marked it (`readyBy`, after an approval
  the reviewer) go live without `--include-draft`, other people's still need it. On a terminal it shows what goes live
  and what stays behind, and asks. `siteChanges` says what the site's own code did meanwhile, on the CMS's publishing
  thread. On CMS 12 a publish whose background task fails to start is tried again, up to twice.
- **Plans take `"project"`**, on the plan for every save step or on a step (`null` for none), and `"ready": true` per
  step. A project the plan names that doesn't exist yet is created by the run before its first save; a step marked
  ready whose content a later step changes is marked ready once the plan has run. `data.projects` lists the projects,
  what was made and what was marked ready. Plans never publish a project, and sealed production runs refuse projects.
- `projects` lists `ready` (items marked Ready to publish) next to `items`, and `projects <project>` takes the project's
  exact name as well as its id.
- Where the site's edit UI has neither project mode nor the Projects gadget, project writes are `refused`
  (`projectsDisabled`).

### MCP server for editors

- **New tools**: `list_projects` and `get_project` (each item's state and `editUrl`) to read, `create_project`,
  `mark_ready_to_publish` and `remove_from_project` to write, and `publish_project` and `reactivate_project` to publish
  or schedule a project, and cancel a schedule. The publish tools are offered on every site, as `publish_content` is,
  and need `AllowPublish` and the `content:publish` scope; elsewhere they are refused with a hint to mark the items
  ready and publish the project in the CMS.
- **`project` and `ready` on `create_content`, `update_content`, `add_language` and `upload_media`** put the draft in a
  project and mark it Ready to publish. Marking ready and taking items out need Edit access on each item, publishing
  needs Publish on each ready item, and reactivating Publish on each scheduled one, all checked before anything changes.
  `mark_ready_to_publish` with `all` leaves out drafts the editor may not edit, and lists them.
- **Publishing puts only ready items live**, with the same checks and consent rule as the CLI: `includeDraft` for the
  changes of anyone other than whoever marked an item ready, and for marking someone else's draft ready. Dry runs
  report each item's problem with `valid: false`.
- **Links for the editor**: `projectUrl` opens the project in the edit UI where the CMS has project mode (and makes it
  the editor's current project); on sites with only the Projects gadget, `projectNote` says to open the gadget and that
  its Publish puts every unpublished item live.
- **Custom write tools** put their plan's drafts in a project with `ContentPlan.InProject(project, ready)`.
- The server's instructions say when to suggest a project, that a save joins one only when it names it, and how to
  publish one (or, where the site doesn't let assistants publish, that the editor publishes it in the CMS). Audit lines
  name the project a call worked on.
- **Fixed:** `get_content` didn't show which projects hold a version of the content (`projects`), as the CLI's `get`
  does. It does now.

### Documentation

- New guides: [Collect changes in a project and publish them together](https://epinova.github.io/opticli/guides/) for
  editors, and [Hand a batch of changes to editors as a project](https://epinova.github.io/opticli/guides/) for
  developers.

## 0.32.0 (12 November 2026)

### CLI and npm package

- **A site that `sites add` (or a plan's `site` step) creates gets an assets folder of its own**, as one made in admin
  mode does: the start page's `SysSiteAssets` folder, created when it has none, as the site's assets root. Add-ons
  that keep data in a site's assets folder now find one, and the site's media is at `/siteassets/`. Before, every such
  site used the global assets folder. `--site-assets global` (`"siteAssets": "global"` in a plan) keeps a new site on
  the global assets folder. With `--update-existing` an existing site's assets root changes only when
  `--site-assets` is given. The result's `siteAssets` gives the root and the folder; a dry run says whether the folder
  would be created. A `SysSiteAssets` folder that another site already has as its assets root is a `validation`
  error, since the CMS doesn't start with two sites on one folder.
- **A plan step with a URL on a host that an earlier `site` step adds is checked when the plan runs** (`deferred` in a
  dry run), as steps in a language an earlier `language` step enables are. Before, a plan that uploaded to
  `http://alloy.localhost:5000/siteassets/` after the `site` step that adds that host failed validation on a database
  without sites ("No site answers host"), and on one with a `*` site its dry run checked the step against that site's
  folders.
- **A page whose shortcut is an external link keeps that link as its `url` in a plan with a `site` step**, which
  0.31.1 fixed for other writes. Before, the URLs given after the `site` step saved replaced it with the page's own
  path, so the first run of a seed plan gave the npm package's `seed.urls` another URL than later runs.

## 0.31.1 (11 November 2026)

### CLI, MCP module and npm package

- **A value the site's own code changes during a save is reported.** When a handler of the CMS's saving events changes
  a property the write set (it clears a tag id, say), the result's `changes` show what was stored instead of what was
  asked for, and `siteChanges` lists the content as `changed`, with each such property's `asked` and `saved` value. The
  CLI warns about it, also for a plan's step. Before, the result showed the asked-for value although something else
  was stored.
- **A page whose shortcut is an external link has that link as its `url` again** in write results, as in versions
  before 0.29.0. Since then the link lost its host (`https://www.example.com/docs` became
  `/docs`), which pointed at a page of the local site.

## 0.31.0 (11 November 2026)

A test database can now be built from code: the CMS creates the schema in an empty database, and one plan adds the
languages, the content and the sites, which such a database lacks.

### CLI

- **`sites add`** creates a site definition (CMS 13: an in-process application) with its start page and hosts
  (`--host name[:port][@lang]`, repeatable; the first host of each language is the primary one). With
  `--update-existing` a site of that name is made to match instead of being a `conflict`. Refused against a shared
  database.
- **`languages enable` and `languages disable`** change which language branches are enabled, adding a branch the
  database lacks (a fresh database has `en` and `sv` enabled). `disable` warns about content in the language and
  refuses to disable every one. Refused against a shared database.
- **Plans take `language` and `site` steps**, so a seed plan builds a database from scratch: steps in a language an
  earlier step enables are checked when the plan runs, and `apply --update-existing` leaves an unchanged site as it is.
  Sealed plans never have them.
- **`serve --https-port <n>`** (or `httpsPort` in the user config) fixes the port of the HTTPS address, so site hosts
  with that port stay right from run to run. A taken port is an error.
- **`serve --profile <name>`** gives the site the launch profile's environment variables too, not only its connection
  string, except those `serve` sets itself. `env --profile` prints them.
- **Write results have `canonicalUrl`**: the content's URL with the host of its site for that language, which says
  which of several sites it is on. After a plan whose `site` step saved, every step's `url` and `canonicalUrl` are as the
  sites have them at the end of the run, also for content saved before its site existed.

### npm package

- `defineSetup()` takes `httpsPort` (`OPTICLI_HTTPS_PORT`), and `OptiCli` a `profile` (`OPTICLI_PROFILE`) that every
  call and the workers pass on.
- The plan type has the `language` and `site` steps.
- **Several sites**: `seed.urls` (and `cms.page()`'s `url`) give content on another site that the served site answers on
  a `*.localhost` host of its own (on the HTTP port or `httpsPort`) that site's address, and `seed.sites` maps each such
  site's name to where to open it. Content on the site at `baseURL` keeps its path, as before. `OptiCli.sites()` lists
  the sites.

### Documentation

- `$id` in a string property stores the content's id as text, for properties that keep ids as strings. This worked
  before; the README and the plan reference now say so.
- A new guide: [Build a test database from scratch](https://epinova.github.io/opticli/guides/).

## 0.30.1 (11 November 2026)

### MCP server for editors

- **A named version needs `includeDraft` too.** `publish_content` with a `version` (or a `123_456` reference) that
  carries someone else's unpublished changes is now refused with `pendingDraft`, as publishing the latest version is,
  until the assistant asked the editor and retries with `includeDraft`. Before, naming the version counted as the
  confirmation, so an assistant could publish a colleague's draft without asking. An editor's own versions publish as
  before. The CLI's `publish --version` is unchanged: there, naming the version is the developer's confirmation.
- **A review request names a colleague's changes.** `requestApproval` (on `update_content`, its dry run, and
  `publish_content`) still sends the version for review, as the reviewer sees all of it, but the result now has a
  `pendingDraft` with the changes someone else saved that went for review too, and the server instructions tell the
  assistant to say so.

### Documentation

- How-to guides for editors, CLI developers and the MCP module, on the landing page under
  [Guides](https://epinova.github.io/opticli/guides/). The README stays the reference.
- The README's MCP options table lists `MaxPlanSteps`, and `sync pull --help` names visitor groups and categories among
  what isn't synced.

## 0.30.0 (11 November 2026)

A site can add tools of its own to the MCP server for editors: read tools, and write tools whose changes the module
checks with a dry run and saves as drafts. The CLI is unchanged.

### MCP server for editors

- **Custom read tools.** `AddOptiCliMcp()` returns a builder, and `.WithTools<T>()` on it offers a class's MCP SDK tool
  methods (each with `[McpServerTool(Name = "...", ReadOnly = true)]`; no `[McpServerToolType]` needed) beside the
  module's tools, on the module's endpoint only, for the signed-in editor, with the module's sign-in, scopes, audit log
  and errors. `IOptiCliEditor`, taken in the tool class's constructor, reads content as the editor (`Load`, `CanRead`,
  `Readable`) and describes it as the module's tools do (`Describe`, a `ContentDescription` with `url` and `editUrl`).
  `OptiCliToolException` fails a call with the module's JSON error (`code`, `message`, `hint`); the CMS's own
  not-found and access-denied exceptions become `not_found` and `refused`, as for the module's tools, and anything else
  a tool throws is logged and reaches the assistant as `internal`, without its message. `.WithInstructions("...")` adds the site's own lines to the
  instructions the server gives assistants. See [Custom tools](https://github.com/Epinova/opticli/blob/main/docs/reference/mcp/tools.md#custom-tools).
- **Custom write tools.** `.WithWriteTool<T>()` takes a class with `[OptiCliWriteTool("name", Title = ..., Kinds = ...)]`
  and `[Description]` that implements `IOptiCliWriteTool<TArgs>`: its `PlanAsync` returns a `ContentPlan`, the steps to
  take (update, create, create in a "For this page" folder, move, move to the recycle bin), where a step can use content
  an earlier one creates (`PlannedContent`). The module makes the tool the assistant sees (`TArgs`' properties as
  arguments, plus `dryRun` and `planToken`), checks every step as the editor with its own dry runs, and runs the plan
  only with the dry run's `planToken` and only if the plan is still the same, saving drafts only. A token runs once and
  expires after 30 minutes (`planTokenExpires`). An update planned from content loaded without a version counts as
  based on its published version, so a newer draft makes it a `conflict`: plan from `Load(..., latest: true)`. A run
  that fails at a step stops there, keeps the steps before it, and reports each step. A write tool needs the
  `content:write` scope, one that may trash is offered only with `AllowDelete`, and each step a run saves has a line in
  the audit log.
- **`MaxPlanSteps`**, a new option (100 by default, 1 to 1000): the most steps a write tool's plan may have. A larger
  plan is refused at the dry run, with a hint to narrow the arguments.
- **`AddOptiCliMcp` returns `IOptiCliMcpBuilder`**, no longer the `IServiceCollection`. A site that ignores what it
  returns needs no change; code that chained other registrations on it goes on through `.Services`:
  `services.AddOptiCliMcp().Services.AddSingleton<...>()`.
- **The module's tools answer arguments they can't take with a `usage` error.** A missing argument, or one of the wrong
  type, gets the JSON error (`code`, `message`, `hint`) the server's instructions promise, instead of the MCP SDK's
  plain "An error occurred invoking ...".
- **Fixed:** a site's own MCP server, registered after `AddOptiCliMcp()`, lost its tools that had the name of one of the
  module's tools (`whoami`, `get_content`, ...). The module now makes its tools itself, outside the SDK's tool list
  every server shares, so each server keeps its own, in either order.
- **Fixed:** editors' assistants couldn't create folders (`create_content` with `SysContentFolder`) since 0.9.0: the
  check against the edit UI's list of content types refused them, as that list never has the CMS's folder type. The
  editor's Create access on the parent, and where a folder may go, still apply.

## 0.29.0 (10 November 2026)

Playwright setup for sites that redirect to HTTPS, URLs that open on the served site, and seed plans that run again
without changes.

### Fixed

- **Write results gave the site's primary host.** The `url` of `create`, `set` and the other writes was absolute (the
  site's primary host, such as `https://site.dev.localhost:9377/en/`) when `opticli serve` ran the site on another port
  than that host's, or the host was a production name; that host doesn't answer locally. It is now host-relative, as
  `get` gives it.
- **`apply --update-existing` changed content that was already as the plan has it.** Rich text whose tags the plan
  writes otherwise than the CMS stores them (`<img ...>`, `<br>`, `alt='x'`, upper-case tags) was saved again on every
  run, and so was a `Url` property given as a path (`/en/`), which the CMS stores as a permanent link. Both are
  unchanged now, and so is the same value written again with `set`.
- **`area add` of an inline block added another copy on every run** of a plan with `--update-existing` when one of its
  values was rich text or a `Url` given as a path: the copy already there didn't count as the same block.

### npm package

- **`https` in `defineSetup()`** (or `OPTICLI_HTTPS=1`): serves the site on `https://localhost:<port>` as well and
  points `baseURL` there, for a site that redirects HTTP to HTTPS. A site that ran before without an HTTPS address fails
  setup, rather than giving tests an address they can't browse.
- **`seed.urls` and `cms.page().url` are paths on `baseURL`,** with the opticli of this release: they had the site's
  primary host (above).
- **Setup's log names the steps the seed plan changed,** and their properties, not only how many.

## 0.28.0 (6 November 2026)

The npm package for Playwright and Node.js is out of preview, and a test can make content that stays on the site.

### npm package

- **Out of preview.** Install it with `npm install -D @epinova/opticli`; its version is opticli's, without `-preview`,
  under the npm tag `latest`.
- **Content a test makes to keep.** `cms.page()` and `cms.apply()` take `keep: true` to leave what they create on the
  site; it needs a parent outside the worker's sandbox. The README says what stays and what goes after a run.
- **Fixed:** `cms.apply()` with `updateExisting` removed content that was there before the test along with what the
  test made.
- **Fixed:** a relative `project` pointed elsewhere for `apply` and the seed plan, which run in the plan's folder.
- **Fixed:** when setup failed after starting the site (a version check, the seed plan), the site kept running.
- **Fixed:** when opticli couldn't be started, Node.js kept running for up to 13 minutes after the error. A command that
  runs too long now fails with `code` `timeout`, not "no JSON on stdout".

## 0.27.0 (6 November 2026)

opticli in Playwright tests: a new npm package (preview) starts the site, seeds the content the tests need and cleans up
after them. Writes return the content's URL, and errors name the opticli version.

### Added

- **Writes return the content's URL.** `create`, `set`, `area`, `composition`, `block create`, `upload`, `translate`,
  `publish`, `unpublish` and `discard` (and their steps in `apply`) have `url`: the public URL in the result's
  language, host-relative as `get` shows it; for a draft of content never published, where it will be once it is.
  Blocks, folders and a dry-run `create` have none. A script that goes on to open the page, such as a Playwright
  test's setup, no longer needs an `opticli url` call per page. A site started with an older opticli's agent gives
  no `url` until `serve` starts it again.
- **Errors name the opticli version.** The JSON error envelope has `meta.version`, as a result's `meta` has, so a
  program that runs opticli can tell which version failed.
- **npm package for Playwright and Node.js (preview).** [`@epinova/opticli`](npm/README.md) on npm runs opticli from Node.js
  with types for its output, and has Playwright fixtures: `defineSetup()` starts the site with `serve`, checks the
  versions, and applies a seed plan before the tests; `cms` gives each test content of its own and removes it after;
  each worker can have a page of its own for that (`opticliSandbox`). Install it with `npm install -D @epinova/opticli@preview`.

### Changed

- **A sealed run names its target.** The run's lines said "production" whatever the database was called, also for a
  staging database given the role `prod`. They now name it as `--db` does ("Started the site against 'staging' (role
  prod) for this run", "Checking the plan against 'staging'"), and so do the summary to confirm, the prompts for the
  event bus and blob storage, the result's text and the seal's. The JSON `target` of a seal and of a run has the name
  too (`target.name`).

## 0.26.0 (28 October 2026)

The MCP server for editors runs on CMS 13, as the new `OptiCli.Mcp.Cms13` package, and is hardened for sites that run
another MCP server beside it, on CMS 12 too.

### MCP server for editors

- **CMS 13: `OptiCli.Mcp.Cms13`.** A second package, built from the same sources for CMS 13 (13.0.0 or newer, .NET 10)
  and released with the same version. `OptiCli.Mcp` stays the package for CMS 12 (12.12.1 / CMS UI 12.16.1 or newer),
  also for a CMS 12 site on .NET 10: one package with a .NET 10 build would have given such a site the CMS 13 build.
  Both have the same `AddOptiCliMcp`, `MapOptiCliMcp` and options, so a site that moves to CMS 13 swaps the package and
  changes nothing else; connections are kept in Dynamic Data Store stores with the same names.
- **The wrong package stops the site at startup.** NuGet refuses most mismatches; where it doesn't, or a site forces
  it, `AddOptiCliMcp` throws "This site runs CMS 13: use the OptiCli.Mcp.Cms13 package instead of OptiCli.Mcp." (or
  the other way round). Before, `OptiCli.Mcp` could start on a CMS 13 site and run its CMS 12 code there.
- **Visual Builder, read only.** `get_content` of an experience gives its composition (sections, rows, columns and
  elements, with display templates and settings) under the same rules as every other read: properties the edit UI hides
  or locks are left out, and a shared block or section the editor can't read shows only its place (`nodeType`, `key`,
  `ref`), never its name, type or values. Dry runs and pending drafts list composition changes by the same rules.
  Editors can't change compositions through the MCP server yet.
- **Uploads on CMS 13** follow the CMS's `MediaFileOptions` (`EPiServer:Cms:MediaFile`: its size limit and allowed
  extensions, matched as the edit UI matches them), and go through the CMS's script parser after the module's own
  checks, as an upload in the edit UI does: refused where the parser refuses the file, and otherwise stored as the
  parser writes it back, checked again for script.

### Hardening

From a review of the module beside other MCP servers. They apply to CMS 12 as well, and matter for a site that runs
another MCP server built on the MCP C# SDK in the same application as the module: its own, or a package's (such as
Optimizely's `Optimizely.Cms.Opal.Tools` on CMS 13). A CMS 12 site that does should update `OptiCli.Mcp` to 0.26.0, all
the more if that server turns on sessions (`Stateless = false`) or the legacy SSE transport. A site with only the module
isn't affected by them.

- **Each MCP endpoint serves only its own server.** The SDK keeps one set of server options for the whole application,
  so the module's endpoint also offered the other server's tools to editors' assistants, and the other endpoint listed
  the module's tools (which refused to run there) under the module's name and instructions. Now
  the module's endpoint has the module's tools only, and the other endpoint keeps its own tools, name and instructions.
- **Sessions are bound to the editor's connection and to the module's endpoint.** Where the other server turned on
  sessions or legacy SSE, the SDK found a session by its ID alone, at any endpoint. Now the module's endpoint takes only
  sessions it made, each only from the connection that made it, and its sessions are refused at other endpoints. It
  serves POST only (and a DELETE that ends one of its own sessions), never a GET stream or legacy SSE. The module's
  sign-in carries a `sub` claim with the connection's ID, which the SDK binds a session to; site code that runs during a
  tool call sees it.
- **A bounded session registry.** The module prunes the sessions it keeps to as many as the SDK keeps idle ones
  (`MaxIdleSessionCount`, at least 1,000), the least recently used first, so an editor's assistant that makes sessions
  in a loop costs bounded memory and time.
- **Fails closed.** If code registered after `AddOptiCliMcp` replaces the SDK hook the module keeps the servers apart
  with, the module's endpoint refuses every request and the site's log says why at startup, rather than serve without
  it.

### Fixed

- `view_media` reports the size of the image it sends. Where the site's image service rounds a scaled side down
  (`EPiServer.ImageLibrary.ImageSharp` 1.0.6, as on CMS 12.24, and CMS 13), it said 1568×784 for an image of 1567×783.
- The Claude Desktop extension opens the sign-in page on Linux. Claude Desktop starts extensions there with D-Bus
  turned off, and can start them without the desktop session's display, so the browser never opened (or Firefox said
  it was already running). The extension now uses the user's own session bus, and fills in a missing display from the
  desktop session (tested on KDE Plasma with Wayland).

## 0.25.0 (28 October 2026)

The MCP server for editors is no longer a preview, and it comes with a Claude Desktop extension.

### MCP server for editors

- **No longer a preview.** `OptiCli.Mcp` is published as a regular release with the same version as the CLI (no
  `-preview` suffix), so `dotnet add package OptiCli.Mcp` installs it without `--prerelease`. It still supports CMS 12
  only (12.12.1 / CMS UI 12.16.1 or newer).
- **Claude Desktop extension.** `opticli-cms.mcpb` is attached to every GitHub release. Editors open it in Claude
  Desktop and enter the site's address; it connects from their own machine through mcp-remote, so nobody has to add a
  connector for the organization. `mcp-extension/build.mjs` builds one with a site's address built in, named after its
  host, so test and production install side by side.
- The README describes the module as an MCP server for any assistant with an OAuth sign-in: clients on the editor's
  machine connect as they are, web-based ones from `AllowedRedirectHosts`. Claude's own ways in are under
  "Connecting Claude".

## 0.24.0 (26 October 2026)

A sealed apply to production can join production's event bus, so no restart is needed afterwards, and can carry media
to production's blob storage. Both are typed at the user's terminal, like the write login, and only offered to a site
that uses the CMS's Azure providers.

### Writing to production

- **Event bus.** When the site's build has the CMS's Azure event provider (`EPiServer.Azure` on CMS 12,
  `EPiServer.Events.Azure` on CMS 13), the run asks for production's Service Bus connection string (with a shared access
  key) and topic, after the write login. Enter skips it: the restart warning, as before. Before the seal is used, the
  topic must exist and have subscriptions (production's servers). The site agent registers the CMS's own provider with
  those values, whatever the site's configuration says (its Key Vault may hold another environment's), and the run checks
  that the site uses them, then that its subscription is on the topic, before the first save. After the saves the site
  sends what its provider still has queued (the CMS would drop it when the site stops), and a probe subscription counts
  the run's messages on the topic.
- With Microsoft Entra ID, the run's site signs in with the token the run signed in with (handed over on its stdin line)
  instead of fetching its own, so it writes as the identity the run checked. On a real site, fetching its own token while
  starting stalled until Azure SQL dropped the login. Should the token expire during a long run, the site fetches its own.
- The flush waits until the site has raised no cache removal for a few seconds before it sends the queue (a save's own
  publish handlers keep raising them), and removals raised while the site stops are listed apart
  (`events.notSentWhileStopping`) rather than counted as the run's.
- **The result says whether a restart is needed**: `restartNeeded` is false only when the run's cache drops were sent
  and counted (`events.sent`); anything uncertain leaves it true, with the reason in `events.problem`.
- Leftover types and properties are accepted only as they are: a sealed plan that writes content of such a type, or sets
  such a property, is refused (`sealedPlan`).
- **Media.** Sealed plans may `upload` (and `replace`) by GUID again, for a site whose build has the CMS's Azure blob
  provider. The run then asks for production's Azure Storage connection string (an account key or a SAS token) and
  container (offered from the site's `EPiServer:Cms:AzureBlobProvider:ContainerName`); the container must exist before
  the seal is used, and Enter refuses the uploads with the seal unused. The site writes through the CMS's Azure blob
  provider with those values, uploads send the bytes the run read and hashed (not the file again), and after the run every
  uploaded file is looked up where production's database now points (`media[]`; one missing is a `conflict`,
  `details.reason: "blobMissing"`).
- Content types and properties only production's database has (classes or properties removed from code, which the CMS
  keeps for their content) no longer stop a seal or the run. The seal records them by name (`leftovers`), the run shows
  them before the user types anything, and the run's site may find exactly those and no other difference (`conflict`,
  `details.reason: "driftNotSealed"` otherwise, before anything is written). Before, a production with such leftovers
  had to be cleaned up in admin mode first.
- `sync push --media` pushes media production lacks or has otherwise too, with its files from the local site's blob
  folder. Without it, media is listed and left out, as before.
- The connection strings are never written to disk, logs, output or the seal, and no option, environment variable or
  configuration entry gives them: the agent takes them only from the run's stdin line. The CLI checks Service Bus and
  Blob Storage with the Azure SDKs (`Azure.Messaging.ServiceBus`, `Azure.Storage.Blobs`; the probe receives over AMQP on
  WebSockets, port 443). The site agent brings no Azure package into the site: it uses the site's own CMS Azure
  providers, so sites without Azure are unaffected.

## 0.23.1 (23 October 2026)

Hardening of the MCP server for editors, from a review of what an assistant's tool calls can reach.

### MCP server for editors (preview)

- Media goes by what the site serves it as. The CMS serves a media item by its URL, whose last segment is made from its
  name, with the content type of that extension. So for an editor, a file the site would serve as HTML, XML or script
  isn't uploaded, whether its file name or the media's name says so (`upload_media`'s `name`, a rename, a replaced
  file). The content types are the site's own, as its media are served with them. Besides `.html`, `.xml` and the others
  it already refused, on a default site that is `.hxt`, `.stm`, `.xsd`, `.dtd`, `.wsdl`, `.jsx` and a few more legacy
  types, which could only be uploaded on a site with a media type that takes any file. An SVG drawing without script
  still uploads, also under a name the site serves as SVG.
- A content reference given as an object (`{"id": 123}`), for a reference property or in a list property's item (also
  in a dictionary), gets the same read check as one given as text: content the editor can't read is `not_found`, as
  missing content is, and a version must be one of the content's own. A reference the property already has may be
  written back as it was. A list item nested more than six levels deep is refused for an editor rather than left
  unchecked.

## 0.23.0 (23 October 2026)

Preview links: the editor sees a draft as visitors will, without the edit UI around it, and the assistant can look at
the draft itself, unless the site turns that off.

### MCP server for editors (preview)

- Write results that saved a version visitors don't see yet (a draft, a version in review or scheduled) have
  `previewUrl`: the CMS's own preview of that version, on the edit link's host. `get_content` gives it too when it read
  such a version (`version: "latest"` or a version id). Not for a dry run, a discarded version, media or folders.
- `previewUrl` needs the editor's sign-in to the CMS, as `editUrl` does: it carries no preview token, so a link in a
  chat transcript shows nobody the draft. The server's instructions and the tool descriptions say so, and tell the
  assistant to give the links to the user rather than open them.
- The same results also have `assistantPreviewUrl`, a link of the assistant's own to that preview, which it opens
  without a sign-in to check how the draft looks, and `assistantPreviewExpires`. On by default; `AllowPreviewLinks:
  false` turns it off, for a site that doesn't want the page as the editor sees it (editor-only navigation, blocks and
  user-specific output) to reach whoever holds the link for its lifetime. The link shows that one version, in its
  language, as the CMS's preview shows it to the editor, and nothing else: GET and HEAD only, on the host it was issued
  on, no cookie, redirect, sign-in or error page comes back, and it stops working after `PreviewLinkLifetime` (10
  minutes by default, at most an hour), when the connection is revoked or ends, or when the editor can no longer read
  the version. The module changes nothing itself, but the site's own code for the page runs as the editor, as in the
  CMS's preview. The link is signed by the module with the site's Data Protection keys and never carries the CMS's
  preview token. `whoami` reports `allowPreviewLinks`, opened links are audited and rate limited per connection
  (`RateLimits.PreviewPerMinute`, with invalid ones per address, `RateLimits.InvalidPreviewPerAddressPerMinute`), and
  the instructions tell the assistant the link is for it alone.

### Fixed

- A site that lists the CMS's packages one by one, without `EPiServer.CMS` or `EPiServer.CMS.AspNetCore` (say
  `EPiServer.CMS.Core`, `EPiServer.Hosting` and `EPiServer.CMS.UI`), is found: a project with the edit UI
  (`EPiServer.CMS.UI`, `EPiServer.Cms.UI` on CMS 13) is a site, and so is a web project referencing any `EPiServer.CMS.*`
  package, a headless one included. A class library with `EPiServer.CMS.Core` still isn't, and where another web
  project references a CMS package for its models, the one with the CMS itself is picked. Before restore, such a site's
  CMS version is read from its `EPiServer.CMS.Core` reference.

## 0.22.0 (22 October 2026)

Feedback from an assistant that published an article through the MCP server: searches answer in time, results give
full addresses, and a save says what the site's own code did besides.

### MCP server for editors (preview)

- `find_content` answers within 20 seconds with what it found so far (`truncated: true`), instead of searching on past
  the time the assistant's client waits (often 60 seconds) on a large site with a cold cache. Its description says it
  matches names only.
- `get_content` and `resolve_url` give the public `url` with the site's host, also for the site the MCP server runs on.
- Every write result has `url`, the content's public address (for a draft, where it will be once published), next to
  `editUrl`; so does a move or a delete.
- The server's instructions say a dry run doesn't run the site's own code, and to tell the user what `siteChanges`
  reports.

### Writes

- A save reports what the site's own code changed meanwhile (handlers of the CMS's save and publish events), as
  `siteChanges`: other content it created, saved, published, moved or deleted, and the saved content itself when the
  site put it below another parent than asked (a site that files articles into year folders) or published it unasked.
  Content the editor can't read is left out. The CLI shows them as a warning. A dry run doesn't save, so it can't
  report them.

## 0.21.0 (22 October 2026)

The MCP server for editors shows Claude the images in the media library.

### MCP server for editors (preview)

- New read tool `view_media`: Claude sees a media item's image instead of asking the editor to open it. It reads as the
  editor, like `get_content`, with the same `lang` and `version` (drafts too); media they can't read is the same
  `not_found` as missing media. The result is the file's description as JSON (version, name, size, and the stored
  file's format and size when it was scaled) and the image itself.
- JPEG, PNG, GIF and WebP files up to 1568 pixels on the longest side and 1 MB are sent as they are stored. Larger ones
  are scaled down by the site's own image service, which makes the edit UI's thumbnails (built into CMS 12 before
  12.17, then `EPiServer.ImageLibrary.ImageSharp`, which `EPiServer.CMS` brings), to `maxSize` (64 to 1568 pixels). A
  PNG that is still over 1 MB becomes a JPEG. Images of more than 64 million pixels aren't decoded; a site without an
  image service still shows small images.
- SVG files are sent as their markup; other files (PDF and the like) are refused with a hint.
- The server's instructions name `view_media`, and count text in images as the site's data.

### Development

- The image reading and scaling are content operations in `OptiCli.Cms`, built and tested for CMS 12 and CMS 13. On
  CMS 13 the image service's interface is in the `EPiServer.ImageLibrary` package, referenced at compile time only. The
  MCP module itself stays CMS 12 only.

## 0.20.0 (21 October 2026)

IDE mode: writes show in the site you run from your IDE, without a restart.

### New

- `opticli ide on|off|status`. `ide on` adds a labelled block to `<Project>.csproj.user` that names opticli's copy of
  the site agent as the Debug build's startup hook (`RuntimeHostConfigurationOption`, so it is in the build's
  `runtimeconfig.json`). The site you start from your IDE (Rider's run or debug, Visual Studio, `dotnet run`,
  `dotnet watch`) then loads the agent with nothing set per run: writes go through that process, so they show there at
  once instead of after a restart. A `.csproj.user` that git tracks is refused; one git doesn't ignore is added to
  `.git/info/exclude`. The copy lives in opticli's state directory (`agent/<version>-<hash>/cms<major>/`), since the
  tool's own path changes with every update; the block only adds the hook while the copy exists. Rebuild after `ide on`
  and `ide off`.
- In IDE mode the agent makes its own token and, once the site listens, writes where it listens to `<project>.ide.json`
  in the state directory (readable by you only), and removes it when the site stops. opticli reaches it on the site's
  own addresses (`localhost`, a loopback or wildcard address, or a name below `.localhost` such as
  `site.dev.localhost`): plain HTTP first, else HTTPS on `localhost` with the ASP.NET Core development certificate, which opticli accepts on loopback addresses (also where OpenSSL doesn't trust it).
- IDE mode never changes how the site starts or stops it from starting: no connection pin, and the scheduler, schema
  updates and content type sync as the site sets them. Against a database other than a local one or the development
  database, or outside Development, the agent stays off for that run and says why in the site's output.
- `serve --status`, `doctor` and every write use the IDE's site while it runs (`mode: "ide"`); a site paused at a
  breakpoint is reported as such. `serve` then starts nothing. `serve` without the IDE's site gives the site the agent
  copy its build names, so one agent loads. `opticli env` is refused while IDE mode is on. `ide status` and `doctor`
  say when the copy is from another opticli version, the build doesn't have the hook yet, or the development database
  changed since `ide on`.
- After `sites primary`, `sites host` and `types remove|remove-property|prune` through the IDE's site, the warning no
  longer asks to restart it.
- Sealing a plan for production (`apply --seal-for`, `sync push`) and the sealed run refuse a build from IDE mode
  (`refused`, exit 3): a run with production's write login loads only this opticli's agent.

## 0.19.0 (16 October 2026)

Fewer drafts from plans, drift that tells removed code from newer code, and access rights in "For this page" folders.

### New

- Drift looks up content types and properties only in the database in the checkout's git history: an item whose class
  (or the property) a commit removed from code, and that `HEAD` doesn't have, carries `removedFromCode` (commit, date,
  subject), and the text says "its class was removed from code in abc1234 (2026-09-14)". When every difference on the
  database's side is such a removal, the advice says pulling won't change it. Only a removal tied to the type counts:
  by its GUID, by its class name when the removed class had no GUID or the same one, and for a property in the type's
  class or the base classes it names; a comment naming the class or property doesn't count. The lookup needs a `.git`
  in the project's folder or above and `git` on the path, and stops after 50 items or 5 seconds; without it drift is
  reported as before. The fingerprint, the counts and what stops writes don't change.

### Changed

- An `apply` plan no longer leaves a draft per step. A `set`, `area` or `composition` step on content and a language
  that an earlier step of the run saved a draft of saves into that draft, as the edit UI does, so a plan leaves one
  draft per content and language (after a step that publishes, the next change starts a new one). The step's result
  says `intoVersion: true`, the step `savedInto` (the draft and the step that made it), and its undo points to that
  step's. opticli saves into the draft only while the database still has it as the branch's latest version, unpublished
  and saved by opticli, and the site checks the same again; otherwise the step makes a new version and its warning says
  so. Steps with `from`, `baseVersion`, `force` or a ref naming a version make a new version as before.
  `apply --version-per-step` keeps a version per step; use it on a site whose own code asks for a new version on every
  save (a handler that sets `RequireNewVersion`), where a save into the draft fails with a `conflict` that says so. A
  later `discard` without a version discards the whole draft, with every step's changes in it, and when a later step
  publishes, schedules or sends the draft for review, the undos of the steps that saved it point to that step's.
  Sealed applies to production and `sync pull` run their plans the same way; `--version-per-step` doesn't apply to a
  sealed plan.
- Drift with the database ahead no longer only says "the environment runs newer code than this checkout: pull and
  build". A content type or property only in the database is often one whose class was removed from code and that the
  CMS kept for its content, so the advice (in `opticli drift`, the `drift` error and its hint, the prompt and `serve`'s
  start message) names both causes and points to `opticli types --orphaned`. With only EF Core migrations or the CMS
  schema version on the database's side it still says "pull and build".
- When `serve` (or `env`) refuses a remote database passed with `--connection` or `OPTICLI_DB`, the hint now says how
  to make it choosable: add it to a launch profile in `Properties/launchSettings.json` (`environmentVariables`,
  `ConnectionStrings__<Name>`), user secrets or `appsettings.Development.json`, then choose it from `opticli db list`
  with `opticli db use <id>`. It only said that a string from the site's configuration can be chosen.

### Fixed

- Windows: the command a seal hands over (`userCommand`) gives a path as it is, without quotes, unless it needs them: a
  backslash no longer counts as a character to quote.
- `access <ref>` with a change failed with an `InvalidCastException` for content in a "For this page" folder. Such
  content has the access rights of the content that owns the folder, and the CMS doesn't let them be changed on their
  own, so a change is now refused (exit 3), as in the edit UI, naming the owner to change instead.
- `access <ref>` without options showed the wrong origin for a "For this page" folder and its content: the asset root's
  ancestors (often the root, `from: 1`) instead of the owner. It now follows the owner as the CMS does, and
  `assetsOf` names it.

### Skill

- The skill says that a plan leaves one draft per content and language (`savedInto`, `--version-per-step`), what to
  tell the user when drift has `removedFromCode` and when it hasn't, that "For this page" content follows its owner's
  access rights (`assetsOf`), and to pass the `serve` refusal's hint on to the user.

## 0.18.0 (16 October 2026)

Environments and content sync: give databases roles (production is only read), export content as a plan,
pull another environment's content into the local database, and let the user write a sealed plan to production.

### New

- Environment roles. `opticli db add <name> --role <local|dev|test|prod>` adds another environment's database by name
  (asking for the connection string, and its password without showing it, at a terminal; or `--connection`), and
  `--db <name>` reads it for a run. `db role <id|name> <role>` gives any database in `db list` a role, and
  `db remove <name>` removes an entry. `db list` and `doctor` show each database's name and role. A role belongs to
  the server and database (compared as SqlClient reads them, ignoring case, `tcp:`, the default port, an instance
  name beside a port and the loopback names; a `Failover Partner` counts too), not to the entry: every entry, setting and connection string that points at a production database is
  production, in every project. A weaker role than a database has needs a terminal, where the user types the
  database's name (`refused`, exit 3, `details.reason: "roleLowered"`, elsewhere), and `db remove` keeps the role.
- A database with the role `prod` is only read, when a run picks it with `--db`, `--connection`, `--profile` or
  `OPTICLI_DB`: responses carry a warning and `meta.database.role: "prod"`, and opticli connects with
  `ApplicationIntent=ReadOnly` unless the string sets the intent. `sql` works, with the warning. It is never the
  development database: `db use` refuses it, and a saved choice, a configured `connection` or a Development default
  that is production is refused with a hint to choose another. `serve`, `env` and every write command, `--dry-run`
  and `apply` included, are `refused` (exit 3, `details.reason: "prodDatabase"`), however the database was picked;
  the check runs before the command reads anything, again on the way to the site agent, and when the site starts.
  `serve --status` warns when the running site's database has been given the role since it started.
- `doctor` warns when the login saved for a production database can write (`db_owner`, `db_datawriter`, or INSERT or
  UPDATE on `tblContent`); it says nothing when it may not ask or can't connect.
- `meta.database` also appears for a local database with a role other than `local`, and has `role`.

- `opticli export <ref>... [--descendants]` (or `--site <name>`, `--all`) writes content of any database as a plan
  folder (`plan.json`, `files/` with media, `texts/` with long rich text) that `apply --update-existing` brings into
  another database with the same GUIDs. It only reads the database, over SQL, so it works on production (`--db prod`).
  Each item is a create, block or upload step with its values and built-in settings (publish dates, URL segment, menu
  visibility, simple address, shortcut, sort order and index, category), parents first and content before what refers
  to it; references go by `$id` to what the plan creates and by GUID to anything else, in blocks, rich text and CMS 13
  compositions (which keep their node keys) too; a cycle is closed by a later `set`; other languages are `translate`
  steps; published branches are published. Media files come from `--blobs` (a folder of blob containers, or an Azure
  container URL with a SAS token) or the local site's blob folder. The result lists what the target must already have
  (`externalRefs`), what was left out and why (`skipped`, newer `drafts`, `dropped` values) and what an export never
  writes (`notExported`).
- `export` reads in batches: a few queries per 250 items instead of about ten per language branch (an `--all` export of
  a 16 000-item site took 10 s instead of 97 s), with the same plan.
- `export` brings folders in a "For this page" folder, and what is in them (before: skipped), and a branch scheduled for
  publishing at a later time as a draft with a `publish` step at that time (before: a plain draft).
- A plan's `create` takes `"for": <ref>` instead of `"parent"`: a folder in that content's "For this page" folder (made
  when it doesn't exist), which has a GUID of its own in every database.
- Plans may have a top-level `"source"` object, which `apply` accepts and doesn't read (`export` will write it). In rich
  text in a plan, `src="$id"` (an image) and `href="$id?epslanguage=sv"` become permanent links too.
- `get` shows a page's `urlSegment` and `visibleInMenu` (as the version shown has them), which `set` writes as
  `PageURLSegment` and `PageVisibleInMenu`.
- `apply --update-existing` replaces an existing upload's file, in a new version, when the step's file differs
  (compared by SHA-256, also in a dry run, which sends no bytes; a warning says so), and is `unchanged` when it is the
  same. Before, the media item kept its file and the step said `unchanged` whatever the file was.
- CMS 13: a whole composition written to content (`composition` in `set`/`create` values or a plan) makes a node with a
  key the content doesn't have with that key, instead of refusing it (`usage`, "no node of the composition has the
  key"). A composition copied from another database keeps its keys, so writing it again finds them and changes
  nothing; before, it was written without keys and saved again, with new ones, every time.
- `blob` and the blob an upload reports read the file blob path from the environment too
  (`EPiServer__Cms__FileBlobProvider__Path`, over appsettings, as the site reads it).

- `opticli sync pull --from <name> (<ref>... | --site <name> | --all)` keeps the local database's content in step with
  another environment's: an export of the source (read over SQL only, so production is fine), a diff against the local
  database by GUID, and an `apply --update-existing` of only what changed, through the local site. What the local
  database lacks is created, what differs updated (published when the source's branch is), what the source moved moved,
  what is in the local recycle bin restored, and with `--delete` what the source deleted since it was synced (or has in
  its recycle bin) moved to the local recycle bin; content only in the local database is left alone. A sync state in the
  user config folder (per project, source and local database) records what each item's versions were, so the next pull
  reads only what changed: a local change to content the source changed too is a conflict, listed and left alone unless
  `--overwrite-local`; a local change alone is kept. The first pull takes the baseline (it can't tell local changes
  from the source's, so it writes over published local changes; a local draft is a conflict). The source's content
  model is compared with the local build's over SQL: content of a type the build lacks is skipped and properties it
  lacks are left out, each listed (`--strict` fails instead), and so are values that refer to content, visitor groups or
  categories the local database doesn't have. An empty diff applies nothing. It writes only to a database with the role
  `local` (`refused`, `details.reason: "syncTarget"`, otherwise). Output: counts and lists per action, `run` with
  `apply`'s report; progress on stderr at a terminal; after Ctrl+C or a failed step the state holds what was saved.
- `move` to the parent the content already has changes nothing (`unchanged` in a plan, a warning); the CMS saved and
  logged a move to the same place.

- A sealed apply writes to production, run by the user in their own terminal; a coding agent prepares and seals it but
  can't run it. `opticli apply <plan> --dry-run --seal-for <prod>` checks the plan against the local site, compares
  production's content model with the local build's and reads production's versions of everything the plan touches
  (over SQL, read only), and writes a seal (user config folder, MAC'd, 24 hours, used once) with the plan's hash (the
  plan and every file it reads), the target, the project and the build's fingerprint. It prints the command for the user
  (`data.userCommand`): `opticli apply <plan> --db <prod> --seal <id>`, which runs only at a terminal (`refused`,
  `details.reason: "sealNeedsTerminal"`, otherwise), shows what the plan does, asks for the database's name and a write
  login (never saved; the saved login stays read-only), refuses when the plan, the build, production's model or a touched
  item changed (`conflict`, exit 5, with what changed), starts the site against production for that run only (shared
  mode, no drift accepted, loopback only, serving only opticli's agent: its pages and edit UI answer 404; the token and the
  login given on its stdin, not in its environment), checks
  production again right before the first save, applies the plan from the bytes it hashed, and stops the site. Sealed
  plans only create, change, move, translate and publish content by GUID: no deletes, unpublishing, access rights,
  discards, uploads, other people's drafts or skipped concurrency checks (`refused`, `details.reason: "sealedPlan"`); the
  summary counts steps that set an expiry that has passed, which takes content offline. The output says to restart
  production's site: its servers keep what they had cached.
- `opticli sync push --to <prod> (<ref>... | --site <name> | --all)`: what changed locally, as a sealed plan for production
  (an export of the local content with only what differs from production). Media is left out and listed (unless
  production has it as it is locally: the same place, values and stored file); so is content
  production changed since the last pull, or never pulled from it (`conflicts`), and content with someone's unpublished
  changes there. It writes nothing to production; the user runs the command it prints.
- The site agent compares its code with a local database's content model on request (`drift?compare=model`), which
  sealing uses: types and properties only in the database would stop the sealed run's writes.

### Changed

- The refusal of a write to production (`prodDatabase`) now points at the sealed apply in its hint.
- `apply` checks a plan of thousands of steps much faster: what each step's check looked up by scanning the whole plan
  is indexed once per plan, and the approval sequence of the content steps publish below is read once per ancestor
  (the dry run of a 17 000-step plan `sync pull` made, reading included: 56 s instead of 240 s).
- `export` leaves out only the ContentArea item it can't write (a reference to content that doesn't exist), not the whole
  area (`dropped` names the item: `MainArea[2]`).
- A `roles` entry in the user config that opticli can't read stops it from using any database until it is fixed, and
  so does a user config that isn't valid JSON or whose root isn't an object (before, such a file was skipped, or
  failed as `internal`): without the roles opticli can't tell production from another database. An empty file counts
  as no settings.
- `--db <name>` with a database name that several connection strings share is no longer ambiguous when they point at
  the same server spelled differently (`localhost` and `127.0.0.1,1433`, say).

### Fixed

- A reference written back as `get` shows it (`set --values`, a plan's `properties`, a composition) is written by its
  GUID, not its ref: a ContentReference, PageReference or ContentReferenceList, one in a local or inline block or a
  block list, a shortcut's `to` and a composition's shared block. Given `get` output from another database (a copy,
  another environment), the ref named whatever content has that id there, often another item, and was written without
  a word. A reference without a GUID, and a content provider's, still go by their ref.
- The site agent's refusal of a value in `get`'s `{type, value}` shape in a plan names `get`, not the MCP tool
  `get_content`.
- CMS 13: a composition left without nodes on a language branch that isn't the master branch and isn't published
  (`set --lang sv` with `{"composition": {"sections": []}}`, or `composition --lang sv remove` of its last node; a draft
  save is enough) is `refused` (exit 3). The CMS (13.3) deleted the master branch's stored blocks on such a save, so
  every node of the published composition read as missing, in opticli and in the CMS.
- CMS 13: a plan's dry run takes a link inside a block (a local block's `Url`, a link in a block list or an inline
  block) to content the plan creates for what it is, as it already did for one in the content's own properties: the CMS
  reports those through the block's validator, so the dry run failed with "unresolved reference" and nothing was
  applied. An export whose start page's logo links to an image it exports ran into it.
- CMS 13: `StartPublish` or `StopPublish` written as `get` shows it (to the millisecond; CMS 13 keeps finer times) is
  the date the content has, not a change. Before, it saved a new version, so applying an export to a database that
  already had the content saved (and published) every such item again.

### Skill

- The agent prepares writes to production as a sealed plan (`sync push`, `apply --dry-run --seal-for`) and hands the user
  the command; it never runs it, fakes a terminal for it, changes a role or touches a seal.
- `sync pull`: run it only when the user asked, `--dry-run` first, and never add `--overwrite-local` or `--delete`, or
  delete the sync state, on its own.
- The skill tells an agent what `prod` means, never to work around a production refusal or a refused role change,
  and to leave `db add` and `db role` to the user.

## 0.17.0 (14 October 2026)

### Fixed

- A database server that answers but refuses the login now gives `login_failed` (still exit 4) instead of
  `unreachable`, whose hint about the network, the firewall and `az login` pointed the wrong way. For Microsoft Entra
  authentication, where the server says only "Login failed for user '<token-identified principal>'", the message and
  `error.details.identity` name the identity that signed in (UPN, object id, application id, tenant), and the hint says
  it has no user in the database and how one is added. A wrong SQL password and a database that doesn't exist
  ("Cannot open database") are `login_failed` too, each with its own hint.
- Connection ids (`db list`, `db use`, `--db`) are the same in every checkout of a project, a git worktree say: they
  were made from the configuration file's full path, so an id from one checkout was "not found" in another. A choice
  saved with an old id still holds.
- A site that crashes while `serve` starts it is reported with its first exception, and that exception's innermost
  inner exception, in the message and in `error.details.firstException`. The last 40 lines of its output, which
  `details.lines` still has, were mostly stack frames, and the cause was often above them.
- `serve` refusing a remote database that isn't the development database says how to choose it: `opticli db use <id>`
  with its id.

### Skill

- Microsoft Entra ID: the README and the skill explain `AZURE_CONFIG_DIR` for keeping a second Azure CLI account for
  the same subscription (signing in replaces the first), and `AZURE_TOKEN_CREDENTIALS=AzureCliCredential` for a site
  that `serve` starts whose `DefaultAzureCredential` picks another credential than the Azure CLI. opticli sets
  neither; the skill tries the second for one run when that failure shows.

## 0.16.1 (9 October 2026)

### Fixed

- The MCP module hides a property on a tab that requires access the editor lacks on the content (a tab only
  administrators see, say) in inline blocks and block lists' items too, as the edit UI does: `get_content` and diffs
  leave it out, writing it is refused (by position, `areaOps`, in a new block or a nested area's), and a whole area or
  list written back keeps it. It was only applied to content's own properties, so an editor's assistant could read and
  change such a property in an inline block or a list item. A local block's properties stay shown, as in the edit UI.
- A shared block given by its ref alone (`{"ref": "37"}`) in a ContentArea inside an inline block (`area add --type T
  --values '{"Items": [{"ref": "37"}]}'`, `create`, a whole area in `--values`) or inside a CMS 13 Visual Builder element
  was refused with "'Items[0]' must be an object"; it is now taken as in a ContentArea of the content itself.

## 0.16.0 (9 October 2026)

### New

- **Inline blocks in ContentAreas (CMS 12.20+ and CMS 13).** Blocks stored in a ContentArea itself, which opticli could
  only read so far, can now be made and changed:
  - **`opticli area <ref> <Prop> add --type T Prop=value...`** (with `--values`, `--name`, `--at`, `--display`) adds a
    new inline block, made as the CMS makes one (the type's default values) and validated by the CMS on save (required
    properties, the area's `[AllowedTypes]`). In a plan: `area` with `"type"`, `"values"` and `"name"`.
  - **`opticli set <ref> 'MainArea[2].Heading=New'`** (or `--values '{"MainArea[2]": {...}}'`) and **`opticli area <ref>
    <Prop> set <position> Prop=value --name N`** change one inline block's values (and name), the rest of it staying.
  - In a whole ContentArea (`--values`), an item with `type` is an inline block, `{"type": "TeaserBlock", "properties":
    {...}, "name": "Intro"}`. An exact copy of a block the area has (as `get` shows it) keeps that block; any other inline
    item is a new block with just the values it gives. It takes over the block it replaces (its render settings,
    personalization, and what the areas inside it pair with) only when which block that is is unambiguous; otherwise a
    warning names the positions whose settings were dropped. So the area `get` shows can be written back as is
    (unchanged, it changes nothing), also with blocks left out or moved.
  - For an editor (MCP), a whole area can't drop values they can't see or change, in the block, its local blocks, its
    block lists' items or the blocks inside its areas at any depth; the error names the value's path. Removing a block on purpose works by its
    area's path.
  - `area add`, `remove`, `move` and `set` take an area inside an inline or local block by its path
    (`'MainArea[0].Items'`), in plans and MCP `areaOps` too, and take an inline block by its position; `ref:` names shared
    blocks only. Area edits of an area that isn't culture-specific are refused outside the master language, as `set` is.
    In a plan, `MainArea[n]` after an `area add` is the new block, in the dry run too.
  - With `apply --update-existing`, an inline `area add` changes nothing when the area already has an inline block of
    its type with the step's name (whatever its values), or without a name, with the step's values; so a plan that adds
    a named block and changes it later runs again unchanged.
  - On a CMS before 12.20 these writes are refused with the version they need. Where the site's CMS 12 edit UI doesn't
    make inline blocks itself (`UIOptions.InlineBlocksInContentAreaEnabled`, off by default), a write that adds one says
    so in a warning: editors still see, edit, move and remove it.
  - The MCP module takes them too (`update_content`: `properties` and `areaOps` with `type` and `values`), with the edit
    UI's rules for each property and the type's access rights for a new block. `get_content` shows an inline block's
    `name`.

### Fixed

- The MCP module builds the edit UI's metadata for a block that isn't content (a block list's item, an inline block) the
  way the edit UI does when the block's own fails (a URL property's editor descriptor needs content to find), instead of
  refusing every change to it.

## 0.15.0 (7 October 2026)

### New

- **Optimizely CMS 13.** One opticli reads and writes CMS 12 and CMS 13 sites, fresh CMS 13 installs and databases
  upgraded from CMS 12 alike. Reads choose their SQL from the database's schema. The package carries two site agents,
  `agent/cms12/` (.NET 8, built against `EPiServer.CMS.Core` 12.0.3) and `agent/cms13/` (.NET 10, 13.0.0), and `serve`
  and `env` pick the one for the project's CMS major (else the database's); `doctor` shows `cmsMajor` for both. A CMS 13
  build against a CMS 12 database is refused (exit 3), since starting it would upgrade the database for good, and so
  is the reverse. Every existing command works on CMS 13, shared databases and drift included. See the README's
  "CMS 13" section.
  - **Sites are applications:** `sites` lists CMS 13's applications in the same shape, without `guid` and with
    `application` (the name that identifies it, which every site argument takes), `applicationType` and `isDefault`.
    `sites primary` and `sites host add|remove` change applications: `*` makes an application the default (moved from
    the previous one in one save), an application's URL follows its hosts (no SiteUrl, so no `--keep-site-url` or
    `--https unset`), a new host without a scheme gets http on loopback names and the site URL's scheme otherwise, and
    `--type preview|media` adds CMS 13's preview and media hosts (refused on CMS 12).
  - **Job names:** CMS 13 stores a job's class name. `jobs` shows the name admin mode shows (a built-in table for the
    CMS's own jobs, `[ScheduledJob(DisplayName = ...)]` in the site's code for its own), and both names work wherever a
    job is named. Remove Unused Content Variations (new in CMS 13) needs `--allow-destructive`.
  - **Content types of unknown origin:** CMS 13 records no class for a type whose class has a GUID. A type with neither
    a class nor a model-sync version on record, and no class in the build with its GUID (made in admin mode, or a code
    type a content import overwrote), is listed by `types --orphaned` with `originUnknown: true` once its class is gone,
    and `types remove`, `remove-property` and `prune` remove it only with **`--include-unknown-origin`**. In a shared
    database's drift such types are listed with `informational: true` and don't stop writes.
- **Visual Builder (CMS 13).** Kinds `experience`, `section`, `element` and `contract` (`--kind page` includes
  experiences, `--kind block` sections and elements). **`get`** of an experience or section shows its **`composition`**
  (sections → rows → columns → elements, each with key, name, type, display template and settings, and an inline
  block's values or a shared block's identity) instead of the `Layout` and `UnstructuredData` properties it is stored in
  (`--fields Layout` shows those); `get --text` prints it as a tree. `search` and `where-used` name the section and
  element a match is in; `where-used --type` counts inline blocks; `types` and `type` show `compositionBehaviors`,
  `contracts`, `implementedBy`, `blueprints`, `inlineUses` and `displayTemplates`; `allowed-in` lists where a
  composition takes a type. **`opticli display-templates`** lists display templates and their settings.
  - **`opticli composition <ref> add|remove|move|set`** changes one node (a section, row, column or element, named by
    its key or its unique name) on a new version, a draft unless `--publish`, with `--dry-run`: new inline blocks with
    their values, shared blocks by reference, sections from a section blueprint, names, display templates and settings
    (checked against the site's), and the whole node as JSON (`--node`). The CMS's structure rules are checked first, in
    opticli's words.
  - **`composition`** in `set` and `create` values (`composition=@file.json`) writes a whole composition in `get`'s shape:
    structure, order, names and styles as given, nodes with a key keep their block and get only the values given, so
    `get`'s output can be edited and written back. Write results list the changed nodes. Plans have an op `composition`.
  - **Content variations:** `versions` marks a variation's versions (`variation`), **`get --variation <key>`** shows one,
    `drafts` and `find --status draft` count a variation's drafts (`variationDrafts`), and **`--variation <key>`** on `set`
    and `composition` writes one.
  - **Blueprints** are left out of `tree`, `children`, `find`, `search` and `drafts` unless **`--blueprints`**.
    **`create --blueprint <ref|name>`** makes content from one (its type, values and composition), and `composition add
    section --blueprint` adds a section from a section blueprint.
- **LocalDB:** a connection string with `AttachDbFilename=|DataDirectory|\...` (the CMS templates' default) is resolved
  to the project's `App_Data` on Windows, as the site does. On Linux and macOS, where LocalDB doesn't exist, it is
  refused (exit 3) with a hint instead of failing with `internal`.

### Changed

- `--values` (on `set`, `create`, `composition` and the other writes) also takes a value as `get` shows it,
  `{"type": ..., "value": ...}`, references, links and local blocks included; a value `get` cut short is refused.
- Validation issues can carry a `code`; CMS 13's unresolved references have `unresolvedReference`. On CMS 13, `apply`
  refuses up front a plan whose rich text links to content a later step creates (CMS 13 refuses such a save): reorder
  it, or add the link in a later `set`.
- `--connection` strings with `Network Library=...` or `np:` are `refused` with a hint instead of `internal`.

### Fixed

- A PageType property (a page list's type filter) takes the page type's name, as `get` shows it, as well as its id or
  GUID; another kind of content type is a `validation` error instead of an internal cast error.
- Two `serve`s started at the same moment (different sites, or separate state) could pick the same free port, and one
  site then failed to start. A starting `serve` now holds a lock on the port it picked until its site listens.
- `get` showed `startPublish: null` for content whose current version is scheduled; it shows the scheduled time.

### Development

- `tests/fixtures/cms13/setup.sh` builds a fresh and an upgraded CMS 13 Alloy site with `VisualBuilderFixture.cs` (Visual
  Builder types, display templates, experiences, blueprints and content variations). `tests/fixtures/edge-cases/setup.sh`
  also builds the edge-case site from the CMS 13 site, with `Cms13Fixture.cs` in place of `Cms12Fixture.cs`, and
  `drift.sh` runs on CMS 13 copies. `read-battery.sh`, `write-battery.sh` and `vb-write-battery.sh` run whole command
  sets against one site. The integration oracle compares compositions node by node with the CMS's own composition
  mapper.
- `OptiCli.Agent.Tests` runs on both builds (net8.0 for CMS 12, net10.0 for CMS 13). CI and the release workflow build
  and pack both agents, `check-package.sh` checks that each is there and built for its .NET, and the Windows CI job
  starts SQL Server Express LocalDB (installing Microsoft's MSI, hash-checked, only when the image lacks it) and requires
  `LocalDbTests` to pass (`OPTICLI_REQUIRE_LOCALDB`).

## 0.14.0 (6 October 2026)

### New

- **`opticli types remove <type>...`**, **`opticli types remove-property <type> <prop>...`** and **`opticli types prune`**
  remove content types and properties that removed code left in the database, through the site (`serve`) with the CMS's
  own `IContentTypeRepository.Delete` and `IPropertyDefinitionRepository.Delete`. Only orphans, as the running site
  judges them: a type whose class it can't load (never one made in admin mode, nor one of the CMS's own) and a property
  that isn't in its type's code (`existsOnModel: false`) on a type defined in code. A type stays (`conflict`, exit 5)
  while content of it exists, also in the recycle bin, while a property has it as its block type, and while page-type
  values name it (the CMS would clear them); opticli never deletes content. A property's stored values, which the CMS
  deletes with it in every version and language, need `--allow-destructive`. Named items are all checked first, and
  nothing is removed if one can't go. `prune` removes every orphaned type that can go (a block type after the
  properties and types that used it) and lists the rest under `kept` with the reason; `--properties` adds orphaned
  properties, which it leaves alone otherwise, since one added in admin mode looks the same. Every command takes
  `--dry-run`. The output records what was removed (names, GUIDs, classes, properties, value counts) to make it again
  by hand: content types aren't versioned. Without `serve`, `unreachable` (exit 4); refused against a shared database
  (exit 3); `apply` has no such steps and the MCP module no such tools.
  Before each removal the site appends the full record to `removals.jsonl` in opticli's state directory (never rotated,
  `recordFile` in the output) and to its own output, so the record survives a lost answer, a timeout or Ctrl+C; a run
  that stops halfway puts the records of what it removed in `error.details.removed`. A property a content provider uses
  (where opticli can't count or see the values) is a `conflict` with no override, and a type that page-type values name
  lists the versions holding them. Each item is checked again just before it goes, and one removal runs at a time per
  site. On a terminal every field of what was removed is shown in full.

### Changed

- **`type`**'s `values` for a property that isn't in the code count content items and versions holding a value (they
  counted rows), and include values stored inside a block property and category selections: what removing it deletes.

### Fixed

- `restore` of a published block, media file or folder no longer says it is live again "at its URL": only pages have one;
  the rest are published again.

### Development

- The edge-case site's `OrphansFixture.cs` adds `EdgeTrashedPage` (an orphaned page type whose only page is in the
  recycle bin), `EdgeRemovedEmptyPage` and its block type `EdgeRemovedBlock` (orphans nothing uses), the property
  `EdgeRemovedEmptyText` (no values) and `EdgeAdminPage` (made in admin mode), makes them again at every start, and
  answers `POST /opticli-fixture/orphans` on loopback so the removal tests can make them again and run twice.
- `OPTICLI_REMOVALS_FILE` names another file for the removal record. The edge-case site sets it to its own
  `App_Data/opticli-removals.jsonl`, so the integration tests' removals stay out of your `removals.jsonl`.

## 0.13.0 (6 October 2026)

### New

- **`opticli trash`** lists the recycle bin, newest first: what was deleted (what was below it is counted in
  `descendants`), who deleted it and when, and `originalParent`, where `restore` puts it back. That is the parent the
  CMS itself stores for every move, for the edit UI's Restore (not the change log, so truncating that changes nothing):
  read through the site while `serve` runs (`meta.source: agent`), otherwise from that store's rows in the database
  (only those), with a warning that says why the site wasn't asked. `--since`, `--by`, `--type`.
- **`opticli restore <ref>`** brings deleted content back out of the recycle bin through the site, as the edit UI's
  Restore does: below the parent it had, or below `--to <parent>` (needed when the CMS has no record of it). It checks
  first that the item is what was deleted (not something below it: the hint names what to restore), that the parent
  exists and isn't in the recycle bin too, and that its type is allowed there. Content keeps its versions, so what was
  published is live again; a warning says so. Give its id or GUID: content in the recycle bin has no URL. `--dry-run`;
  in a plan `{"op": "restore", "ref": ..., "to": ...}`, checked when the plan runs if an earlier step deletes the same
  item (under whatever ref). The MCP module has no such tool. `delete`'s undo hint is now `opticli restore <ref>`.
- **`opticli users add <name>`** makes a local login for a restored database through the site's ASP.NET Identity (the
  user class the CMS UI uses, found at runtime), as the CMS's first-admin registration does: approved, in `WebAdmins`
  unless `--role` says otherwise (a missing role is created), tagged as made by opticli. The password comes from
  `--password-stdin`, a hidden prompt on a terminal, or is generated and written to a file only you can read
  (`passwordFile`); it is never printed. When the site's answer gets lost, that file is kept (the error says where) and
  only `users remove` deletes it. **`opticli users remove <name>`** removes only users opticli made, and
  **`opticli users roles`** shows the roles with member counts and what the virtual roles map to, never names or
  addresses. Refused against a shared database and on sites without ASP.NET Identity (exit 3); the MCP module has no
  such tools.
- **`opticli types --orphaned`** lists content types whose class is gone from the code, which the CMS keeps while
  content uses them, with their class on record (`modelType`) and how many items use them. With `serve` running the site
  says which classes it can't load (packages' too); otherwise the site's sources are scanned for the types of its own
  assemblies, with a warning that says why the site wasn't asked. **`opticli type`** now gives a property that isn't in the code (`existsOnModel: false`) its
  stored `values` (on content and in versions), and warns about such properties. Reads only: opticli doesn't remove
  types or properties.
- **`opticli history <ref>`** reads the CMS's change log for one item: creates, publishes, scheduled publishes, review
  requests, moves (from, to), deletes to and restores from the recycle bin, permanent deletes and deleted versions, with
  who and when. It is the only record of moves and deletes. `--since`, `--by`.
- **`find --status scheduled|expired`**: content with a version waiting to be published (`publishAt`), and published
  content whose stop-publish date has passed (`expiredAt`); in the master language unless `--lang`, as the rest of
  `find`.
- **`opticli categories`** lists the category tree with the names `set` takes, whether each is selectable, and how many
  items have it; **`opticli visitor-groups`** lists the visitor groups (the ids ContentAreas store, names, how the
  criteria combine), reading only that store's names and settings, never its criteria or notes. `get` already shows
  category names and the visitor groups of ContentArea items, and `set` already takes category names. A `set` with an
  unknown category on a site with many of them now points to `opticli categories` instead of listing them all.

### Development

- The edge-case site gets `UsersFixture.cs`: a user opticli didn't make, which `users remove` must refuse. The
  integration tests add a user, sign in with it on the CMS's login page, and remove it again.
- And `OrphansFixture.cs`: a page type whose class doesn't exist, used by a page, and an EdgePage property its class
  doesn't declare, with a value.

## 0.12.0 (6 October 2026)

### Changed

- **`opticli serve` turns the site's scheduler off.** A restored production database has its jobs enabled with next
  runs in the past, and a site started against it started every overdue one at once: imports and syncs reached
  external systems, emails went out, and Automatic Emptying of Trash deleted the recycle bin for good. Now the site
  agent turns `SchedulerOptions.Enabled` off for every `serve` run (the site's output says so), as it already did
  against a shared database. The site still registers its jobs, and `opticli jobs run` still runs one.
  `serve --scheduler` (or `"scheduler": true` in the user config, which `--scheduler false` overrides) leaves the
  scheduler as the site sets it and warns about the overdue jobs; against a shared database it is a `usage` error.
  `serve --status`, `serve` and `doctor` report `scheduler: on|off` as the running site has it, and `doctor` counts the
  overdue jobs. `opticli env` leaves the scheduler as the site sets it (`OPTICLI_SCHEDULER=on`): that run is yours.

### New

- **`opticli jobs`** lists the site's scheduled jobs from the database: schedule, next run, `overdue`, last run and
  how it ended (`succeeded`, `failed`, `cancelled`, `unableToStart`, `aborted`), whether one runs now (`"stale"` when
  the process running it stopped pinging), its class, and its file and line in the site's code. A job in the code that
  the database doesn't have yet is listed with `registered: false`. `--failed`, `--enabled`, `--disabled`, `--all`.
- **`opticli jobs log [<job>]`** lists runs, latest first, with status, trigger (scheduler, user, restart), server,
  duration and the job's message in full; without a job, every job's runs (`--failed --since 1d`: what failed since
  yesterday). A `<job>` is its id, name, class or a part of its name only it has.
- **`opticli jobs run <job>`** runs a job now through the site, as the admin UI's "Start manually" does, also while the
  scheduler is off, as the user `opticli`. It waits by reading the job tables, shows the job's status messages on a
  terminal, and ends with the run's status, duration and message: exit 0 when it succeeded, the new exit code 7
  (`job_failed`) when it didn't. `--no-wait` returns once it has started; `--timeout <s>` (the new error code
  `timeout`, exit 4) and Ctrl+C stop the waiting, not the job. Jobs that delete for good (the CMS's emptying of the
  recycle bin, removal of unused files, trimming of versions, truncating of the change log, ...; Commerce's removal of
  expired carts, archived items and lowest-price history) and Archive Function, which moves content across the site,
  need `--allow-destructive`. Jobs other than the CMS's own (the site's, add-ons') run with a warning, in
  `meta.warnings` or, when the run fails, `error.details.warnings`: opticli doesn't know what they change or contact.
- **`opticli jobs stop <job>`** stops a job the site runs, as the admin UI's Stop does, and waits up to 30 s for it to
  end; **`opticli jobs set <job>`** changes `--enabled`, the interval (`--every 1h`, `manual`) and the next run
  (`--next now|<time>`) through the CMS's job repository, showing the job before and after; a change that lets the
  scheduler run a job `jobs run` needs `--allow-destructive` for (enabled with a next run, sooner, more often) needs it
  too. `--every 1M` is refused as ambiguous (`m` is minutes, `mo` months). Both take `--dry-run`.
- Against a shared database `jobs run`, `stop` and `set` are refused (exit 3): the deployed site's scheduler uses the
  same jobs. The MCP module has no such tools.
- `doctor` and `serve --status` warn when the running site's agent is older than this release: it doesn't turn the
  scheduler off, so overdue jobs may run there.

### Development

- The edge-case site gets `JobsFixture.cs` (copied by `setup.sh`): "opticli test job", a manual, stoppable job with
  status messages that fails on request, and, with `OPTICLI_FIXTURE_SCHEDULER=on`, a scheduler the site turns on itself.
  The integration tests run, fail, stop and reschedule it.

## 0.11.0 (6 October 2026)

### MCP server for editors (preview): security fixes

Fixes from a security review of `OptiCli.Mcp` 0.9.0-preview (the same module shipped in 0.10.0-preview and
0.10.1-preview). Update before using it on a real site.

- **Return addresses are limited to Claude's and the editor's own machine.** A new option, `AllowedRedirectHosts`
  (`claude.ai` and `claude.com` by default), lists the hosts an app's OAuth redirect URI may be on, over https and
  matched exactly; a loopback address is always allowed. Checked when an app registers and again at every sign-in, also
  for apps with a metadata document and apps registered before. Before, any app could register any https return
  address under any name, and an editor's Allow would send the code there. `["*"]` allows any https host (with a
  warning at startup). The consent page now says plainly, in a warning, that an app which registered itself chose its
  own name.
- **Registration can't fill the database.** `RateLimits.RegisterPerMinute` is 10 by default (was 60), an IPv6 address
  counts by its /64, and registration stops (429) at 5,000 registered apps with no connection and no sign-in under way.
  Such an app is deleted a day after it registered (was 30 days). The cleanup runs in the background, never holding up
  a token response, without the request's context, and deletes at most 500 rows a run. Clients are only queried with a
  filter; grants and codes, which exist only by an editor's consent, are still loaded whole, and `register` counts the
  unused clients at most once a minute.
- **No script in rich text or links for editors.** Rich text with `<script>`, event handler attributes, `javascript:`,
  `vbscript:` or non-image `data:` URLs, `<iframe>`, `<object>`, `<form>` and the like is refused, however it is
  written; links (link properties and collections, URL properties, URLs in list properties' items, a shortcut's
  external link) must be http, https, mailto, tel or relative, have no attributes but href, title and target, and are
  given as objects, not as the CMS's stored markup, without control characters. What a property already has, live, may
  be written back as it is, but nothing new: script inside a `<textarea>`, `<style>`, `<template>`, `<svg>` and the like
  doesn't count as already there. The README recommends the CMS's own `ScriptParser` `SavingMode=Remove` too.
- **No files that run script for editors:** HTML, XML, XSLT, JavaScript and compressed SVG uploads are refused, an SVG
  file with script, `foreignObject`, event handlers or script links is refused (an ordinary drawing uploads), and so is
  markup named as a raster image. The README recommends `X-Content-Type-Options: nosniff` for the site's media.
- **Changes that put content live need the publish gate.** Moving content that has a published version or anything
  below it, discarding a version scheduled for publishing, and saving content without versions are refused
  (`publishingOff`) where the site doesn't allow publishing, also in a dry run. Content that may be live isn't moved
  below another approval sequence (`approvalSequence`).
- **Discarding someone else's draft needs `AllowDelete`** (`deletingOff`), as well as `includeDraft`. An editor's own
  drafts can be discarded as before.
- **What the edit UI checks is checked for editors:** the access rights per language (no draft, publish, unpublish,
  discard or new branch in a language the editor may not edit); properties the edit UI hides or locks for the editor
  (not shown in edit mode, `[Editable(false)]`, a tab whose required access they lack, or an editor descriptor or
  metadata extender, through the CMS UI's own metadata, also inside local blocks), which `get_content`,
  `get_content_type` and diffs leave out and writes refuse (and refused altogether for an item whose metadata can't be
  built), and which a rewritten block list keeps; and the access a group of content types requires on the parent of
  new content.
- **Content references given as text get the read check.** A ContentArea or reference list given as text (or a
  number) is refused, with a hint to give an array of refs; a content reference given as a number, and an embedded
  block in rich text, is looked up as the editor, so content they can't read is the same `not_found` as missing content.
- **Uploads follow the CMS UI's own upload rules:** its size limit (the lower of it and `MaxUploadBytes` applies) and,
  from CMS UI 12.33, its allowed file extensions.
- **The consent and connections pages refuse to be framed** whatever headers the site sends (`Sec-Fetch-Dest`), and
  their posts are refused when the browser says they came from another site (`Sec-Fetch-Site`).
- **The metadata documents are sent with `Cache-Control: no-store`.** The README recommends `RequireHost` or the
  site's `AllowedHosts`, and forwarded headers from known proxies only.
- `find_content` doesn't count content the editor can't read, so `truncated` no longer tells of it.

**Breaking** for a site that implements `IOAuthStore` itself: `DeleteExpiredAsync` takes a limit,
`CountUnusedClientsAsync` is new, and the store is called from a background thread after the request (without its
HttpContext or services, with the site's `ApplicationStopping` as the token), so it must be thread-safe and not scoped.
The developer's site agent (`opticli serve`) behaves as before.

### Fixed

- A value in `get_content`'s `{type, value}` shape, or one JSON can't make into the property's type, is a usage error
  rather than an internal one, for the CLI as well. New content whose save failed inside the CMS (a value the database
  can't take) is no longer reported as saved because it could still be found by its GUID for a moment.

### Development

- The MCP test site has a block type with properties the edit UI hides or locks, a language only administrators may
  edit, and a search fixture; `serve.sh --no-publish` now also turns deleting off (the module's defaults).

## 0.10.1 (5 October 2026)

### Fixed

- `sites primary "Site A=<host>"` with a host that is already the site's primary host for one language, beside its
  primary host for every language, kept the host's language but still moved SiteUrl onto it. Such a pair now works
  as `Site A@<lang>=<host>` would: SiteUrl stays unless it was on the primary host the pair replaces, and a warning
  says how the pair was read. On a site whose hosts are all for one language, a pair without `@lang` still moves
  SiteUrl. As before, every primary pair makes the Edit host undefined (unless `--keep-edit`).

## 0.10.0 (5 October 2026)

### New

- **`opticli sites primary` points a restored database's sites at localhost.** A copy of a production database has
  the production host names, so locally every site but the one with `*` is unreachable and absolute URLs point at
  production. `sites primary "Site A=localhost:5001" "Site B=localhost:5002"` adds each host if the site lacks it and
  makes it the primary host; the previous primary host and the Edit host become undefined, and SiteUrl follows,
  keeping its path (`--keep-edit`, `--keep-site-url` to leave them). `Site A@nb=...` sets a language's primary host,
  `https://localhost:5001/` or `--https true|false|unset` its https setting; a default port goes (`localhost:443` is
  `localhost` with https). The production hosts stay. It saves through the site (needs `serve`), which clears the site
  definition cache: no restart for the site `serve` runs; a warning says to restart any other process running it.
- A pair without `@lang` replaces the site's primary host: on a site whose only primary host is bound to a language
  (all its hosts for one language), the new host gets that language, so one pair is enough, in any order with the
  other pairs; a host that is already primary keeps its language, so running the pairs again changes nothing. A
  `@lang` pair moves SiteUrl when SiteUrl was on the primary host it replaces. `doctor` and `--from-config` read saved entries the same
  way. Once SiteUrl has moved, a SiteUrl that was on a host that isn't primary can't be put back with the commands.
- All pairs are one batch, checked before any site is saved, including the host the CMS adds for SiteUrl
  (`validation`, exit 5, naming the pair): a host another site has, two primary hosts for a site and language, `*`
  twice. Should the CMS still refuse a site while saving, the error says which sites were already saved. A site already
  as asked is `unchanged`, so it is safe to run again; `--dry-run` shows each site's end state. `meta.warnings` names
  languages whose URLs still use a production host.
- `--save` keeps the pairs, with `--keep-edit` and `--keep-site-url`, in the opticli user config for the project
  (`sites.primary`), so after the next restore `opticli sites primary --from-config` is all it takes; it skips a saved
  site the database doesn't have, and `--forget <site>` drops one. `doctor` reports sites whose primary host differs
  from the saved mapping.
- `opticli sites host add <site> <host>` (`--type`, `--lang`, `--https`) and `sites host remove <site> <host>` change
  one host. The site's last host and SiteUrl's host can't be removed.
- Against a shared database `sites primary` and `sites host remove` are refused (exit 3), and `sites host add` only
  adds hosts of type undefined: the deployed site uses the same site definitions. The MCP module has no such tool.

### Fixed

- URLs in every output (`url`, `get`, `resolve`, ...) used a language's primary host for the site's other languages
  when that host was listed before the site's own primary host, as it is once `sites primary` has added a local one.
  The host for every language comes first now, as in the CMS.
- A command with its own text rendering (`drift`, the new `sites primary`) left its warnings out in text mode; they
  are on stderr now, as for tables.

### Development

- The edge-case site has three more sites (`Edge hosts A`, `B`, and `C`, whose hosts are all for one language) for
  the site host integration tests, which put their hosts back after each test. Run
  `tests/fixtures/edge-cases/setup.sh` again to add them.

## 0.9.0 (5 October 2026)

### MCP server for editors (preview)

`OptiCli.Mcp` is published as a prerelease (`0.9.0-preview`): the options and tools may still change, and it supports
CMS 12 only, not CMS 13.


- **New package `OptiCli.Mcp`** for the site itself: editors connect Claude (claude.ai, Claude Desktop, Claude Code) to
  an Optimizely CMS 12 site, sign in with the site's own login, and Claude works as them, with their access rights.
  `services.AddOptiCliMcp()` and `endpoints.MapOptiCliMcp()` (after `MapContent()` and the site's own endpoints); the endpoint is
  `/episerver/opticli/mcp`. Needs CMS 12.12.1 and CMS UI 12.16.1 or newer. See the README's "MCP server for editors".
- A small OAuth 2.1 authorization server in the module: client ID metadata documents and dynamic client registration,
  PKCE, refresh tokens that rotate (a rotated-out one used again revokes the connection, after a grace period for a
  client's own retries, `RefreshTokenReuseGrace`), a consent page where the editor unticks what the app shouldn't do,
  and a gate checked at consent and at every refresh: the editor's current roles (`AllowedRoles`) and, for an
  account the site manages (ASP.NET Identity), that it is still active; a user synchronized from an external login
  goes by their roles. `ConnectionLifetime` (30 days) bounds a connection from when it was allowed, however often it is
  refreshed. Rate limits are options (`RateLimits`); the token
  endpoint counts per client, as claude.ai's editors share its addresses. The site's default authentication scheme
  is left as it is.
- Tools: `whoami`, `get_content`, `list_children`, `resolve_url`, `find_content`, `get_content_type`, `list_versions`,
  `create_content`, `update_content`, `add_language`, `upload_media`, `discard_draft`, `move_content`,
  `publish_content` and `unpublish_content`, which refuse (`publishingOff`) unless the site sets `AllowPublish`, and
  `delete_content`, offered only when it sets `AllowDelete`. Both options are off by default. `requestApproval` only
  ever sends content for review, so it works without publishing. Content an editor can't read is the same
  `not_found` as missing content. Every write result has `editUrl`, the version in the CMS edit UI.
- The module's errors (the MCP endpoint's 401 above all) have a body and switch the site's status code pages off, so
  custom error pages don't replace them.
- `/episerver/opticli/connections` lists an editor's connections with Revoke (administrators see everyone's), and the
  `OptiCli.Mcp.Audit` log category records sign-ins, tokens, revocations and tool calls.

### Development

- The content operations moved from the site agent to `src/OptiCli.Cms`, which the site agent and the MCP module both
  run. Nothing changes for the CLI.
- `tests/fixtures/mcp/setup.sh` builds an MCP test site from the edge-case site, and `tests/OptiCli.Mcp.Integration`
  tests the module end to end with the official MCP SDK's client.

## 0.8.0 (2 October 2026)

### Behaviour changes

- **The pending-draft check looks at content, not version numbers.** A publish asked for confirmation whenever
  someone else had saved a version between the published one and the one going live. It now asks only when the
  version going live still has that draft's value for at least one property the draft changed. A change based on the
  published version (see `--from`) therefore doesn't ask about drafts it leaves out, and nor do later publishes
  built on it. A draft by someone else that changed nothing no longer asks either.

### New

- `set` and `area` take **`--from <version|published>`** (`"from"` on plan steps): the change is based on that
  version instead of the latest, while the concurrency check still compares against the latest. With
  `--from published --publish` a change goes live without someone else's older draft. Newer versions the change leaves
  out are listed in `leftOut` with a warning: they stay as they are, and edit mode opens the new version instead.
  The conflict hint suggests `--from` when a newer version is in the way.

### Fixed

- **Publishing a language branch before its master language.** The CMS publishes a branch other than the master
  only once the master branch has been published, and refuses at save time, so every dry run missed it and a plan
  stopped halfway. `set`, `area`, `translate` and `publish` with `--lang` now refuse before saving (`validation`,
  exit 5, `details.reason: "masterNotPublished"`), also as a dry run. A plan's dry run checks the order of its
  publishes, and the hint names the step to move. A scheduled publish or a review request only warns, since the CMS
  saves those and fails when they come due. When the CMS refuses on its own, the hint now says to publish the master
  first instead of suggesting `--dry-run`.
- A plan's `publish` or `set` of another language of content the plan creates was dry-run as a publish of the master
  branch, so it always passed; it is now `deferred`.

### Skill

- Agents name content next to its ref ("About us" (page 10, draft 10_1473)) and say who saved a version and when,
  instead of giving bare ids.

## 0.7.1 (2 October 2026)

- Fixed: classes declared without a body (`public class AboutBlock : SiteBlockData;`, C# 12) were left out of the
  source scan. A plan's `area add` was refused as not allowed by `[AllowedTypes]` when the area named a base class
  of such a block, although the CMS allowed it; `allowed-in` missed the same areas, and `type` found no C# class.
  A body-less base class broke this for every type derived from it.

## 0.7.0 (2 October 2026)

### Shared databases

- **Writes stop when the build and a shared database differ** (drift). Against a remote development database the
  site doesn't sync its content types, so the database keeps what the deployed code made. A write would run this
  build's code against content the deployed site serves, so it now asks on a terminal, and elsewhere fails with
  `drift` (exit 5) with the differences in `error.details`. `--accept-drift <fingerprint>` confirms (once for a plan:
  `apply --accept-drift`); the fingerprint stops counting when the differences change. Dry runs don't stop.
- `opticli drift` lists what differs: content types and properties in the code but not in the database and the
  reverse, changed property types and culture-specific settings, renames a migration step hasn't applied, EF Core
  migrations, Dynamic Data Store types and the CMS schema version. Each says which side is ahead: `local`,
  `database`, or `unknown` when the database doesn't say; the report as a whole can also be `both`. `serve` prints a
  summary, `doctor` has a `drift` section, and once `serve` has reported drift, commands that use the database and
  `serve --status` carry a warning.
- `serve` refuses (exit 3) a build with EF Core migrations the database's `__EFMigrationsHistory` lacks, since a site
  that migrates at startup would apply them for everyone. `--allow-pending-migrations` starts it anyway.
- `serve` says when the database's CMS schema version doesn't fit the build's packages, before it starts the site,
  instead of leaving a stack trace in the log.
- Dynamic Data Store remapping is off in shared mode too: a store whose type changed fails when used, and drift
  reports it.
- `serve` predicts whether the CMS starts against a schema one version newer from the build's EPiServer.Framework
  version (12.17 and later accept it).
- README: a "Shared databases" section on what opticli can't turn off (the site's own startup code), and why a login
  without DDL rights helps.

## 0.6.0 (1 October 2026)

### Behaviour changes

These can break scripts and plans that relied on the old behaviour:
- **Approval sequences are respected.** A publish of content with an approval sequence (its own or inherited) is
  refused (exit 3, `details.reason: "approvalSequence"`), also as a dry run and as a plan step. It skipped the
  reviewers before. `--request-approval` sends it for review instead. Content in review can't be changed until a
  reviewer decides (`conflict`, `details.reason: "inReview"`).
- **`delete` stops when other content references what it deletes** (`conflict`, `details.reason: "referenced"`,
  `details.references`), and asks on a terminal. `--ignore-references` (a plan's `"ignoreReferences": true`) deletes all
  the same. In a plan, references from content that earlier steps change only warn.
- `get --lang` follows the language settings: a replacement language, or the first fallback language that has a
  branch, before the master language. `languageRule` says when they decided.
- A diff's `before` gives ContentArea items an explicit `"group": ""` and `"visitorGroups": []`, so sending it back
  takes away personalization added since.
- Any publish clears a stop-publish date that has passed (with a warning), unless the change sets one.

### Writes

- `opticli unpublish <ref>` takes a published branch offline, as the edit UI's expiry does. Undo with
  `opticli publish <ref> --version <previouslyPublished>`.
- `opticli discard <ref> [--version <id>]` deletes a version that was never published. It can't be undone; a version
  someone else saved needs `--include-draft`.
- `--publish-at <time>` on `set`, `area`, `create` and `publish` schedules the publish. `drafts` and `versions` show the
  time.
- `--request-approval` on every write that can publish, and `apply --request-approval`.
- `translate --with-blocks` also translates the blocks in the content's "For this page" folder.
  `translate --remove --confirm` deletes a language branch.
- `upload <file> --replace <media-ref>` gives existing media a new file, as a new version of the same type.
- Plans: `href="$id"` (and `data-contentguid`/`data-contentlink`) in rich text and `@file` HTML link to content the
  plan creates, as permanent links. Links to later steps need a `guidNamespace`. New ops `unpublish` and `discard`.
- Visitor group ids in ContentAreas are checked: an unknown id is refused.
- Saving a fetch-data page no longer always counts as a change.

### Reads

- `get` and `access` show the approval sequence that applies (`approval`).
- `get` shows `visitorGroupNames` beside visitor group ids, `personalized[]` for rich text only some visitor groups
  see, and `projects`. A fetch-data page notes where its empty properties come from.
- `where-used --type <T>` lists every instance of a type with its usages. A reference only inside personalized rich
  text says which `visitorGroups` see it.
- `opticli projects [<id>]` lists projects and their items.
- URLs: a site whose start page has language settings only takes its active languages as a prefix.
- `get` notes when published content is offline because its stop-publish date has passed.

### serve, env and configuration

- The site agent refuses requests that came through a proxy or tunnel (`X-Forwarded-For`, `Forwarded`, ...), and stops
  reading a body at the size limit.
- A body the site's web server refuses as too large (IIS 404.13, 413) is reported as such, and an agent that rejects a
  field this CLI sends says to restart `serve`.
- Ctrl+C while the site starts stops it within 5 s, so the `cancelled` envelope is printed.
- `env` warns about another spelling of the connection string exported in the shell.
- `Directory.Build.targets` is read too (it can set `UserSecretsId`). JSON configuration values come out as ASP.NET Core
  reads them (`True`/`False`, `""` for null, empty sections).

### Project

- `tests/fixtures/edge-cases/` builds an edge-case site from an Alloy site for the integration tests: fetch-data pages,
  a nested site, approvals, language settings, personalization, a project and PDF media. The integration tests run
  one at a time.

## 0.5.0 (1 October 2026)

### Output changes

These can break scripts that read opticli's JSON:
- `get`: ContentArea items carry `group` and `visitorGroups` as their own fields, the same names `set` takes. They
  were nested under `personalization`.
- `doctor` and `db list`: a connection string from `OPTICLI_DB` shows as source `optiCliDb`. `environment` now means
  an exported `ConnectionStrings__<Name>`.
- An interrupted command exits 130 with error code `cancelled`. It exited 1 with no output.

### Safety

- `sql`:
  - Closed a way around the read-only check: SQL Server ends a `--` comment at a lone carriage return, which opticli
    didn't see.
  - Look-alike (fullwidth) letters in names are refused.
  - So are server-wide views, functions, logs and traces. In `sys`, only the views that describe the database's own
    schema are allowed.
  - A query that ends its transaction fails instead of returning rows.
- **Publishing someone else's changes needs confirmation.** A publish whose version holds changes someone else saved
  since the published version stops. It shows who saved what, and asks on a terminal; elsewhere it fails with
  `conflict` (exit 5).
  - `--include-draft` (a plan step's `"includeDraft": true`) confirms.
  - `publish --version <id>` publishes that version as it is.
  - Drafts opticli saved itself don't need confirmation.
- Undo hints after a publish name the version that was live before, not the version the change was based on.
- Writing a whole ContentArea keeps each item's group and visitor groups. `group` and `visitorGroups` can be set, and
  unknown display options are refused with a suggestion.
- `UserSecretsId`, the target framework and the assembly name are read through `Directory.Build.props` and the files
  it imports, so user secrets set there are no longer skipped.
- An exported `ConnectionStrings__<Name>` counts as the site's connection string, in ASP.NET Core's order. `serve`
  removes other spellings of it, so they can't win over the pinned database.
- `create`, `block`, `upload` and `move` check where content may go: the CMS's allowed types, and where pages,
  blocks, media and folders may live. `create` refuses media types; use `upload`.

### Plans

- A `publish` after `set`, `area` or `translate` steps on the same content validates. So does a `set` on a language
  branch an earlier `translate` creates.
- `delete` and `move` of content that contains a start page or asset root are refused during validation, not
  halfway through the plan.
- A failing or interrupted `apply` still prints what it saved, with undo hints and `details.partial`.
- A restore from the recycle bin happens last, and is undone if the save fails.
- A save that the site's own code fails after is reported as saved, with `siteError`.

### serve and env

- `serve`:
  - A lock stops two `serve` runs from starting the same site twice.
  - `--stop` asks the site to shut down gracefully on every OS, including Windows, before signalling or killing it.
  - Sites with `Kestrel:Endpoints` in their configuration start: opticli adds its own endpoint. A timeout names the
    addresses the site listened on.
  - `--foreground` sends the site's output to stderr when stdout is redirected, so stdout stays one JSON line.
  - Each run starts a new log, and the last 3 are kept. `--logs --tail` reads from the end.
  - A corrupt state file counts as stale. Ctrl+C while the site starts stops it.
  - Build output is found in runtime-specific folders, `OutputPath`, `BaseOutputPath` and artifacts output. The
    "sources are newer" check follows project references.
  - macOS starts the site in its own session.
- `env` honours `--json` and blanks stale `OPTICLI_*` exports.
- `db use` keeps the config file's permissions.
- `skill install` ignores CRLF line endings when it checks for local edits.

### Reads

- `allowed-in`: media types match `ImageData`/`VideoData` only when the CMS records them as images or videos.
- `search --lang` and `find --where Block.Prop --lang` find shared values, as `get` shows them.
- URLs:
  - Fetch-data pages keep their own URL.
  - The nearest start page decides which site a page belongs to.
  - Simple addresses resolve only on their own site, and also below existing pages.
  - A site whose assets root is the global one gets `/globalassets/` media URLs.
- `PageType` properties were always empty; they show the type name.
- Faster on large sites: instance counts load only for `types` and `type`, `children` reads full details only for
  the page it returns, and wide `where-used --pages` traversals index references once.

### Project

- CI runs the tests on Linux, Windows and macOS. A release checks that the tag is on `main` and that the package
  holds the site agent and starts.
- New tests for the CLI itself and for the site agent's startup code.

## 0.4.1 (30 September 2026)

- Fixed:
  - `serve --status`, `--logs` and `--stop`, which 0.4.0 refused;
  - `"https": true` in the user config, which 0.4.0 ignored.

## 0.4.0 (30 September 2026)

- `opticli upload` creates media from files up to 50 MB, also as a plan step.
- `opticli access` shows and changes an item's access rights:
  - grants for roles and users;
  - revokes;
  - breaking and restoring inheritance, with a lock-out check.
- Plans:
  - `"@file"` values;
  - fixed GUIDs with `"guidNamespace"`;
  - `apply --update-existing`;
  - dry runs that check steps on planned content against stand-ins.
- Page settings in `get` and as values to set: Shortcut, SimpleAddress, Category, ChildSortOrder, SortIndex,
  StartPublish and StopPublish. Block list properties can be written.
- Content provider refs (`63__provider`) work as `get` prints them.
- `serve --https` also binds `https://localhost` for sites that redirect to HTTPS.
- `skill install` replaces a copy it installed, when the files are unedited, without `--force`.
- `delete` lists the "For this page" folders that stay behind, as in the CMS.

## 0.3.0 (30 September 2026)

- First release on nuget.org.
- Reads straight from the database: `doctor`, `get`, `tree`, `find`, `search`, `where-used`, `resolve`, `type`,
  `allowed-in`, `versions`, `drafts`, `sql` and more.
- Writes through the running site with `serve`: `set`, `create`, `block`, `area`, `publish`, `translate`, `move`,
  `delete` and `apply`.
- A Claude Code skill, installed with `opticli skill install`.
