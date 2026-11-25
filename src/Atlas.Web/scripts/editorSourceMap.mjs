// The single table of `@atlas/markdown` public specifiers Atlas may import, and the editor
// source file each one maps to in opt-in source mode (ATLAS_EDITOR_MODE=source). The editor is the workspace
// package src/Atlas.Markdown; Atlas imports its built dist (scripts/build-editor.mjs) unless source mode is on.
// Consumed by vite.config.ts (bundler aliases) and scripts/build.mjs (TypeScript paths), and
// checked by src/editorBoundary.guard.test.ts: every specifier Atlas imports must be listed here, and
// every entry of `editorSourceExports` must be exported by the editor package.
//
// `plugins/secrets` (WP4 V6) may be imported only by the document widgets and src/secrets, the Atlas host of password
// references. Deliberately absent: anything below `concealment/` other than its public entry and stylesheet.
import { existsSync, readdirSync, readFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

// Not `new URL("..", import.meta.url)`: Vite rewrites that pattern as an asset URL when a test imports this file.
export const webRoot = resolve(dirname(fileURLToPath(import.meta.url)), "..");
/** The editor package of this repository (`@atlas/markdown`, a package of the src/Atlas.Web workspace). */
export const editorPackageRoot = resolve(webRoot, "../Atlas.Markdown");

const plugins = ["youtrack", "attachments", "contacts", "charts", "diagrams", "math", "toc", "emoji", "openapi", "heading-anchors", "editor-tools", "secrets", "concealed", "pages"];
const themes = ["ocean", "forest", "midnight"];

/** Public specifier -> path inside the editor package, for the entries Atlas may import. Frozen; order is irrelevant. */
export const editorSourceExports = Object.freeze({
  "@atlas/markdown": "src/index.ts",
  "@atlas/markdown/core": "src/core/index.ts",
  // The collaborator palette without the editor, for the shell's first screen (live/collaboratorColors.ts, eager).
  "@atlas/markdown/core/userColor": "src/core/userColor.ts",
  "@atlas/markdown/vue": "src/vue/index.ts",
  "@atlas/markdown/icons": "src/icons/index.ts",
  // The slash menu's matching without the editor, for the shell's command palette (eager).
  "@atlas/markdown/match": "src/match/index.ts",
  "@atlas/markdown/concealment": "src/concealment/index.ts",
  "@atlas/markdown/concealment/style.css": "src/concealment/style.css",
  // `assist` (0.9.0): the optional block suggestions module (plan §0, §12 H.4). Only src/assist imports it, lazily.
  "@atlas/markdown/assist": "src/assist/index.ts",
  "@atlas/markdown/assist/style.css": "src/assist/assist.css",
  ...Object.fromEntries(plugins.map(name => [`@atlas/markdown/plugins/${name}`, `src/plugins/${name}/index.ts`])),
  "@atlas/markdown/collaboration/signalr": "src/collaboration/signalr.ts",
  "@atlas/markdown/source": "src/source/index.ts",
  // The JavaScript reference of the managed authority's parity fixtures (src/server/liveWrite.ts, dev-time); never part of the browser bundle.
  "@atlas/markdown/server": "src/server/index.ts",
  // The schema manifest of Atlas's plugins, exported for the server (src/hostDocumentSchema.test.ts only).
  "@atlas/markdown/schema": "src/schema/index.ts",
  "@atlas/markdown/styles/base.css": "src/styles/editor.css",
  // The state tokens (P12), loaded globally by main.ts so the shell, boards and the editor share them.
  "@atlas/markdown/styles/states.css": "src/styles/states.css",
  ...Object.fromEntries(themes.map(theme => [`@atlas/markdown/themes/${theme}.css`, `src/styles/${theme}.css`])),
});

/**
 * Public entries newer than the pinned release. Source mode maps them only when the editor checkout exports
 * them; Atlas may import them once the pin includes them and they move to `editorSourceExports`. None at the moment
 * (`assist` moved with the 0.9.0 pin).
 */
export const unreleasedEditorExports = Object.freeze({});

/**
 * Atlas stand-ins for unreleased entries (path inside this web project). Wherever an unreleased entry is not
 * mapped to the editor source (package mode, or an editor checkout without it), its specifier resolves to the
 * stand-in, which reports the feature as absent. Remove the stand-in when the entry moves to `editorSourceExports`.
 * A script stand-in also needs a tsconfig.json `paths` entry, so package mode type-checks against it.
 */
export const unreleasedEditorFallbacks = Object.freeze({});

/** Stand-ins for the unreleased entries that `entries` (the mapped specifiers of the current mode) lacks. */
export function editorFallbackEntries(entries) {
  return Object.freeze(Object.fromEntries(Object.entries(unreleasedEditorFallbacks)
    .filter(([name]) => !(name in entries))
    .map(([name, path]) => [name, resolve(webRoot, path)])));
}

/** Exact-match Vite aliases for the stand-ins `editorFallbackEntries` selects. */
export function editorFallbackAliases(entries) {
  return Object.entries(editorFallbackEntries(entries)).map(([name, path]) => ({
    find: new RegExp(`^${escape(name)}$`),
    replacement: path,
  }));
}

/** TypeScript `paths` for the stand-ins `editorFallbackEntries` selects. */
export function editorFallbackPaths(entries) {
  return Object.fromEntries(Object.entries(editorFallbackEntries(entries)).map(([name, path]) => [name, [path]]));
}

export function isEditorSourceMode(env = process.env) {
  return env.ATLAS_EDITOR_MODE === "source";
}

/** The editor package of this repository, or ATLAS_EDITOR_SOURCE (another checkout's editor package). */
export function editorSourceRoot(env = process.env) {
  return resolve(env.ATLAS_EDITOR_SOURCE ?? editorPackageRoot);
}

export function assertEditorSource(editor) {
  if (!existsSync(resolve(editor, "src/vue/index.ts")))
    throw new Error(`Local editor source was not found at ${editor}. Set ATLAS_EDITOR_SOURCE to an editor package (src/Atlas.Markdown), or unset ATLAS_EDITOR_MODE to use the built @atlas/markdown dist.`);
}

/** The `exports` of a package.json as public specifiers (`.` -> `@atlas/markdown`). */
export function exportedSpecifiers(manifest) {
  return Object.keys(manifest.exports ?? {}).map(path => path === "." ? "@atlas/markdown" : `@atlas/markdown/${path.slice(2)}`);
}

/** Everything source mode maps: the pinned entries plus the unreleased ones the editor checkout exports. */
export function editorSourceEntries(editor) {
  const manifest = resolve(editor, "package.json");
  const exported = existsSync(manifest) ? exportedSpecifiers(JSON.parse(readFileSync(manifest, "utf8"))) : [];
  return Object.freeze({
    ...editorSourceExports,
    ...Object.fromEntries(Object.entries(unreleasedEditorExports).filter(([name]) => exported.includes(name))),
  });
}

const escape = value => value.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");

/** Exact-match Vite aliases for every mapped public specifier. */
export function editorSourceAliases(editor) {
  return Object.entries(editorSourceEntries(editor)).map(([name, path]) => ({
    find: new RegExp(`^${escape(name)}$`),
    replacement: resolve(editor, path),
  }));
}

/** TypeScript `paths` for the script entries (stylesheets need no declarations). */
export function editorSourcePaths(editor) {
  return Object.fromEntries(Object.entries(editorSourceEntries(editor))
    .filter(([name]) => !name.endsWith(".css"))
    .map(([name, path]) => [name, [resolve(editor, path)]]));
}

/**
 * The editor's ambient declaration files (`declare module ...`, e.g. Monaco's Monarch grammars). Nothing
 * imports them, so a TypeScript program that follows the public entries needs them listed explicitly.
 */
export function editorSourceDeclarations(editor) {
  const root = resolve(editor, "src");
  if (!existsSync(root)) return [];
  return readdirSync(root, { recursive: true, encoding: "utf8" })
    .filter(path => path.endsWith(".d.ts"))
    .map(path => resolve(root, path))
    .filter(path => /^\s*declare\s+module\s+['"]/m.test(readFileSync(path, "utf8")));
}

/**
 * Editor source directories whose imports go through `editorSourceRewrites`: the OpenAPI plugin (Scalar,
 * JSON/YAML workers, schema setup) and the shared Monaco source editor (slim Monaco, base worker).
 */
export const editorRewriteDirectories = Object.freeze(["src/plugins/openapi", "src/source"]);

/**
 * The OpenAPI plugin and the source editor import Scalar and Monaco through aliases that exist only in the
 * editor's own vite.config.ts. Mirrors those aliases: `from` is the specifier as written in the editor
 * source, `to` its replacement (a file with query, or a `$1` pattern) exactly as the editor build uses it.
 */
export function editorSourceRewrites(editor) {
  const modules = resolve(editor, "node_modules");
  return [
    { from: /^\.\/scalar-standalone\?url$/, to: `${resolve(modules, "@scalar/api-reference/dist/browser/standalone.js")}?url&no-inline` },
    { from: /^\.\/monaco-editor\.worker\?worker$/, to: `${resolve(modules, "monaco-editor/esm/vs/editor/editor.worker.js")}?worker` },
    { from: /^\.\/monaco-json\.worker\?worker$/, to: `${resolve(modules, "monaco-editor/esm/vs/language/json/json.worker.js")}?worker` },
    { from: /^\.\/monaco-yaml\.worker\?worker$/, to: `${resolve(editor, "src/plugins/openapi/yaml.worker.ts")}?worker` },
    { from: /^monaco-editor\/esm\/vs\/(.+)$/, to: `${resolve(modules, "monaco-editor/esm/vs")}/$1` },
  ];
}

/** Keep the source Scalar URL identical to the packaged plugin asset identity.
 * CSP acceptance recognizes only this vendor's caught Zod JIT capability probe. */
export function editorAssetFileName(asset) {
  const scalar = asset.originalFileNames.some(path => path.replaceAll("\\", "/").endsWith("/@scalar/api-reference/dist/browser/standalone.js"));
  return scalar ? "assets/scalar-standalone-[hash][extname]" : "assets/[name]-[hash][extname]";
}
