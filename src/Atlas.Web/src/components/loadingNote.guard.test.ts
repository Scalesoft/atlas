import { readdirSync, readFileSync } from "node:fs";
import { join, resolve } from "node:path";
import { describe, expect, it } from "vitest";

/* A spinner line (role="status" with an atlas-spinner inside) is LoadingNote: it reserves room, fades in after a delay and
   stands still under reduced motion. A hand-written copy drifts from that. Baseline: PageShareLinks (moves with its owner in 3D). */
const BASELINE = ["sharing/PageShareLinks.vue"];
function files(dir: string): string[] {
  return readdirSync(dir, { withFileTypes: true }).flatMap((entry) => {
    const path = join(dir, entry.name);
    if (entry.isDirectory()) return entry.name === "node_modules" ? [] : files(path);
    return entry.name.endsWith(".vue") && entry.name !== "LoadingNote.vue" ? [path] : [];
  });
}
const handWritten = /role="status"[^>]*>\s*<AtlasIcon[^>]*atlas-spinner/;

describe("spinner status lines", () => {
  it("are LoadingNote, never hand-written", () => {
    const root = resolve(process.cwd(), "src/components");
    const findings = files(root).filter((file) => handWritten.test(readFileSync(file, "utf8"))).map((file) => file.slice(root.length + 1).replaceAll("\\", "/")).filter((file) => !BASELINE.includes(file));
    expect(findings, "use LoadingNote (settings/LoadingNote.vue) instead of a hand-written spinner line").toEqual([]);
  });
});
