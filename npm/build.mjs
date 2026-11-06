// Builds the npm package into dist/, which is what gets published (`npm publish ./dist`): the compiled lib/, and a
// package.json with opticli's version (<Version> in Directory.Build.props) and without what only building needs.
import { execFileSync } from 'node:child_process';
import { copyFileSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const dist = join(here, 'dist');
const props = readFileSync(join(here, '..', 'Directory.Build.props'), 'utf8');
const version = props.match(/<Version>([^<]+)<\/Version>/)[1];

rmSync(dist, { recursive: true, force: true });
execFileSync(process.execPath, [join(here, 'node_modules', 'typescript', 'bin', 'tsc'), '-p', join(here, 'tsconfig.json')], { stdio: 'inherit' });

const versionFile = join(dist, 'lib', 'version.js');
const compiled = readFileSync(versionFile, 'utf8');
if (!compiled.includes("'0.0.0-dev'")) throw new Error(`${versionFile} has no '0.0.0-dev' to replace.`);
writeFileSync(versionFile, compiled.replace("'0.0.0-dev'", `'${version}'`));

const { scripts, devDependencies, ...manifest } = JSON.parse(readFileSync(join(here, 'package.json'), 'utf8'));
manifest.version = version;
manifest.files = ['lib', 'README.md', 'LICENSE'];
writeFileSync(join(dist, 'package.json'), `${JSON.stringify(manifest, null, 2)}\n`);
copyFileSync(join(here, 'README.md'), join(dist, 'README.md'));
copyFileSync(join(here, '..', 'LICENSE'), join(dist, 'LICENSE'));
console.log(`dist: opticli ${version}`);
