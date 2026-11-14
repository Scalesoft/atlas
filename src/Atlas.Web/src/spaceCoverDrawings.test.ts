// @vitest-environment node
import { describe, expect, it } from "vitest";
import catalog from "./assets/space-covers/catalog.json";
import { spaceCoverDrawingKeys, spaceCoverDrawingUrl } from "./spaceCoverDrawings";

describe("space cover drawings", () => {
  // An entry without a drawing would be an empty tile in the gallery, a drawing without an entry could never be chosen,
  // and a data URL here means the drawing would be inlined into the script.
  it("ships one drawing per catalog entry, each under a URL of its own", () => {
    expect([...spaceCoverDrawingKeys].sort()).toEqual(catalog.map((entry) => entry.id).sort());
    const urls = spaceCoverDrawingKeys.map((key) => spaceCoverDrawingUrl(key)!);
    expect(new Set(urls).size).toBe(urls.length);
    expect(urls.filter((url) => url.startsWith("data:"))).toEqual([]);
    expect(spaceCoverDrawingUrl("it-backup")).toContain("it-backup.svg");
    expect(spaceCoverDrawingUrl("no-such-drawing")).toBeUndefined();
    expect(spaceCoverDrawingUrl(null)).toBeUndefined();
  });
});
