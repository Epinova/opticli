// What every opticli command prints with --json: the envelope around its data ("Output and exit codes" in docs/reference/cli/output.md).
import type { ErrorBody } from './generated/types.ts';

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

export interface ErrorEnvelope {
  ok: false;
  error: ErrorBody;
  /** opticli 0.27.0 and newer: the version that failed. */
  meta?: Pick<Meta, 'version'>;
}

export type Envelope<T> = OkEnvelope<T> | ErrorEnvelope;
