// @vitest-environment node
import { readdirSync, readFileSync } from "node:fs";
import { join } from "node:path";
import { describe, expect, it } from "vitest";

describe("hand-rolled lazy components", () => {
  it("leaves no defineAsyncComponent outside lazyComponent and the gallery", () => {
    const root = join(process.cwd(), "src");
    const offenders: string[] = [];
    const walk = (dir: string) => {
      for (const entry of readdirSync(dir, { withFileTypes: true })) {
        const path = join(dir, entry.name);
        if (entry.isDirectory()) { if (entry.name !== "dev" && entry.name !== "node_modules") walk(path); }
        else if (/\.(vue|ts)$/.test(entry.name) && !/\.test\.ts$/.test(entry.name) && entry.name !== "lazyComponent.ts" && /\bdefineAsyncComponent\(/.test(readFileSync(path, "utf8"))) offenders.push(path);
      }
    };
    walk(root);
    expect(offenders).toEqual([]);
  });
});
