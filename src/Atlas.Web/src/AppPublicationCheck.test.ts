import { afterEach, beforeEach, describe, expect, it, vi, type Mock } from "vitest";
import { defineComponent, h, onMounted, watch } from "vue";
import { flushPromises, type VueWrapper } from "@vue/test-utils";
import { toast } from "vue-sonner";
import type { Page } from "./api";
import { jsonResponse as response, mountApp, quietRoutes, resetBrowser, routedFetch, sessionRoutes, type Route } from "./testing/appHarness";

// The check before publishing (package E, plan §9) in the shell: App saves, the check runs on the saved page, and
// Publikovat behaves exactly as before when nothing is found. The editor is a stand-in with the two calls App uses.
const PASSWORD = "Srv#2026!rack";
const editorState = vi.hoisted(() => ({ content: "", reveal: undefined as unknown as Mock<(target: unknown) => boolean> }));
vi.mock("./components/DocumentEditor.vue", () => ({ __esModule: true, default: defineComponent({
  props: ["value", "readonly"], emits: ["ready", "change", "collaborationStatus", "editIntent"],
  setup(props, { emit, expose }) {
    expose({ focus: () => true, capture: () => ({ contentJson: editorState.content, markdown: "" }), reveal: (target: unknown) => editorState.reveal(target) });
    watch(() => props.readonly, () => emit("collaborationStatus", { enabled: true, canSave: true }));
    onMounted(() => { emit("ready"); emit("collaborationStatus", { enabled: true, canSave: true }); });
    return () => h("div", { class: "editor-stand-in" });
  },
}) }));

const rich = (...content: unknown[]) => JSON.stringify({ format: "atlas-rich", version: 1, document: { type: "doc", content } });
const text = (value: string) => ({ type: "text", text: value });
const withPassword = rich({ type: "heading", attrs: { level: 2 }, content: [text("Přístup")] }, { type: "paragraph", content: [text(`Účet admin, heslo ${PASSWORD}`)] });
const plain = rich({ type: "paragraph", content: [text("Postup bez nálezů.")] });
const page: Page = { id: "page-1", spaceId: "space-1", parentId: null, title: "VPN z mobilu", version: 2,
  isPublished: false, publishedVersion: null, contentJson: plain, activeFrom: "2026-10-04T08:00:00Z", canEdit: true };

let wrapper: VueWrapper | undefined;
// A publication is confirmed in a toast; the header keeps only the state chip (D4).
let toasted = vi.spyOn(toast, "success");
beforeEach(() => { toasted = vi.spyOn(toast, "success"); });
let requests: { method: string; path: string; body: string }[] = [];
// Notes every request, before any route looks at it, and answers none.
const record: Route = (path, init) => {
  requests.push({ method: init?.method ?? "GET", path, body: typeof init?.body === "string" ? init.body : "" });
  return undefined;
};
async function start(content: string, check: Route, me: Record<string, unknown> = {}) {
  requests = [];
  editorState.content = content;
  editorState.reveal = vi.fn((_target: unknown) => true);
  const team: Route = (path, init) => {
    if (path === "/api/pages/page-2/publish") return response({ ...page, id: "page-2", title: "Other draft", isPublished: true });
    if (path === "/api/pages/page-2" && init?.method === "PUT") return response({ ...page, id: "page-2", title: "Other draft", version: 4 });
    if (path === "/api/pages/page-2") return response({ ...page, id: "page-2", title: "Other draft" });
    if (path.startsWith("/api/pages/page-2/connections")) return response({ linksHere: { items: [], total: 0 } });
    if (path.startsWith("/api/pages/page-1/publication-check")) { const answer = check(path, init); if (answer) return answer; }
    if (path === "/api/pages/page-1" && init?.method === "PUT") {
      const body = JSON.parse(init.body as string);
      return response({ ...page, title: body.title, contentJson: body.contentJson, version: 3 });
    }
    if (path === "/api/pages/page-1/publish") return response({ ...page, version: 3, isPublished: true, publishedVersion: 3 });
    if (path === "/api/auth/me") return response({ id: "user-1", username: "writer", displayName: "Writer", isWikiAdmin: false, appearance: { layout: "connected", accent: "blue", mode: "system" }, ...me });
    if (path === "/api/spaces") return response([{ id: "space-1", name: "Team", role: "Writer", icon: "book", color: "blue" }]);
    if (path === "/api/pages/page-1") return response(page);
    if (path === "/api/spaces/space-1/tree") return response({ spaceId: "space-1", version: 1, items: [{ ...page, sortOrder: 1024, canEdit: true }] });
    if (path === "/api/spaces/space-1/pages") return response([page]);
    if (path.startsWith("/api/pages/page-1/connections")) return response({ linksHere: { items: [], total: 0 } });
  };
  wrapper = mountApp({ url: "/#/pages/page-1", fetch: routedFetch(record, team, sessionRoutes, quietRoutes) });
  await flushPromises(); await flushPromises(); await flushPromises();
  // No edit mode to enter: the page is saved but unpublished, which is when Publikovat shows beside the lock.
  await vi.dynamicImportSettled();
  await flushPromises();
  return wrapper;
}
const settle = async () => { await flushPromises(); await new Promise(resolve => setTimeout(resolve, 30)); await flushPromises(); };
const publishClick = async (app: VueWrapper) => { await app.get(".document-actions .publish-main").trigger("click"); await settle(); };
const panel = () => document.querySelector<HTMLElement>('[role="dialog"][aria-label="Před publikací"]');
const panelButton = (name: string) => [...panel()!.querySelectorAll<HTMLButtonElement>("button")].find(button => button.textContent?.trim() === name)!;
const findings = () => [...panel()!.querySelectorAll("li.publish-finding strong")].map(row => row.textContent?.trim());
const writes = () => requests.filter(request => (request.method !== "GET" && !request.path.endsWith("/visit")) || request.path.includes("publication-check"))
  .map(request => `${request.method} ${request.path}`);
const published = () => requests.filter(request => request.path === "/api/pages/page-1/publish");
const linkCheck = (links: unknown[]): Route => () => Promise.resolve(response({ version: 3, links }));

afterEach(() => { wrapper?.unmount(); wrapper = undefined; resetBrowser(); });

describe("publication check in the shell", () => {
  it.each([{ links: [] }, { links: [{ targetPageId: "missing", state: "unavailable", title: null }] }])("discards a publication check after navigating to another page (%j)", async ({ links }) => {
    let complete!: (response: Response) => void;
    const app = await start(plain, () => new Promise<Response>(resolve => { complete = resolve; }));
    await publishClick(app);
    expect(requests.some(request => request.path.includes("/publication-check"))).toBe(true);
    history.pushState(null, "", "/#/pages/page-2");
    window.dispatchEvent(new PopStateEvent("popstate"));
    await settle();
    expect((app.get(".document-title-text").element as HTMLTextAreaElement).value).toBe("Other draft");
    complete(response({ version: 3, links }));
    await settle();
    expect(requests.filter(request => request.path.endsWith("/publish"))).toEqual([]);
    expect(panel()).toBeNull();
  });
  it("publishes at once with the same request as today when nothing is found", async () => {
    const app = await start(plain, linkCheck([]));
    await publishClick(app);
    // Saved once, checked on the saved version, published with the body it always had.
    expect(writes()).toEqual([
      "PUT /api/pages/page-1",
      "GET /api/pages/page-1/publication-check?expectedVersion=3",
      "POST /api/pages/page-1/publish",
    ]);
    expect(JSON.parse(published()[0]!.body)).toEqual({ expectedVersion: 3 });
    expect(panel()).toBeNull();
    expect(toasted).toHaveBeenCalledWith("Stránka byla publikována.", expect.anything());
  });

  it("warns about a password with assist switched off, goes back to it and publishes anyway", async () => {
    // Protecting passwords at publication is always on: it does not depend on the assist module (§0).
    const app = await start(withPassword, linkCheck([]), { features: { editorAssist: false } });
    await publishClick(app);
    expect(published()).toHaveLength(0);
    expect(findings()).toEqual(["Možné heslo v textu (sekce Přístup)"]);
    expect(panelButton("Publikovat i tak")).toBeDefined();
    panelButton("Zpět k úpravám").click(); await settle();
    expect(panel()).toBeNull();
    expect(editorState.reveal).toHaveBeenCalledExactlyOnceWith({ kind: "text", path: [1], from: 18, to: 18 + PASSWORD.length });
    expect(published()).toHaveLength(0);

    await publishClick(app);
    panelButton("Publikovat i tak").click(); await settle();
    expect(published()).toHaveLength(1);
    expect(JSON.parse(published()[0]!.body)).toEqual({ expectedVersion: 3 });
    // The first Publikovat checkpointed the page; nothing changed since, so neither the second nor confirming the findings saves again.
    expect(writes().filter(write => write.startsWith("PUT"))).toHaveLength(1);
    expect(toasted).toHaveBeenCalledWith("Stránka byla publikována.", expect.anything());
  });

  it("lists dead and unpublished links from the server and an empty section", async () => {
    const content = rich({ type: "heading", attrs: { level: 2 }, content: [text("Poznámky")] });
    const app = await start(content, linkCheck([
      { targetPageId: "0a1b2c3d-0000-0000-0000-000000000001", state: "unavailable", title: null },
      { targetPageId: "0a1b2c3d-0000-0000-0000-000000000002", state: "unpublished", title: "Staré VPN" },
    ]));
    await publishClick(app);
    expect(findings()).toEqual(["Odkaz na nedostupnou stránku", "Odkaz na nepublikovanou stránku „Staré VPN“", "Prázdná sekce „Poznámky“"]);
    expect(panel()!.querySelector('a[href="#/pages/0a1b2c3d-0000-0000-0000-000000000002"]')!.textContent).toContain("Otevřít");
    const show = [...panel()!.querySelectorAll<HTMLButtonElement>("li.publish-finding button")][1]!;
    show.click(); await settle();
    expect(editorState.reveal).toHaveBeenCalledExactlyOnceWith({ kind: "page", pageId: "0a1b2c3d-0000-0000-0000-000000000002" });
    expect(published()).toHaveLength(0);
  });

  it("publishes when the server check fails or does not answer within its budget", async () => {
    const failing = await start(plain, () => Promise.resolve(response({ title: "Chyba" }, 500)));
    await publishClick(failing);
    expect(published()).toHaveLength(1);
    expect(panel()).toBeNull();
    wrapper!.unmount(); wrapper = undefined; document.body.innerHTML = "";

    // Date too: the budget's deadline is Date.now() + 1500, and real time passing before its timer starts shortened it (CI run 155).
    vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout", "Date"] });
    const silent = await start(plain, () => new Promise<Response>(() => {}));
    await silent.get(".document-actions .publish-main").trigger("click");
    await vi.advanceTimersByTimeAsync(1499); await flushPromises();
    expect(published()).toHaveLength(0);
    await vi.advanceTimersByTimeAsync(1); await flushPromises(); await flushPromises();
    expect(published()).toHaveLength(1);
    expect(panel()).toBeNull();
  });

  it("never sends or logs the detected value outside the page's own save", async () => {
    const logged: unknown[] = [];
    for (const method of ["log", "info", "warn", "error", "debug"] as const)
      vi.spyOn(console, method).mockImplementation((...args: unknown[]) => { logged.push(...args); });
    const app = await start(withPassword, linkCheck([]));
    await publishClick(app);
    panelButton("Publikovat i tak").click(); await settle();
    expect(published()).toHaveLength(1);
    // The draft save carries the page as the author wrote it; nothing else does: not the check, not the publication.
    const others = requests.filter(request => !(request.method === "PUT" && request.path === "/api/pages/page-1"));
    expect(others.filter(request => `${request.path} ${request.body}`.includes(PASSWORD))).toEqual([]);
    expect(JSON.stringify(logged.map(String))).not.toContain(PASSWORD);
    expect(document.body.innerHTML).not.toContain(PASSWORD);
  });
});
