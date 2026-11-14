import { afterEach, describe, expect, it, vi } from "vitest";
import platform from "../../../../tests/fixtures/content/platform-manifest.json";
import { objectCardViews } from "../boards/objectViews";
import { kindInfo, relationKinds, relationsApi, typeInfo, usageApi } from "./relationsApi";

afterEach(() => { vi.unstubAllGlobals(); });

describe("the registries the server declares (tests/fixtures/content/platform-manifest.json, PlatformManifestTests)", () => {
  it("knows every relation kind and the end it uses", () => {
    expect([...relationKinds].sort()).toEqual(platform.relationKinds.map(entry => entry.kind));
    for (const { kind, used } of platform.relationKinds) expect(kindInfo[kind as keyof typeof kindInfo].used, kind).toBe(used);
  });
  it("names every object type and draws it on a board", () => {
    expect(platform.objectTypes.filter(id => typeInfo(id).icon === "box"), "Add the type to typeInfos in relationsApi.ts").toEqual([]);
    // A type with no card of its own, and why.
    const without: Record<string, string> = {
      "atlas.page": "a page is a page card (PageCard.vue)",
      "atlas.board": "a board is a card that opens it (isBoardCard)",
      "atlas.note": "a private note is drawn as its title until it has a card",
    };
    expect(platform.objectTypes.filter(id => !objectCardViews[id] && !without[id]), "Add the card to boards/objectViews.ts").toEqual([]);
  });
});

describe("reading relations", () => {
  const edge = (kind: string) => ({ sourceId: "a", targetId: "b", kind, count: 1 });
  it("skips a relation kind the web does not know yet, in the graph and in the uses", async () => {
    vi.stubGlobal("fetch", vi.fn(async (url: string) => Response.json(url.includes("/usage/")
      ? { objectId: "b", items: [{ placeId: "a", placeTypeId: "atlas.page", placeTitle: "A", spaceId: null, spaceName: null, kind: "attendee", count: 1, draft: false },
        { placeId: "a", placeTypeId: "atlas.page", placeTitle: "A", spaceId: null, spaceName: null, kind: "link", count: 1, draft: false }], total: 2, hidden: 0, nextCursor: null }
      : { nodes: [], edges: [edge("attendee"), edge("link")] })));
    expect((await relationsApi.relations("b", 1))!.edges.map(item => item.kind)).toEqual(["link"]);
    expect((await usageApi.usage("b"))!.items.map(item => item.kind)).toEqual(["link"]);
  });
});
