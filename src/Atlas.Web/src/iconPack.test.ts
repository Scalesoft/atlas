import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { iconPackPart, iconPackPartCount } from "./iconPackPart";

// The generated files are replaced by small mocks: the cases below hold for any pack. The factories are registered
// again for every case, because a factory result outlives vi.resetModules() while a throwing one runs on each import.
const pack = { markupLoads: 0, markupFailures: 0 };

// Every case starts from a pack that was never requested (module state is the cache).
const load = async () => ({ ...(await import("./iconPack")), ...(await import("./lazyComponent")) });

beforeEach(() => {
  vi.resetModules();
  pack.markupLoads = 0;
  pack.markupFailures = 0;
  // Every part answers the same drawing; a failing part throws on each call.
  vi.doMock("virtual:icon-pack/parts", () => ({
    default: Array.from({ length: iconPackPartCount }, () => async () => {
      pack.markupLoads++;
      if (pack.markupFailures > 0) {
        pack.markupFailures--;
        throw new Error("Failed to fetch dynamically imported module");
      }
      return { default: { dog: '<path d="M1 1"/>' } };
    }),
  }));
  vi.doMock("./assets/icon-pack/catalog.json", () => ({
    default: {
      lucide: "1.0.0",
      categories: [{ id: "nature", label: "Příroda" }],
      core: {},
      icons: [{ id: "dog", label: "Pes", category: "nature", words: "zvíře dog" }],
    },
  }));
});
afterEach(() => {
  vi.restoreAllMocks();
  localStorage.clear();
});

describe("icon pack drawings", () => {
  it("loads the part of a name once for concurrent callers, and every part only for the whole pack", async () => {
    const { iconPackMarkup, iconPackPartLoaded, iconPackUnavailable, loadIconPackMarkup } = await load();
    expect(iconPackMarkup.value).toBeNull();
    expect(iconPackPartLoaded("dog")).toBe(false);
    await Promise.all([loadIconPackMarkup("dog"), loadIconPackMarkup("dog")]);
    await loadIconPackMarkup("dog");
    expect(pack.markupLoads).toBe(1);
    expect(iconPackPartLoaded("dog")).toBe(true);
    expect(iconPackMarkup.value).toEqual({ dog: '<path d="M1 1"/>' });
    expect(iconPackUnavailable.value).toBe(false);
    await loadIconPackMarkup();
    expect(pack.markupLoads).toBe(iconPackPartCount);
  });

  // A deploy replaces the hashed pack file like any chunk: one retry, then the shell is told and the next call tries again.
  it("rejects after a retry, flags the build, and a later call recovers", async () => {
    const { buildOutdated, iconPackMarkup, iconPackUnavailable, loadIconPackMarkup } = await load();
    pack.markupFailures = 2;
    const results = await Promise.allSettled([loadIconPackMarkup("dog"), loadIconPackMarkup("dog")]);
    expect(results.map((result) => result.status)).toEqual(["rejected", "rejected"]);
    expect(pack.markupLoads).toBe(2);
    expect(iconPackUnavailable.value).toBe(true);
    expect(buildOutdated.value).toBe(true);
    expect(iconPackMarkup.value).toBeNull();

    await loadIconPackMarkup("dog");
    expect(iconPackMarkup.value).toEqual({ dog: '<path d="M1 1"/>' });
    expect(iconPackUnavailable.value).toBe(false);
  });

  it("splits the generated drawings into parts that together are the pack, each holding the names of its hash", async () => {
    vi.doUnmock("virtual:icon-pack/parts");
    const { default: parts } = await import("virtual:icon-pack/parts");
    const { default: markup } = await import("./assets/icon-pack/markup.json");
    const loaded = await Promise.all(parts.map((part) => part()));
    loaded.forEach(({ default: drawings }, index) => {
      expect(Object.keys(drawings).every((name) => iconPackPart(name) === index)).toBe(true);
      // Even enough that one icon costs little: about 28 drawings a part.
      expect(Object.keys(drawings).length).toBeLessThan(60);
    });
    expect(Object.assign({}, ...loaded.map((part) => part.default))).toEqual(markup);
  });
});

describe("icon pack catalog", () => {
  it("gives the Czech label of a pack icon once the catalog has arrived", async () => {
    const { iconPackCatalog, iconPackLabel, loadIconPackCatalog } = await load();
    expect(iconPackLabel("dog")).toBeUndefined();
    await loadIconPackCatalog();
    expect(iconPackCatalog.value?.categories).toHaveLength(1);
    expect(iconPackLabel("dog")).toBe("Pes");
    expect(iconPackLabel("rocket")).toBeUndefined();
  });
});

describe("recently used icons", () => {
  it("keeps the last 16 ids, newest first and without repeats", async () => {
    const { readRecentIcons, rememberRecentIcon } = await load();
    expect(readRecentIcons()).toEqual([]);
    for (let i = 0; i < 20; i++) rememberRecentIcon(`icon-${i}`);
    rememberRecentIcon("icon-10");
    const recent = readRecentIcons();
    expect(recent).toHaveLength(16);
    expect(recent.slice(0, 3)).toEqual(["icon-10", "icon-19", "icon-18"]);
    expect(recent.filter((id) => id === "icon-10")).toHaveLength(1);
  });

  it("reads as empty and ignores writes while storage throws", async () => {
    const { readRecentIcons, rememberRecentIcon } = await load();
    vi.spyOn(Storage.prototype, "getItem").mockImplementation(() => { throw new Error("blocked"); });
    vi.spyOn(Storage.prototype, "setItem").mockImplementation(() => { throw new Error("blocked"); });
    expect(readRecentIcons()).toEqual([]);
    expect(rememberRecentIcon("rocket")).toEqual(["rocket"]);
  });
});
