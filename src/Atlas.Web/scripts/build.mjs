// Type-checks the frontend (vue-tsc) and, unless --typecheck is passed, builds it with Vite.
// --package forces the built @atlas/markdown dist even when ATLAS_EDITOR_MODE=source (it is built first when stale);
// in source mode, TypeScript resolves the public editor specifiers from scripts/editorSourceMap.mjs.
import { spawnSync } from "node:child_process";
import { unlinkSync, writeFileSync } from "node:fs";
import { resolve } from "node:path";
import { assertEditorSource, editorFallbackPaths, editorSourceDeclarations, editorSourceEntries, editorSourcePaths, editorSourceRoot, isEditorSourceMode, webRoot } from "./editorSourceMap.mjs";

const args = process.argv.slice(2);
const sourceMode = !args.includes("--package") && isEditorSourceMode();
const env = { ...process.env, ATLAS_EDITOR_MODE: sourceMode ? "source" : "package" };
let config = "tsconfig.json";
let generated;
if (!sourceMode && spawnSync(process.execPath, [resolve(webRoot, "scripts/build-editor.mjs")], { stdio: "inherit" }).status !== 0)
  process.exit(1);
if (sourceMode) {
  const editor = editorSourceRoot();
  assertEditorSource(editor);
  generated = resolve(webRoot, `tsconfig.source.${process.pid}.generated.json`);
  writeFileSync(generated, JSON.stringify({
    extends: "./tsconfig.json",
    // `extends` replaces `paths` wholesale, so the application alias is repeated here.
    // Unreleased entries this checkout does not export keep their Atlas stand-ins (as in tsconfig.json).
    compilerOptions: { paths: { "@/*": ["./src/*"], ...editorFallbackPaths(editorSourceEntries(editor)), ...editorSourcePaths(editor) } },
    // Added to the inherited `include`: ambient editor declarations no import reaches.
    files: editorSourceDeclarations(editor),
  }, null, 2));
  config = generated;
}

function run(bin, params) {
  const result = spawnSync(process.execPath, [resolve(webRoot, "node_modules", bin), ...params], { cwd: webRoot, env, stdio: "inherit" });
  if (result.error) throw result.error;
  if (result.status !== 0) process.exitCode = result.status ?? 1;
  return result.status === 0;
}

let checked = false;
try {
  // Incremental: a small change is checked in seconds instead of re-checking the whole frontend.
  const buildInfo = resolve(webRoot, "node_modules/.cache", `typecheck-${sourceMode ? "source" : "package"}.tsbuildinfo`);
  checked = run("vue-tsc/bin/vue-tsc.js", ["--noEmit", "-p", config, "--incremental", "--tsBuildInfoFile", buildInfo]);
} finally {
  if (generated) unlinkSync(generated);
}
if (checked && !args.includes("--typecheck"))
  run("vite/bin/vite.js", ["build", ...args.filter(arg => arg !== "--package")]);
