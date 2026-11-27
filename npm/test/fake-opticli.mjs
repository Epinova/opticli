#!/usr/bin/env node
// Stands in for opticli in the tests. FAKE_OPTICLI_RESPONSES names a JSON file: an array of
// {"match": "<start of the arguments>", "exit": 0, "envelope": {...}} or {"match", "exit", "raw": "<stdout>", "stderr"},
// with "delayMs" to answer late;
// the first whose match the arguments (joined by spaces) start with answers, with what follows a `--` moved before the
// options (`get --lang sv --json -- 123` is matched as `get 123 --lang sv --json`). Each call is appended to
// FAKE_OPTICLI_LOG as a JSON line: {args, cwd, stdin}.
import { appendFileSync, readFileSync } from 'node:fs';

const args = process.argv.slice(2);
let stdin = '';
for await (const chunk of process.stdin) stdin += chunk;
appendFileSync(process.env.FAKE_OPTICLI_LOG, `${JSON.stringify({ args, cwd: process.cwd(), stdin })}\n`);

const responses = JSON.parse(readFileSync(process.env.FAKE_OPTICLI_RESPONSES, 'utf8'));
const end = args.indexOf('--');
const first = args.findIndex((a) => a.startsWith('-'));
const words = end < 0 ? args : [...args.slice(0, first), ...args.slice(end + 1), ...args.slice(first, end)];
const response = responses.find((r) => words.join(' ').startsWith(r.match));
if (!response) {
  process.stderr.write(`fake-opticli: no response for ${args.join(' ')}\n`);
  process.exit(99);
}
if (response.delayMs) await new Promise((r) => setTimeout(r, response.delayMs));
if (response.stderr) process.stderr.write(response.stderr);
process.stdout.write(response.raw ?? `${JSON.stringify(response.envelope)}\n`);
process.exit(response.exit ?? 0);
