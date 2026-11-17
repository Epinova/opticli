// The package's public surface: every name its entry points export, and the methods of its classes, read from the
// built declarations (dist/lib) with the TypeScript compiler. surface.txt is the checked-in list; surface.test.mjs fails
// when the two differ, so a rename or removal (a breaking change) is never an accident.
//
// After a deliberate change: `npm run build && node test/surface.mjs --write`, and say what changed in CHANGELOG.md.
import { readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import ts from 'typescript';

const here = import.meta.dirname;
export const listFile = join(here, 'surface.txt');
const entryPoints = { '@epinova/opticli': 'index.d.ts', '@epinova/opticli/playwright': 'playwright/index.d.ts' };

/** One line per exported name (`<entry> <kind> <name>`) and per public member of an exported class (`<Class>.<member>`). */
export function surface() {
  const lib = join(here, '..', 'dist', 'lib');
  const files = Object.values(entryPoints).map((f) => join(lib, f));
  const program = ts.createProgram(files, { strict: true, skipLibCheck: true, target: ts.ScriptTarget.ES2022 });
  const checker = program.getTypeChecker();
  const lines = new Set();
  for (const [entry, file] of Object.entries(entryPoints)) {
    const source = program.getSourceFile(join(lib, file));
    if (!source) throw new Error(`No ${file} in dist/lib: run npm run build first.`);
    for (const exported of checker.getExportsOfModule(checker.getSymbolAtLocation(source))) {
      const symbol = exported.flags & ts.SymbolFlags.Alias ? checker.getAliasedSymbol(exported) : exported;
      const kind =
        symbol.flags & ts.SymbolFlags.Class ? 'class'
        : symbol.flags & ts.SymbolFlags.Function ? 'function'
        : symbol.flags & ts.SymbolFlags.Variable ? 'const'
        : symbol.flags & ts.SymbolFlags.Interface ? 'interface'
        : 'type';
      lines.add(`${entry} ${kind} ${exported.name}`);
      if (kind !== 'class') continue;
      for (const member of checker.getPropertiesOfType(checker.getDeclaredTypeOfSymbol(symbol))) {
        const modifiers = ts.getCombinedModifierFlags(member.valueDeclaration ?? member.declarations?.[0]);
        if (member.name.startsWith('#') || modifiers & (ts.ModifierFlags.Private | ts.ModifierFlags.Protected)) continue;
        // The package's own members, not Error's (which depend on TypeScript's lib).
        if (!member.declarations?.some((d) => d.getSourceFile().fileName.startsWith(lib))) continue;
        lines.add(`${exported.name}.${member.name}`);
      }
    }
  }
  return [...lines].sort();
}

export const checkedIn = () => readFileSync(listFile, 'utf8').split('\n').filter(Boolean);

if (process.argv.includes('--write')) {
  const lines = surface();
  writeFileSync(listFile, `${lines.join('\n')}\n`);
  console.log(`${listFile}: ${lines.length} names`);
}
