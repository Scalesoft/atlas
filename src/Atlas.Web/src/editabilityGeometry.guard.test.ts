import { readdirSync, readFileSync } from "node:fs";
import { join, relative, resolve } from "node:path";
import { describe, expect, it } from "vitest";

/*
 * Guard of the one-editor contract: a reader's page has the
 * writer's geometry, hidden affordances hold their place and a change of the right to write moves no line. So no rule that applies
 * only to an editable or only to a read-only editor may set a box measure; paint (colour, opacity, visibility, cursor) and positions
 * of absolutely placed affordances may differ.
 */

const web = resolve(process.cwd(), "src");
const markdown = resolve(process.cwd(), "../Atlas.Markdown/src");

function files(dir: string): string[] {
  return readdirSync(dir, { withFileTypes: true }).flatMap(entry => {
    const path = join(dir, entry.name);
    if (entry.isDirectory()) return entry.name === "node_modules" || entry.name === "dist" ? [] : files(path);
    return /\.(vue|css)$/.test(entry.name) ? [path] : [];
  });
}

const EDITABILITY = /contenteditable|\.readonly(?![\w-])|\.ProseMirror-focused/;
const BOX = /(?:^|[;{\s])((?:margin|padding)(?:-[a-z-]+)?|(?:min-|max-)?(?:height|width)|block-size|inline-size|font-size|line-height|font|content-visibility|contain-intrinsic-[a-z-]+)\s*:/;
/** Rules that may stay, with the reason. */
const ALLOWED: Record<string, string> = {
  // A board's text card is not a page: while edited, the block handle's gutter hangs out of the card to its left; margin and padding
  // cancel out, so the text does not move (253bf72f1).
  ".board-text.board-text.md-editor.paper:not(.readonly):not(.is-inline)": "board text card: editing gutter outside the card, text in place",
  ".board-text.board-text.md-editor.paper:not(.readonly):not(.is-inline) .paper-inner": "board text card: editing gutter outside the card, text in place",
};

describe("editability and geometry (J7.1)", () => {
  it("no rule for only the editable or only the read-only editor sets a box measure", () => {
    const findings: string[] = [];
    for (const file of [...files(markdown), ...files(web)]) {
      if (/\.(test|spec)\./.test(file)) continue;
      let text = readFileSync(file, "utf8").replace(/\/\*[\s\S]*?\*\//g, "");
      if (file.endsWith(".vue")) text = [...text.matchAll(/<style[^>]*>([\s\S]*?)<\/style>/g)].map(match => match[1]).join("\n");
      for (const [, selectors, body] of text.matchAll(/([^{}]+)\{([^{}]*)\}/g)) {
        const selector = selectors!.trim().replace(/\s+/g, " ");
        if (!EDITABILITY.test(selector) || !BOX.test(` ${body}`)) continue;
        if (selector.split(",").every(part => ALLOWED[part.trim()])) continue;
        // An editor without the paper is a form field (boards, dialogs, comments), not a page: its grip gutter is its own.
        if (selector.includes(":not(.paper)")) continue;
        findings.push(`${relative(resolve(process.cwd(), ".."), file).replaceAll("\\", "/")}: ${selector}`);
      }
    }
    expect(findings).toEqual([]);
  });
});
