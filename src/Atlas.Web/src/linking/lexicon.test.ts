import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { nextTick, ref } from "vue";
import { api, ApiError } from "../api";
import { invalidateLexicon, lexiconOf, lexiconReady, refreshLexicon, setLexiconAccount, useLexicon, type Lexicon } from "./lexicon";

vi.mock("../api", async importOriginal => ({ ...await importOriginal<typeof import("../api")>(), api: vi.fn() }));
const call = vi.mocked(api);

const lexicon = (version: string, names: string[] = ["Marek Musil"]): Lexicon => ({
  version, truncated: false, youTrackProjects: [], pages: [{ id: "p1", title: "Plán vydání" }],
  people: names.map((name, i) => ({ key: `object:c${i}`, contactId: `c${i}`, userId: null, name, aliases: ["MMu"], org: null, role: null, uses: 0 })),
});
const idle = () => new Promise(resolve => setTimeout(resolve, 5));

beforeEach(() => { call.mockReset(); localStorage.clear(); setLexiconAccount(null); setLexiconAccount("u1"); });
afterEach(() => { setLexiconAccount(null); });

describe("useLexicon", () => {
  it("answers the last known lexicon at once and asks the server only in idle time, with the known version", async () => {
    call.mockResolvedValueOnce(lexicon("v1"));
    expect(lexiconOf("s1")).toBeUndefined();
    expect(call).not.toHaveBeenCalled();
    await idle();
    expect(call).toHaveBeenCalledExactlyOnceWith("/spaces/s1/lexicon", {});
    expect(lexiconOf("s1")?.version).toBe("v1");

    // Another visit of the same account: the stored copy at once, revalidated by its ETag (304 keeps it).
    setLexiconAccount(null);
    setLexiconAccount("u1");
    call.mockRejectedValueOnce(new ApiError(304, ""));
    expect(lexiconOf("s1")?.people[0]?.name).toBe("Marek Musil");
    await idle();
    expect(call).toHaveBeenLastCalledWith("/spaces/s1/lexicon", { headers: { "If-None-Match": "\"v1\"" } });
    expect(lexiconOf("s1")?.version).toBe("v1");
  });

  it("keeps each account's copy apart and forgets a space out of reach", async () => {
    call.mockResolvedValueOnce(lexicon("v1"));
    await lexiconReady("s1");
    setLexiconAccount("u2");
    expect(lexiconOf("s1")).toBeUndefined();
    setLexiconAccount("u1");
    expect(lexiconOf("s1")?.version).toBe("v1");
    call.mockRejectedValueOnce(new ApiError(404, "Prostor nebyl nalezen."));
    await refreshLexicon("s1");
    expect(lexiconOf("s1")).toBeUndefined();
    expect(localStorage.length).toBe(0);
  });

  it("revalidates after a page or contact change, keeps the old copy while the request fails, and drops an answer for another account", async () => {
    call.mockResolvedValueOnce(lexicon("v1"));
    await lexiconReady("s1");
    expect(lexiconOf("s1")?.version).toBe("v1");
    await idle();
    expect(call).toHaveBeenCalledTimes(1);

    call.mockResolvedValueOnce(lexicon("v2", ["Marek Musil", "Jana Nováková"]));
    invalidateLexicon();
    await idle();
    expect(call).toHaveBeenCalledTimes(2);
    expect(lexiconOf("s1")?.people).toHaveLength(2);

    call.mockRejectedValueOnce(new ApiError(503, "Nedostupné"));
    await refreshLexicon("s1");
    expect(lexiconOf("s1")?.version).toBe("v2");

    let answer!: (value: Lexicon) => void;
    call.mockReturnValueOnce(new Promise(resolve => { answer = resolve; }) as never);
    const late = refreshLexicon("s1");
    setLexiconAccount("u2");
    answer(lexicon("v3"));
    await late;
    expect(lexiconOf("s1")).toBeUndefined();
  });

  it("re-reads in a component on every answer and revalidates the new space on a change", async () => {
    call.mockImplementation(async path => lexicon(String(path).includes("s2") ? "v-s2" : "v-s1"));
    const space = ref("s1");
    const current = useLexicon(space);
    expect(current.value).toBeUndefined();
    await idle();
    expect(current.value?.version).toBe("v-s1");
    space.value = "s2";
    await nextTick();
    await idle();
    expect(current.value?.version).toBe("v-s2");
  });
});
