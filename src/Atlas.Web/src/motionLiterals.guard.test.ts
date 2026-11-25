// @vitest-environment node
import { readdirSync, readFileSync } from "node:fs";
import { join, relative, resolve } from "node:path";
import { describe, expect, it } from "vitest";
import { MOTION_LITERAL_ALLOWLIST, MOTION_LITERAL_BASELINE } from "./motionLiterals.baseline";

/*
 * Guard of the motion unification:
 * components write no durations, delays or easings of their own and no @keyframes; they use the tokens of
 * src/motion.css and the primitives of src/motion/aiMotion.ts. The sources (motion.css, styles/ai.css and its lazy part styles/aiBusy.css) are
 * exempt; an exception that stays (keep: funkční / smyčka práce / podpis) is in the allowlist with its reason.
 *
 * Everything that existed when the guard came (M0) is frozen in the baseline. Packages M1–M4 convert it and
 * delete their lines from the baseline: the test fails on a new finding and also on a baseline line that no
 * longer exists, so the baseline can only shrink.
 */

const root = process.cwd();
const sourceRoot = resolve(root, "src");
const SOURCES = new Set(["src/motion.css", "src/styles/ai.css", "src/styles/aiBusy.css"]);

export interface Finding {
  file: string;
  line: number;
  /** What the baseline and the allowlist match: `@keyframes name`, or the declaration with collapsed spaces. */
  text: string;
}

function files(dir: string): string[] {
  return readdirSync(dir, { withFileTypes: true }).flatMap((entry) => {
    const path = join(dir, entry.name);
    if (entry.isDirectory()) return entry.name === "node_modules" ? [] : files(path);
    return /\.(vue|css)$/.test(entry.name) ? [path] : [];
  });
}

/** Replaces a match with spaces of the same shape, so line numbers stay right. */
const blank = (text: string) => text.replace(/[^\n]/g, " ");

const DURATION = /(?<![\w.-])\d*\.?\d+m?s(?![\w-])/;
const CURVE = /cubic-bezier\(|(?<![\w-])(?:ease|ease-in|ease-out|ease-in-out)(?![\w-])/;
const DECLARATION = /(?<![\w-])((?:transition|animation)(?:-[a-z]+)*)\s*:\s*([^;{}"]+)/g;
const KEYFRAMES = /@keyframes\s+([\w-]+)/g;
/** An inline custom property that carries a delay or duration, e.g. `:style="{ '--x-delay': `${i * 12}ms` }"`. */
const INLINE_TIMING = /['"](--[\w-]*(?:delay|duration)[\w-]*)['"]\s*:\s*`([^`]*?m?s)`/g;

function lineAt(text: string, index: number) {
  return text.slice(0, index).split("\n").length;
}

function scanCss(file: string, css: string, findings: Finding[]) {
  const text = css.replace(/\/\*[\s\S]*?\*\//g, blank);
  for (const match of text.matchAll(KEYFRAMES)) findings.push({ file, line: lineAt(text, match.index!), text: `@keyframes ${match[1]}` });
  for (const match of text.matchAll(DECLARATION)) {
    const value = match[2]!.trim();
    if (!DURATION.test(value) && !CURVE.test(value)) continue;
    findings.push({ file, line: lineAt(text, match.index!), text: `${match[1]}: ${value.replace(/\s+/g, " ")}` });
  }
}

/** Every literal duration, delay, easing and @keyframes outside the motion sources. */
export function scanMotionLiterals(): Finding[] {
  const findings: Finding[] = [];
  for (const path of files(sourceRoot).sort()) {
    const file = relative(root, path).replace(/\\/g, "/");
    if (SOURCES.has(file)) continue;
    const content = readFileSync(path, "utf8");
    if (file.endsWith(".css")) {
      scanCss(file, content, findings);
      continue;
    }
    // A Vue file: the <style> blocks as CSS, the template's style bindings; scripts are TypeScript.
    const withoutScripts = content.replace(/<script\b[\s\S]*?<\/script>/g, blank).replace(/<!--[\s\S]*?-->/g, blank);
    const styles = withoutScripts.replace(/<style\b[^>]*>([\s\S]*?)<\/style>|[\s\S]/g, (all, css: string | undefined) => (css === undefined ? blank(all) : `${blank(all.slice(0, all.indexOf(css)))}${css}${blank(all.slice(all.indexOf(css) + css.length))}`));
    scanCss(file, styles, findings);
    const template = withoutScripts.replace(/<style\b[\s\S]*?<\/style>/g, blank);
    for (const attribute of template.matchAll(/(?<![\w-]):?style="([^"]*)"/g)) {
      const offset = attribute.index! + attribute[0].indexOf(attribute[1]!);
      const value = attribute[1]!;
      for (const match of value.matchAll(DECLARATION)) {
        const declared = match[2]!.trim();
        if (DURATION.test(declared) || CURVE.test(declared)) findings.push({ file, line: lineAt(template, offset + match.index!), text: `${match[1]}: ${declared.replace(/\s+/g, " ")}` });
      }
      for (const match of value.matchAll(INLINE_TIMING)) findings.push({ file, line: lineAt(template, offset + match.index!), text: `${match[1]}: \`${match[2]}\`` });
    }
  }
  return findings;
}

const key = (finding: Pick<Finding, "file" | "text">) => `${finding.file} :: ${finding.text}`;

/** `a` minus `b`, counting duplicates (one line may repeat a declaration). */
function difference(a: string[], b: string[]) {
  const left = new Map<string, number>();
  for (const item of b) left.set(item, (left.get(item) ?? 0) + 1);
  return a.filter((item) => {
    const count = left.get(item) ?? 0;
    if (count) left.set(item, count - 1);
    return !count;
  });
}

describe("motion literals", () => {
  const findings = scanMotionLiterals();
  const allowed = (finding: Finding) => MOTION_LITERAL_ALLOWLIST.some((entry) => entry.file === finding.file && finding.text.includes(entry.match));
  const counted = findings.filter((finding) => !allowed(finding));

  it("adds no literal duration, easing or @keyframes outside motion.css and styles/ai.css", () => {
    const added = difference(counted.map(key), MOTION_LITERAL_BASELINE);
    const where = added.map((item) => {
      const finding = counted.find((candidate) => key(candidate) === item)!;
      return `${finding.file}:${finding.line}  ${finding.text}`;
    });
    expect(where, "Use the motion.css tokens and the aiMotion.ts primitives; a deliberate exception goes to MOTION_LITERAL_ALLOWLIST with its reason").toEqual([]);
  });

  it("only shrinks the frozen baseline: a converted literal leaves it", () => {
    const stale = difference(MOTION_LITERAL_BASELINE, counted.map(key));
    expect(stale, "Delete these lines from MOTION_LITERAL_BASELINE (src/motionLiterals.baseline.ts)").toEqual([]);
  });

  it("keeps every allowlisted exception reasoned and in use", () => {
    for (const entry of MOTION_LITERAL_ALLOWLIST) {
      expect(entry.reason.trim().length, `${entry.file} ${entry.match}`).toBeGreaterThan(10);
      expect(findings.some((finding) => finding.file === entry.file && finding.text.includes(entry.match)), `${entry.file} ${entry.match}`).toBe(true);
    }
  });

  it("finds the kinds of literal it guards against", () => {
    // The scanner itself: a regression here would silently empty the guard.
    const sample: Finding[] = [];
    scanCss("sample.css", [
      ".a { transition: opacity 120ms ease; }",
      ".b { animation: x var(--atlas-motion-base) cubic-bezier(.2,.6,.2,1); }",
      ".c { animation-delay: .15s; }",
      ".d { transition: opacity var(--atlas-motion-fast) var(--atlas-ease-out); }",
      "/* .e { transition: 1s; } */",
      "@keyframes local-thing { to { opacity: 1; } }",
    ].join("\n"), sample);
    expect(sample.map((finding) => [finding.line, finding.text])).toEqual([
      [6, "@keyframes local-thing"],
      [1, "transition: opacity 120ms ease"],
      [2, "animation: x var(--atlas-motion-base) cubic-bezier(.2,.6,.2,1)"],
      [3, "animation-delay: .15s"],
    ]);
  });
});
