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
property values (a page's `ContentReference` to a product):

| Ref | Names |
|---|---|
| `code:<code>` | a node or entry by its code (`code:P-1001`), the same in every database |
| `catalog:<name>` | a catalog by its name (catalogs have no code) |
| `catalog-root` | the catalog root, which catalogs are made below |
| `<id>__CatalogContent` | as opticli prints catalog content: `1__CatalogContent` (an entry), `1073741825__CatalogContent` (a node), `-2147483646__CatalogContent` (a catalog; Commerce encodes the kind in the id, so catalogs and the root have negative ids). The id is the database's own |
| a GUID | the content's GUID, which a plan can fix (`guid`, `guidNamespace`) |

Catalog content is found by the site's Commerce agent, not in the CMS database: Commerce doesn't register it in
`tblMappedIdentity`, where other content providers' content is. A GUID is looked up there first, then in Commerce. A
CMS property that references catalog content shows its ref and `"provider": "CatalogContent"` in `get`.

## Catalog content: create and set

Catalogs, nodes and entries are content, and their types come from the content model (`opticli types`, `opticli type
FashionProduct`): `create` and `set` write them as they write pages, through the site agent. The properties are the
type's, Commerce's own among them: `Code` on nodes and entries (Commerce makes one when it is left out), `DisplayName`,
`SeoUri`; on a catalog `DefaultCurrency`, `DefaultLanguage` and `WeightBase` (required), `LengthBase`,
`CatalogLanguages` and `Sites` (the sites a catalog belongs to; none: every site).

| Command | Does |
|---|---|
| `create catalog-root --type CatalogContent --name <name> ...` | a catalog |
| `create <catalog-ref> --type <type> --name <name> [Prop=value]... [--lang] [--publish] [--guid]` | a node, product, variant, bundle or package below a catalog or node (its parent is its primary node) |
| `set <catalog-ref> [Prop=value]... [--name] [--lang] [--publish] [--base-version] [--force]` | a new version with the changes; `set <ref> --publish` alone publishes a draft |

Catalog content is a draft until it is published (`--publish`, or `apply --publish`). A plan's `create` step with a
`guid` (or a `guidNamespace`) and `apply --update-existing` updates the content when it exists, and reports
`unchanged` when nothing differs. What catalog content doesn't take is a `usage` error before anything is saved:
`--for` and `--blueprint`, projects (`--in-project`; a plan's own `project` leaves catalog steps out, as it leaves out
folders), `--request-approval`, `--publish-at`, `--from`, `--variation`, ContentArea and composition edits. `publish`,
`unpublish`, `discard`, `move`, `delete`, `translate` and `access` refuse catalog content: opticli never deletes it
(Commerce has no recycle bin for it); use Commerce's catalog UI. Results print catalog content's ref and version as
Commerce has them (`1__CatalogContent`, `1_4_CatalogContent`); `get` and `versions` don't read them.

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
| `commerce price set <entry> --market <id> --currency <code> --amount <n> [--price-type all\|user\|group] [--price-code <code>] [--min-quantity <n>] [--valid-from <time>] [--valid-until <time>] [--dry-run]` | makes or updates one price |
| `commerce price remove <entry> --market <id> --currency <code> [--price-type] [--price-code] [--min-quantity] [--valid-from] [--dry-run]` | removes one price |
| `commerce prices <entry>` | every price of the entry: `{market, currency, amount, priceType, priceCode, minQuantity, validFrom, validUntil}` |

Commerce keeps prices on variants and packages; a product or bundle is a `usage` error. A price is keyed by market,
currency, price type and code (`all`, the default, has no code; `user` takes a user name, `group` a customer price
group), minimum quantity (default 0) and `--valid-from`: `set` makes or updates the row with that key (its amount and
`--valid-until`), and the entry's other prices stay as they are. Without `--valid-from`, the key names the price with
no end, the base price, and a new one starts today (UTC). A campaign price is another row, with `--valid-from` and
`--valid-until` (an end needs a start). Times are UTC unless they have an offset, to the second. The market must exist
(`not_found` lists the markets); a currency is an ISO 4217 code. The result is `{entry, code, price, status, before,
dryRun}`; `before` is the row an update changed. In a plan: `{"op": "price", "entry": "$dress-s", "market": "DEFAULT",
"currency": "USD", "amount": 79}` (`priceType`, `priceCode`, `minQuantity`, `validFrom`, `validUntil`, `"remove": true`).

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
| `commerce markets` | the markets: `{id, name, enabled, defaultCurrency, currencies, defaultLanguage, languages, countries, description, pricesIncludeTax}` |

A market id is 1 to 8 letters, digits, `-` or `_`; Commerce comes with `DEFAULT`. A new market needs `--currencies` and
`--languages`; the lists given replace the market's (compared as sets), and the defaults are the first of each unless
given (one of the list). Countries are ISO 3166-1 alpha-3 codes (`SWE`). What isn't given keeps its value. The result
is `{market, status, changes, dryRun}`. In a plan: `{"op": "market", "market": "NORDIC", "currencies": ["SEK", "NOK"],
"languages": ["en"], "countries": ["SWE", "NOR"]}` (`name`, `enabled`, `defaultCurrency`, `defaultLanguage`,
`description`, `pricesIncludeTax`).

A catalog belongs to every site unless its `Sites` property names some: `opticli set catalog:Fashion Sites=...`.

## Reads

Catalog content is read through the site (`meta.source: agent`), with `commerce get`, `commerce children` and `commerce
tree`. `get`, `children` and `tree` read the CMS database, where it isn't: given a catalog ref, they say to use these.

| Command | Shows |
|---|---|
| `commerce` | `{version, catalogRoot, catalogRootGuid, catalogs: [{ref, guid, name}]}` |
| `commerce get <ref> [--lang <code>]` | one catalog item as the site loads it: `ref`, `guid`, `name`, `type`, `kind`, `language`, `masterLanguage`, `languages`, `status`, `parent`, `startPublish`, `saved`, and every property `{type, value}` (the model's and Commerce's: `Code`, `DisplayName`, `SeoUri`, ...); the published version, else the latest |
| `commerce children [<ref>] [--limit <n>]` | `{parent, children, more}`: the catalogs below the root (the default), the nodes and entries below a catalog or node, also the entries a node has as an extra category; each `{ref, guid, name, type, kind, code, parent, masterLanguage, languages, version}`. At most `--limit` (200) |
| `commerce tree [<ref>] [--depth <n>]` | depth first, each `{depth, ref, guid, name, type, kind, code}`: the item and `--depth` (3) levels below it |
| `commerce relations`, `commerce prices`, `commerce stock`, `commerce warehouses`, `commerce markets` | as above |

## Plans

Every write above is also a plan step (`relation`, `price`, `warehouse`, `inventory`, `market`, and `create` and `set`
for catalog content), checked by `apply --dry-run` with the rest of the plan. A `$id` of catalog content is its ref
(`1__CatalogContent`), in later steps' refs and in property values: a start page's `"Catalog": "$catalog"`. A step that
needs content the plan makes first is `deferred` (or `simulated`, below a stand-in parent) in a dry run; with
`--update-existing`, catalog content a step's GUID already names counts as existing, so a second run checks every step
fully and reports `unchanged`. Put markets and warehouses before the prices and stock that use them. See
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

A Commerce command on a site without the Commerce agent is `not_found` (the site has no Commerce, or the agent didn't
load: `serve --status` shows why), and so is a catalog ref that names nothing. A value Commerce doesn't take (a currency,
a country, a price on a product, a relation between a variant and a product) is `usage`; a market or warehouse that
doesn't exist is `not_found`. Writes against a shared database pass the drift check as every write does.
