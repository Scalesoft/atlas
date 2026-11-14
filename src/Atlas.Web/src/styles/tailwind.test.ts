import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { describe, expect, it } from "vitest";

// The build-output test (scripts/tailwind-output.test.mjs) can only see tokens
// the compiled bundle actually contains, and Tailwind v4 emits a `@theme` entry
// only when some scanned utility class needs it. Until components are migrated
// that is a handful of the map, so a typo or a self-reference in the rest would
// compile to exactly the same output as the correct version — silently. These
// assertions read the source instead, so the whole map is covered from day one.
// Read from the project root: under jsdom `import.meta.url` is not a file: URL.
// The token map lives in theme.css, imported by tailwind.css (and referenced by the lazy
// settings stylesheet); both are read so the checks cover the map wherever it is kept.
const tailwindCss =
  readFileSync(resolve(process.cwd(), "src/styles/tailwind.css"), "utf8") +
  readFileSync(resolve(process.cwd(), "src/styles/theme.css"), "utf8");
const shellCss = readFileSync(resolve(process.cwd(), "src/shell.css"), "utf8");
const paletteCss = readFileSync(resolve(process.cwd(), "src/styles/palettes.css"), "utf8");
// The editor mapping block (`.md-theme-midnight`) declares the editor's own names (--bg-1, --paper, ...), which are not design tokens.
const paletteTokenBlocks = paletteCss.replace(/^[^{}\n]*\.md-theme-midnight\s*\{[^}]*\}/m, "");
if (paletteTokenBlocks === paletteCss) throw new Error("no `.md-theme-midnight` block found in palettes.css");
const motionCss = readFileSync(resolve(process.cwd(), "src/motion.css"), "utf8");

function themeBlock(): string {
  const match = /@theme inline \{([\s\S]*?)\n\}/.exec(tailwindCss);
  if (!match) throw new Error("no `@theme inline` block found in tailwind.css");
  return match[1]!;
}

function themeEntries(): Map<string, string> {
  const entries = new Map<string, string>();
  for (const [, name, value] of themeBlock().matchAll(
    /^\s*--([a-z0-9-]+):\s*([^;]+);/gm,
  ))
    entries.set(name!, value!.trim());
  return entries;
}

// Names the generated shadcn components reference as utilities (bg-muted,
// text-accent-foreground, border-input, ring-ring, …). Tailwind silently drops
// a utility whose backing token is undefined: no error, no warning, no style.
const REQUIRED = [
  "color-background",
  "color-foreground",
  "color-card",
  "color-card-foreground",
  "color-popover",
  "color-popover-foreground",
  "color-primary",
  "color-primary-foreground",
  "color-secondary",
  "color-secondary-foreground",
  "color-muted",
  "color-muted-foreground",
  "color-accent",
  "color-accent-foreground",
  "color-destructive",
  "color-border",
  "color-input",
  "color-ring",
  // Status tones (StatusBadge, settings UI): amber warning is not the error red.
  "color-success",
  "color-success-soft",
  "color-warning",
  "color-warning-soft",
  "color-info",
  "color-info-soft",
];

describe("the @theme token map", () => {
  it("defines every token the generated components reference", () => {
    const defined = themeEntries();
    const missing = REQUIRED.filter((name) => !defined.has(name));
    expect(missing).toEqual([]);
  });

  it("never defines a token in terms of itself", () => {
    const cycles = [...themeEntries()]
      .filter(([name, value]) => value === `var(--${name})`)
      .map(([name]) => name);
    expect(cycles).toEqual([]);
  });

  it("only points at app tokens that shell.css or palettes.css actually declares", () => {
    // Catches a typo such as `var(--canvass)`, which would otherwise resolve to
    // nothing and render the utility invisible rather than failing loudly.
    // The colour tokens live in palettes.css (shell.css keeps only the unthemed fallback and the dimensions);
    // the motion defaults point at the motion tokens of motion.css, which is the other token source.
    const declared = new Set(
      [...shellCss.matchAll(/^\s*(--[a-z0-9-]+):/gm), ...paletteTokenBlocks.matchAll(/^\s*(--[a-z0-9-]+):/gm), ...motionCss.matchAll(/^\s*(--[a-z0-9-]+):/gm)].map(([, name]) => name!),
    );
    const dangling: string[] = [];
    for (const [name, value] of themeEntries())
      for (const [, referenced] of value.matchAll(/var\((--[a-z0-9-]+)\)/g))
        if (!declared.has(referenced!)) dangling.push(`${name} -> ${referenced}`);
    expect(dangling).toEqual([]);
  });

  it("keeps warning amber and separate from error in light and dark", () => {
    const entries = themeEntries();
    expect(entries.get("color-warning")).toBe("var(--warning)");
    expect(entries.get("color-warning-soft")).toBe("var(--warning-bg)");
    const blocks = [
      /^:root:not\(\[data-mode="dark"\]\),\s*\.atlas\[data-mode="light"\] \{([\s\S]*?)\n\}/m.exec(paletteCss)?.[1] ?? "",
      /^:root\[data-mode="dark"\],\s*\.atlas\[data-mode="dark"\] \{([\s\S]*?)\n\}/m.exec(paletteCss)?.[1] ?? "",
    ];
    for (const block of blocks)
      for (const token of ["--warning", "--warning-bg", "--info", "--info-bg"])
        // A colour of its own, not a reference to another token; the dark tints are oklch since 4cd308f2a (dark in calm greys).
        expect(block, token).toMatch(new RegExp(`^\\s*${token}:\\s*(?:#[0-9a-f]{6}|oklch\\([^)]*\\));`, "m"));
  });

  it("does not confuse the app's brand accent with shadcn's subtle accent", () => {
    // The app's `--accent` is the brand colour (blue/green) and belongs on
    // shadcn's `primary`. shadcn's own `accent` is the subtle hover fill, which
    // in this app is `--hover`. Swapping them paints menu hovers brand-blue.
    const entries = themeEntries();
    expect(entries.get("color-primary")).toBe("var(--accent)");
    expect(entries.get("color-accent")).toBe("var(--hover)");
  });
});
