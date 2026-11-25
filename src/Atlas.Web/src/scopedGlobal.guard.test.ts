// @vitest-environment node
import { readdirSync, readFileSync } from "node:fs";
import { join, relative, resolve } from "node:path";
import { describe, expect, it } from "vitest";

/*
 * Guard of `:global()` in Vue styles (review 3, D3/D4): in a scoped block Vue keeps only what the `:global(...)` holds and drops the rest
 * of the selector, so `:global([data-atlas-reduced-motion]) .x { … }` styles `<html>` and never `.x`. Wrap the whole selector,
 * `:global([data-atlas-reduced-motion] .x)`, or leave the ancestor plain (`.board-node.is-selected .x`: an ancestor needs no scope).
 * In a block without `scoped` Vue does not compile `:global()` at all and the browser drops the rule.
 */

const sourceRoot = resolve(process.cwd(), "src");

function files(dir: string): string[] {
  return readdirSync(dir, { withFileTypes: true }).flatMap((entry) => {
    const path = join(dir, entry.name);
    if (entry.isDirectory()) return entry.name === "node_modules" ? [] : files(path);
    return entry.name.endsWith(".vue") ? [path] : [];
  });
}

/** Splits a selector list on its top-level commas (not those inside `:is(…)` or `:global(…)`). */
function selectors(list: string): string[] {
  const parts: string[] = [];
  let depth = 0, start = 0;
  for (let i = 0; i < list.length; i++) {
    if (list[i] === "(") depth++;
    else if (list[i] === ")") depth--;
    else if (list[i] === "," && depth === 0) { parts.push(list.slice(start, i)); start = i + 1; }
  }
  return [...parts, list.slice(start)].map(part => part.trim());
}

/** Whether the selector is one `:global(...)` and nothing else. */
function wholeGlobal(selector: string): boolean {
  if (!selector.startsWith(":global(")) return false;
  let depth = 0;
  for (let i = ":global".length; i < selector.length; i++) {
    if (selector[i] === "(") depth++;
    else if (selector[i] === ")" && --depth === 0) return i === selector.length - 1;
  }
  return false;
}

export function partialGlobals(source: string): string[] {
  const found: string[] = [];
  for (const block of source.matchAll(/<style\b([^>]*)>([\s\S]*?)<\/style>/g)) {
    const scoped = /\bscoped\b/.test(block[1]!);
    const css = block[2]!.replace(/\/\*[\s\S]*?\*\//g, "");
    for (const rule of css.matchAll(/([^{};]+)\{/g)) {
      const prelude = rule[1]!.trim();
      if (prelude.startsWith("@") || !prelude.includes(":global(")) continue;
      for (const selector of selectors(prelude))
        if (selector.includes(":global(") && (!scoped || !wholeGlobal(selector))) found.push(selector.replace(/\s+/g, " "));
    }
  }
  return found;
}

describe(":global() in Vue styles", () => {
  it("recognises the partial form and the unscoped block", () => {
    expect(partialGlobals("<style scoped>:global([a]) .x { b: 1 } .y :global(.z) { c: 1 }</style>")).toEqual([":global([a]) .x", ".y :global(.z)"]);
    expect(partialGlobals("<style scoped>.p, :global(:is(:root, .q)[a] .x) { b: 1 }</style>")).toEqual([]);
    expect(partialGlobals("<style>:global(.q .x) { b: 1 }</style>")).toEqual([":global(.q .x)"]);
  });

  it("wraps the whole selector in a scoped block", () => {
    const findings = files(sourceRoot).flatMap(file =>
      partialGlobals(readFileSync(file, "utf8")).map(selector => `${relative(sourceRoot, file).replaceAll("\\", "/")}: ${selector}`));
    expect(findings).toEqual([]);
  });
});
