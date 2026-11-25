// What opticli takes as input: property values and plans (skill/reference.md "Plans"). Hand-written, as opticli parses
// them from JSON with no C# types to generate them from; the result types are generated (generated/types.ts). Readonly,
// so plans and values written `as const` fit.

/** A content reference as opticli takes it: an id ("123"), a version ("123_456"), a GUID or a URL ("/en/about/"). */
export type ContentRef = string;

/**
 * A property value as `--values` takes it: a string (also HTML for rich text, an id for a reference), number, boolean
 * or null (clears it), or a structured value (a ContentArea, a link, a local block, a list).
 */
export type PropertyValue =
  | string
  | number
  | boolean
  | null
  | readonly ContentAreaItem[]
  | LinkValue
  | readonly PropertyValue[]
  | { readonly [property: string]: PropertyValue };

/**
 * A ContentArea item: existing content by ref or GUID, or a new block (`type`): an inline block (CMS 12.20+), or, where the
 * site's edit UI makes those (`serve({ status: true })`: `inlineBlocks: 'shared'`), a shared block in the content's "For
 * this page" folder, which the result's `sharedBlocks` lists with its ref.
 */
export type ContentAreaItem =
  | { readonly ref: ContentRef; readonly displayOption?: string; readonly group?: string; readonly visitorGroups?: readonly string[] }
  | { readonly guid: string; readonly displayOption?: string }
  | { readonly type: string; readonly name?: string; readonly properties: Properties; readonly displayOption?: string };

export interface LinkValue {
  readonly href: ContentRef;
  readonly text?: string;
  readonly title?: string;
  readonly target?: string;
}

export type Properties = { readonly [property: string]: PropertyValue };

// Plans (`apply`): skill/reference.md "Plans". "$id" refers to what an earlier create, block or upload made.

// Each op takes exactly the fields `opticli apply --help` lists for it (WritePlan.Fields): opticli refuses any other as
// an unknown field.

/** A step that makes content (create, block, upload): an `id` for "$id" in later steps, and its GUID. */
interface Creating {
  /** Names what this step makes, for "$id" in later steps (needed with a plan's `guidNamespace`). */
  readonly id?: string;
  /** The new content's GUID (else one derived from the plan's `guidNamespace` and `id`, or a new one). */
  readonly guid?: string;
}

/** A step that saves a version, which can go into a project (opticli 0.33 and newer). */
interface Saving {
  readonly publish?: boolean;
  /**
   * Where an approval sequence applies, send the change for review instead of publishing (as `requestApproval` on a
   * single write).
   */
  readonly requestApproval?: boolean;
  /**
   * Publishing would put someone else's unpublished draft live too: do it anyway (only when the user said so). Where the
   * site makes new area blocks shared, it also lets an item of a whole area take over a block of the content's "For this
   * page" folder whose latest version is a draft someone else saved, saving over it (opticli 0.35 and newer). It never
   * publishes someone else's drafts of other blocks along: a `publish` step's `withBlocks` names those.
   */
  readonly includeDraft?: boolean;
  /**
   * The project (its id or exact name) the saved draft goes into, instead of the plan's `project`; `null` for none. Not
   * with `publish`: `projectsPublish` puts the project's ready items live together.
   */
  readonly project?: string | number | null;
  /** Also mark the saved version Ready to publish in its project. */
  readonly ready?: boolean;
}

/** A save that can be scheduled. */
interface Scheduling {
  /** Publish at this time instead of now, e.g. `"2025-03-01T08:00:00Z"` (UTC unless it has an offset). */
  readonly publishAt?: string;
}

/** A change that can be based on another version than the latest (set, area, composition). */
interface Basing {
  /** `"published"`, or a version (`456` or `"123_456"`): base the change on it, leaving newer drafts out. */
  readonly from?: 'published' | number | (string & {});
  /** The version (its number, `456`) expected to be the latest: the save is refused when another one is. */
  readonly baseVersion?: number;
  /** Skip that check. */
  readonly force?: boolean;
}

export type PlanStep =
  | (Creating &
      Saving &
      Scheduling & {
        readonly op: 'create';
        readonly parent?: ContentRef;
        /** Instead of `parent`: a folder in this content's "For this page" folder. */
        readonly for?: ContentRef;
        readonly name: string;
        readonly properties?: Properties;
        readonly lang?: string;
        // `type`, or (CMS 13) a `blueprint`, whose type the new content then is.
      } & ({ readonly type: string; readonly blueprint?: ContentRef } | { readonly blueprint: ContentRef; readonly type?: string }))
  | (Creating &
      Saving & {
        readonly op: 'block';
        readonly type: string;
        readonly name: string;
        readonly for?: ContentRef;
        readonly parent?: ContentRef;
        readonly properties?: Properties;
        readonly lang?: string;
      })
  | (Creating &
      Saving & {
        readonly op: 'upload';
        readonly file: string;
        readonly for?: ContentRef;
        readonly parent?: ContentRef;
        /** Replace this media's file instead of uploading new media (no `for`, `parent`, `type`, `id` or `guid`). */
        readonly replace?: ContentRef;
        readonly name?: string;
        readonly type?: string;
        readonly properties?: Properties;
      })
  | (Saving &
      Scheduling &
      Basing & {
        readonly op: 'set';
        readonly ref: ContentRef;
        readonly properties?: Properties;
        readonly name?: string;
        readonly lang?: string;
        /** CMS 13: the content variation to change. */
        readonly variation?: string;
      })
  | (Saving &
      Scheduling &
      Basing & {
        readonly op: 'area';
        readonly ref: ContentRef;
        readonly property: string;
        /** add (`item`, or a new block's `type`, `values`, `name`, `id`), remove (`index` or `item`), move (`index` or `item`, `to`), set (an inline block's `index` and `values` or `name`). */
        readonly action: 'add' | 'remove' | 'move' | 'set';
        readonly item?: ContentRef;
        readonly type?: string;
        readonly values?: Properties;
        readonly name?: string;
        readonly index?: number;
        readonly at?: number;
        readonly to?: number;
        /** The display option of an added item. */
        readonly display?: string;
        readonly lang?: string;
        /**
         * An add with `type`: later steps reach the block it makes as `"$id"`, where the site makes new area blocks shared
         * blocks in the content's "For this page" folder (an inline block has no ref; change it by its position).
         */
        readonly id?: string;
      })
  | (Saving &
      Scheduling &
      Basing & {
        /** CMS 13: a change to a Visual Builder composition. */
        readonly op: 'composition';
        readonly ref: ContentRef;
        /** add (`nodeType`, `in`, `at`, `value`), remove (`node`), move (`node`, `in`, `at`) or set (`node`, `value`). */
        readonly action: 'add' | 'remove' | 'move' | 'set';
        /** A node: its key, or its name if unique. */
        readonly node?: string;
        readonly nodeType?: 'section' | 'row' | 'column' | 'element';
        readonly in?: string;
        readonly at?: number;
        /** The node as `get`'s composition shows one (add), or its `name`, `displayTemplate`, `displaySettings`, `properties` (set). */
        readonly value?: { readonly [field: string]: PropertyValue };
        readonly lang?: string;
        readonly variation?: string;
      })
  | (Saving & {
      readonly op: 'translate';
      readonly ref: ContentRef;
      readonly lang: string;
      readonly name?: string;
      readonly properties?: Properties;
      /** Also give every block in its "For this page" folder the branch. */
      readonly withBlocks?: boolean;
      /** Delete the branch with all its versions instead (can't be undone; needs `confirm`). */
      readonly remove?: boolean;
      readonly confirm?: boolean;
    })
  | (Scheduling & {
      readonly op: 'publish';
      readonly ref: ContentRef;
      /** The version's number (`456` of `123_456`); the latest by default. */
      readonly version?: number;
      readonly lang?: string;
      readonly includeDraft?: boolean;
      readonly requestApproval?: boolean;
      /**
       * Where the site makes new area blocks shared: blocks of the content's own "For this page" folder that it shows and
       * that are live already, or whose draft someone else saved, whose drafts go live with it (`--with-block`; refs or
       * `"$id"`): naming a block is the only way another person's draft of it goes along (`includeDraft` doesn't). By
       * default only blocks never published go along (with your own drafts), and the block drafts earlier steps of the run
       * saved on that content. Of content published already, only these and the run's block drafts go along,
       * never-published blocks named here too.
       */
      readonly withBlocks?: readonly ContentRef[];
      /** Blocks of that folder never published, kept as drafts (`--without-block`). */
      readonly withoutBlocks?: readonly ContentRef[];
    })
  | { readonly op: 'unpublish'; readonly ref: ContentRef; readonly lang?: string }
  | {
      readonly op: 'discard';
      readonly ref: ContentRef;
      /** The version's number (`456` of `123_456`); else the draft. */
      readonly version?: number;
      readonly lang?: string;
      readonly includeDraft?: boolean;
    }
  | { readonly op: 'move'; readonly ref: ContentRef; readonly to: ContentRef }
  | { readonly op: 'delete'; readonly ref: ContentRef; readonly ignoreReferences?: boolean }
  | { readonly op: 'restore'; readonly ref: ContentRef; readonly to?: ContentRef }
  | {
      /** Access rights, as `opticli access`. */
      readonly op: 'access';
      readonly ref: ContentRef;
      /** Role or group → levels: `{ "Authenticated": "Read" }`, or the levels as an array (`["Read", "Edit"]`). */
      readonly grant?: { readonly [role: string]: string | readonly string[] };
      /** User → levels, as `grant`. */
      readonly grantUsers?: { readonly [user: string]: string | readonly string[] };
      /** Roles, groups or users whose entries are removed. */
      readonly revoke?: string | readonly string[];
      readonly breakInheritance?: boolean;
      readonly inherit?: boolean;
      readonly allowUnknownRole?: boolean;
    }
  | {
      /** A site definition (CMS 13: an application); with `--update-existing` (as setup applies) one of that name is made to match. */
      readonly op: 'site';
      readonly name: string;
      readonly startPage: ContentRef;
      /** `name[:port][@lang]`, optionally with a scheme, or `*`; in each language the first is the primary host. */
      readonly hosts: readonly (string | SiteHostSpec)[];
      readonly https?: boolean;
      /** The site's assets root: `own` (the default for a new site), a `SysSiteAssets` folder below the start page, at `/siteassets/`; `global`, the global assets folder. An existing site's changes only when given (opticli 0.32 and newer). */
      readonly siteAssets?: 'own' | 'global';
    }
  | {
      /** Enables (or, with `enabled: false`, disables) a language branch, before the steps that use it. */
      readonly op: 'language';
      readonly lang: string | readonly string[];
      readonly enabled?: boolean;
    }
  | {
      /**
       * Optimizely Commerce: relates catalog content, as `opticli commerce relation add|remove`: a product and a variant,
       * a bundle or package and an entry (with a `quantity`), a node and an entry (an extra category).
       */
      readonly op: 'relation';
      /** A catalog ref: `$id`, `code:<code>`, `catalog:<name>`, a GUID or `id__CatalogContent`. */
      readonly parent: ContentRef;
      readonly child: ContentRef;
      readonly quantity?: number;
      readonly sortOrder?: number;
      readonly remove?: boolean;
    }
  | {
      /**
       * Optimizely Commerce: one price of a variant or package, as `opticli commerce price set|remove`, keyed by market,
       * currency, price type and code, minimum quantity and `validFrom` (none: the base price, with no end).
       */
      readonly op: 'price';
      readonly entry: ContentRef;
      readonly market: string;
      readonly currency: string;
      /** Required unless `remove`. */
      readonly amount?: number;
      /** `all` (default), `user`, `group`, or a price type of the site's own by name or number (`commerce()`'s `priceTypes`). */
      readonly priceType?: 'all' | 'user' | 'group' | (string & {});
      /** None for `all`; the user name or price group for `user` and `group`; any text or none for the site's own types. */
      readonly priceCode?: string;
      readonly minQuantity?: number;
      /** UTC unless it has an offset: `"2026-12-01T00:00:00Z"`. */
      readonly validFrom?: string;
      /** Needs `validFrom`. */
      readonly validUntil?: string;
      readonly remove?: boolean;
    }
  | {
      /** Optimizely Commerce: a warehouse, made or changed by its code, as `opticli commerce warehouse set`. */
      readonly op: 'warehouse';
      readonly code: string;
      readonly name?: string;
      readonly active?: boolean;
      readonly primary?: boolean;
      readonly fulfillmentCenter?: boolean;
      readonly pickupLocation?: boolean;
      readonly deliveryLocation?: boolean;
      readonly sortOrder?: number;
    }
  | {
      /** Optimizely Commerce: a variant's or package's stock in a warehouse, as `opticli commerce inventory set`. */
      readonly op: 'inventory';
      readonly entry: ContentRef;
      readonly warehouse: string;
      readonly inStock?: number;
      readonly backorder?: number;
      readonly preorder?: number;
      readonly tracked?: boolean;
    }
  | {
      /**
       * Optimizely Commerce: a market, made or changed by its id, as `opticli commerce market set`; or removed, as
       * `opticli commerce market remove`, with `remove: true` and no other field (Commerce removes its prices with it).
       */
      readonly op: 'market';
      readonly market: string;
      readonly name?: string;
      readonly enabled?: boolean;
      /** All its currencies (replacing its list); a new market needs them. */
      readonly currencies?: readonly string[];
      readonly defaultCurrency?: string;
      /** All its languages (replacing its list); a new market needs them. */
      readonly languages?: readonly string[];
      readonly defaultLanguage?: string;
      /** ISO 3166-1 alpha-3 codes. */
      readonly countries?: readonly string[];
      readonly description?: string;
      readonly pricesIncludeTax?: boolean;
      readonly remove?: boolean;
    };

/** A host of a `site` step, as an object. */
export interface SiteHostSpec {
  readonly name: string;
  readonly type?: 'undefined' | 'primary' | 'edit' | 'redirect-permanent' | 'redirect-temporary' | 'preview' | 'media';
  readonly lang?: string;
  readonly https?: boolean;
}

export interface Plan {
  /** Gives every create, block and upload a GUID derived from its `id`, so the plan can run again. */
  readonly guidNamespace?: string;
  /**
   * A project (its id or exact name) every step that saves a version puts its draft in, unless the step names another
   * or `null`. A name no unpublished project has is created when the plan runs (`projects` in the result says which).
   * Plans never publish a project.
   */
  readonly project?: string | number;
  readonly operations: readonly PlanStep[];
}

/** The `id`s a plan's steps declare, as a union of string literals for a plan written `as const`. */
export type PlanIds<P extends Plan> = P['operations'][number] extends infer S ? (S extends { readonly id: infer I extends string } ? I : never) : never;
