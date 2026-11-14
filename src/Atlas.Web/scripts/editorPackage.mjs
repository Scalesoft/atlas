// Pure checks behind scripts/build-editor.mjs and scripts/verify-editor-package.mjs; tested by src/editorPackage.test.ts.
import { createHash } from "node:crypto";
import { existsSync, readdirSync, readFileSync, statSync } from "node:fs";
import { resolve } from "node:path";

export const packageName = "@atlas/markdown";
/** Written into the editor's dist after a build: the editorInputHash it was built from. */
export const editorBuildStamp = ".atlas-build";

/** The OpenAPI plugin's lazily loaded files; Scalar is `standalone-*` in source mode, `scalar-standalone-*` from the package. */
export const openApiAssets = [
  { name: "Scalar standalone bundle", pattern: /^(?:scalar-)?standalone-[\w-]+\.js$/ },
  // A file, never inlined as a data: URL (vite.config.ts assetsInlineLimit): the CSP would block it and the frame stay empty.
  { name: "OpenAPI reference frame script", pattern: /^scalarFrame-[\w-]+\.js$/ },
  { name: "Monaco editor worker", pattern: /^editor\.worker-[\w-]+\.js$/ },
  { name: "Monaco JSON worker", pattern: /^json\.worker-[\w-]+\.js$/ },
  { name: "Monaco YAML worker", pattern: /^yaml\.worker-[\w-]+\.js$/ },
];

export function missingAssets(files) {
  return openApiAssets.filter(asset => !files.some(file => asset.pattern.test(file))).map(asset => asset.name);
}

/** What the editor's dist build reads (package.json "build"); untracked files beside them (obj/, editor backups) do not count. */
const buildInputs = ["package.json", "vite.config.ts", "tsconfig.build.json", "schema", "src"];

/** The editor package's build input files, relative and sorted, without unit tests (*.test.ts) and Markdown. */
export function editorInputFiles(editor) {
  const walk = path => statSync(resolve(editor, path)).isDirectory()
    ? readdirSync(resolve(editor, path)).flatMap(name => name === "node_modules" ? [] : walk(`${path}/${name}`))
    : /\.test\.ts$|\.md$/.test(path) ? [] : [path];
  return buildInputs.filter(path => existsSync(resolve(editor, path))).flatMap(walk).sort();
}

/** One hash of the editor's build inputs and the workspace lockfile (the versions it is built against). */
export function editorInputHash(editor, lockfile) {
  const hash = createHash("sha256");
  for (const path of editorInputFiles(editor)) hash.update(`${path}\0`).update(readFileSync(resolve(editor, path))).update("\0");
  return hash.update(readFileSync(lockfile)).digest("hex");
}
