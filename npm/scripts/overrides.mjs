// What opticli's schema can't say, for generate.mjs. Small on purpose, and reviewed with the generated diff: each entry
// names a place in the schema, and the generator fails when one no longer exists.

/** Type declarations added to generated/types.ts, for the types below. */
export const declarations = `
/** A JSON value, as opticli prints it. */
export type JsonValue = string | number | boolean | null | JsonValue[] | { [key: string]: JsonValue };

/** A cell of \`sql\`'s rows: dates are strings, binary is \`0x…\` hex, long strings are cut unless \`full\`. */
export type SqlValue = string | number | boolean | null;

/**
 * A property as \`get\` shows it: its type and value, with what \`get\` adds for reading (\`culture\`, \`links\`,
 * \`blockType\`, \`target\`, \`blocks\`, \`personalized\`, and \`truncated\` with the full \`length\` for a cut value).
 */
export interface PropertyOut {
  /** The property's type: \`String\`, \`XhtmlString\`, \`ContentArea\`, \`ContentReference\`, ... */
  type: string;
  value: JsonValue;
  /** The value was cut short (\`full\` shows it whole). */
  truncated?: boolean;
  /** The whole value's length, when it was cut. */
  length?: number;
  [decoration: string]: JsonValue | undefined;
}
`;

/**
 * Property types the schema leaves open (`x-dynamic`): property values, which depend on the content type, and details
 * whose shape depends on the error. `Type.property` → TypeScript.
 */
export const properties = {
  'ContentDocument.properties': 'Record<string, PropertyOut>',
  'PropertyChange.before': 'JsonValue',
  'PropertyChange.after': 'JsonValue',
  'SiteChangedProperty.asked': 'JsonValue',
  'SiteChangedProperty.saved': 'JsonValue',
  'CompositionFieldChange.before': 'JsonValue',
  'CompositionFieldChange.after': 'JsonValue',
  'ErrorBody.details': 'Record<string, unknown>',
};

/** Types with type parameters: `sql<Row>()` types its rows by the caller. */
export const generics = {
  SqlResult: { parameters: 'Row extends object = Record<string, SqlValue>', properties: { rows: 'Row[]' } },
};

/** Methods typed by hand: their type parameters, and the result type in place of the schema's. */
export const methods = {
  sql: { typeParameters: 'Row extends object = Record<string, SqlValue>', result: 'SqlResult<Row>', imports: ['SqlResult', 'SqlValue'] },
};

/** Option types: `--values` takes the properties as an object, which the generated method sends as JSON. */
export const options = {
  '--values': { type: 'Properties', description: 'Property values as an object (sent as JSON): see `Properties`.' },
};

/** Imports the generated commands need from the hand-written layer: input types, which no C# type describes. */
export const handWrittenImports = ['Properties'];

/** Argument names that aren't identifiers (`apply`'s `plan.json`). Command path → argument → name. */
export const argumentNames = {
  apply: { 'plan.json': 'plan' },
};

/**
 * How long a call may take by default (milliseconds), as an expression over `options`, for commands that wait longer than
 * the runner's default; `call.timeoutMs` overrides it.
 */
export const timeouts = {
  // A start may build the site first (minutes), then waits --timeout seconds for it to answer.
  serve: '((options?.timeout ?? 180) + 600) * 1000',
  apply: '600_000',
  // Waits as long as the job runs, unless --timeout or --no-wait.
  'jobs run': '((options?.timeout ?? 3540) + 60) * 1000',
};
