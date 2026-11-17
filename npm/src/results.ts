// The results of the hand-written methods (create, set, apply): the generated types, with what those methods
// guarantee made required, and a plan's results typed by their step.
import type {
  AccessOutput,
  LanguagesOutput,
  MoveOutput,
  PlanRun,
  PlanStepResult,
  RemoveLanguageOutput,
  RestoreOutput,
  SiteHostsView,
  WriteOutput,
} from './generated/types.ts';
import type { ContentRef, Plan, PlanIds } from './types.ts';

/** What `set` returns: `ref` is always there. */
export type SavedContent = WriteOutput & { ref: ContentRef };

/** What `create` returns when it isn't a dry run: the new content's `ref`, `version`, `guid`, `type` and `name`. */
export type CreatedContent = WriteOutput & { ref: ContentRef; version: string; guid: string; type: string; name: string };

type StepOf<Op extends string, Result> = Omit<PlanStepResult, 'op' | 'result'> & {
  op: Op;
  /** The step's result, as the single command would print it. */
  result?: Result;
};

/** A plan step's result, typed by its `op` (narrow on `op` to read `result`). */
export type AppliedStep =
  | StepOf<'create' | 'block' | 'upload' | 'set' | 'area' | 'composition' | 'publish' | 'unpublish' | 'discard', WriteOutput>
  | StepOf<'translate', WriteOutput | RemoveLanguageOutput>
  | StepOf<'move' | 'delete', MoveOutput>
  | StepOf<'restore', RestoreOutput>
  | StepOf<'access', AccessOutput>
  | StepOf<'site', SiteHostsView>
  | StepOf<'language', LanguagesOutput>;

/** The ids a plan declares, or any string for a plan whose ids TypeScript can't see (not written `as const`). */
export type PlanId<P extends Plan> = [PlanIds<P>] extends [never] ? string : PlanIds<P>;

/** What `apply` returns: `created` keyed by the plan's step ids, each step's result typed by its op. */
export interface AppliedPlan<P extends Plan = Plan> extends Omit<PlanRun, 'created' | 'operations'> {
  operations: AppliedStep[];
  /** Plan id → the content it created or updated, for every create, block and upload with an `id` (none for a dry run). */
  created: Record<PlanId<P>, ContentRef>;
}
