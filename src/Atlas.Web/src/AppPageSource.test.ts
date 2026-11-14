import { afterEach, describe, expect, it, vi } from "vitest";
import { defineComponent, h, onMounted } from "vue";
import { flushPromises, type VueWrapper } from "@vue/test-utils";
import { toast } from "vue-sonner";
import type { Page } from "./api";
import { emptyDocument } from "./document";
import { jsonResponse as response, mountApp, quietRoutes, resetBrowser, routedFetch, sessionRoutes, type Route } from "./testing/appHarness";

// The page source feature from the host's side: who gets it, flush before open, save through the endpoint,
// remount like a restore, and the unsaved-work guards. The editor is a fake that records host calls.
const editor = vi.hoisted(() => ({
  mounts: 0, calls: [] as string[], props: undefined as Record<string, unknown> | undefined,
  emit: undefined as undefined | ((event: string, ...args: unknown[]) => void),
  resolveSource: true,
}));
vi.mock("./components/DocumentEditor.vue", () => ({ __esModule: true, default: defineComponent({
  props: ["value", "readonly", "canEditSource", "saveSource", "pages"],
  emits: ["ready", "change", "collaborationStatus", "sourceDraft", "requestSource", "importTitle", "editIntent"],
  setup(props, { emit, expose }) {
    editor.mounts++;
    editor.props = props;
    editor.emit = emit as (event: string, ...args: unknown[]) => void;
    expose({
      capture: () => ({ contentJson: JSON.stringify(props.value), markdown: "", collaborationGeneration: "generation-1" }),
      openSource: () => { editor.calls.push("openSource"); return true; },
      // Like the real editor: once the draft is applied or dropped (true) it reports that it is no longer open.
      resolveSourceDraft: async () => { editor.calls.push("resolveSourceDraft"); if (editor.resolveSource) emit("sourceDraft", false); return editor.resolveSource; },
      discardSourceDraft: () => { editor.calls.push("discardSourceDraft"); },
      resolveDetailDraft: async () => true, discardDetailDraft: () => {},
      focus: () => { editor.calls.push(props.readonly ? "focus-readonly" : "focus"); return !props.readonly; },
    });
    onMounted(() => { emit("ready"); emit("collaborationStatus", { enabled: true, canSave: true }); });
    return () => h("div", { class: "editor-fake" });
  },
}) }));

const page: Page = { id: "page-1", spaceId: "space-1", parentId: null, title: "Runbook", version: 2,
  isPublished: true, publishedVersion: 1, contentJson: JSON.stringify(emptyDocument()), activeFrom: "2026-01-01T08:00:00Z", canEdit: true };
const fixed = { format: "atlas-rich", version: 1, document: { type: "doc", content: [{ type: "paragraph", content: [{ type: "text", text: "Fixed" }] }] } };
let wrapper: VueWrapper | undefined;
const requests: { path: string; method: string; body?: unknown }[] = [];
// Notes every request, before any route looks at it, and answers none.
const record: Route = (path, init) => {
  requests.push({ path, method: init?.method ?? "GET", body: init?.body ? JSON.parse(init.body as string) : undefined });
  return undefined;
};
async function start(role: string, custom?: Route, wikiAdmin = false) {
  editor.mounts = 0; editor.calls = []; editor.resolveSource = true; requests.length = 0;
  const team: Route = (path, init) => {
    if (path.includes("/history?")) return response({ pageId: page.id, currentVersion: 2, items: [], nextBeforeVersion: null });
    if (path === "/api/admin/setup") return response({ pending: false });
    if (path === "/api/admin/backups") return response({ warning: null });
    if (path === "/api/auth/me") return response({ id: "user-1", username: "admin", displayName: "Admin", isWikiAdmin: wikiAdmin, appearance: { layout: "connected", accent: "blue", mode: "system" } });
    if (path === "/api/spaces") return response([{ id: "space-1", name: "Team", role, icon: "book", color: "blue" }]);
    if (path === "/api/pages/page-1" && (init?.method ?? "GET") === "GET") return response(page);
    if (path === "/api/pages/page-1" && init?.method === "PUT") return response({ ...page, ...JSON.parse(init.body as string), version: 3 });
    if (path === "/api/spaces/space-1/tree") return response({ spaceId: "space-1", version: 1, items: [{ ...page, sortOrder: 1024, canEdit: true }] });
    if (path === "/api/spaces/space-1/pages") return response([page]);
  };
  wrapper = mountApp({ url: "/#/pages/page-1", fetch: routedFetch(record, custom, team, sessionRoutes, quietRoutes) });
  await flushPromises(); await flushPromises(); await flushPromises();
  return wrapper;
}
async function menuItem(app: VueWrapper, label: string) {
  await app.get('[aria-label="Další akce stránky"][aria-expanded]').trigger("click");
  await new Promise(resolve => setTimeout(resolve, 40));
  return [...document.querySelectorAll<HTMLElement>('[role="menuitem"], button')].find(item => item.textContent?.trim() === label);
}
async function closeMenu() { document.dispatchEvent(new KeyboardEvent("keydown", { key: "Escape", bubbles: true })); await flushPromises(); }
type SaveSource = (value: unknown, markdown: string, signal: AbortSignal) => Promise<Record<string, unknown>>;
const saveSource = () => editor.props!.saveSource as SaveSource;
afterEach(() => { wrapper?.unmount(); wrapper = undefined; resetBrowser(); });
/** A button of the open UnsavedChangesDialog (Zůstat, Zahodit, Uložit). */
const leaveButton = (label: string) => [...document.querySelectorAll<HTMLButtonElement>('dialog[open] button')].find(item => item.textContent?.trim() === label);

/** The caret enters the page's text (3.6: the editor is always editable, there is no Upravit button). */
const beginEditing = async (app: VueWrapper) => {
  const text = document.createElement("div"); text.className = "ProseMirror"; text.setAttribute("contenteditable", "true");
  app.get(".document-body").element.append(text); text.dispatchEvent(new FocusEvent("focusin", { bubbles: true })); text.remove();
  await flushPromises();
};

describe("page source in the host", () => {
  it("gives the source to space and wiki admins as the Zdroj view of editing, with no separate page action", async () => {
    let app = await start("Writer");
    expect(editor.props!.canEditSource).toBe(false);
    app.unmount(); wrapper = undefined; document.body.innerHTML = "";

    app = await start("Writer", undefined, true);
    expect(editor.props!.canEditSource).toBe(true);
    app.unmount(); wrapper = undefined; document.body.innerHTML = "";

    app = await start("Admin");
    expect(editor.props!.canEditSource).toBe(true);
    expect(editor.props!.pages).toEqual([expect.objectContaining({ id: "page-1", title: "Runbook" })]);
    // The Dokument | Zdroj switch is the way in; the page menu has no source action of its own.
    expect(await menuItem(app, "Upravit zdroj stránky")).toBeUndefined();
    await closeMenu();
    // Asked for while the editor is not attached, the host attaches it first and then opens the source over it.
    editor.emit!("requestSource"); await flushPromises(); await flushPromises();
    expect(editor.calls).toEqual(["focus", "openSource"]);
    expect(requests.some(request => request.method === "PUT")).toBe(false);
  });

  it("saves the author's own pending edits before opening the source", async () => {
    const app = await start("Admin");
    await beginEditing(app);
    editor.emit!("change"); await flushPromises();
    const order: string[] = [];
    const fetch = vi.mocked(globalThis.fetch);
    const inner = fetch.getMockImplementation()!;
    fetch.mockImplementation(async (input, init) => { if (init?.method === "PUT") order.push(`PUT ${String(input)}`); return inner(input, init); });
    const originalOpen = editor.calls;
    editor.emit!("requestSource"); await flushPromises(); await flushPromises();
    expect(order).toEqual(["PUT /api/pages/page-1"]);
    expect(originalOpen.filter(call => call !== "focus")).toEqual(["openSource"]);
    expect(requests.find(request => request.method === "PUT")!.body).toMatchObject({ expectedVersion: 2, collaborationGeneration: "generation-1" });
  });

  it("replaces the page through PUT /source and remounts the editor like a history restore", async () => {
    const success = vi.spyOn(toast, "success");
    const app = await start("Admin", (path, init) => path === "/api/pages/page-1/source" && init?.method === "PUT"
      ? Promise.resolve(response({ ...page, ...JSON.parse(init.body as string), version: 3 })) : undefined);
    await beginEditing(app);
    expect(editor.mounts).toBe(1);
    const result = await saveSource()(fixed, "Fixed", new AbortController().signal);
    await flushPromises();
    expect(result).toEqual({ ok: true });
    const put = requests.find(request => request.path === "/api/pages/page-1/source")!;
    expect(put.method).toBe("PUT");
    expect(put.body).toEqual({ contentJson: JSON.stringify(fixed), markdown: "Fixed", expectedVersion: 2 });
    expect(editor.mounts).toBe(2);
    expect(JSON.parse(JSON.stringify(editor.props!.value))).toEqual(fixed);
    expect(success).toHaveBeenCalledWith(expect.stringContaining("Zdroj stránky byl uložen"), expect.anything());
  });

  it("keeps the draft on a version conflict, retries against the current version and reports others' live edits", async () => {
    let refreshed = false;
    const app = await start("Admin", (path, init) => {
      if (path === "/api/pages/page-1/source") {
        const body = JSON.parse(init!.body as string);
        if (body.expectedVersion === 2) return Promise.resolve(response({ title: "Konflikt", detail: "Stránku mezitím změnil jiný uživatel." }, 409));
        return Promise.resolve(response({ title: "Živé úpravy", detail: "Neuložené živé úpravy.", code: "collaboration_unsaved" }, 409));
      }
      if (path === "/api/pages/page-1" && (init?.method ?? "GET") === "GET" && refreshed) return Promise.resolve(response({ ...page, version: 5, contentJson: JSON.stringify(fixed) }));
    });
    await beginEditing(app);
    refreshed = true;
    const signal = new AbortController().signal;
    const conflict = await saveSource()(fixed, "Fixed", signal);
    expect(conflict).toEqual({ ok: false, reason: "conflict" });
    expect(editor.mounts).toBe(1);
    const live = await saveSource()(fixed, "Fixed", signal);
    expect(live).toEqual({ ok: false, reason: "live-edits" });
    expect(requests.filter(request => request.path === "/api/pages/page-1/source").map(request => (request.body as { expectedVersion: number }).expectedVersion)).toEqual([2, 5]);
    expect(editor.mounts).toBe(1);
    expect(app.find(".editor-fake").exists()).toBe(true);
  });

  // 3.6-0, rule 2: the page asks nothing for its own changes, but the source draft is a component's own unconfirmed input.
  it("guards leaving and unloading while a source draft is unsaved: the source asks about it, nothing else does", async () => {
    const app = await start("Admin");
    await beginEditing(app);
    editor.emit!("sourceDraft", true); await flushPromises();

    const unload = new Event("beforeunload", { cancelable: true });
    window.dispatchEvent(unload);
    expect(unload.defaultPrevented).toBe(true);

    editor.resolveSource = false;
    await app.get(".breadcrumb-root").trigger("click"); await flushPromises();
    expect(editor.calls).toContain("resolveSourceDraft");
    expect(leaveButton("Zůstat")).toBeUndefined();
    expect(app.find(".editor-fake").exists()).toBe(true);

    editor.resolveSource = true;
    await app.get(".breadcrumb-root").trigger("click"); await flushPromises(); await flushPromises();
    expect(editor.calls.filter(call => call === "resolveSourceDraft")).toHaveLength(2);
    expect(app.find(".editor-fake").exists()).toBe(false);
  });

  // Locking ends the source view; its unsaved draft must not go with it unasked.
  it("asks about an unsaved source draft before locking the page and stays unlocked while the reader keeps it", async () => {
    const app = await start("Admin");
    await beginEditing(app);
    editor.emit!("sourceDraft", true); await flushPromises();

    editor.resolveSource = false;
    await app.get("[data-edit-lock]").trigger("click"); await flushPromises();
    expect(editor.calls).toContain("resolveSourceDraft");
    expect(editor.props!.readonly).toBe(false);

    editor.resolveSource = true;
    await app.get("[data-edit-lock]").trigger("click"); await flushPromises();
    expect(editor.props!.readonly).toBe(true);
  });

  it("fills an empty title from imported Markdown only while the editor takes input", async () => {
    const app = await start("Admin", (path, init) => path === "/api/pages/page-1" && (init?.method ?? "GET") === "GET" ? Promise.resolve(response({ ...page, title: "" })) : undefined);
    const input = () => app.get('[aria-label="Název stránky"]').element as HTMLTextAreaElement;
    editor.emit!("importTitle", "Imported"); await flushPromises();
    expect(input().value).toBe("");
    await beginEditing(app);
    editor.emit!("importTitle", "Imported"); await flushPromises();
    expect(input().value).toBe("Imported");
    expect(app.get("[data-page-save-state]").text()).toContain("Ukládám");
  });
});
