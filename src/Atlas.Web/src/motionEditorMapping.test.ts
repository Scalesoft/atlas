// @vitest-environment node
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { describe, expect, it } from "vitest";

/*
 * Host remap contract of @atlas/markdown 0.10.0 (its README, "Motion tokens"; plan §4.8): every core motion token
 * the editor declares is either mapped onto an Atlas token in src/motion.css or deliberately left alone. A token a new
 * editor release adds fails here until it is mapped or listed with its reason.
 */

const root = process.cwd();
const editorCss = readFileSync(resolve(root, "node_modules/@atlas/markdown/dist/atlas-markdown.css"), "utf8");
const motionCss = readFileSync(resolve(root, "src/motion.css"), "utf8").replace(/\/\*[\s\S]*?\*\//g, "");

/** Not mapped, with the reason. */
const LEFT_ALONE: Record<string, string> = {
  "--md-motion-rise": "geometry of the editor; reduced motion zeroes it itself",
  "--md-motion-pop-from": "geometry of the editor; reduced motion resets it itself",
  "--md-motion-surface": "alias of --md-motion-target",
  "--md-motion-time-scale": "multiplies only the editor's JavaScript literals; Atlas slows its tokens directly",
};

const declared = (css: string, prefix: string) =>
  [...new Set([...css.matchAll(new RegExp(String.raw`(--md-${prefix}-[a-z0-9-]+)\s*:`, "g"))].map(match => match[1]!))].sort();

/** The mapping rule: the declaration block that carries `--md-motion-fast`, outside the reduced-motion rules. */
const mapping = motionCss.match(/:root,\s*\.atlas :where\(\.md-editor, \.md-renderer\)\s*\{([^}]*)\}/)?.[1] ?? "";

describe("the editor's motion tokens on the Atlas tokens", () => {
  const tokens = declared(editorCss, "motion");

  it("the editor declares its motion tokens", () => {
    expect(tokens.length).toBeGreaterThan(10);
  });

  it.each(tokens)("%s is mapped or left alone on purpose", (token) => {
    if (token in LEFT_ALONE) {
      expect(mapping, `${token} must stay unmapped`).not.toContain(`${token}:`);
    } else {
      expect(mapping, `${token} needs a mapping in src/motion.css`).toMatch(new RegExp(String.raw`${token}:\s*var\(--atlas-[a-z-]+\)`));
    }
  });

  it("the left-alone list has no stale entries", () => {
    for (const token of Object.keys(LEFT_ALONE)) expect(tokens).toContain(token);
  });

  it("the signature conceal timing is not remapped", () => {
    expect(mapping).not.toContain("--md-conceal-");
  });

  it("the mapping covers body-level surfaces and the editor scope", () => {
    expect(motionCss).toMatch(/:root,\s*\.atlas :where\(\.md-editor, \.md-renderer\)\s*\{[^}]*--md-motion-fast:/);
  });

  it("loops stand still under reduced motion: loop, spin and pulse are 0s", () => {
    const media = motionCss.match(/@media \(prefers-reduced-motion: reduce\)\s*\{\s*:root,\s*\.atlas :where\(\.md-editor, \.md-renderer\)\s*\{([^}]*)\}/)?.[1] ?? "";
    const preview = motionCss.match(/\[data-atlas-reduced-motion\],\s*\[data-atlas-reduced-motion\] \.atlas :where\(\.md-editor, \.md-renderer\)\s*\{([^}]*)\}/)?.[1] ?? "";
    for (const block of [media, preview]) {
      for (const token of ["loop", "spin", "pulse"]) expect(block).toMatch(new RegExp(String.raw`--md-motion-${token}:\s*0s`));
    }
  });
});
