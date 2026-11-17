// The package's public surface: every name its entry points export, the methods of its classes, the properties of its
// interfaces and what its type aliases resolve to, read from the built declarations (dist/lib) with the TypeScript
// compiler. surface.txt is the checked-in list; surface.test.mjs fails when the two differ, so a rename or removal (a
// breaking change) is never an accident.
//
// After a deliberate change: `npm run build && node test/surface.mjs --write`, and say what changed in CHANGELOG.md.
import { readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import ts from 'typescript';

const here = import.meta.dirname;
export const listFile = join(here, 'surface.txt');
const entryPoints = { '@epinova/opticli': 'index.d.ts', '@epinova/opticli/playwright': 'playwright/index.d.ts' };

/**
 * One line per exported name (`<entry> <kind> <name>`), per public member of an exported class (`<Class>.<member>`),
 * per property of an exported interface (`<Interface>.<property>`, with `?` when it is optional), and for an exported
 * type alias what it resolves to (see {@link aliasLines}).
 */
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
      if (kind === 'type' && symbol.flags & ts.SymbolFlags.TypeAlias && ownDeclaration(symbol, lib)) {
        for (const line of aliasLines(checker, exported.name, checker.getDeclaredTypeOfSymbol(symbol))) lines.add(line);
        continue;
      }
      if (kind === 'interface') {
        // Each property, inherited ones too, `?` when optional: a property dropped, or made optional in a result (or
        // required in an options object), changes the list.
        for (const member of checker.getPropertiesOfType(checker.getDeclaredTypeOfSymbol(symbol))) {
          lines.add(`${exported.name}.${member.name}${member.flags & ts.SymbolFlags.Optional ? '?' : ''}`);
        }
        continue;
      }
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

const ownDeclaration = (symbol, lib) => symbol.declarations?.some((d) => d.getSourceFile().fileName.startsWith(lib));

const typeText = (checker, type) =>
  checker.typeToString(
    type,
    undefined,
    ts.TypeFormatFlags.NoTruncation | ts.TypeFormatFlags.UseSingleQuotesForStringLiteralType | ts.TypeFormatFlags.InTypeAlias,
  );

/**
 * An object type made in place (a literal, an intersection, `Omit<…>`), not an interface or class, which is listed
 * under its own name.
 */
const anonymousObject = (type) =>
  (type.flags & ts.TypeFlags.Intersection && type.types.every((t) => t.flags & ts.TypeFlags.Object)) ||
  (type.flags & ts.TypeFlags.Object && !(type.symbol && type.symbol.flags & (ts.SymbolFlags.Interface | ts.SymbolFlags.Class)) && !(type.objectFlags & ts.ObjectFlags.Reference));

const literalOnly = (type) =>
  type.flags & ts.TypeFlags.Union ? type.types.every((t) => t.flags & ts.TypeFlags.Literal) : !!(type.flags & ts.TypeFlags.Literal);

const propertyLines = (checker, prefix, type) =>
  checker.getPropertiesOfType(type).map((p) => `${prefix}.${p.name}${p.flags & ts.SymbolFlags.Optional ? '?' : ''}`);

/**
 * What a type alias resolves to, so a member it loses (a property of `CreatedContent`, a value of `VersionStatus`, a
 * step of `PlanStep`) changes the list:
 * - an object type (`CreatedContent`, `CreateCallOptions`): `<Alias>.<property>`, `?` when optional, as for interfaces;
 * - a union: `<Alias> = <member>` for each member that is a value or a named type (`VersionStatus = 'published'`,
 *   `TypeUsageRow = InstanceUsage`); for members that are object types of their own, `<Alias>[<discriminant>]` and
 *   their properties when they share a property with literal values (`PlanStep[op='create'].parent?`), else the
 *   member written out;
 * - anything else (`ContentRef = string`, an index signature): `<Alias> = <type>`. A generic conditional type
 *   (`PlanIds<P>`) is listed by name only.
 */
function aliasLines(checker, name, type) {
  if (type.flags & ts.TypeFlags.Conditional) return [];
  if (!(type.flags & ts.TypeFlags.Union)) {
    if (anonymousObject(type)) {
      const properties = propertyLines(checker, name, type);
      if (properties.length > 0) return properties;
    }
    return [`${name} = ${typeText(checker, type)}`];
  }
  const objects = type.types.filter(anonymousObject);
  const discriminant = objects.length
    ? checker
        .getPropertiesOfType(objects[0])
        .map((p) => p.name)
        .sort()
        .find((key) =>
          objects.every((o) => {
            const property = checker.getPropertyOfType(o, key);
            return property && literalOnly(checker.getTypeOfSymbol(property));
          }),
        )
    : undefined;
  const lines = [];
  for (const member of type.types) {
    if (!objects.includes(member) || !discriminant) {
      lines.push(`${name} = ${typeText(checker, member)}`);
      continue;
    }
    const value = checker.getTypeOfSymbol(checker.getPropertyOfType(member, discriminant));
    // Each value of the discriminant, sorted: TypeScript orders a union's literals by when it first met them.
    const values = (value.flags & ts.TypeFlags.Union ? value.types : [value]).map((t) => typeText(checker, t)).sort();
    const label = `${name}[${discriminant}=${values.join(' | ')}]`;
    lines.push(label, ...propertyLines(checker, label, member));
  }
  return lines;
}

export const checkedIn = () => readFileSync(listFile, 'utf8').split('\n').filter(Boolean);

if (process.argv.includes('--write')) {
  const lines = surface();
  writeFileSync(listFile, `${lines.join('\n')}\n`);
  console.log(`${listFile}: ${lines.length} names`);
}
