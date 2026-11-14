import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { defineComponent, h, onMounted } from "vue";
import { flushPromises, type VueWrapper } from "@vue/test-utils";
import type { Page } from "./api";
import { emptyDocument } from "./document";
import { useAiCapabilities } from "./composables/useAiCapabilities";
import { useWorkspacePanel } from "./composables/useWorkspacePanel";
import { jsonResponse as response, memoryStorage, mountApp, quietRoutes, resetBrowser, routedFetch, sessionRoutes, type Route } from "./testing/appHarness";
// The lazy views these tests open (the page's footer with its connections, the tags, the outline, the comments, the settings) are
// transformed on their first import, which under full parallel load with a cold module cache did not fit vi.waitFor's 1 s (review
// cycle 2, P2-8). Imported with the file, outside every waiting window and every hook's time; the application's own dynamic
// imports then find them ready. The dock (PageSidePanel) is left out: the tests pin the requests of a dock that arrives after the footer.
import "./components/PageConnections.vue";
import "./components/PageTags.vue";
import "./components/PageIconPicker.vue";
import "./components/PageOutline.vue";
import "./components/PageMobileBar.vue";
import "./components/comments/CommentThread.vue";
import "./components/settings/SettingsHost.vue";
import "./components/settings/global/GlobalSettings.vue";
import "./components/settings/general/BackupsPage.vue";
import "./components/PersonalLibraryPanel.vue";

// Request shape of the shell: nothing waits for an answer it does not need.
// The factory runs when the application first imports the editor module, so it also counts the starts of that import
// (vi.doMock below makes the next import run it again) and can make one fail.
const editor = vi.hoisted(() => ({ imports: 0, failNext: false }));
function editorModule() {
  editor.imports++;
  if (editor.failNext) { editor.failNext = false; throw new Error("The editor chunk did not load."); }
  return { __esModule: true, default: defineComponent({
    props: ["value", "readonly"], emits: ["ready"],
    setup(_, { emit, expose }) {
      expose({ focus: () => true, capture: () => ({}), navigateToHeading: vi.fn(), selectionActions: () => [] });
      onMounted(() => emit("ready"));
      return () => h("div", { class: "document-stub" });
    },
  }) };
}
vi.mock("./components/DocumentEditor.vue", editorModule);

const page: Page = { id: "page-1", spaceId: "space-1", parentId: null, title: "První článek", version: 1, isPublished: true,
  publishedVersion: 1, contentJson: JSON.stringify(emptyDocument()), activeFrom: "2026-09-26T08:00:00Z", canEdit: true };
let wrapper: VueWrapper | undefined;
const settle = async () => { for (let i = 0; i < 4; i++) await flushPromises(); };
/** The connections, the dock, the tags and the editor are lazy chunks: wait for their imports too. */
const settleLazy = async () => { await vi.dynamicImportSettled(); await settle(); await settle(); };

const engineering: Route = path => {
  if (path === "/api/auth/me") return response({ id: "user-1", username: "writer", displayName: "Writer", isWikiAdmin: false, appearance: { layout: "connected", accent: "blue", mode: "system" } });
  if (path === "/api/spaces") return response([{ id: "space-1", name: "Engineering", role: "Writer", icon: "book", color: "blue" }]);
  if (path === "/api/spaces/space-1/tree") return response({ spaceId: "space-1", version: 1, items: [{ ...page }, { ...page, id: "page-2", title: "Druhý článek" }] });
  if (path === "/api/pages/page-1") return response(page);
  if (path === "/api/pages/page-2") return response({ ...page, id: "page-2", title: "Druhý článek" });
  if (/\/tags$/.test(path)) return response({ tags: [] });
  if (/\/connections\?limit=\d+$/.test(path)) return response({ linksHere: { items: [], total: 0 } });
};
function server(hold?: Route) { return routedFetch(hold, engineering, sessionRoutes, quietRoutes); }
const requests = (fetchMock: ReturnType<typeof server>, path: string) => fetchMock.mock.calls.filter(([requested]) => String(requested) === path).length;

afterEach(() => { wrapper?.unmount(); wrapper = undefined; resetBrowser(); });

describe("shell request shape", () => {
  it("requests a linked page together with the session check instead of after spaces and trees", async () => {
    let releaseSpaces!: () => void;
    const fetchMock = server(path => path === "/api/spaces"
      ? new Promise(resolve => { releaseSpaces = () => resolve(response([{ id: "space-1", name: "Engineering", role: "Writer", icon: "book", color: "blue" }])); })
      : undefined);
    wrapper = mountApp({ url: "/#/pages/page-1", fetch: fetchMock }); await settle();
    const requested = fetchMock.mock.calls.map(([path]) => String(path));
    // With its bundle (personal state, tags, links), so the page view asks for little more.
    expect(requested).toContain("/api/pages/page-1?bundle=true");
    expect(requested.indexOf("/api/pages/page-1?bundle=true")).toBeLessThan(requested.indexOf("/api/spaces"));
    // The spaces have not answered yet: the page's view is already downloading, and Knihovna is not drawn under it.
    expect(editor.imports).toBeGreaterThan(0);
    expect(wrapper.find(".workspace-bar").exists()).toBe(true);
    expect(wrapper.find(".library-overview").exists()).toBe(false);
    releaseSpaces(); await settle();
    // The linked page is fetched once: the start-up request is the one the page view uses.
    expect(fetchMock.mock.calls.filter(([path]) => String(path).startsWith("/api/pages/page-1?"))).toHaveLength(1);
    expect((wrapper.get(".document-title-text").element as HTMLTextAreaElement).value).toContain("První článek");
  });

  it("requests the tree of a linked space together with the session check and reads it once", async () => {
    let releaseSpaces!: () => void;
    const fetchMock = server(path => path === "/api/spaces"
      ? new Promise(resolve => { releaseSpaces = () => resolve(response([{ id: "space-1", name: "Engineering", role: "Writer", icon: "book", color: "blue" }])); })
      : undefined);
    wrapper = mountApp({ url: "/#/spaces/space-1", fetch: fetchMock }); await settle();
    const requested = fetchMock.mock.calls.map(([path]) => String(path));
    expect(requested.indexOf("/api/spaces/space-1/tree")).toBeGreaterThanOrEqual(0);
    expect(requested.indexOf("/api/spaces/space-1/tree")).toBeLessThan(requested.indexOf("/api/spaces"));
    releaseSpaces(); await settle();
    expect(fetchMock.mock.calls.filter(([path]) => String(path) === "/api/spaces/space-1/tree")).toHaveLength(1);
    expect(wrapper.get(".space-overview").text()).toContain("Engineering");
  });

  it("leaves the page view's chunk of a session that starts on Knihovna to idle time", async () => {
    const idle = vi.fn();
    vi.stubGlobal("requestIdleCallback", idle);
    vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout"] });
    try {
      wrapper = mountApp({ url: "/#/library/spaces", fetch: server() }); await settle();
      // Knihovna is up; its own requests (the cover images) go first, the warm-up has not even asked for idle time.
      expect(wrapper.find(".library-overview").exists()).toBe(true);
      expect(idle).not.toHaveBeenCalled();
      await vi.advanceTimersByTimeAsync(2500);
      // Asked for once, with a deadline; nothing is fetched before the browser calls back.
      expect(idle.mock.calls.filter(([, options]) => options?.timeout === 4000)).toHaveLength(1);
    } finally { vi.useRealTimers(); }
  });

  it("reads the connections of an opened page once, also after the visit is recorded", async () => {
    let recordVisit: (() => void) | undefined;
    const fetchMock = server(path => /\/visit$/.test(path)
      ? new Promise(resolve => { recordVisit = () => resolve(response({ isFavorite: false, visitedAt: null })); })
      : undefined);
    wrapper = mountApp({ url: "/#/pages/page-1", fetch: fetchMock });
    const reads = () => fetchMock.mock.calls.filter(([path]) => String(path).includes("/connections"));
    // The footer (a lazy view) has asked while the visit is still on its way; recording it must not ask again.
    await vi.waitFor(() => { expect(reads()).toHaveLength(1); expect(recordVisit).toBeDefined(); });
    recordVisit!(); await settle(); await settle();
    expect(reads()).toHaveLength(1);
  });

  it("opens a page without a history request and shows its last change from the page answer", async () => {
    const fetchMock = server();
    wrapper = mountApp({ url: "/#/pages/page-1", fetch: fetchMock }); await settle();
    expect(fetchMock.mock.calls.some(([path]) => String(path).includes("/history"))).toBe(false);
    expect(wrapper.get(".document-updated time").attributes("datetime")).toBe(page.activeFrom);
  });

  it("does not read a tree it has just read again before rendering the next page", async () => {
    let treeReads = 0;
    const fetchMock = server(path => { if (path === "/api/spaces/space-1/tree") treeReads++; return undefined; });
    wrapper = mountApp({ url: "/#/pages/page-1", fetch: fetchMock }); await settle();
    await wrapper.findAll(".tree-page").find(link => link.text().includes("Druhý článek"))!.trigger("click"); await settle();
    // 3.6-G N7: the tree read a moment ago is not read again for the next page; the page is on screen, the shell no longer busy.
    expect(treeReads).toBe(1);
    expect((wrapper.get(".document-title-text").element as HTMLTextAreaElement).value).toContain("Druhý článek");
    expect(wrapper.get(".workspace-bar").attributes("aria-busy")).toBe("false");
  });

  it("reads a page pointed at in the tree ahead, and opens it from that read without asking again (3.6-G N2)", async () => {
    const fetchMock = server();
    wrapper = mountApp({ url: "/#/pages/page-1", fetch: fetchMock }); await settle();
    const row = wrapper.findAll(".tree-page").find(link => link.text().includes("Druhý článek"))!;
    row.element.dispatchEvent(new Event("pointerover", { bubbles: true })); await settle();
    const reads = () => fetchMock.mock.calls.map(([path]) => String(path)).filter(path => path.startsWith("/api/pages/page-2?"));
    // Read ahead with its bundle, never recording a visit: only opening a page is one.
    expect(reads()).toEqual(["/api/pages/page-2?bundle=true"]);
    await row.trigger("click"); await settle();
    expect(reads()).toHaveLength(1);
    expect((wrapper.get(".document-title-text").element as HTMLTextAreaElement).value).toContain("Druhý článek");
    expect(fetchMock.mock.calls.some(([path, init]) => path === "/api/pages/page-2/visit" && init?.method === "POST")).toBe(true);
  });

  it("requests no page at start for an address that does not open one", async () => {
    // The address names a page id, but the shell does not route it and shows Knihovna: a request for the page is wasted.
    const fetchMock = server();
    wrapper = mountApp({ url: "/#/pages/page-1?unknown=1", fetch: fetchMock }); await settle();
    expect(fetchMock.mock.calls.map(([path]) => String(path)).filter(path => path.startsWith("/api/pages/page-1"))).toEqual([]);
    expect(wrapper.find(".document-title").exists()).toBe(false);
  });

  it("requests the page at start for the address of its access view", async () => {
    // The spaces never answer, so everything requested here is what start-up asks for before it routes.
    const fetchMock = server(path => path === "/api/spaces" ? new Promise<Response>(() => {}) : undefined);
    wrapper = mountApp({ url: "/#/pages/page-1/access/links", fetch: fetchMock }); await settle();
    expect(fetchMock.mock.calls.map(([path]) => String(path))).toContain("/api/pages/page-1?bundle=true");
  });
});

// Opening a page records a visit. The visit moves the page to the top of the recent list; it changes nothing a page's
// connections show, so recording it must not read them again (and blank the footer's outgoing links while it does).
describe("page open request shape", () => {
  const linked = "6f1c1d6e-3b4a-4c58-9a5e-0d1f2e3a4b5c";
  const connections = "/api/pages/page-1/connections?limit=50";
  const withLink = { ...page, contentJson: JSON.stringify({ ...emptyDocument(), document: { type: "doc", content: [{ type: "paragraph", content: [{ type: "pageReference", attrs: { id: linked, title: "Odkazovaná" } }] }] } }) };
  const { values: memory, storage } = memoryStorage();
  let recordVisit!: () => void;
  let verifications = 0;
  const reading: Route = (path, init) => {
    if (path === "/api/pages/page-1") return response(withLink);
    if (path === connections) return response({ linksHere: { items: [{ id: "page-2", spaceId: "space-1", spaceName: "Engineering", title: "Druhý článek" }], total: 1 } });
    // The first verification of the links in the text answers; a later one stays open, so what the footer shows meanwhile is what a reload blanks.
    if (path === "/api/pages/references" && init?.method === "POST") return ++verifications === 1 ? response({ items: [{ id: linked, title: "Odkazovaná" }] }) : new Promise<Response>(() => {});
    if (path === "/api/pages/page-1/visit") return new Promise<Response>(resolve => { recordVisit = () => resolve(response({ isFavorite: false, visitedAt: "2026-10-10T08:00:00Z" })); });
  };
  const outgoing = (app: VueWrapper) => app.findAll('.page-connections-graph a[title="Odkaz ve stránce"]').map(link => link.text());
  const dockSections = () => document.querySelectorAll("[data-connections-list]").length;
  async function open(width: number) {
    Object.defineProperty(window, "innerWidth", { configurable: true, writable: true, value: width });
    const fetchMock = server(reading);
    wrapper = mountApp({ url: "/#/pages/page-1", fetch: fetchMock }); await settleLazy();
    return fetchMock;
  }
  beforeEach(() => { memory.clear(); verifications = 0; vi.stubGlobal("localStorage", storage); });
  afterEach(() => {
    wrapper?.unmount(); wrapper = undefined;
    useWorkspacePanel().reset(); useAiCapabilities().stop();
    Object.defineProperty(window, "innerWidth", { configurable: true, writable: true, value: 1024 });
  });

  it("reads the connections of the footer once and keeps its links while the visit is recorded", async () => {
    const fetchMock = await open(1024);
    expect(requests(fetchMock, connections)).toBe(1);
    expect(requests(fetchMock, "/api/pages/references")).toBe(1);
    expect(outgoing(wrapper!)).toEqual(["Odkazovaná"]);
    expect(requests(fetchMock, "/api/pages/page-1/visit")).toBe(1);
    recordVisit(); await settleLazy();
    expect(requests(fetchMock, connections)).toBe(1);
    expect(requests(fetchMock, "/api/pages/references")).toBe(1);
    expect(outgoing(wrapper!)).toEqual(["Odkazovaná"]);
  });

  it("reads the connections of the footer and of the open dock at most once each, and not again when the visit is recorded", async () => {
    const fetchMock = await open(1440);
    expect(dockSections()).toBe(1);
    // Fetched with the page, the footer and the dock mount together and share one deduped read; apart, they read once each.
    const reads = requests(fetchMock, connections);
    expect(reads).toBeGreaterThanOrEqual(1);
    expect(reads).toBeLessThanOrEqual(2);
    recordVisit(); await settleLazy();
    expect(requests(fetchMock, connections)).toBe(reads);
  });

  it.each([1440, 1024])("reads a writer's publication once on opening (at %i px: with the dock on Obsah, which hands it to the top bar, or without)", async width => {
    const fetchMock = server((path, init) => path === "/api/pages/page-1/publication"
      ? response({ mode: null, spaceMode: "Manual", effective: "Manual", publishedVersion: 1, publishedAt: "2026-11-01T08:00:00Z", unpublishedChanges: false, canChange: true })
      : reading(path, init));
    Object.defineProperty(window, "innerWidth", { configurable: true, writable: true, value: width });
    wrapper = mountApp({ url: "/#/pages/page-1", fetch: fetchMock }); await settleLazy(); await settleLazy();
    expect(dockSections()).toBe(width > 1280 ? 1 : 0);
    expect(requests(fetchMock, "/api/pages/page-1/publication")).toBe(1);
  });

  it("keeps the connections of the footer and its links when the page is marked as a favorite", async () => {
    // The star changes the reader's own state, which the connections (linking pages, links in the text) do not carry.
    const fetchMock = server((path, init) => path === "/api/pages/page-1/favorite" ? response({ isFavorite: true, visitedAt: null }) : reading(path, init));
    wrapper = mountApp({ url: "/#/pages/page-1", fetch: fetchMock }); await settleLazy();
    expect(requests(fetchMock, connections)).toBe(1);
    await wrapper.get(".favorite-button").trigger("click"); await settleLazy();
    expect(requests(fetchMock, "/api/pages/page-1/favorite")).toBe(1);
    expect(wrapper.get(".favorite-button").attributes("aria-pressed")).toBe("true");
    expect(requests(fetchMock, connections)).toBe(1);
    expect(requests(fetchMock, "/api/pages/references")).toBe(1);
    expect(outgoing(wrapper)).toEqual(["Odkazovaná"]);
  });

  it("lists the page just read first when the reader opens the recent list after the visit", async () => {
    // The recent list is shown with no page open and reads on arrival, so the visit has no list to refresh.
    let visited = false;
    const fetchMock = server(path => path.startsWith("/api/me/library/recent")
      ? response({ items: visited ? [{ id: "page-1", spaceId: "space-1", spaceName: "Engineering", title: "První článek", parentId: null, version: 1, isPublished: true, isFavorite: false, visitedAt: "2026-10-10T08:00:00Z", publishedAt: null }] : [], hasMore: false, nextOffset: null })
      : reading(path));
    wrapper = mountApp({ url: "/#/pages/page-1", fetch: fetchMock }); await settleLazy();
    visited = true; recordVisit(); await settleLazy();
    history.replaceState(null, "", "/#/library/recent"); window.dispatchEvent(new PopStateEvent("popstate")); await settleLazy();
    expect(requests(fetchMock, "/api/me/library/recent?offset=0&limit=20")).toBe(1);
    expect(wrapper.get(".library-items").text()).toContain("První článek");
  });
});

// The editor chunk is most of the cost of the first page, and the address names a page before anything else is known.
// It is requested with the page and not after the answers (spaces, administrator checks) that precede the routing; a
// request that only fills a badge is not waited for at all.
describe("deep link start-up", () => {
  const administrator: Route = path => {
    if (path === "/api/auth/me") return response({ id: "user-1", username: "admin", displayName: "Admin", isWikiAdmin: true, appearance: { layout: "connected", accent: "blue", mode: "system" } });
    if (path === "/api/admin/setup") return response({ pending: false });
  };
  /** Spaces that answer when the test says so: until then nothing is routed. */
  function heldSpaces() {
    let answer!: () => void;
    const route: Route = path => path === "/api/spaces"
      ? new Promise(resolve => { answer = () => resolve(response([{ id: "space-1", name: "Engineering", role: "Writer", icon: "book", color: "blue" }])); })
      : undefined;
    return { route, answer: () => answer() };
  }
  beforeEach(() => { editor.imports = 0; editor.failNext = false; vi.doMock("./components/DocumentEditor.vue", editorModule); });

  it("requests the editor chunk for a page address before the spaces answer, and imports it once", async () => {
    const spaces = heldSpaces();
    wrapper = mountApp({ url: "/#/pages/page-1", fetch: server(spaces.route) }); await settleLazy();
    // The spaces have not answered, so nothing is routed yet and the page is not on screen; the editor is on its way.
    expect(wrapper.find(".document-title").exists()).toBe(false);
    expect(editor.imports).toBe(1);
    spaces.answer(); await settleLazy();
    expect((wrapper.get(".document-title-text").element as HTMLTextAreaElement).value).toContain("První článek");
    expect(wrapper.find(".document-stub").exists()).toBe(true);
    expect(editor.imports).toBe(1);
  });

  it.each(["/#/library/recent", "/#/pages/page-1/access"])("does not request the editor chunk at start for %s, which does not show a page", async (url) => {
    wrapper = mountApp({ url, fetch: server(heldSpaces().route) }); await settleLazy();
    expect(editor.imports).toBe(0);
  });

  it("does not start the page view's warm-up in the middle of a motion, and asks for idle time again when it has ended", async () => {
    // Evaluating the editor takes the main thread for a few frames. 2.6.1: the warm-up of a session that starts on
    // Knihovna landed in a space folding in the rail, 2.7 s in, and the fold dropped three frames at its turn.
    const idle = vi.fn();
    vi.stubGlobal("requestIdleCallback", idle);
    let end!: () => void;
    const moving = { playState: "running", effect: { getComputedTiming: () => ({ endTime: 280 }) }, finished: new Promise<void>(resolve => { end = resolve; }) };
    // A loop (a spinner, the living logo) never ends and is not waited for.
    const loop = { playState: "running", effect: { getComputedTiming: () => ({ endTime: Infinity }) }, finished: new Promise<void>(() => {}) };
    const animations = vi.fn(() => [moving, loop] as unknown as Animation[]);
    (document as { getAnimations?: typeof document.getAnimations }).getAnimations = animations;
    vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout"] });
    try {
      wrapper = mountApp({ url: "/", fetch: server() }); await settle();
      await vi.advanceTimersByTimeAsync(2500);
      const warmUps = () => idle.mock.calls.filter(([, options]) => options?.timeout === 4000);
      expect(warmUps()).toHaveLength(1);
      (warmUps()[0]![0] as () => void)();
      await settleLazy();
      expect(editor.imports).toBe(0);
      expect(warmUps()).toHaveLength(1);
      animations.mockReturnValue([loop] as unknown as Animation[]);
      end(); await settle();
      expect(warmUps()).toHaveLength(2);
      (warmUps()[1]![0] as () => void)();
      await settleLazy();
      expect(editor.imports).toBe(1);
    } finally { vi.useRealTimers(); delete (document as { getAnimations?: unknown }).getAnimations; }
  });

  it("opens the page when the early request of the editor chunk failed", async () => {
    editor.failNext = true;
    const spaces = heldSpaces();
    wrapper = mountApp({ url: "/#/pages/page-1", fetch: server(spaces.route) }); await settleLazy();
    expect(editor.imports).toBe(1);
    // A failed import is not remembered: opening the page asks again.
    spaces.answer(); await settleLazy();
    expect(editor.imports).toBe(2);
    expect((wrapper.get(".document-title-text").element as HTMLTextAreaElement).value).toContain("První článek");
    expect(wrapper.find(".document-stub").exists()).toBe(true);
    expect(wrapper.find(".global-notice").exists()).toBe(false);
  });

  it("shows the first page without waiting for the backup check of an administrator, and the warning when it arrives", async () => {
    let answerBackups!: (warning: string) => void;
    const fetchMock = server(path => path === "/api/admin/backups"
      ? new Promise(resolve => { answerBackups = warning => resolve(response({ warning })); })
      : administrator(path));
    wrapper = mountApp({ url: "/#/pages/page-1", fetch: fetchMock }); await settleLazy();
    expect(requests(fetchMock, "/api/admin/backups")).toBe(1);
    // It is asked after the two reads the first render needs, not before them.
    const asked = fetchMock.mock.calls.map(([path]) => String(path));
    expect(asked.indexOf("/api/admin/backups")).toBeGreaterThan(Math.max(asked.indexOf("/api/admin/setup"), asked.indexOf("/api/spaces")));
    expect((wrapper.get(".document-title-text").element as HTMLTextAreaElement).value).toContain("První článek");
    expect(wrapper.get(".account .settings-nav").attributes("aria-label")).toBeUndefined();
    answerBackups("Zálohování není nastavené."); await settleLazy();
    expect(wrapper.get(".account .settings-nav").attributes("aria-label")).toContain("Zálohování není nastavené.");
  });

  it("does not show the backups as in order on the settings overview before the backup check answers", async () => {
    // The overview reads the warning for its tile, its list of issues and the count in the navigation: no answer yet is not "in order".
    let answerBackups!: (warning: string | null) => void;
    const fetchMock = server(path => path === "/api/admin/backups"
      ? new Promise(resolve => { answerBackups = warning => resolve(response({ warning })); })
      : administrator(path));
    wrapper = mountApp({ url: "/#/settings/security/overview", fetch: fetchMock }); await settleLazy();
    const tile = () => wrapper!.find('[data-stat="backups"]');
    expect(tile().exists() ? tile().text() : "").not.toContain("V pořádku");
    answerBackups("Zálohování není nastavené."); await settleLazy();
    expect(tile().text()).toContain("Vyžadují pozornost");
    expect(tile().text()).toContain("Zálohování není nastavené.");
  });

  it("keeps the warning the settings reported when an older answer of the start-up check arrives afterwards", async () => {
    // The backups page reports its own, newer reading to the shell; the check started with the application is still on its way.
    let startUpCheck!: (warning: string) => void;
    let reads = 0;
    const archives = { settings: { enabled: true, intervalMinutes: 1440, mode: "full", destination: "smb://files.example.test/wiki-backups", username: "backup-service", domain: "EXAMPLE", hasPassword: true, retentionCount: 10 },
      runs: [], nextRunAt: null, lastSuccessAt: null, warning: "Poslední záloha selhala." };
    const mirror = { settings: { enabled: false, remoteUrl: "", branch: "main", publicKey: null, hostKeys: [] }, running: false, progress: null, pendingPages: 0, lastSyncAt: null,
      lastCommit: null, lastError: null, lastErrorAt: null, nextAttemptAt: null, warning: null };
    const fetchMock = server(path => {
      if (path === "/api/admin/backups") return ++reads === 1 ? new Promise(resolve => { startUpCheck = warning => resolve(response({ warning })); }) : response(archives);
      if (path === "/api/admin/backups/git") return response(mirror);
      if (path === "/api/storage-profiles") return response([]);
      return administrator(path);
    });
    wrapper = mountApp({ url: "/#/library/recent", fetch: fetchMock }); await settleLazy();
    history.pushState(null, "", "/#/settings/general/backups"); window.dispatchEvent(new PopStateEvent("popstate")); await settleLazy();
    expect(wrapper.get(".account .settings-nav").attributes("aria-label")).toContain("Poslední záloha selhala.");
    startUpCheck("Zálohování není nastavené."); await settleLazy();
    expect(wrapper.get(".account .settings-nav").attributes("aria-label")).toContain("Poslední záloha selhala.");
  });
});
