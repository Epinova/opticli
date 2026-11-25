---
title: Seed a Commerce catalog for Playwright tests
summary: Build an Optimizely Commerce site's catalog, prices, stock and markets from a plan, so end-to-end tests start from the same products on every machine and in CI.
audience: developers
order: 8
written-for: 0.37.0
---

End-to-end tests of a Commerce site need products to show, prices to check and stock to buy. Made by hand in the
catalog UI, they differ between machines and drift as editors work. With opticli the catalog comes from code: one
plan makes the catalog, its nodes, products, variants and bundles, relates them, gives the variants prices and stock,
and sets up the markets, all through the running site. Run again, the plan changes nothing. By the end of this guide
you have a seed plan for the catalog, a database that starts empty, and Playwright tests that read what the plan made.

## Before you start

- opticli 0.36.0 or newer, and the `@epinova/opticli` npm package set up as in
  [Seed CMS content for Playwright tests](playwright-seed-content.md).
- A site on Optimizely Commerce 14 (CMS 12) or 15 (CMS 13), built, with its catalog model in code (the examples use a
  `FashionNode`, `FashionProduct`, `FashionVariant` and `FashionBundle`, a `StartPage` with a `Catalog` reference, and
  an image media type for `.png` files).
- SQL Server for the site's two databases: the CMS's (`EPiServerDB`) and Commerce's (`EcfSqlConnection`).

## Steps

### 1. Start from empty databases

A test database is easiest to trust when it starts empty. Point both connection strings in the site's
`appsettings.Development.json` (or a launch profile) at empty databases, and start the site once:

```bash
opticli serve
opticli serve --status
```

The CMS and Commerce make their schemas at the first start. `serve --status` shows `commerce` with the site's Commerce
version and `agent: true`: opticli's Commerce agent runs in the site, which every Commerce command needs. If it says
`agent: false`, its `error` says why. `opticli commerce` shows the catalog root and that there are no catalogs yet; a
fresh database has the market `DEFAULT` and the warehouse `default`.

### 2. Look at the catalog model

The catalog types are content types like any other, so opticli reads them from the database the site's model sync
filled:

```bash
opticli types
opticli type FashionProduct
```

`type` lists the properties a plan can set, Commerce's own among them: `Code`, `DisplayName` and `SeoUri` on nodes and
entries, `DefaultCurrency`, `DefaultLanguage`, `WeightBase` and `LengthBase` on a catalog. The code is what tests use
to find a product (`code:P-1001`), and it is the same in every database.

### 3. Write the seed plan

The plan makes the catalog first, then what goes in it, then the prices and stock. Markets and warehouses come before
the prices and stock that use them. The product's image is uploaded first, to the global assets, and attached to the
product. A fixed `guidNamespace` gives everything the same GUID on every run.

```ts
// e2e/seed.ts
import type { Plan } from '@epinova/opticli';

export const seedPlan = {
  guidNamespace: '5b2e9c41-7d0a-4f3e-8c61-2a9d4e7b1f05', // any GUID; keep it
  operations: [
    { op: 'market', market: 'NORDIC', name: 'Nordic', currencies: ['SEK', 'NOK'], languages: ['en'], countries: ['SWE', 'NOR'] },
    { op: 'warehouse', code: 'oslo', name: 'Oslo', pickupLocation: true },
    // The global assets folder has this GUID in every CMS database. Files are relative to planDir.
    { op: 'upload', id: 'dressImage', file: 'files/dress.png', parent: 'e56f85d0-e833-4e02-976a-2d11fe4d598c', publish: true },
    { op: 'create', id: 'catalog', type: 'CatalogContent', parent: 'catalog-root', name: 'Fashion',
      properties: { DefaultCurrency: 'USD', DefaultLanguage: 'en', WeightBase: 'kgs', LengthBase: 'cm' } },
    { op: 'create', id: 'women', type: 'FashionNode', parent: '$catalog', name: 'Women', properties: { Code: 'women' } },
    { op: 'create', id: 'dress', type: 'FashionProduct', parent: '$women', name: 'Summer dress',
      properties: { Code: 'P-1001', Brand: 'Acme', Description: '<p>Light and airy.</p>', CommerceMediaCollection: ['$dressImage'] } },
    { op: 'create', id: 'dressS', type: 'FashionVariant', parent: '$women', name: 'Summer dress S', properties: { Code: 'P-1001-S', Size: 'S' } },
    { op: 'create', id: 'dressM', type: 'FashionVariant', parent: '$women', name: 'Summer dress M', properties: { Code: 'P-1001-M', Size: 'M' } },
    { op: 'create', id: 'set', type: 'FashionBundle', parent: '$women', name: 'Dress set', properties: { Code: 'B-2001' } },
    { op: 'relation', parent: '$dress', child: '$dressS' },
    { op: 'relation', parent: '$dress', child: '$dressM', sortOrder: 1 },
    { op: 'relation', parent: '$set', child: '$dressS', quantity: 2 },
    { op: 'price', entry: '$dressS', market: 'DEFAULT', currency: 'USD', amount: 79 },
    { op: 'price', entry: '$dressS', market: 'DEFAULT', currency: 'USD', amount: 59, validFrom: '2026-12-01T00:00:00Z', validUntil: '2027-01-01T00:00:00Z' },
    { op: 'price', entry: '$dressS', market: 'NORDIC', currency: 'SEK', amount: 799 },
    { op: 'price', entry: '$dressM', market: 'DEFAULT', currency: 'USD', amount: 79 },
    { op: 'inventory', entry: '$dressS', warehouse: 'default', inStock: 25 },
    { op: 'inventory', entry: '$dressM', warehouse: 'oslo', inStock: 0, backorder: 5 },
    { op: 'create', id: 'start', type: 'StartPage', parent: '1', name: 'Home',
      properties: { Catalog: '$catalog', FeaturedProduct: '$dress' } },
  ],
} as const satisfies Plan;
```

`catalog-root` is where catalogs go. `$dress` in a later step is the product the `dress` step made: a relation, a
price and the start page's `FeaturedProduct` all take it. A product's variants are relations, not children: variants
sit in the node like the product, and the `relation` steps tie them to it. Prices and stock go on variants (and
packages), not on the product. The price without `validFrom` is the base price; the one with `validFrom` and
`validUntil` is a December campaign beside it. A site with price types of its own (members' prices, say, in its
`EPiServer:Commerce:CatalogOptions:SalePriceTypes` setting) prices them the same way, by the type's key in lower case:
`{ op: 'price', entry: '$dressS', market: 'DEFAULT', currency: 'USD', amount: 69, priceType: 'member' }`.
`opticli commerce` lists the site's types as `priceTypes`.

`CommerceMediaCollection` is the product's Assets tab in the catalog UI: the images (and other media) the site shows
with it. It takes the whole list, each a media ref, or `{ ref: '$dressImage', groupName: 'swatch', sortOrder: 1 }`
for another group or order. Images go in a media folder, not "for" the product: catalog content has no "For this page"
folder, and opticli refuses `for: '$dress'` on an upload.

A seed plan grows with the site. To sell in euro too, add a market and its prices to the plan:

```ts
    { op: 'market', market: 'EURO', name: 'Euro', currencies: ['EUR'], languages: ['en'] },
    { op: 'price', entry: '$dressS', market: 'EURO', currency: 'EUR', amount: 69 },
```

On a database the plan seeded before, the dry run lists that price as `deferred`: the market isn't there yet. The dry
run still checks the price against the market the step before makes (its currencies, the variant, the dates), and the
site checks the market itself when the plan runs. The same goes for stock in a new warehouse, and for a price in a
currency the plan adds to a market. A market the suite no longer needs goes with `opticli commerce market remove EURO`
(or a step with `remove: true`), and Commerce removes its prices with it.

### 4. Apply it twice

With the npm package, `defineSetup({ plan: seedPlan })` applies the plan before the tests, as for CMS content. To
try it from a terminal first, put the same `guidNamespace` and `operations` in a JSON file and run it with
`--update-existing`, which makes every step safe to run again, and `--publish`, which publishes the catalog content
(`defineSetup()` passes both):

```bash
opticli apply e2e/seed.json --update-existing --publish --dry-run
opticli apply e2e/seed.json --update-existing --publish
opticli apply e2e/seed.json --update-existing --publish
```

The first run lists every step as `saved`. The second lists them all as `unchanged`: the catalog content is as the
plan says, and so are the relations, prices, stock, warehouse and market. On a database the first run seeded, the dry
run checks every step against what is there.

### 5. Look at what it made

```bash
opticli commerce tree catalog:Fashion
opticli commerce get code:P-1001
opticli resolve /en/fashion/women/summer-dress/
opticli commerce relations code:P-1001
opticli commerce prices code:P-1001-S
opticli commerce stock code:P-1001-M
```

Catalog content isn't in the CMS database, so `get`, `children` and `tree` don't read it: the `commerce` reads ask the
site. `commerce get` shows the product's `CommerceMediaCollection` and its `url`, where your site's catalog routing
puts it (null if the site doesn't route catalog content); `resolve` takes that URL back to the product. The start
page's `Catalog` and `FeaturedProduct` show the catalog's and product's refs in `opticli get` on the page: a negative
`<id>__CatalogContent` for the catalog and `<id>__CatalogContent` for the product. The ids are each database's own, so
don't copy them into a plan or test: even on a fresh database the first catalog is `-2147483647__CatalogContent` or
`-2147483646__CatalogContent`, depending on whether Commerce had already made its own `System.Archived` catalog.
opticli takes them back wherever it takes a ref; plans and tests use `$id`s, `code:` or `catalog:Fashion`.

### 6. Read the catalog in a test

`seed.refs` has each step's ref, catalog content's too, and the `opticli` fixture has a method for every `commerce`
command:

```ts
// e2e/catalog.spec.ts
import { expect, test } from './fixtures.ts';

test('the small dress has a December campaign price', async ({ opticli, seed }) => {
  const { prices } = await opticli.commercePrices(seed.refs.dressS!);
  const usd = prices.filter((price) => price.market === 'DEFAULT');
  expect(usd.map((price) => price.amount).sort()).toEqual([59, 79]);
});

test('the medium dress is out of stock but can be backordered', async ({ opticli }) => {
  const { stock } = await opticli.commerceStock('code:P-1001-M');
  expect(stock).toEqual([expect.objectContaining({ warehouse: 'oslo', inStock: 0, backorder: 5 })]);
});
```

To test a change, make it in the test and set it back afterwards, or let the next run's seed put it back: for example
`opticli.commercePriceSet('code:P-1001-S', { market: 'DEFAULT', currency: 'USD', amount: 69 })`. A product page the
site renders is reached by its URL, as your site's routing gives it: `(await opticli.commerceGet(seed.refs.dress!)).url`.

## Check it worked

- `opticli serve --status` shows `commerce.agent: true`.
- A second `opticli apply ... --update-existing --publish` lists every step as `unchanged`.
- `opticli commerce prices code:P-1001-S` lists the base price, the campaign price and the Nordic price.

## Troubleshooting

- **"'code:P-1001' is a catalog ref, which needs the site's Commerce agent"**: the site isn't running, has no Commerce,
  or the agent didn't load: see `commerce` in `opticli serve --status`, and restart the site after a build.
- **"has 2 DEFAULT USD prices ... without an end"**: two base prices with the same key exist (made in the UI, say).
  Give the step a `validFrom` to name one, or remove the other with `opticli commerce price remove ... --valid-from`.
- **A product is refused a price**: Commerce keeps prices and stock on variants and packages. Price the variants.
- **"'...' is not a price type of this site"**: a type of the site's own is named by its key in lower case (`MEMBER`
  is `member`), or by its number. `opticli commerce` lists the site's `priceTypes`.
- **An upload `for` a product is refused ("... is catalog content")**: catalog content has no "For this page" folder.
  Upload the image to a media folder and attach it with `CommerceMediaCollection`, as the dress's image above.
- **`publish` or `delete` refuses catalog content**: opticli only creates and sets it (`set <ref> --publish` publishes
  a draft). To start over, start from empty databases again.

Every command and option is in the
[Optimizely Commerce reference](https://github.com/Epinova/opticli/blob/main/docs/reference/cli/commerce.md#optimizely-commerce).
