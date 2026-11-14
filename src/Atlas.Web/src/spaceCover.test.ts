// @vitest-environment node
import { describe, expect, it, vi } from "vitest";
import { api } from "./api";
import { spaceCoverApi, spaceCoverSrc, type SpaceCover } from "./spaceCover";

vi.mock("./api", async (original) => ({ ...await original<typeof import("./api")>(), api: vi.fn() }));

const cover = (value: Partial<SpaceCover>): SpaceCover => ({ source: "Auto", catalogKey: null, version: null, tinted: true, ...value });

describe("space cover", () => {
  it("resolves the image of an upload and of a known drawing, and nothing where a space has no image", () => {
    expect(spaceCoverSrc("space 1", cover({ source: "Upload", version: "a1" }))).toBe("/api/spaces/space%201/cover?v=a1");
    expect(spaceCoverSrc("space-1", cover({ source: "Catalog", catalogKey: "it-backup" }))).toContain("it-backup.svg");
    expect(spaceCoverSrc("space-1", cover({ catalogKey: "it-database" }))).toContain("it-database.svg");
    // An older server sent { version, tinted } without a source: no image rather than a broken one.
    const legacy = { version: "a1", tinted: true } as unknown as SpaceCover;
    for (const none of [cover({ source: "None" }), cover({}), cover({ source: "Catalog", catalogKey: "no-such-drawing" }), null, undefined, legacy]) {
      expect(spaceCoverSrc("space-1", none)).toBeUndefined();
    }
  });

  it("sends a choice and a match the way the server reads them", async () => {
    const chosen = cover({ source: "Catalog", catalogKey: "it-backup" });
    vi.mocked(api).mockResolvedValueOnce(chosen).mockResolvedValueOnce({ catalogKey: "it-backup" });
    expect(await spaceCoverApi.choose("space 1", { source: "Catalog", catalogKey: "it-backup" })).toBe(chosen);
    expect(api).toHaveBeenLastCalledWith("/spaces/space%201/cover/choice", { method: "PUT", body: '{"source":"Catalog","catalogKey":"it-backup"}' });
    const signal = new AbortController().signal;
    expect(await spaceCoverApi.match({ name: "Zálohy", description: "", icon: "book-open" }, signal)).toEqual({ catalogKey: "it-backup" });
    expect(api).toHaveBeenLastCalledWith("/space-covers/match", { method: "POST", body: '{"name":"Zálohy","description":"","icon":"book-open"}', signal });
  });
});
