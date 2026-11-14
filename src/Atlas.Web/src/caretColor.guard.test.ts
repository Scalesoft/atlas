// @vitest-environment node
import { readdirSync, readFileSync } from "node:fs";
import { join, relative, resolve } from "node:path";
import { describe, expect, it } from "vitest";

/*
 * Guard of the caret colour: the native caret takes `color` of the field, so an editable surface that inherits a light
 * (dark-mode) or inverted text colour draws a white caret on a white field. The app therefore sets the caret from the
 * text token in one place (styles/refresh-theme.css) and no component writes a caret colour of its own, except from a token.
 */

const sourceRoot = resolve(process.cwd(), "src");

function files(dir: string): string[] {
  return readdirSync(dir, { withFileTypes: true }).flatMap((entry) => {
    const path = join(dir, entry.name);
    if (entry.isDirectory()) return entry.name === "node_modules" ? [] : files(path);
    return /\.(vue|css)$/.test(entry.name) ? [path] : [];
  });
}

/** What a caret may be: a theme token (not the accent's ink, which is white on the accent), or the keywords that follow the text. */
const ALLOWED = /^(?:var\(--(?:text|muted|foreground)\)|currentColor|inherit|auto)$/i;
const DECLARATION = /(?<![\w-])caret-color\s*:\s*([^;}"\n]+)/g;

describe("caret colour", () => {
  it("is never a literal or an inverted token anywhere in the app", () => {
    const findings: string[] = [];
    for (const file of files(sourceRoot)) {
      const text = readFileSync(file, "utf8").replace(/\/\*[\s\S]*?\*\//g, (m) => m.replace(/[^\n]/g, " "));
      for (const match of text.matchAll(DECLARATION)) {
        const value = match[1]!.trim().replace(/\s*!important$/, "");
        if (!ALLOWED.test(value)) findings.push(`${relative(sourceRoot, file).replaceAll("\\", "/")}: caret-color: ${value}`);
      }
    }
    expect(findings).toEqual([]);
  });

  it("is set from the text token on every editable surface (fields, textareas, the page editor and everything built on it)", () => {
    const theme = readFileSync(join(sourceRoot, "styles/refresh-theme.css"), "utf8").replace(/\/\*[\s\S]*?\*\//g, "");
    const rule = theme.match(/([^{}]*)\{[^{}]*caret-color\s*:\s*var\(--text\)[^{}]*\}/);
    expect(rule, "styles/refresh-theme.css needs a caret-color: var(--text) rule").not.toBeNull();
    for (const selector of ["input", "textarea", '[contenteditable="true"]', ".ProseMirror"]) expect(rule![1]).toContain(selector);
  });
});
