// @vitest-environment node
import { afterEach, describe, expect, it, vi } from "vitest";
import { historyApi } from "./history";

vi.mock("./api", () => ({ api: vi.fn() }));
import { api } from "./api";

const revision = (id: string) => ({ id, pageId: "p", markdown: id });
afterEach(() => { historyApi.forget(); vi.mocked(api).mockReset(); });

describe("history revision cache", () => {
  it("fetches a revision once and shares it with a prefetch", async () => {
    vi.mocked(api).mockImplementation(async (path: string) => revision(path.split("/").at(-1)!));
    historyApi.prefetch("p", "a");
    const signal = new AbortController().signal;
    expect(await historyApi.revision("p", "a", signal)).toMatchObject({ id: "a" });
    expect(await historyApi.revision("p", "a", signal)).toMatchObject({ id: "a" });
    expect(api).toHaveBeenCalledTimes(1);
  });
  it("keeps the 60 most recently used revisions", async () => {
    vi.mocked(api).mockImplementation(async (path: string) => revision(path.split("/").at(-1)!));
    const signal = new AbortController().signal;
    for (let i = 0; i < 60; i++) await historyApi.revision("p", `r${i}`, signal);
    await historyApi.revision("p", "r0", signal);            // r0 becomes the newest
    await historyApi.revision("p", "r60", signal);           // evicts r1, not r0
    vi.mocked(api).mockClear();
    await historyApi.revision("p", "r0", signal);
    expect(api).not.toHaveBeenCalled();
    await historyApi.revision("p", "r1", signal);
    expect(api).toHaveBeenCalledTimes(1);
  });
  it("shares an older history page and forgets everything on forget()", async () => {
    vi.mocked(api).mockResolvedValue({ pageId: "p", currentVersion: 9, items: [], nextBeforeVersion: null });
    const signal = new AbortController().signal;
    await historyApi.list("p", 5, signal); await historyApi.list("p", 5, signal);
    expect(api).toHaveBeenCalledTimes(1);
    historyApi.forget();
    await historyApi.list("p", 5, signal);
    expect(api).toHaveBeenCalledTimes(2);
  });
  it("reads the steps of a version by group and names a version", async () => {
    vi.mocked(api).mockResolvedValue({ pageId: "p", currentVersion: 9, items: [], nextBeforeVersion: null });
    await historyApi.steps("p", "g", null); await historyApi.steps("p", "g", null);
    expect(api).toHaveBeenCalledTimes(1);
    expect(vi.mocked(api).mock.calls[0]![0]).toContain("group=g");
    vi.mocked(api).mockResolvedValue({ revisionId: "r", version: 4 });
    await historyApi.nameVersion("p", 3, "Před auditem");
    expect(vi.mocked(api).mock.calls.at(-1)).toEqual(["/pages/p/versions", { method: "POST", body: JSON.stringify({ expectedVersion: 3, label: "Před auditem" }) }]);
  });
});
