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

/** A ContentArea item: existing content by ref or GUID, or an inline block (CMS 12.20+). */
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

interface StepBase {
  /** Names what this step makes, for "$id" in later steps (needed with a plan's `guidNamespace`). */
  readonly id?: string;
  readonly guid?: string;
  readonly publish?: boolean;
  readonly lang?: string;
  /**
   * Where an approval sequence applies, send the change for review instead of publishing (as `requestApproval` on a
   * single write).
   */
  readonly requestApproval?: boolean;
}

/** A step that saves a version, which can go into a project (opticli 0.33 and newer). */
interface SaveStep extends StepBase {
  /**
   * The project (its id or exact name) the saved draft goes into, instead of the plan's `project`; `null` for none. Not
   * with `publish`: `projectsPublish` puts the project's ready items live together.
   */
  readonly project?: string | number | null;
  /** Also mark the saved version Ready to publish in its project. */
  readonly ready?: boolean;
}

export type PlanStep =
  | (SaveStep & { readonly op: 'create'; readonly parent?: ContentRef; readonly for?: ContentRef; readonly type: string; readonly name: string; readonly properties?: Properties })
  | (SaveStep & { readonly op: 'block'; readonly type: string; readonly name: string; readonly for?: ContentRef; readonly parent?: ContentRef; readonly properties?: Properties })
  | (SaveStep & { readonly op: 'upload'; readonly file: string; readonly for?: ContentRef; readonly parent?: ContentRef; readonly name?: string; readonly properties?: Properties })
  | (SaveStep & { readonly op: 'set'; readonly ref: ContentRef; readonly properties?: Properties; readonly name?: string; readonly from?: string })
  | (SaveStep & {
      readonly op: 'area';
      readonly ref: ContentRef;
      readonly property: string;
      readonly action: 'add' | 'remove' | 'move' | 'set';
      readonly item?: ContentRef;
      readonly type?: string;
      readonly values?: Properties;
      readonly at?: number;
      readonly displayOption?: string;
    })
  | (StepBase & { readonly op: 'publish' | 'unpublish' | 'discard'; readonly ref: ContentRef; readonly version?: string })
  | (StepBase & { readonly op: 'move'; readonly ref: ContentRef; readonly to: ContentRef })
  | (StepBase & { readonly op: 'delete'; readonly ref: ContentRef; readonly ignoreReferences?: boolean })
  | (StepBase & { readonly op: 'restore'; readonly ref: ContentRef; readonly to?: ContentRef })
  | (SaveStep & { readonly op: 'translate'; readonly ref: ContentRef; readonly lang: string; readonly name?: string; readonly properties?: Properties })
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
