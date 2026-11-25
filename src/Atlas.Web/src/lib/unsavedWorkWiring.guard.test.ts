// @vitest-environment node
import { readdirSync, readFileSync } from "node:fs";
import { join } from "node:path";
import { describe, expect, it } from "vitest";

// unsavedWork.ts only keeps the work; the shell must ask about it before the tab closes (hasUnsavedWork) and drop it when the
// account's session ends (forgetUnsavedWork). Both once had no caller: work went silently and drafts outlived the account.
describe("unsaved work outside the page's editor", () => {
  it("is asked about and forgotten by a caller outside lib/", () => {
    const src = join(import.meta.dirname, "..");
    const sources: string[] = [];
    const walk = (dir: string) => {
      for (const entry of readdirSync(dir, { withFileTypes: true })) {
        const path = join(dir, entry.name);
        if (entry.isDirectory()) { if (!["lib", "dev", "node_modules"].includes(entry.name)) walk(path); }
        else if (/\.(vue|ts)$/.test(entry.name) && !/\.test\.ts$/.test(entry.name)) sources.push(readFileSync(path, "utf8"));
      }
    };
    walk(src);
    for (const name of ["hasUnsavedWork", "forgetUnsavedWork"])
      expect(sources.some(text => new RegExp(`\\b${name}\\(`).test(text)), `${name}() has no caller outside lib/`).toBe(true);
  });
});
