// Packs the Claude Desktop extension into dist/, with opticli's version (<Version> in Directory.Build.props).
//
//   node build.mjs                                  dist/opticli-cms.mcpb: asks for the CMS address when installed
//   node build.mjs https://test.example.com [name]  dist/opticli-cms-test-example-com.mcpb: that site built in, no
//                                                   question; [name] is what Claude Desktop shows (default: the host)
//
// Site-specific builds have names of their own, so an editor can install several side by side (test and production).

import { execFileSync } from "node:child_process";
import { cpSync, mkdirSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { join } from "node:path";

const [site, displayName] = process.argv.slice(2);
const root = import.meta.dirname;
const stage = join(root, "dist", "stage");
const manifest = JSON.parse(readFileSync(join(root, "manifest.json"), "utf8"));
const props = readFileSync(join(root, "..", "Directory.Build.props"), "utf8");
manifest.version = props.match(/<Version>([^<]+)<\/Version>/)[1];

let file = "opticli-cms.mcpb";
if (site) {
  const url = new URL(site);
  const slug = url.host.toLowerCase().replace(/[^a-z0-9]+/g, "-");
  manifest.name = `opticli-cms-${slug}`;
  manifest.display_name = displayName || `Optimizely CMS (${url.host})`;
  manifest.description = `Find, read and edit content in the Optimizely CMS on ${url.host}.`;
  manifest.long_description = manifest.long_description.replace(
    /Fill in the site's address when you install it \(ask your developer for it\)\. /, "");
  manifest.server.mcp_config.args[1] = site;
  delete manifest.user_config;
  file = `${manifest.name}.mcpb`;
}

rmSync(stage, { recursive: true, force: true });
mkdirSync(stage, { recursive: true });
for (const f of ["server", "package.json", "package-lock.json"]) cpSync(join(root, f), join(stage, f), { recursive: true });
writeFileSync(join(stage, "manifest.json"), JSON.stringify(manifest, null, 2) + "\n");

const windows = process.platform === "win32";
const run = (cmd, args) => execFileSync(cmd, args, { cwd: stage, stdio: "inherit", shell: windows });
const mcpb = join(root, "node_modules", ".bin", windows ? "mcpb.cmd" : "mcpb");
run("npm", ["ci", "--omit=dev", "--no-audit", "--no-fund"]);
run(mcpb, ["validate", "manifest.json"]);
run(mcpb, ["pack", ".", join(root, "dist", file)]);
rmSync(stage, { recursive: true, force: true });
console.log(`\ndist/${file}`);
