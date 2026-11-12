// Types for opticli's JSON output: the README's "Output and exit codes" and skill/reference.md.
// Hand-written for the commands a test suite needs; result types list the fields worth reading, and opticli may add
// more. Input types are readonly, so plans and values written `as const` fit.

/** A content reference as opticli takes it: an id ("123"), a version ("123_456"), a GUID or a URL ("/en/about/"). */
export type ContentRef = string;

export interface Meta {
  /** `db` for reads, `agent` for writes, `cli` for local commands; absent on an error. */
  source?: 'db' | 'agent' | 'cli';
  /** The opticli version that answered. */
  version: string;
  /** Cursor for the next page (`--cursor`). */
  next?: string;
  warnings?: string[];
  /** Present when the database is remote, or has a role other than `local`. */
  database?: { server: string; name: string; local: boolean; development?: boolean; role?: string };
}

export interface OkEnvelope<T> {
  ok: true;
  data: T;
  meta: Meta;
}

/** `error.code`, with the exit code in comments. */
export type ErrorCode =
  | 'usage' // 1
  | 'internal' // 1
  | 'not_found' // 2
  | 'refused' // 3
  | 'unreachable' // 4
  | 'login_failed' // 4
  | 'timeout' // 4
  | 'conflict' // 5
  | 'validation' // 5
  | 'drift' // 5
  | 'needs_selection' // 6
  | 'job_failed' // 7
  | 'cancelled'; // 130

export interface ErrorBody {
  code: ErrorCode;
  message: string;
  /** What to do next. */
  hint?: string;
  details?: Record<string, unknown>;
}

export interface ErrorEnvelope {
  ok: false;
  error: ErrorBody;
  /** opticli 0.27.0 and newer: the version that failed. */
  meta?: Pick<Meta, 'version'>;
}

export type Envelope<T> = OkEnvelope<T> | ErrorEnvelope;

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

export interface ServeResult {
  state: 'running' | 'stopped' | 'starting' | (string & {});
  message: string;
  mode?: 'background' | 'foreground' | 'ide';
  pid?: number;
  port?: number;
  /** Where the site agent listens (HTTP on 127.0.0.1). */
  url?: string;
  /** With `--https`: the address to browse. */
  browseUrl?: string;
  scheduler?: 'on' | 'off';
  /** The site agent the running site has: its opticli version, CMS version and runtime. */
  agent?: { version: string; protocol: number; cmsVersion: string; runtime: string; environment: string };
  /** `serve --stop`: whether a site was stopped. */
  stopped?: boolean;
}

export interface ChangedProperty {
  property: string;
  before?: unknown;
  after?: unknown;
}

/** What `create`, `set`, `publish` and the other single writes return. */
export interface WriteResult {
  ref: ContentRef;
  /** The version written (`id_version`); for a dry run, the version the change was applied to. */
  version?: string;
  guid?: string;
  type?: string;
  name?: string;
  language?: string;
  status?: 'published' | 'checkedOut' | 'checkedIn' | 'delayedPublish' | 'awaitingApproval' | (string & {});
  parent?: ContentRef;
  saved: boolean;
  published: boolean;
  dryRun: boolean;
  valid: boolean;
  baseVersion?: string;
  changes?: ChangedProperty[];
  validation?: { property?: string; message: string; severity?: string }[];
  /** The version that was live before a publish: publish it again to go back. */
  previouslyPublished?: string;
  /**
   * The public URL, host-relative on the served site as `get` shows it; for a draft of content never published, where
   * it will be. None for blocks, folders and a dry-run create.
   */
  url?: string;
  /** The same URL with the host of the content's site for its language, as the CMS links to it (opticli 0.31 and newer). */
  canonicalUrl?: string;
  /** What the site's own code did during the save (handlers of the CMS's content events); a dry run never has these. */
  siteChanges?: SiteChange[];
}

/** A change the site's own code made while content was saved. */
export interface SiteChange {
  content: { ref: string; id: number; version?: number; guid: string; name: string; type?: string; language?: string; status?: string; parent?: string };
  /** `created`, `saved`, `published`, `moved`, `deleted`; for the saved content itself, `changed` when other values were stored than asked for. */
  actions: ('created' | 'saved' | 'published' | 'moved' | 'deleted' | 'changed' | (string & {}))[];
  /** For `moved`: where it was (for the saved content, the parent it was asked to go below). */
  previousParent?: string;
  /** For `changed`: the properties the write set that the site changed. An empty value is left out. */
  properties?: { property: string; asked?: unknown; saved?: unknown }[];
}

/** A site as `opticli sites` lists it. */
export interface SiteInfo {
  id: number;
  /** CMS 12 only. */
  guid?: string;
  name: string;
  /** SiteUrl (CMS 12), or the application's URL (CMS 13). */
  url?: string;
  startPage?: ContentRef;
  masterLanguage?: string;
  hosts: { name: string; type: string; language?: string; https?: boolean }[];
  /** CMS 13: the application's name. */
  application?: string;
  isDefault?: boolean;
}

export interface DeleteResult {
  ref: ContentRef;
  guid: string;
  name: string;
  parent: ContentRef;
  previousParent: ContentRef;
  moved: boolean;
  dryRun: boolean;
  descendants: number;
  recycleBin: boolean;
}

export interface UrlResult {
  ref: ContentRef;
  guid: string;
  type: string;
  languages: { language: string; name: string; status: string; path: string | null; url: string | null; site: string }[];
}

export interface PropertyOut {
  type: string;
  value: unknown;
  culture?: string;
  truncated?: boolean;
  length?: number;
}

/** What `get` returns. */
export interface ContentItem {
  ref: ContentRef;
  guid: string;
  type: string;
  name: string;
  language: string;
  status: string;
  url?: string;
  kind: 'page' | 'block' | 'media' | 'folder' | (string & {});
  version: string;
  parent: ContentRef;
  saved: string;
  changedBy: string;
  properties: Record<string, PropertyOut>;
}

// Plans (`apply`): skill/reference.md "Plans". "$id" refers to what an earlier create, block or upload made.

interface StepBase {
  /** Names what this step makes, for "$id" in later steps (needed with a plan's `guidNamespace`). */
  readonly id?: string;
  readonly guid?: string;
  readonly publish?: boolean;
  readonly lang?: string;
}

export type PlanStep =
  | (StepBase & { readonly op: 'create'; readonly parent?: ContentRef; readonly for?: ContentRef; readonly type: string; readonly name: string; readonly properties?: Properties })
  | (StepBase & { readonly op: 'block'; readonly type: string; readonly name: string; readonly for?: ContentRef; readonly parent?: ContentRef; readonly properties?: Properties })
  | (StepBase & { readonly op: 'upload'; readonly file: string; readonly for?: ContentRef; readonly parent?: ContentRef; readonly name?: string; readonly properties?: Properties })
  | (StepBase & { readonly op: 'set'; readonly ref: ContentRef; readonly properties?: Properties; readonly name?: string; readonly from?: string })
  | (StepBase & {
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
  | (StepBase & { readonly op: 'translate'; readonly ref: ContentRef; readonly lang: string; readonly name?: string; readonly properties?: Properties })
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
  readonly operations: readonly PlanStep[];
}

/** The `id`s a plan's steps declare, as a union of string literals for a plan written `as const`. */
export type PlanIds<P extends Plan> = P['operations'][number] extends infer S ? (S extends { readonly id: infer I extends string } ? I : never) : never;

export interface PlanStepResult {
  index: number;
  op: PlanStep['op'] | (string & {});
  id?: string;
  guid?: string;
  status: 'saved' | 'unchanged' | 'valid' | 'simulated' | 'deferred' | 'invalid' | 'failed' | (string & {});
  result?: Partial<WriteResult> & { existing?: boolean; restored?: boolean };
  error?: ErrorBody;
  warnings?: string[];
  undo?: unknown;
}

export interface ApplyResult {
  dryRun: boolean;
  operations: PlanStepResult[];
  /** `id` of each create, block and upload → the content it made or updated. */
  created: Record<string, ContentRef>;
}
