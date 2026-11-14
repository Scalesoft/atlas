import { readdirSync, readFileSync } from "node:fs";
import { join, relative, resolve } from "node:path";
import { describe, expect, it } from "vitest";

/*
 * Guard of page-wide restyles (3.6 perf F5): Chrome keeps one invalidation set for every `:has()` rule of the page, so a `:has()`
 * anchored on an element that holds the page editor (the workspace, the editor's root and surface) made every change inside the
 * editor restyle all lists and tables of the page (~1 100 elements per keystroke). The state goes on that element as a class or an
 * attribute where it changes instead. Allowed: a `:has()` anchored inside a block (a table, a heading), and one that looks only at the
 * element's own children (`:has(> .x)`), which Chrome checks on a change of those children alone.
 */

const roots = [resolve(process.cwd(), "src"), resolve(process.cwd(), "../Atlas.Markdown/src")];
const EDITOR_ANCESTORS = ["workspace", "workspace-content", "workspace-body", "document-body", "document-editor-surface", "atlas-document-editor", "md-editor", "editor-body", "paper", "paper-inner", "ProseMirror"];

function files(dir: string): string[] {
  return readdirSync(dir, { withFileTypes: true }).flatMap((entry) => {
    const path = join(dir, entry.name);
    if (entry.isDirectory()) return entry.name === "node_modules" || entry.name === "dist" ? [] : files(path);
    return /\.(vue|css)$/.test(entry.name) ? [path] : [];
  });
}

describe(":has() over the page editor", () => {
  it("is never anchored on an element that holds the editor", () => {
    const findings: string[] = [];
    // The compound right before `:has(`, e.g. `.md-editor:not(.x)` in `.md-editor:not(.x):has(...)`.
    const anchored = /([\w.#\-[\]="':()]*?):has\((?!\s*>\s*[\w.#\-[\]="':]+\s*\))/g;
    for (const root of roots) for (const file of files(root)) {
      const text = readFileSync(file, "utf8").replace(/\/\*[\s\S]*?\*\//g, "");
      for (const match of text.matchAll(anchored)) {
        const compound = match[1]!.replace(/:not\([^)]*\)/g, "");
        const classes = [...compound.matchAll(/\.([\w-]+)/g)].map(found => found[1]!);
        if (classes.some(name => EDITOR_ANCESTORS.includes(name)) || /(^|[^\w-])main(?![\w-])/.test(compound))
          findings.push(`${relative(resolve(process.cwd(), ".."), file).replaceAll("\\", "/")}: ${match[0]}`);
      }
    }
    expect(findings).toEqual([]);
  });
});
