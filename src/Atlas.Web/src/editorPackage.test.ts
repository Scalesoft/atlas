import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join } from "node:path";
import { afterEach, describe, expect, it } from "vitest";
import { editorInputFiles, editorInputHash, missingAssets } from "../scripts/editorPackage.mjs";

describe("editor package verification", () => {
  let root: string | undefined;
  afterEach(() => { if (root) rmSync(root, { recursive: true, force: true }); });
  const write = (files: Record<string, string>) => {
    for (const [path, text] of Object.entries(files)) {
      mkdirSync(dirname(join(root!, path)), { recursive: true });
      writeFileSync(join(root!, path), text);
    }
  };

  it("builds the editor from its sources and configuration, not from tests, documentation or outputs", () => {
    root = mkdtempSync(join(tmpdir(), "atlas-editor-"));
    const editor = join(root, "editor");
    const lockfile = join(root, "pnpm-lock.yaml");
    write({
      "pnpm-lock.yaml": "lockfileVersion: '9.0'\n",
      "editor/package.json": "{}", "editor/vite.config.ts": "", "editor/tsconfig.build.json": "{}", "editor/src/index.ts": "",
      "editor/src/vue/Editor.vue": "", "editor/schema/markdown-schema.json": "{}", "editor/src/index.test.ts": "", "editor/README.md": "",
      "editor/src/plugins/toc/README.md": "", "editor/tests/contracts/architecture.mjs": "", "editor/dist/index.js": "",
      "editor/node_modules/vue/index.js": "", "editor/src/node_modules/x.js": "", "editor/obj/project.assets.json": "{}", "editor/vitest.setup.ts": "",
    });
    expect(editorInputFiles(editor)).toEqual(["package.json", "schema/markdown-schema.json", "src/index.ts", "src/vue/Editor.vue", "tsconfig.build.json", "vite.config.ts"]);

    const built = editorInputHash(editor, lockfile);
    write({ "editor/src/index.test.ts": "changed", "editor/dist/index.js": "changed", "editor/obj/project.assets.json": "changed" });
    expect(editorInputHash(editor, lockfile)).toBe(built);
    write({ "editor/src/index.ts": "changed" });
    const changed = editorInputHash(editor, lockfile);
    expect(changed).not.toBe(built);
    write({ "pnpm-lock.yaml": "lockfileVersion: '9.0'\n# another resolution\n" });
    expect(editorInputHash(editor, lockfile)).not.toBe(changed);
  });

  it("finds the Scalar bundle, its frame script and the three Monaco workers in package and source builds", () => {
    const packaged = ["scalar-standalone-CMkGv8G--CMkGv8G-.js", "scalarFrame-BXp2YcQ1.js", "editor.worker-CmawOYbg-D_J60jWM.js", "json.worker-Cyx8GFRT-CgRY1D0Y.js", "yaml.worker-DesFrTa1-BjQl_X_i.js"];
    const source = ["standalone-CMkGv8G-.js", "scalarFrame-D2rJk8aL.js", "editor.worker-CBY86rJS.js", "json.worker-DnNLsSL2.js", "yaml.worker-CI1_dCTi.js"];
    expect(missingAssets(["index-uBHD7TA5.js", ...packaged])).toEqual([]);
    expect(missingAssets(source)).toEqual([]);
    expect(missingAssets(["index-uBHD7TA5.js", "json.worker-DnNLsSL2.js"])).toEqual([
      "Scalar standalone bundle",
      "OpenAPI reference frame script",
      "Monaco editor worker",
      "Monaco YAML worker",
    ]);
  });
});
