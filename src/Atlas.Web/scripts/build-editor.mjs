// Builds the editor's dist (src/Atlas.Markdown: its library build and declarations, what Atlas imports) unless the dist
// already holds a build of the current sources and lockfile. Run by `pnpm run build:editor` and `pnpm dev`,
// scripts/build.mjs, tests/TestParts.ps1, the CI web step and the AtlasBuildFrontend target (every publish).
//
// shortcut: two builds started at once in one checkout race on dist; the test scripts build before their parallel parts.
import { spawnSync } from "node:child_process";
import { existsSync, readFileSync, writeFileSync } from "node:fs";
import { resolve } from "node:path";
import { editorBuildStamp, editorInputHash, packageName } from "./editorPackage.mjs";
import { editorPackageRoot, webRoot } from "./editorSourceMap.mjs";

const stamp = resolve(editorPackageRoot, "dist", editorBuildStamp);
const hash = editorInputHash(editorPackageRoot, resolve(webRoot, "pnpm-lock.yaml"));
if (existsSync(stamp) && readFileSync(stamp, "utf8").trim() === hash) {
  console.log(`${packageName} dist is current.`);
} else {
  // Through the workspace root: pnpm run inside src/Atlas.Markdown finds no workspace and would install it on its own.
  // A shell finds pnpm's Windows shim; the command holds no user input.
  const result = spawnSync(`pnpm --filter ${packageName} run build`, { cwd: webRoot, stdio: "inherit", shell: true });
  if (result.error) throw result.error;
  if (result.status !== 0) process.exit(result.status ?? 1);
  writeFileSync(stamp, `${hash}\n`);
}
