# Optimizely Commerce

opticli writes and reads Optimizely Commerce through the running site, as it writes CMS content: catalogs, nodes,
products, variants, bundles and packages, their relations, prices, warehouses, stock and markets. With plan steps,
`$id`s, fixed GUIDs, `--dry-run`, and `unchanged` when a plan runs again, so an E2E suite can seed a Commerce site from
code. Commerce 14 (CMS 12) and Commerce 15 (CMS 13) are supported.

```sh
opticli serve                                                            # the site, with the Commerce agent
opticli commerce                                                         # Commerce's version, the catalog root, the catalogs
opticli create catalog-root --type CatalogContent --name Fashion --publish \
  DefaultCurrency=USD DefaultLanguage=en WeightBase=kgs LengthBase=cm
opticli create catalog:Fashion --type FashionNode --name Women Code=women --publish
opticli create code:women --type FashionProduct --name "Summer dress" Code=P-1001 Brand=Acme --publish
opticli create code:women --type FashionVariant --name "Summer dress S" Code=P-1001-S Size=S --publish
opticli commerce relation add code:P-1001 code:P-1001-S
opticli commerce price set code:P-1001-S --market DEFAULT --currency USD --amount 79
opticli commerce inventory set code:P-1001-S --warehouse default --in-stock 25
opticli set code:P-1001 Brand="Acme Co" --publish
opticli commerce prices code:P-1001-S
```

## How it works

Commerce sites get a second agent. opticli's site agent loads `OptiCli.Agent.Commerce.dll` (shipped in the tool, built
for Commerce 14 and 15) into a site whose build has `EPiServer.Commerce.Core` (its `.deps.json` lists it), and only
there: a site without Commerce runs exactly as before. `serve`, `serve --status` and `doctor` show `commerce`: the
site's Commerce version and whether the agent loaded (`agent: false`, with `error` saying why). `doctor` shows the
version the project builds against as `project.commerceVersion`. `OPTICLI_COMMERCE=off` in the site's environment turns
the Commerce agent off.

Everything goes through the site, never through SQL: catalog content through `IContentRepository` (the site agent's
own content writes), relations through `IRelationRepository`, prices through `IPriceDetailService`, warehouses through
`IWarehouseRepository`, stock through `IInventoryService` and markets through `IMarketService`. Reads too: catalog
content isn't in the CMS database (it is in Commerce's), so opticli reads it from the running site. `sql` stays
read-only.

Commerce makes its own schema in an empty database at the site's first start, next to the CMS's, so a test database
needs no setup: point the site's `EPiServerDB` and `EcfSqlConnection` connection strings at empty databases and run
`opticli serve` once.

## Catalog refs

Catalog content has refs of its own. They work wherever a command or a plan takes a ref for catalog content, and in
the values of reference properties (a page's `ContentReference`, `ContentArea` or reference list), where opticli
turns `code:`, `catalog:` and `catalog-root` into the content's `id__CatalogContent` through the site (a text
property keeps such a value as text). A ContentArea or reference list can mix catalog content and CMS content:
`opticli set 9 --values '{"MainContentArea": [{"ref": "code:P-1001"}, {"ref": "123"}], "RelatedContent": ["catalog:Fashion", "45"]}'`. In a plan, name content the plan makes by its `$id`: a `code:` the plan makes
first doesn't exist yet when the dry run checks the step.

| Ref | Names |
|---|---|
| `code:<code>` | a node or entry by its code (`code:P-1001`), the same in every database |
| `catalog:<name>` | a catalog by its name (catalogs have no code) |
| `catalog-root` | the catalog root, which catalogs are made below |
| `<id>__CatalogContent` | as opticli prints catalog content: `1__CatalogContent` (an entry), `1073741825__CatalogContent` (a node), `-2147483646__CatalogContent` (a catalog; Commerce encodes the kind in the id, so catalogs and the root have negative ids). The id is the database's own, and only an id Commerce gives content names it (`-5__CatalogContent` names nothing) |
| a GUID | the content's GUID, which a plan can fix (`guid`, `guidNamespace`) |

Catalog content is found by the site's Commerce agent, not in the CMS database: Commerce doesn't register it in
`tblMappedIdentity`, where other content providers' content is. A GUID is looked up there first, then in Commerce (only
on a site whose content model has catalog types: elsewhere an unknown GUID is `not_found` as before, site running or
not). A CMS property that references catalog content shows its ref and `"provider": "CatalogContent"` in `get`, and so
does a rich-text link to it when the site runs (a link has only the GUID, which `get` asks the site about).

Catalogs go below `catalog-root`, nodes and entries below a catalog or node, and CMS content below CMS content: a
catalog type below CMS content, or a CMS type below catalog content, is a `usage` error in the dry run and the run
(and in a plan, also below content the plan makes).

## Catalog content: create and set

Catalogs, nodes and entries are content, and their types come from the content model (`opticli types`, `opticli type
FashionProduct`): `create` and `set` write them as they write pages, through the site agent. The properties are the
type's, Commerce's own among them: `Code` on nodes and entries (Commerce makes one when it is left out), `DisplayName`,
`SeoUri`; on a catalog `DefaultCurrency`, `DefaultLanguage` and `WeightBase` (required), `LengthBase`,
`CatalogLanguages` (its languages besides the default, see [Languages and translations](#languages-and-translations))
and `Sites` (the sites a catalog belongs to; none: every site).

| Command | Does |
|---|---|
| `create catalog-root --type CatalogContent --name <name> ...` | a catalog |
| `create <catalog-ref> --type <type> --name <name> [Prop=value]... [--lang] [--publish] [--guid]` | a node, product, variant, bundle or package below a catalog or node (its parent is its primary node) |
| `set <catalog-ref> [Prop=value]... [--name] [--lang] [--publish] [--base-version] [--force]` | a new version with the changes; `set <ref> --publish` alone publishes a draft. `--base-version` takes the version as a result prints it (`1_33_CatalogContent`) or its number |
| `translate <catalog-ref> --lang <code> [Prop=value]... [--publish]` | a translation into one of its catalog's languages: on Commerce 15 a new language branch, else (Commerce 14, or a branch that exists) a new version in that language, as `set --lang` (`existing: true`). See [Languages and translations](#languages-and-translations) |

Catalog content is a draft until it is published (`--publish`, or `apply --publish`). A plan's `create` step with a
`guid` (or a `guidNamespace`) and `apply --update-existing` updates the content when it exists, and reports
`unchanged` when nothing differs. What catalog content doesn't take is a `usage` error before anything is saved:
`--for` and `--blueprint`, projects (`--in-project`; a plan's own `project` leaves catalog steps out, as it leaves out
folders), `--request-approval`, `--publish-at`, `--from`, `--variation`, ContentArea and composition edits. `translate`
takes catalog content ([Languages and translations](#languages-and-translations)); `publish`, `unpublish`, `discard`,
`move`, `delete`, `translate --remove` and `access` refuse it: opticli never deletes it (Commerce has no recycle bin
for it); use Commerce's catalog UI. Results print catalog content's ref and version as Commerce has them
(`1__CatalogContent`, `1_4_CatalogContent`); `get` and `versions` don't read them.

Blocks and media don't go "for" catalog content or below it: catalog content has no "For this page" folder, and
holds no CMS content. `upload --for` or `--parent`, `block create --for` or `--parent`, and a plan's `create` step
with `for`, given a catalog ref (or, in a plan, the `$id` of catalog content), are a `usage` error in the dry run and
the run, whose hint says what to do instead: upload the image to a media folder and attach it with
`CommerceMediaCollection`.

### Languages and translations

A catalog's languages are its `DefaultLanguage` and its `CatalogLanguages` (a list of language codes): Commerce keeps
the catalog's content in those languages only, and its catalog URLs have the language in them (`/sv/fashion/women/`).
Give the list when the catalog is made, or set it on an existing catalog; Commerce adds the default language itself.
Every language in it must be enabled in the CMS (`opticli languages`), and a market sells in the languages it has
(`commerce market set --languages`).

```bash
opticli create catalog-root --type CatalogContent --name Fashion --publish \
  --values '{"DefaultCurrency": "USD", "DefaultLanguage": "en", "WeightBase": "kgs", "LengthBase": "cm", "CatalogLanguages": ["en", "sv"]}'
opticli set catalog:Fashion --values '{"CatalogLanguages": ["en", "sv", "nb"]}' --publish
```

Nodes and entries are made in the catalog's default language (their master language) and translated with `translate`
or `set --lang`, which write the culture-specific properties (`DisplayName`, `SeoUri`, and the model's
`[CultureSpecific]` ones). `--publish` publishes the translation in the same step:

```bash
opticli translate code:women --lang sv DisplayName=Dam Teaser="För henne" --publish
opticli set code:women --lang sv DisplayName=Dam --publish
```

- **Commerce 14** keeps catalog content in every language of its catalog from the start: when it lists an item's
  versions, it saves a version of its own in each language that has none (saved by `system`), a copy of what it serves
  in that language. `translate` saves into that language as `set --lang` does (`existing: true`), and neither counts
  Commerce's copy as someone else's draft: the translation is published without `--include-draft`. The copy is left
  out only while it is what Commerce made: the first version in that language, saved by `system` (or with no user
  name) within a second of a save in another language, with every culture-specific property empty but the `SeoUri`
  Commerce makes for the language (`<name>-sv.aspx`). A draft someone else saved in that language, a later version,
  or a first version that gives the language a value, still stops the publish, as for any content
  (`details.reason: "pendingDraft"`; see [Writing](commands.md#writing-needs-opticli-serve)). What a job or an import
  saves as `system`, or without a user name, right after a save in another language and with no value of that language
  passes as Commerce's copy; it carries only shared values, which are the master language's.
- **Commerce 15** makes a language branch as the CMS does: `translate` makes it (`set --lang` on an item without the
  branch is `not_found`, and says to translate it first). A `translate` of a branch that exists saves into it as `set
  --lang` does, so a plan's `translate` step runs again `unchanged` on either major.

Catalog content has one name, the master language's, in every language: Commerce drops a name saved in another
language. The name shown in a language is its `DisplayName`, so `--name` with `--lang` in another language is a `usage`
error (unless it is the name the item has). A language that isn't one of the catalog's is a `usage` error before
anything is saved, for `create`, `set` and `translate`, with the `set catalog:... --values '{"CatalogLanguages":
[...]}'` that adds it: Commerce 14 has no such language to save into, Commerce 15 saves a version it then can't load,
and a `create` would be saved in the default language instead. In a plan, a step in a language that an earlier step
adds to the `CatalogLanguages` of the step's own catalog is `deferred` in the dry run, with a warning naming that step,
and checked by the site when the plan runs, so a seed plan can grow by a language. An earlier step that gives another
existing catalog the language, or makes a new catalog, leaves the step checked as usual; one whose catalog the dry run
can't find (a `catalog:` name the plan makes, say) counts as the step's own. `commerce get --lang sv` shows the language's
published version, else its latest draft.

### Product images: CommerceMediaCollection

A catalog item's images and other assets (the Assets tab in Commerce's catalog UI) are its `CommerceMediaCollection`
property: a list of CMS media. `create` and `set` take it as a JSON array, the whole list (`[]` empties it); each item
is a media ref (an id, a GUID, a plan's `$id` of an upload), or `{"ref": ..., "groupName": ..., "sortOrder": ...}`.
`groupName` defaults to `default`, `sortOrder` to the item's place in the array. A ref that isn't media, and the
same media item twice (Commerce keeps one row per item), are a `usage` error in the dry run too. Setting the same
list again is `unchanged`. `commerce get`, and a change's `before` and `after`, show it as `[{ref, groupName,
sortOrder}]`, in sort order.

```bash
opticli upload shirt.png --parent <media folder> --publish
opticli set code:P-1001 --values '{"CommerceMediaCollection": ["<media ref>"]}' --publish
opticli set code:P-1001-S --values '{"CommerceMediaCollection": [{"ref": "<media ref>", "groupName": "swatch", "sortOrder": 1}]}'
```

## Relations

| Command | Does |
|---|---|
| `commerce relation add <parent> <child> [--quantity <n>] [--sort-order <n>] [--dry-run]` | relates catalog content |
| `commerce relation remove <parent> <child> [--dry-run]` | removes the relation |
| `commerce relations <ref>` | its `children` and `parents`, each `{ref, code, name, kind, quantity, sortOrder}` |

Which relation it is follows from the two (`kind`): a product and a variant (`variant`), a bundle or a package and an
entry (`bundleEntry`, `packageEntry`, with `--quantity`, default 1), a node and an entry (`category`: an extra node,
besides the entry's own parent). Any other pair is a `usage` error. The result is `{parent, child, kind, status,
quantity, sortOrder, dryRun}`: `created`, `updated` (another quantity or sort order), `unchanged`, `removed` or `absent`
(nothing to remove). In a plan: `{"op": "relation", "parent": "$dress", "child": "$dress-s"}` (`quantity`, `sortOrder`,
`"remove": true`).

## Prices

| Command | Does |
|---|---|
| `commerce price set <entry> --market <id> --currency <code> --amount <n> [--price-type <type>] [--price-code <code>] [--min-quantity <n>] [--valid-from <time>] [--valid-until <time>] [--dry-run]` | makes or updates one price |
| `commerce price remove <entry> --market <id> --currency <code> [--price-type] [--price-code] [--min-quantity] [--valid-from] [--dry-run]` | removes one price |
| `commerce prices <entry>` | every price of the entry: `{market, currency, amount, priceType, priceCode, minQuantity, validFrom, validUntil}` |

Commerce keeps prices on variants and packages; a product or bundle is a `usage` error. A price is keyed by market,
currency, price type and code (`all`, the default, has no code; `user` takes a user name, `group` a customer price
group; a type of the site's own takes any code or none, below), minimum quantity (default 0) and `--valid-from`: `set`
makes or updates the row with that key (its amount and `--valid-until`), and the entry's other prices stay as they
are. Without `--valid-from`, the key names the price with
no end, the base price, and a new one starts today (UTC). A campaign price is another row, with `--valid-from` and
`--valid-until` (an end needs a start, and comes after it). Times are UTC unless they have an offset, to the second. The
market must exist (`not_found` lists the markets), and the currency must be one of the market's. These are checked
before a dry run answers, as `usage` errors. The result is `{entry, code, price, status, before, dryRun}`; `before` is
the row an update changed, and `price` is null for a removal that found nothing (`absent`). In a plan: `{"op": "price", "entry": "$dress-s", "market": "DEFAULT",
"currency": "USD", "amount": 79}` (`priceType`, `priceCode`, `minQuantity`, `validFrom`, `validUntil`, `"remove": true`).

### Price types of the site's own

A site can add price types to Commerce's three in its settings, `EPiServer:Commerce:CatalogOptions:SalePriceTypes`
(`[{"Key": "MEMBER", "Value": 3, "Description": "Members"}]`; Commerce writes them to its `PriceType` table at start).
opticli names such a type by its key in lower case: `--price-type member` (any case) writes it, its number
(`--price-type 3`) too, and `commerce prices` shows `"priceType": "member"`. `commerce` lists the site's types as
`priceTypes: [{name, id, description}]`. A key that would read as another name (`all`, a number) is named by its number,
and a row whose type the site no longer defines shows its number as a string (`"7"`); `price remove --price-type 7`
removes such a row. A type the site doesn't have is a `usage` error that lists the site's types.

Rows are told apart by their type: the base price (`all`) never names a `member` row with the same market, currency,
quantity and start. A type of the site's own takes any price code, or none: the code is kept as given (only the ends
are trimmed), so a site that keeps a rule there can write it as JSON (`--price-code '{"tier": "gold"}'`). Commerce keeps
at most 256 characters; a longer code is a `usage` error before a dry run answers. The schema's `priceType` is open:
`all`, `user`, `group`, or the site's own name.

```sh
opticli commerce price set code:P-1001-S --market DEFAULT --currency USD --amount 69 --price-type member
opticli commerce price set code:P-1001-S --market DEFAULT --currency USD --amount 59 --price-type outlet --price-code '{"tier": "clearance"}'
```

## Warehouses and stock

| Command | Does |
|---|---|
| `commerce warehouse set <code> [--name] [--active] [--primary] [--fulfillment-center] [--pickup-location] [--delivery-location] [--sort-order <n>] [--dry-run]` | makes or changes a warehouse |
| `commerce warehouses` | the warehouses: `{code, name, active, primary, fulfillmentCenter, pickupLocation, deliveryLocation, sortOrder}` |
| `commerce inventory set <entry> --warehouse <code> [--in-stock <n>] [--backorder <n>] [--preorder <n>] [--tracked] [--dry-run]` | sets an entry's stock in a warehouse |
| `commerce stock <entry>` | the entry's stock in every warehouse: `{warehouse, inStock, backorder, preorder, tracked}` |

The flags take `true` or `false`. What isn't given keeps its value: a new warehouse is active and nothing else, named
after its code; a new stock record is 0 and tracked. Commerce comes with the warehouse `default`. `--in-stock` is what
can be bought now (Commerce's purchase available quantity). Stock, like prices, is on variants and packages. A
warehouse's result is `{warehouse, status, changes, dryRun}` (`changes`: the fields that changed); stock's is `{entry,
code, stock, status, before, dryRun}`. In a plan: `{"op": "warehouse", "code": "oslo", "name": "Oslo"}`
(`active`, `primary`, `fulfillmentCenter`, `pickupLocation`, `deliveryLocation`, `sortOrder`) and `{"op":
"inventory", "entry": "$dress-s", "warehouse": "default", "inStock": 25}` (`backorder`, `preorder`, `tracked`).

## Markets

| Command | Does |
|---|---|
| `commerce market set <id> [--name] [--enabled] [--currencies <a,b>] [--default-currency] [--languages <a,b>] [--default-language] [--countries <a,b>] [--description] [--prices-include-tax] [--dry-run]` | makes or changes a market |
| `commerce market remove <id> [--dry-run]` | removes a market, and Commerce its prices |
| `commerce markets` | the markets: `{id, name, enabled, defaultCurrency, currencies, defaultLanguage, languages, countries, description, pricesIncludeTax}` |

A market id is 1 to 8 letters, digits, `-` or `_`; Commerce comes with `DEFAULT`. A new market needs `--currencies` and
`--languages`; the lists given replace the market's (compared as sets), and the defaults are the first of each unless
given (one of the list). Currencies must be ones Commerce has (its currency list); languages cultures .NET knows (`en`,
`sv-SE`; `xx-YY` is a `usage` error), and one the CMS hasn't enabled is a warning; countries ISO 3166-1 alpha-3 codes
(`SWE`). A name another market has is a `usage` error (Commerce keeps names unique). What isn't given keeps its
value. The result is `{market, status, changes, dryRun}`. In a plan: `{"op": "market", "market": "NORDIC",
"currencies": ["SEK", "NOK"], "languages": ["en"], "countries": ["SWE", "NOR"]}` (`name`, `enabled`, `defaultCurrency`, `defaultLanguage`,
`description`, `pricesIncludeTax`).

An existing market keeps its lists unless the change gives them, also when they are empty. Only SQL makes such a market
(a `Market` row without `MarketCurrencies` or `MarketLanguages` rows): Commerce reads its lists as they are, empty, and
doesn't take the defaults for them, so the market offers no currency or language in its lists until it is saved.
Commerce's own save (`IMarketService`, the admin UI, opticli) always adds the default currency and language to the
lists. So `market set <id> --name ...` on such a market saves the defaults in its lists too, and `changes` says so
(`["name", "currencies", "languages"]`). A default is checked against its list only when the market has one, or a list
is given.

`market remove` removes a market through `IMarketService.DeleteMarket`. Commerce removes the market's prices with it,
in every catalog, and takes it off the payment and shipping methods that name it (its database's foreign keys cascade);
orders and carts keep naming the market, and a catalog entry's excluded markets keep its id. A dry run says what it
would remove; Commerce can't count the market's prices short of reading every entry's, so the result doesn't say how
many (a warning says that they go). Commerce keeps each entry's prices in its cache, which the database's removal
doesn't touch, so the removal also clears Commerce's price caches, on every server of the site: reads and price writes
after it don't meet the removed prices, and the next reads fill the caches again. `DEFAULT` is Commerce's own, which it falls back to, and is a `usage` error: turn it
off with `market set DEFAULT --enabled false` instead. Nothing to remove is `absent`, with no `market`. The removal
takes only the id (another field is a `usage` error). In a plan: `{"op": "market", "market": "NORDIC", "remove":
true}`.

A catalog belongs to every site unless its `Sites` property names some: `opticli set catalog:Fashion Sites=...`.

## Reads

Catalog content is read through the site (`meta.source: agent`), with `commerce get`, `commerce children` and `commerce
tree`. `get`, `children` and `tree` read the CMS database, where it isn't: given a catalog ref, they say to use these.

| Command | Shows |
|---|---|
| `commerce` | `{version, catalogRoot, catalogRootGuid, catalogs: [{ref, guid, name}], priceTypes: [{name, id, description}]}`: `priceTypes` are Commerce's three (`all` 0, `user` 1, `group` 2) and [the site's own](#price-types-of-the-sites-own) |
| `commerce get <ref> [--lang <code>]` | one catalog item as the site loads it: `ref`, `guid`, `name`, `type`, `kind` (`catalog`, `node`, `product`, `variation`, `bundle`, `package`), `language`, `masterLanguage`, `languages`, `status`, `parent`, `startPublish`, `saved`, `url`, and every property `{type, value}` (the model's and Commerce's: `Code`, `DisplayName`, `SeoUri`, `CommerceMediaCollection`, ...); the published version in that language, else its latest draft |
| `commerce children [<ref>] [--limit <n>]` | `{parent, children, more}`: the catalogs below the root (the default), the nodes and entries below a catalog or node, also the entries a node has as an extra category; each `{ref, guid, name, type, kind, code, parent, masterLanguage, languages, version, catalogLanguages}` (`languages`: the languages it is in, on Commerce 14 every one of its catalog's; `catalogLanguages`: its catalog's). At most `--limit` (200) |
| `commerce tree [<ref>] [--depth <n>]` | depth first, each `{depth, ref, guid, name, type, kind, code}`: the item and `--depth` (3) levels below it |
| `commerce relations`, `commerce prices`, `commerce stock`, `commerce warehouses`, `commerce markets` | as above |

`url` is where the site's catalog routing puts the item (`/en/fashion/women/summer-dress/`); null on a site that
doesn't route catalog content (Commerce's routing is the site's own registration, `CatalogRouteHelper.MapDefaultHierarchialRouter`).
`resolve` takes such a URL back: the CMS database knows nothing of catalog URLs, so when it finds nothing for a URL on a
Commerce site, `resolve` asks the running site, and prints the catalog content with `matchedBy: "catalog"` (and no
`site`, `host` or `languageSource`). Without a running site it stays `not_found`. Other commands don't take a catalog
URL as a ref: give them the ref `resolve` prints.

## Plans

Every write above is also a plan step (`relation`, `price`, `warehouse`, `inventory`, `market`, and `create`, `set`
and `translate` for catalog content), checked by `apply --dry-run` with the rest of the plan. A `$id` of catalog
content is its ref (`1__CatalogContent`), in later steps' refs and in property values: a start page's `"Catalog": "$catalog"`. A step that
needs content the plan makes first is `deferred` (or `simulated`, below a stand-in parent) in a dry run; with
`--update-existing`, catalog content a step's GUID already names counts as existing, so a second run checks every step
fully and reports `unchanged`. Put markets and warehouses before the prices and stock that use them. A `price` step
whose market an earlier `market` step makes, or gives other currencies, is `deferred` in a dry run, with a warning
naming that step: the site checks it in all else (the entry, the amount, the dates, the currency against the
currencies that step gives the market), and checks that the market exists when the plan runs. An `inventory` step
whose warehouse an earlier `warehouse` step makes is checked the same way. So a seed plan can grow by a market and its
prices on a database it seeded before. Any other change to a market or warehouse (a rename, say) leaves the steps after
it checked as usual, and so does a market or warehouse that is already as the plan wants it. A `price` step in a
market that an earlier step removes (`"remove": true`) is a `usage` error in the dry run, naming that step, so the plan
doesn't run half: a plan that removes a market and then prices in it is a mistake. A `market` step between them that
makes the market again, with its currencies and languages (a new market needs them, a `usage` error otherwise), is
`deferred`, and the price is checked against the currencies it gives. Warehouses can't be removed. See
[the guide](../../guides/developers/seed-a-commerce-catalog.md).

```json
{
  "guidNamespace": "0f3c...",
  "operations": [
    {"op": "market", "market": "NORDIC", "currencies": ["SEK"], "languages": ["en"], "countries": ["SWE"]},
    {"op": "create", "id": "catalog", "type": "CatalogContent", "parent": "catalog-root", "name": "Fashion",
     "properties": {"DefaultCurrency": "USD", "DefaultLanguage": "en", "WeightBase": "kgs", "LengthBase": "cm"}, "publish": true},
    {"op": "create", "id": "women", "type": "FashionNode", "parent": "$catalog", "name": "Women", "properties": {"Code": "women"}, "publish": true},
    {"op": "create", "id": "dress", "type": "FashionProduct", "parent": "$women", "name": "Summer dress", "properties": {"Code": "P-1001"}, "publish": true},
    {"op": "create", "id": "dress-s", "type": "FashionVariant", "parent": "$women", "name": "Summer dress S", "properties": {"Code": "P-1001-S"}, "publish": true},
    {"op": "relation", "parent": "$dress", "child": "$dress-s"},
    {"op": "price", "entry": "$dress-s", "market": "DEFAULT", "currency": "USD", "amount": 79},
    {"op": "price", "entry": "$dress-s", "market": "NORDIC", "currency": "SEK", "amount": 799},
    {"op": "inventory", "entry": "$dress-s", "warehouse": "default", "inStock": 25},
    {"op": "create", "type": "StartPage", "parent": "1", "name": "Home", "properties": {"Catalog": "$catalog", "FeaturedProduct": "$dress"}, "publish": true}
  ]
}
```

## Errors

A Commerce command, or a catalog ref in `create`, `set` or a plan, on a site without the Commerce agent is `not_found`
(the site has no Commerce, or the agent didn't load: `serve --status` shows why), and so is a catalog ref that names
nothing. With no site running at all, it is `unreachable`, as for every write. A value Commerce doesn't take (a currency,
a country, a price on a product, a relation between a variant and a product) is `usage`; a market or warehouse that
doesn't exist is `not_found`. Writes against a shared database pass the drift check as every write does.
