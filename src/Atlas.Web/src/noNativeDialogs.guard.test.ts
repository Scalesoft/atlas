import { readdirSync, readFileSync } from "node:fs";
import { join, resolve } from "node:path";
import { describe, expect, it } from "vitest";

/* No window.confirm / alert / prompt in the web app: a question goes through the house dialogs (ConfirmDialog, UnsavedChangesDialog). */
function files(dir: string): string[] {
  return readdirSync(dir, { withFileTypes: true }).flatMap((entry) => {
    const path = join(dir, entry.name);
    if (entry.isDirectory()) return entry.name === "node_modules" || entry.name === "dist" ? [] : files(path);
    return /\.(vue|ts)$/.test(entry.name) && !/\.(test|spec)\.ts$/.test(entry.name) ? [path] : [];
  });
}

describe("native browser dialogs", () => {
  it("are never opened by the app", () => {
    const findings = files(resolve(process.cwd(), "src")).filter((file) => /\bwindow\.(confirm|alert|prompt)\s*\(/.test(readFileSync(file, "utf8").replace(/\/\/.*$|\/\*[\s\S]*?\*\//gm, "")));
    expect(findings).toEqual([]);
  });
});
