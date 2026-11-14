import { existsSync, readdirSync, readFileSync, realpathSync } from "node:fs";
import { join, relative, resolve, sep } from "node:path";
import { describe, expect, it } from "vitest";
import { editorBuildStamp, editorInputHash } from "../scripts/editorPackage.mjs";
import {
  editorPackageRoot,
  editorRewriteDirectories,
  editorSourceEntries,
  editorSourceExports,
  editorSourceRewrites,
  editorSourceRoot,
  exportedSpecifiers,
  isEditorSourceMode,
  unreleasedEditorExports,
  unreleasedEditorFallbacks,
  webRoot,
} from "../scripts/editorSourceMap.mjs";

const sourceRoot = resolve(webRoot, "src");
const editor = editorSourceRoot();
// Package mode (the default, the editor's built dist) may import only the listed entries; source mode also maps the
// unreleased entries the editor sources already export.
const sourceMode = isEditorSourceMode();
const mapped = sourceMode ? editorSourceEntries(editor) : editorSourceExports;
// An unreleased entry with an Atlas stand-in is importable in every mode (the stand-in reports it absent).
const importable = { ...unreleasedEditorFallbacks, ...mapped };
const readJson = (path: string) => JSON.parse(readFileSync(path, "utf8"));
const specifier = /(?:\bfrom|\bimport|\bimport\s*\(|\bvi\.mock\s*\()\s*["'](@atlas\/markdown(?:\/[^"']*)?)["']/g;

function files(directory: string): string[] {
  return readdirSync(directory, { withFileTypes: true }).flatMap(entry => {
    const path = join(directory, entry.name);
    return entry.isDirectory() ? files(path) : /\.(ts|vue|css)$/.test(entry.name) ? [path] : [];
  });
}
/** Every `@atlas/markdown` specifier Atlas imports (or mocks), with the files using it. */
function importedSpecifiers() {
  const used = new Map<string, string[]>();
  for (const file of files(sourceRoot)) {
    for (const [, name] of readFileSync(file, "utf8").matchAll(specifier))
      used.set(name!, [...(used.get(name!) ?? []), relative(webRoot, file)]);
  }
  return used;
}

describe("editor package boundary", () => {
  const used = importedSpecifiers();

  it("imports only public specifiers listed in scripts/editorSourceMap.mjs", () => {
    expect(used.size).toBeGreaterThan(0);
    const unlisted = [...used].filter(([name]) => !(name in importable));
    expect(unlisted, "add the specifier to editorSourceMap.mjs, or use a public entry").toEqual([]);
  });
  it("keeps the secrets plugin in its host and concealment internals out of Atlas", () => {
    // WP4 V6: only the document widgets (registration) and src/secrets (host services, picker) touch the plugin;
    // DocumentEditor dispatches its insert commands. Nothing else may reach password references directly.
    // The gallery example drives the plugin with local sample services only (no vault is reached).
    const hosts = ["src/documentWidgets.ts", "src/components/DocumentEditor.vue", "src/components/dev/SecretReferenceExample.vue"];
    const outside = [...used].filter(([name]) => name.startsWith("@atlas/markdown/plugins/secrets"))
      .flatMap(([, users]) => users.map(file => file.split(sep).join("/")))
      .filter(file => !hosts.includes(file) && !file.startsWith("src/secrets/") && !file.endsWith(".test.ts"));
    expect(outside).toEqual([]);
    const forbidden = [...used].filter(([name]) => name.startsWith("@atlas/markdown/concealment/") && name !== "@atlas/markdown/concealment/style.css");
    expect(forbidden).toEqual([]);
    expect(Object.keys(mapped).filter(name => name.includes("secrets"))).toEqual(["@atlas/markdown/plugins/secrets"]);
    expect(Object.keys(mapped).filter(name => name.startsWith("@atlas/markdown/concealment")))
      .toEqual(["@atlas/markdown/concealment", "@atlas/markdown/concealment/style.css"]);
  });
  it("keeps the optional assist module inside src/assist, loaded lazily (plan §0, §12 H.4)", () => {
    // Only src/assist reaches the module (and tests); the app reaches src/assist's editor code only through loadAssist.
    const users = [...used].filter(([name]) => name.startsWith("@atlas/markdown/assist"))
      .flatMap(([, files]) => files.map(file => file.split(sep).join("/")));
    expect(users.length).toBeGreaterThan(0);
    expect(users.filter(file => !file.startsWith("src/assist/") && !file.endsWith(".test.ts"))).toEqual([]);
    const app = files(sourceRoot).filter(file => !/\.test\.ts$/.test(file) && !file.split(sep).join("/").includes("/src/assist/"));
    const eager = /(?:^|\n)\s*import\s+(?!type\b)[^;]*?from\s+["'][^"']*\/assist\/(?:atlasAssist|index)["']|import\s+["'][^"']*\/assist\/atlasAssist["']/;
    expect(app.filter(file => eager.test(readFileSync(file, "utf8"))).map(file => relative(webRoot, file))).toEqual([]);
    const loader = readFileSync(resolve(sourceRoot, "assist/loadAssist.ts"), "utf8");
    expect(loader).toMatch(/await import\("@atlas\/markdown\/assist"\)/);
    expect(loader).toMatch(/await import\("\.\/atlasAssist"\)/);
    expect(loader).not.toMatch(/^import\s+(?!type\b)/m);
    // The stylesheet comes with the adapter, never with the app.
    expect(readFileSync(resolve(sourceRoot, "assist/atlasAssist.ts"), "utf8")).toContain('import "@atlas/markdown/assist/style.css"');
    // The module is a released entry (0.9.0): every mode resolves it to the editor, never to an Atlas stand-in.
    expect(editorSourceExports).toMatchObject({ "@atlas/markdown/assist": "src/assist/index.ts", "@atlas/markdown/assist/style.css": "src/assist/assist.css" });
  });
  it("type-checks package mode exactly as the bundler resolves it (tsconfig.json paths)", () => {
    // A script stand-in of an unreleased entry has its tsconfig path; no other editor specifier is redirected.
    const paths = readJson(resolve(webRoot, "tsconfig.json")).compilerOptions.paths as Record<string, string[]>;
    const standIns = Object.entries(unreleasedEditorFallbacks).filter(([name]) => !name.endsWith(".css"));
    for (const [name, path] of standIns) expect(paths[name], name).toEqual([`./${path}`]);
    expect(Object.keys(paths).filter(name => name.startsWith("@atlas/markdown"))).toEqual(standIns.map(([name]) => name));
  });
  it("maps the OpenAPI plugin and the concealment entry", () => {
    expect(editorSourceExports).toMatchObject({
      "@atlas/markdown": "src/index.ts",
      "@atlas/markdown/plugins/openapi": "src/plugins/openapi/index.ts",
      "@atlas/markdown/concealment": "src/concealment/index.ts",
      "@atlas/markdown/concealment/style.css": "src/concealment/style.css",
    });
  });

  it("lists only entries the editor package exports", () => {
    const installed = readJson(resolve(webRoot, "node_modules/@atlas/markdown/package.json"));
    const exported = exportedSpecifiers(installed);
    expect(Object.keys(editorSourceExports).filter(name => !exported.includes(name)),
      `not exported by @atlas/markdown@${installed.version}`).toEqual([]);
    expect(Object.keys(unreleasedEditorExports).filter(name => name in editorSourceExports)).toEqual([]);
    // A stand-in exists only for an unreleased entry, and its file exists.
    expect(Object.keys(unreleasedEditorFallbacks).filter(name => !(name in unreleasedEditorExports))).toEqual([]);
    expect(Object.values(unreleasedEditorFallbacks).filter(path => !existsSync(resolve(webRoot, path)))).toEqual([]);
  });

  // Atlas imports the editor's dist: a stale one would test and ship other code than src/Atlas.Markdown holds.
  it.skipIf(sourceMode)("imports a dist built from the current editor sources", () => {
    const stamp = resolve(editorPackageRoot, "dist", editorBuildStamp);
    const built = existsSync(stamp) ? readFileSync(stamp, "utf8").trim() : "no build";
    expect(built, "run pnpm run build:editor").toBe(editorInputHash(editorPackageRoot, resolve(webRoot, "pnpm-lock.yaml")));
  });
  it("shares one copy of every singleton with the editor package", () => {
    // A second Vue or ProseMirror would split reactivity, plugin keys and decorations between Atlas and the editor.
    const copy = (root: string, name: string) => realpathSync(resolve(root, "node_modules", name));
    const singletons = ["vue", "yjs", "y-protocols", "@tiptap/core", "@tiptap/pm", "@tiptap/vue-3", "@tiptap/y-tiptap"];
    // A pnpm install run inside src/Atlas.Markdown gives the editor a store of its own, and later workspace installs keep it.
    expect(singletons.filter(name => copy(webRoot, name) !== copy(editorPackageRoot, name)),
      "delete src/Atlas.Markdown/node_modules, then pnpm install --frozen-lockfile --force in src/Atlas.Web").toEqual([]);
  });

  // ATLAS_EDITOR_SOURCE may name another checkout; without one the in-repo editor is checked.
  it("maps each specifier to an existing file the editor publicly exports", () => {
    const exported = exportedSpecifiers(readJson(resolve(editor, "package.json")));
    const entries = editorSourceEntries(editor);
    const missing = Object.entries(entries).filter(([, path]) => !existsSync(resolve(editor, path)));
    expect(missing).toEqual([]);
    expect(Object.keys(entries).filter(name => !exported.includes(name))).toEqual([]);
    // Every rewritten specifier must still be what the editor source imports, and every target must exist.
    const code = editorRewriteDirectories
      .filter(directory => existsSync(resolve(editor, directory)))
      .flatMap(directory => files(resolve(editor, directory)))
      .filter(file => file.endsWith(".ts") && !file.endsWith(".test.ts"))
      .map(file => readFileSync(file, "utf8")).join("\n");
    const imported = [...code.matchAll(/from\s+'([^']+)'|import\s+'([^']+)'/g)].map(([, a, b]) => a ?? b ?? "");
    for (const { from, to } of editorSourceRewrites(editor)) {
      expect(imported.some(name => from.test(name)), String(from)).toBe(true);
      if (!to.includes("$1")) expect(existsSync(to.split("?", 1)[0]!), to).toBe(true);
    }
  });
});
