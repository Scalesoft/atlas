// @vitest-environment node
import { readdirSync, readFileSync, statSync } from "node:fs";
import { join, relative, sep } from "node:path";
import { expect, it } from "vitest";

// The barrel re-exports the intent rules and the link phrases; code outside assist/ that imports it carries them into
// the first screen (PERF1-3: about 5 KiB gzip). Import the module you need instead (secretDetector, examples, span).
const src = join(__dirname, "..", "..");
function files(dir: string): string[] {
  return readdirSync(dir).flatMap((name) => {
    const path = join(dir, name);
    return statSync(path).isDirectory() ? (name === "node_modules" ? [] : files(path)) : /\.(ts|vue)$/.test(name) && !/\.test\.ts$/.test(name) ? [path] : [];
  });
}

it("lets no code outside assist/ import the rules barrel", () => {
  const offenders = files(src)
    .filter((file) => !relative(src, file).startsWith(`assist${sep}`) && !relative(src, file).includes(`components${sep}dev${sep}`))
    .filter((file) => /from\s+["'][./]*(?:\.\.\/)*(?:\.\/)?assist\/rules["']/.test(readFileSync(file, "utf8")))
    .map((file) => relative(src, file));
  expect(offenders).toEqual([]);
});
