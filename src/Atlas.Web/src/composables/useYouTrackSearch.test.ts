// @vitest-environment node
import { describe, expect, it, vi } from "vitest";
import { effectScope } from "vue";
import { useYouTrackSearch } from "./useYouTrackSearch";

const spaces = [{ id: "a", name: "A" }, { id: "b", name: "B" }, { id: "c", name: "C" }];
const settle = () => new Promise(resolve => setTimeout(resolve, 0));

function setup(configured: () => Promise<string[]>) {
  const asked: string[] = [];
  const fetchSpaces = vi.fn(configured);
  const scope = effectScope();
  const search = scope.run(() => useYouTrackSearch(async (_query, spaceId) => { asked.push(spaceId); return []; }, fetchSpaces))!;
  return { search, asked, fetchSpaces, scope };
}

describe("useYouTrackSearch", () => {
  it("asks only the spaces with the integration, read once per search session (UX SE3-2)", async () => {
    const { search, asked, fetchSpaces, scope } = setup(async () => ["b"]);
    search.search("sm", spaces);
    search.search("smlouva", spaces);
    await settle();
    expect(asked).toEqual(["b"]);
    expect(search.attempted.value).toBe(true);
    search.search("smlouvy", spaces);
    await settle();
    expect(fetchSpaces).toHaveBeenCalledTimes(1);
    search.stop();
    search.search("jira", spaces);
    await settle();
    expect(fetchSpaces).toHaveBeenCalledTimes(2);
    scope.stop();
  });

  it("asks nothing and shows no YouTrack state without the integration", async () => {
    const { search, asked, scope } = setup(async () => []);
    search.search("server", spaces);
    await settle();
    expect(asked).toEqual([]);
    expect(search.attempted.value).toBe(false);
    expect(search.pending.value).toBe(0);
    scope.stop();
  });

  it("asks every space when the list cannot be read", async () => {
    const { search, asked, scope } = setup(() => Promise.reject(new Error("offline")));
    search.search("server", spaces);
    await settle();
    expect(asked.sort()).toEqual(["a", "b", "c"]);
    scope.stop();
  });
});
