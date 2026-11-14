import { afterEach, describe, expect, it, vi } from "vitest";
import { downloadContentExport, exportFileStem, rememberCoverDetails, rememberedCoverDetails } from "./contentExport";
afterEach(() => { vi.restoreAllMocks(); vi.unstubAllGlobals(); vi.useRealTimers(); });
describe("ZIP download", () => {
  it("uses the authenticated API and releases its temporary download after clicking", async () => {
    vi.useFakeTimers();
    const fetch = vi.fn().mockResolvedValue(new Response(new Blob(["zip"], { type: "application/zip" })));
    vi.stubGlobal("fetch", fetch);
    Object.defineProperty(URL, "createObjectURL", { configurable: true, value: vi.fn(() => "blob:test") });
    Object.defineProperty(URL, "revokeObjectURL", { configurable: true, value: vi.fn() });
    const click = vi.spyOn(HTMLAnchorElement.prototype, "click").mockImplementation(function (this: HTMLAnchorElement) {
      expect(this.download).toBe("Příručky.zip"); expect(this.isConnected).toBe(true);
    });
    await downloadContentExport("spaces", "space-1", "zip", { name: "Příručky" });
    expect(fetch).toHaveBeenCalledWith("/api/spaces/space-1/export", { credentials: "same-origin" });
    expect(click).toHaveBeenCalledOnce(); expect(document.querySelector('a[download]')).toBeNull();
    await vi.advanceTimersByTimeAsync(1000); expect(URL.revokeObjectURL).toHaveBeenCalledWith("blob:test");
  });
});

it("downloads the saved page PDF through the authenticated export endpoint", async () => {
  vi.useFakeTimers();
  const fetch = vi.fn().mockResolvedValue(new Response(new Blob(["%PDF-"], { type: "application/pdf" })));
  vi.stubGlobal("fetch", fetch);
  Object.defineProperty(URL, "createObjectURL", { configurable: true, value: vi.fn(() => "blob:pdf") });
  Object.defineProperty(URL, "revokeObjectURL", { configurable: true, value: vi.fn() });
  const click = vi.spyOn(HTMLAnchorElement.prototype, "click").mockImplementation(function (this: HTMLAnchorElement) {
    expect(this.download).toBe("Acme Care - příručka.pdf");
  });
  await downloadContentExport("pages", "page-1", "pdf", { name: "Acme Care: příručka", coverDetails: ["revision", "revisionDate"] });
  expect(fetch).toHaveBeenCalledWith("/api/pages/page-1/export?format=pdf&details=revision%2CrevisionDate", { credentials: "same-origin" });
  expect(click).toHaveBeenCalledOnce();
});

it("exports the selected historical revision without changing the active document", async () => {
  vi.useFakeTimers();
  const fetch = vi.fn().mockResolvedValue(new Response(new Blob(["%PDF-"], { type: "application/pdf" })));
  vi.stubGlobal("fetch", fetch);
  Object.defineProperty(URL, "createObjectURL", { configurable: true, value: vi.fn(() => "blob:history") });
  Object.defineProperty(URL, "revokeObjectURL", { configurable: true, value: vi.fn() });
  const click = vi.spyOn(HTMLAnchorElement.prototype, "click").mockImplementation(function (this: HTMLAnchorElement) {
    expect(this.download).toBe("Příručka – revize 2.pdf");
  });
  await downloadContentExport("pages", "page-1", "pdf", { name: "Příručka – revize 2", revisionId: "old-2" });
  expect(fetch).toHaveBeenCalledWith("/api/pages/page-1/export?format=pdf&revisionId=old-2", { credentials: "same-origin" });
  expect(click).toHaveBeenCalledOnce();
});

it("names files after the title, portable on every file system", () => {
  expect(exportFileStem("  Návod: A/B  krok?  ", "stranka")).toBe("Návod - A-B krok");
  expect(exportFileStem("Verze 2.1.", "stranka")).toBe("Verze 2.1");
  expect(exportFileStem("...", "stranka")).toBe("stranka");
  expect(exportFileStem("CON", "stranka")).toBe("stranka");
  expect(exportFileStem("x".repeat(150), "stranka")).toHaveLength(100);
});

it("remembers the cover details of the last manual PDF export", () => {
  const stored = new Map<string, string>();
  vi.stubGlobal("localStorage", { getItem: (key: string) => stored.get(key) ?? null, setItem: (key: string, value: string) => stored.set(key, value) });
  expect(rememberedCoverDetails()).toEqual(["revision", "state", "revisionDate", "exportedAt", "pageId"]);
  rememberCoverDetails(["revisionDate", "revision"]);
  expect(rememberedCoverDetails()).toEqual(["revision", "revisionDate"]);
  rememberCoverDetails([]);
  expect(rememberedCoverDetails()).toEqual([]);
});
