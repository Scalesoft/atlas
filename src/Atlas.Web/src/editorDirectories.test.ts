import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { api, ApiError } from "./api";
import { pageReferenceNode } from "@atlas/markdown/plugins/pages";
import { createContactDirectory, createKnownPeople, createPageSource, createPeopleSearch, forgetPageReferences } from "./editorDirectories";
import { objectApi } from "./objects/objectApi";
import { setLexiconAccount, type Lexicon, type LexiconPerson } from "./linking/lexicon";
// The batched check keeps its answers for 30 s across editors: each test starts without them.
afterEach(() => { forgetPageReferences(); });

vi.mock("./api", async importOriginal => ({ ...await importOriginal<typeof import("./api")>(), api: vi.fn() }));
const call = vi.mocked(api);
const signal = new AbortController().signal;

beforeEach(() => { call.mockReset(); });

describe("page links ([[ and the slash menu)", () => {
  it("recognizes only this Atlas page route without requesting metadata", () => {
    const source = createPageSource(() => []);
    expect(source.matchUrl(new URL("/#/pages/p1", location.origin).href)).toBe(true);
    expect(source.matchUrl("https://other.example/#/pages/p1")).toBe(false);
    expect(source.matchUrl(new URL("/#/pages/p1?heading=part", location.origin).href)).toBe(false);
    expect(call).not.toHaveBeenCalled();
  });
  it("resolves pasted page URLs through current server access, even for a cached title", async () => {
    const source = createPageSource(() => [{ id: "p1", title: "Cached" }]);
    call.mockResolvedValueOnce({ id: "p1", title: "Current" });
    await expect(source.resolveUrl(new URL("/#/pages/p1", location.origin).href, { signal }))
      .resolves.toEqual({ id: "p1", title: "Current", url: "#/pages/p1" });
    expect(call).toHaveBeenCalledWith("/pages/p1", { signal });
    call.mockRejectedValueOnce(new ApiError(404, "Unavailable"));
    await expect(source.resolveUrl(new URL("/#/pages/p1", location.origin).href, { signal })).resolves.toBeNull();
    call.mockClear();
    await expect(source.resolveUrl("https://other.example/#/pages/p1", { signal })).resolves.toBeNull();
    expect(call).not.toHaveBeenCalled();
  });

  it("searches every space the reader sees and links to the page route", async () => {
    call.mockResolvedValueOnce({ items: [{ id: "p1", spaceId: "s", spaceName: "Wiki", title: "Onboarding", snippet: "" }], hasMore: false });
    const found = await createPageSource(() => []).search("onb", { signal });
    expect(call).toHaveBeenCalledWith("/search?q=onb&limit=10", { signal });
    expect(found).toEqual([{ id: "p1", title: "Onboarding", url: "#/pages/p1", detail: "Wiki" }]);
  });
  const hit = (id: string, title: string, spaceId: string, extra: object = {}) => ({ id, title, spaceId, spaceName: spaceId, snippet: "", ...extra });
  const answer = (everywhere: object[], inSpace: object[] = []) => call.mockImplementation(async (path: string) =>
    ({ items: path.includes("spaceId=") ? inSpace : everywhere, hasMore: false }));
  it("an empty query lists the recent pages, this space first, so the preselected first item is a page the reader knows", async () => {
    call.mockResolvedValueOnce({ items: [hit("far", "Jiný prostor", "other"), hit("near", "Tento prostor", "here")], hasMore: false });
    const found = await createPageSource(() => [], { spaceId: () => "here" }).search("", { signal });
    expect(call).toHaveBeenCalledWith("/me/library/recent?offset=0&limit=10", { signal });
    expect(found.map(page => page.id)).toEqual(["near", "far"]);
  });
  it("orders the current space first, then exact title, prefix and alias matches", async () => {
    answer(
      [
        hit("o-exact", "Jana", "other"),
        hit("c-body", "Zápis z porady", "here"),
        hit("c-alias", "Kontakt", "here", { matchedAlias: "Jana" }),
        hit("c-prefix", "Jana Nováková", "here"),
        hit("c-inside", "Tým Jana", "here"),
        hit("c-exact", "JANA", "here"),
      ],
      [hit("c-body", "Zápis z porady", "here"), hit("c-prefix", "Jana Nováková", "here"), hit("c-only", "Vedení Jany", "here")],
    );
    const found = await createPageSource(() => [{ id: "tree", title: "Jana ze stromu" }], { spaceId: () => "here" }).search("jana", { signal });
    // the loaded tree no longer jumps the queue; within a group: exact, prefix, a word of the title, alias, then server order (the space-scoped answer first)
    expect(found.map(page => page.id)).toEqual(["c-exact", "c-prefix", "c-inside", "c-alias", "c-body", "c-only", "o-exact"]);
  });
  it("finds by word starts and space names without diacritics, best match first (fuzzy hits of the server last)", async () => {
    answer([
      hit("fuzzy", "Příručka lékaře", "p", { spaceName: "Portál" }),
      hit("space", "test", "z", { spaceName: "Začínáme" }),
      hit("word", "Jak začít s Atlasem", "p", { spaceName: "Portál" }),
      hit("prefix", "Začátek práce", "p", { spaceName: "Portál" }),
    ]);
    const found = await createPageSource(() => []).search("Zač", { signal });
    expect(found.map(page => page.id)).toEqual(["prefix", "word", "space", "fuzzy"]);
  });
  it("asks for the whole wiki and for the current space at once, and keeps published pages and own drafts without duplicates", async () => {
    answer([hit("a", "Plán", "other"), hit("d", "Koncept plánu", "a b", { isDraft: true })], [hit("d", "Koncept plánu", "a b", { isDraft: true })]);
    const found = await createPageSource(() => [], { spaceId: () => "a b" }).search("plán", { signal });
    expect(call).toHaveBeenCalledTimes(2);
    expect(call).toHaveBeenCalledWith("/search?q=pl%C3%A1n&limit=10", { signal });
    expect(call).toHaveBeenCalledWith("/search?q=pl%C3%A1n&spaceId=a%20b&limit=10", { signal });
    expect(found.map(page => page.id)).toEqual(["d", "a"]);
  });
  it("labels drafts in the link list without changing the stored reference", async () => {
    answer([hit("pub", "VPN", "it", { spaceName: "IT a infrastruktura" }), hit("draft", "VPN pro dodavatele", "ops", { spaceName: "Provoz", isDraft: true })]);
    const source = createPageSource(() => [{ id: "pub", title: "VPN" }]);
    const found = await source.search("vpn", { signal });
    expect(found.map(page => (page as { detail?: string }).detail)).toEqual(["IT a infrastruktura", "Provoz · Koncept"]);
    // The label is for the list only: the node written into the document and a resolved link carry none.
    expect(pageReferenceNode(found[1]!)).toEqual({ type: "pageReference", attrs: { id: "draft", title: "VPN pro dodavatele", url: "#/pages/draft" } });
    expect(await source.resolve!("pub", { signal })).toEqual({ id: "pub", title: "VPN", url: "#/pages/pub" });
  });
  it("aborts both searches when the query changes", async () => {
    const signals: AbortSignal[] = [];
    call.mockImplementation((_path: string, init?: RequestInit) => new Promise((_, reject) => {
      signals.push(init!.signal!);
      init!.signal!.addEventListener("abort", () => reject(new DOMException("Aborted", "AbortError")));
    }));
    const source = createPageSource(() => [], { spaceId: () => "here" });
    const first = new AbortController();
    const pending = source.search("ja", { signal: first.signal });
    first.abort();
    await expect(pending).rejects.toMatchObject({ name: "AbortError" });
    expect(signals).toHaveLength(2);
    expect(signals.every(s => s.aborted)).toBe(true);
  });
  const p = (n: number) => `00000000-0000-4000-8000-${String(n).padStart(12, "0")}`;
  it("resolves a link from the loaded tree first and verifies the links of one render in one request without opening pages", async () => {
    const source = createPageSource(() => [{ id: p(1), title: "Renamed" }]);
    await expect(source.resolve!(p(1), { signal })).resolves.toEqual({ id: p(1), title: "Renamed", url: `#/pages/${p(1)}` });
    expect(call).not.toHaveBeenCalled();
    call.mockResolvedValueOnce({ items: [{ id: p(2), title: "Elsewhere" }] });
    const upper = p(2).toUpperCase();
    const resolved = await Promise.all([p(2), p(3), p(2), upper, "not-a-page"].map(id => source.resolve!(id, { signal })));
    expect(call).toHaveBeenCalledTimes(1);
    expect(call).toHaveBeenCalledWith("/pages/references", { method: "POST", body: JSON.stringify({ ids: [p(2), p(3)] }) });
    expect(resolved).toEqual([
      { id: p(2), title: "Elsewhere", url: `#/pages/${p(2)}` },
      null,
      { id: p(2), title: "Elsewhere", url: `#/pages/${p(2)}` },
      { id: upper, title: "Elsewhere", url: `#/pages/${upper}` },
      null,
    ]);
  });
  it("names a pasted board link from the batched check, with the board icon, never from the whole board (UX PG4-4)", async () => {
    const source = createPageSource(() => []);
    const boardUrl = new URL(`/#/boards/${p(7)}`, location.origin).href;
    expect(source.matchUrl(boardUrl)).toBe(true);
    call.mockResolvedValueOnce({ items: [{ id: p(7), title: "Roadmap", type: "board" }] });
    await expect(source.resolveUrl(boardUrl, { signal })).resolves.toEqual({ id: p(7), title: "Roadmap", url: `#/boards/${p(7)}`, icon: "layout-dashboard" });
    expect(call).toHaveBeenCalledWith("/pages/references", { method: "POST", body: JSON.stringify({ ids: [p(7)] }) });
    // A page id behind a board address is no board; a rendered board link resolves the same way.
    call.mockResolvedValueOnce({ items: [{ id: p(8), title: "A page" }] });
    await expect(source.resolveUrl(new URL(`/#/boards/${p(8)}`, location.origin).href, { signal })).resolves.toBeNull();
    // Answers are kept 30 s (static page and live editor share them); a renamed board shows once they are gone.
    await expect(source.resolve!(p(7), { signal })).resolves.toMatchObject({ title: "Roadmap", url: `#/boards/${p(7)}` });
    forgetPageReferences();
    call.mockResolvedValueOnce({ items: [{ id: p(7), title: "Roadmap 2", type: "board" }] });
    await expect(source.resolve!(p(7), { signal })).resolves.toMatchObject({ title: "Roadmap 2", url: `#/boards/${p(7)}` });
  });
  // The static page and the live editor built under it (O10) resolve the same links a moment apart: one request serves both.
  it("joins a check of the same links still on its way instead of sending it again", async () => {
    let answer!: (value: { items: { id: string; title: string }[] }) => void;
    call.mockReturnValueOnce(new Promise(resolve => { answer = resolve; }) as never);
    const first = createPageSource(() => []).resolve!(p(3), { signal });
    await Promise.resolve();
    const second = createPageSource(() => []).resolve!(p(3), { signal });
    answer({ items: [{ id: p(3), title: "Sdílená" }] });
    await expect(first).resolves.toMatchObject({ title: "Sdílená" });
    await expect(second).resolves.toMatchObject({ title: "Sdílená" });
    expect(call.mock.calls.filter(([path]) => path === "/pages/references")).toHaveLength(1);
  });
  it("splits large renders into requests of 100 links and fails only the links of a failed request", async () => {
    const source = createPageSource(() => []);
    call.mockResolvedValueOnce({ items: [{ id: p(1), title: "First" }] });
    call.mockRejectedValueOnce(new ApiError(500, "Požadavek se nepodařilo dokončit."));
    const results = await Promise.allSettled(Array.from({ length: 101 }, (_, i) => source.resolve!(p(i + 1), { signal })));
    expect(call.mock.calls.map(([, init]) => JSON.parse(String(init!.body)).ids.length)).toEqual([100, 1]);
    expect(results[0]).toEqual({ status: "fulfilled", value: { id: p(1), title: "First", url: `#/pages/${p(1)}` } });
    expect(results[99]).toEqual({ status: "fulfilled", value: null });
    expect(results[100]).toMatchObject({ status: "rejected" });
  });
});

describe("[[ ordering by the section around the caret (E4)", () => {
  afterEach(() => { vi.useRealTimers(); });
  const hit = (id: string, title: string, spaceId: string, extra: object = {}) => ({ id, title, spaceId, spaceName: spaceId, snippet: "", ...extra });
  const pages = [
    hit("c-exact", "VPN", "here"),
    hit("c-a", "VPN pro dodavatele", "here"),
    hit("c-b", "VPN klient na Macu", "here"),
    hit("c-draft", "VPN koncept", "here", { isDraft: true }),
    hit("o-a", "VPN v kanceláři", "other"),
    hit("o-b", "VPN doma", "other"),
  ];
  const lexical = ["c-exact", "c-a", "c-b", "c-draft", "o-a", "o-b"];
  /** Search answers at once; page-order answers with what `order` returns or throws. */
  function serve(order: () => unknown) {
    call.mockImplementation(async (path: string) => {
      if (path === "/assist/page-order") return order();
      return { items: path.includes("spaceId=") ? pages.filter(page => page.spaceId === "here") : pages, hasMore: false };
    });
  }
  const section = { pageId: "page-1", context: "Vzdálená správa serverovny přes VPN z domova." };
  const source = (order: () => typeof section | null = () => section) => createPageSource(() => [], { spaceId: () => "here", order });

  it("keeps the lexical order when page ordering is late, fails or is unavailable", async () => {
    // Without the capability (or without a section to compare) nothing is asked.
    serve(() => { throw new Error("unexpected"); });
    expect((await source(() => null).search("vpn", { signal })).map(page => page.id)).toEqual(lexical);
    expect(call.mock.calls.some(([path]) => path === "/assist/page-order")).toBe(false);

    // 503 ai_unavailable or any other failure: the lexical order.
    serve(() => { throw new ApiError(503, "Tahle funkce teď není k dispozici."); });
    expect((await source().search("vpn", { signal })).map(page => page.id)).toEqual(lexical);
    // The server's embedding past its budget answers 200 without items: the lexical order.
    serve(() => ({ items: [] }));
    expect((await source().search("vpn", { signal })).map(page => page.id)).toEqual(lexical);

    // Later than 300 ms: the list never waits longer, and the late answer is dropped.
    vi.useFakeTimers();
    let answer!: (value: unknown) => void;
    serve(() => new Promise(resolve => { answer = resolve; }));
    let done = false;
    const pending = source().search("vpn", { signal }).finally(() => { done = true; });
    await vi.advanceTimersByTimeAsync(299);
    expect(done).toBe(false);
    await vi.advanceTimersByTimeAsync(1);
    expect(done).toBe(true);
    expect((await pending).map(page => page.id)).toEqual(lexical);
    answer({ items: [{ pageId: "o-b", similarity: 0.9 }, { pageId: "c-b", similarity: 0.8 }] });
  });

  it("orders within groups by section similarity and keeps an exact title first", async () => {
    serve(() => ({ items: [{ pageId: "o-b", similarity: 0.91 }, { pageId: "c-b", similarity: 0.83 }, { pageId: "c-exact", similarity: 0.2 }, { pageId: "c-a", similarity: 0.1 }, { pageId: "o-a", similarity: 0.05 }] }));
    const found = await source().search("vpn", { signal });
    // The current space stays first and its exact title stays on top; the draft (not in the answer) keeps its place.
    expect(found.map(page => page.id)).toEqual(["c-exact", "c-b", "c-a", "c-draft", "o-b", "o-a"]);
    const request = call.mock.calls.find(([path]) => path === "/assist/page-order")!;
    expect(request[1]).toMatchObject({ method: "POST" });
    expect(JSON.parse(String(request[1]!.body))).toEqual({ pageId: "page-1", context: section.context, candidates: lexical });
  });
});

describe("contacts directory (@ mentions)", () => {
  it("offers active accounts as internal contacts, then the directory's (by object id) and the pages' contacts, each person once", async () => {
    call.mockImplementation(async (path: string) => {
      if (path.startsWith("/pages/")) return [{ id: "u1", name: "Jana Nováková", username: "jnovakova", email: "jana@example.com" }, { id: "u2", name: "Jana Bez mailu", email: null }];
      if (path.startsWith("/objects")) return { items: [{ id: "c1", title: "Jana Nováková", data: { name: "Jana Nováková", emails: ["JANA@example.com"] } },
        { id: "c2", title: "Jana Malá", data: { name: "Jana Malá", organization: "AB Klinika" } }] };
      return { items: [{ title: "Kontakty", spaceName: "Zakázky", widgets: [{ kind: "contact", title: "Honza Jana", detail: "" }, { kind: "contact", title: "Eva", detail: "" }] }] };
    });
    const found = await createContactDirectory(() => "page 1").search("jana", { signal });
    expect(call).toHaveBeenCalledWith("/pages/page%201/people?q=jana", { signal });
    expect(found).toEqual([
      { name: "Jana Nováková", username: "jnovakova", emails: ["jana@example.com"], directoryId: "u1", kind: "internal", internalUser: { id: "u1", name: "Jana Nováková" }, objectId: "c1" },
      { name: "Jana Bez mailu", directoryId: "u2", kind: "internal", internalUser: { id: "u2", name: "Jana Bez mailu" } },
      { name: "Jana Malá", organization: "AB Klinika", objectId: "c2" },
      { name: "Honza Jana" },
    ]);
  });
  it("refreshes one contact and answers null for an account that is gone", async () => {
    const directory = createContactDirectory(() => "p");
    call.mockResolvedValueOnce({ id: "u1", name: "Jana", username: "jnovakova", email: null });
    await expect(directory.get("u1", { signal })).resolves.toEqual({ name: "Jana", username: "jnovakova", directoryId: "u1", kind: "internal", internalUser: { id: "u1", name: "Jana" } });
    expect(call).toHaveBeenLastCalledWith("/pages/p/people/u1", { signal });
    // An account without a username refreshes without the field instead of storing an empty login.
    call.mockResolvedValueOnce({ id: "u2", name: "Bez loginu", username: null, email: null });
    await expect(directory.get("u2", { signal })).resolves.not.toHaveProperty("username");
    call.mockRejectedValueOnce(new ApiError(404, "Uživatel nebyl nalezen."));
    await expect(directory.get("u9", { signal })).resolves.toBeNull();
  });
  it("searches nothing before the page exists", async () => {
    await expect(createContactDirectory(() => undefined).search("jana", { signal })).resolves.toEqual([]);
    expect(call).not.toHaveBeenCalled();
  });
});

describe("known people (name recognition in the editor)", () => {
  const person = (key: string, name: string, extra: Partial<LexiconPerson> = {}): LexiconPerson =>
    ({ key, contactId: key.startsWith("object:") ? key.slice(7) : null, userId: key.startsWith("user:") ? key.slice(5) : null, name, aliases: [], org: null, role: null, uses: 0, ...extra });
  const lexicon = (people: LexiconPerson[]): Lexicon => ({ version: "v1", truncated: false, people, pages: [], youTrackProjects: [] });
  const idle = () => new Promise(resolve => setTimeout(resolve, 5));
  beforeEach(() => { setLexiconAccount("reset"); setLexiconAccount(null); });

  it("recognises the space's lexicon people, read once for every editor of the space", async () => {
    call.mockImplementation(async (path: string) => (path === "/spaces/s/lexicon" ? lexicon([
      person("object:c1", "Jana Nováková", { userId: "u1", aliases: ["Janička"], org: "AB" }), person("user:u2", "Petr Svoboda")]) : { items: [] }));
    const people = await createKnownPeople(() => "s")();
    expect(people).toEqual([
      { name: "Jana Nováková", organization: "AB", directoryId: "u1", objectId: "c1", aliases: ["Janička"] },
      { name: "Petr Svoboda", directoryId: "u2", kind: "internal", internalUser: { id: "u2", name: "Petr Svoboda" } },
    ]);
    call.mockClear();
    await createKnownPeople(() => "s")();
    expect(call).not.toHaveBeenCalled();
    await expect(createKnownPeople(() => undefined)()).resolves.toEqual([]);
  });
  it("finds by initials and aliases from the lexicon, which the servers' word search does not return, and shows them before any answer", async () => {
    call.mockImplementation(async (path: string) => {
      if (path === "/spaces/s/lexicon") return lexicon([person("user:u1", "Marek Musil", { uses: 1 }), person("user:u2", "Michal Macek"),
        person("object:c9", "Jana Nováková", { aliases: ["Janička"] })]);
      if (path.startsWith("/pages/")) return [];
      if (path.startsWith("/objects")) return { items: [], total: 0, page: 1, pageSize: 50 };
      return { items: [] };
    });
    const search = createPeopleSearch(() => "page-initials", () => "s");
    expect(search.cached("MM")).toEqual([]);
    await createKnownPeople(() => "s")();
    // The picker's first render: the lexicon at once, the most used first.
    expect(search.cached("MM").map(hit => hit.contact.name)).toEqual(["Marek Musil", "Michal Macek"]);
    expect((await search("MMa", signal)).map(hit => hit.contact.name)).toEqual(["Michal Macek"]);
    const [jana] = await search("janička", signal);
    expect(jana).toMatchObject({ objectId: "c9", aliases: ["Janička"] });
    expect(jana!.contact).not.toHaveProperty("aliases");
    // A pick after typing an alias teaches the contact (`@MMu`).
    const change = vi.spyOn(objectApi, "change").mockResolvedValue({ version: 2, duplicate: false } as never);
    createContactDirectory(() => "page-initials", () => "s").learn!("MMu", { name: "Marek Musil", objectId: "c-m" });
    createContactDirectory(() => "page-initials", () => "s").learn!("Mar", { name: "Marek Musil", objectId: "c-m" });
    expect(change).toHaveBeenCalledExactlyOnceWith("c-m", { learnAlias: "MMu" });
    change.mockRestore();
  });
  it("never waits for the lexicon: the servers' answer alone before it comes (P17)", async () => {
    let answer!: (value: Lexicon) => void;
    call.mockImplementation(async (path: string) => {
      if (path === "/spaces/s/lexicon") return new Promise(resolve => { answer = resolve; });
      if (path.startsWith("/pages/")) return [{ id: "u2", name: "Jana Nováková", username: "jnovakova", email: null }];
      return { items: [], total: 0, page: 1, pageSize: 50 };
    });
    const search = createPeopleSearch(() => "page-stale", () => "s");
    expect((await search("MM", signal)).map(hit => hit.contact.name)).toEqual([]);
    await idle();
    answer(lexicon([person("user:u1", "Marek Musil")]));
    await idle();
    expect((await search("MM", signal)).map(hit => hit.contact.name)).toEqual(["Marek Musil"]);
  });
  it("finds people on a board (no page): the directory and the space's lexicon, never the page's accounts", async () => {
    call.mockImplementation(async (path: string) => {
      if (path === "/spaces/s/lexicon") return lexicon([person("user:u1", "Marek Musil")]);
      if (path.startsWith("/objects")) return { items: [{ id: "c2", title: "Marta Malá", data: { name: "Marta Malá" } }], total: 1, page: 1, pageSize: 50 };
      return { items: [] };
    });
    const directory = createContactDirectory(() => undefined, () => "s");
    await createKnownPeople(() => "s")();
    expect((await directory.search("mar", { signal })).map(contact => contact.name)).toEqual(["Marta Malá", "Marek Musil"]);
    expect(call.mock.calls.some(([path]) => String(path).startsWith("/pages/"))).toBe(false);
    await expect(directory.get("u1", { signal })).resolves.toMatchObject({ name: "Marek Musil", directoryId: "u1" });
  });
});
