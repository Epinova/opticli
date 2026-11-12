---
title: Build a test database from scratch
summary: Start from an empty database, and let one seed plan add the languages, the content and the sites, so end-to-end tests run on a database that code builds the same way every time.
audience: developers
order: 5
written-for: 0.32.0
---

A copy of a shared database drifts, holds data you may not want in tests, and is slow to restore. A database built
from code doesn't: the CMS creates its schema in an empty database, and an opticli plan adds what the tests need. The
CMS leaves out two things a site can't render without, sites and the languages beyond `en` and `sv`, and opticli adds
those as plan steps too. By the end of this guide you have a launch profile for the test database, a seed plan that
builds it, and a site that answers on fixed ports.

## Before you start

- opticli 0.31.0 or newer, and a SQL Server you can create databases on (a container is fine).
- The site's repository, built (`opticli serve --build` builds it too).
- To run the plan from Playwright, the npm package: see [Seed CMS content for Playwright tests](playwright-seed-content.md).

## Steps

### 1. Create an empty database and a launch profile for it

```bash
sqlcmd -S localhost -U sa -Q "CREATE DATABASE [alloy-e2e]"
```

Give the test database a launch profile of its own in `Properties/launchSettings.json`, with the connection string and
whatever else the site needs in tests:

```json
{
  "profiles": {
    "E2E": {
      "commandName": "Project",
      "environmentVariables": {
        "ASPNETCORE_ENVIRONMENT": "Development",
        "ConnectionStrings__EPiServerDB": "Server=localhost;Database=alloy-e2e;User Id=sa;Password=...;TrustServerCertificate=True",
        "Search__IndexName": "alloy-e2e"
      }
    }
  }
}
```

Mind the password in a committed file. `--profile E2E` makes opticli use that database, and `serve --profile E2E`
gives the site the profile's other variables too, as `dotnet run --launch-profile E2E` would. Variables `serve` sets
itself are left out, such as the address and the environment, and its output names what it applied.

### 2. Start the site on fixed ports

```bash
opticli serve --profile E2E --port 5199 --https-port 5443
```

On the first start the CMS creates its schema in the empty database (`CreateDatabaseSchema` is on by default). Site
hosts carry a port, so the site must listen on the same ports every run: `--https-port` fixes the HTTPS port as
`--port` fixes the HTTP one, and a port that is taken is an error rather than another port. To set them once, put
`port` and `httpsPort` in opticli's user config ([Files](https://github.com/Epinova/opticli#files)).

`opticli sites` now prints `[]`, and `opticli languages` lists `en` and `sv`.

### 3. Write the seed plan

```json
{
  "guidNamespace": "3f6d2a9e-6c1b-4f7e-9a51-0d2b8c4e7f10",
  "operations": [
    {"op": "language", "lang": "no"},
    {"op": "create", "id": "home", "parent": "1", "type": "StartPage", "name": "Hjem", "lang": "no", "publish": true},
    {"op": "translate", "ref": "$home", "lang": "en", "name": "Home", "publish": true},
    {"op": "create", "id": "tag", "parent": "$home", "type": "StandardPage", "name": "Fokus", "lang": "no", "publish": true},
    {"op": "create", "id": "article", "parent": "$home", "type": "ArticlePage", "name": "Artikkel", "lang": "no", "publish": true,
     "properties": {"TagId": "$tag"}},
    {"op": "site", "name": "Alloy", "startPage": "$home",
     "hosts": ["localhost:5199", "alloy-en.localhost:5199@en", "https://localhost:5443"]}
  ]
}
```

- The `language` step comes before the content in that language.
- The `site` step comes after its start page. In each language the first host is the primary one: here
  `localhost:5199` for every language and `alloy-en.localhost:5199` for English. Browsers send `*.localhost` to your
  machine, so a site per host name needs no hosts file. Add a step per site for a multi-site solution.
- The site gets an assets folder of its own, as a site made in admin mode does: a `SysSiteAssets` folder below the
  start page, at `/siteassets/`. Upload a step's media there with its URL as the parent
  (`"parent": "http://localhost:5199/siteassets/"`). `"siteAssets": "global"` keeps the site on the global assets
  folder instead.
- `"$tag"` in a string property stores the tag page's id as text (`"456"`): `TagId` stands for a property of your own
  that keeps a content id as a string. In a reference property `"$tag"` is the content itself.
- `guidNamespace` gives everything the plan creates the same GUIDs in every database, so the plan can run again.

### 4. Dry-run it, then apply it

```bash
opticli apply seed.plan.json --update-existing --profile E2E --dry-run
opticli apply seed.plan.json --update-existing --profile E2E
```

In the dry run the `language` step is `valid`, and the steps in Norwegian are `deferred`, since the database doesn't
have the language enabled until the plan runs. The `site` step is `deferred` too: its start page doesn't exist yet.
So is a step whose parent is a URL on one of the site's hosts, such as an upload to `/siteassets/`. The real run saves every step. Run it again and every step is `unchanged`: with `--update-existing` the `site` step makes
the site of that name match instead of failing.

### 5. Check the site

```bash
opticli sites --profile E2E
curl -k https://localhost:5443/
```

The site has its hosts and start page, and its URLs render. From Playwright, `defineSetup()` takes the same plan, the
profile (`opticli: { profile: 'E2E' }`) and `httpsPort: 5443`.

### 6. Add a second site

A second `site` step with a start page of its own and a `*.localhost` host on the same port gives a second site that
the same `serve` answers:

```json
{"op": "create", "id": "svHome", "parent": "1", "type": "StartPage", "name": "Hem", "lang": "sv", "publish": true},
{"op": "site", "name": "Alloy Sverige", "startPage": "$svHome", "hosts": ["alloy-sv.localhost:5199", "https://alloy-sv.localhost:5443"]}
```

The plan's results give content on that site its address there (`canonicalUrl`), and in Playwright `seed.urls` and
`seed.sites` open it on `http://alloy-sv.localhost:5199/`, with no hosts file: browsers send `*.localhost` to your
machine.

## If something goes wrong

| What you see | What to do |
|---|---|
| `'no' is not an enabled language` on a `site` step | Put the `language` step before it. Outside a plan: `opticli languages enable no`. |
| `There is already a site named Alloy` (`conflict`) | Run with `--update-existing`, which updates that site. |
| `... has this host already` (`validation`) | Another site has the host. Every site needs host names of its own; `opticli sites` lists them. |
| `HTTPS port 5443 is already in use` | Another site or a stopped site's leftover holds the port. `opticli serve --status` in that project, or pick another port everywhere it appears. |
| `Launch profile 'E2E' does not set ConnectionStrings:EPiServerDB` | `--profile` picks the database from the profile, so the profile needs the connection string. |
| A page answers 404 on a host | The host's port isn't the one the site listens on. Use fixed ports (step 2). |
| Requests to `/` redirect to a registration page | The Alloy template asks for a first administrator when there are no users: `opticli users add` makes one, on a site with ASP.NET Identity. |

## Related

- [A database built from scratch](https://github.com/Epinova/opticli#a-database-built-from-scratch) in the README
- [serve and env](https://github.com/Epinova/opticli#serve-and-env)
- [Plan format](https://github.com/Epinova/opticli/blob/main/skill/reference.md#plans-apply)
- [Seed CMS content for Playwright tests](playwright-seed-content.md)
