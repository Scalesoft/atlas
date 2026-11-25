import { afterEach, beforeAll, describe, expect, it, onTestFinished, vi } from "vitest";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { flushPromises, type VueWrapper } from "@vue/test-utils";
import { checkSessionAfterLiveFailure } from "./api";
import { useAiCapabilities } from "./composables/useAiCapabilities";
import { useWorkspacePanel } from "./composables/useWorkspacePanel";
import { useAssistant } from "./assistant/useAssistant";
import { emptyDocument } from "./document";
import { MARKDOWN_LEAVE_START_EVENT } from "./keyboard/editorEvents";
import { treeExpansionKey } from "./treeExpansion";
import { draftKeeper, drafts, holdUnsavedWork } from "./lib/unsavedWork";
import DocumentEditor, { codeBlockHighlightingReady } from "./components/DocumentEditor.vue";
import { jsonResponse as response, memoryStorage, mountApp as mountShell, quietRoutes, resetBrowser, routedFetch, sessionRoutes, type Route } from "./testing/appHarness";
// These shell tests exercise local draft/publication responses. Transport and
// editor collaboration lifecycle have their own component/integration tests.
vi.mock("./collaboration", async (original) => ({
  ...await original<typeof import("./collaboration")>(),
  joinPageCollaboration: vi.fn(() => new Promise(() => {})),
}));

// Action menus are portalled and only exist while open, so their items are found
// in the document rather than inside the trigger's container. Every assertion
// that used to walk `.action-disclosure button` uses this instead.
const settleMenu = () => new Promise(resolve => setTimeout(resolve, 40));
const menuItems = () => [...document.querySelectorAll<HTMLElement>('[role=menu] [role=menuitem]')];
/** A button of the open UnsavedChangesDialog (Zůstat, Zahodit, Uložit); undefined while it is not asking. */
const leaveButton = (label: string) => [...document.querySelectorAll<HTMLButtonElement>('dialog[open] button')].find(item => item.textContent?.trim() === label);
/** The title field's text (a writer's title is a textarea, not a heading; 3.6-A). */
const titleOf = (app: VueWrapper) => (app.get('.document-title-text').element as HTMLTextAreaElement).value;
/** The collaboration session as the editor reports it once it has joined and synced: only then a checkpoint can be saved (3.6-B). */
const synced = { enabled: true, canSave: true, connection: 'online', pendingChanges: 0, projectionSave: 'current', error: null, generation: 'session' };
/**
 * The reader points into the text: the live editor attaches at once (3.6-F), there is no button to press. With `ready` the editor
 * also reports its session synced, which these shell tests drive themselves (the collaboration join never settles in them).
 */
async function startEditing(app: VueWrapper, ready = true) {
  // The caret enters the page's text (3.6: the editor is always editable, so a click just focuses it).
  const text = document.createElement('div'); text.className = 'ProseMirror'; text.setAttribute('contenteditable', 'true');
  app.get('.document-body').element.append(text); text.dispatchEvent(new FocusEvent('focusin', { bubbles: true })); text.remove(); await flushPromises();
  const editor = app.getComponent(DocumentEditor);
  if (ready) { editor.vm.$emit('ready'); editor.vm.$emit('collaborationStatus', { ...synced }); await flushPromises(); }
  return editor;
}
const puts = (fetchMock: ReturnType<typeof requestMock>) => fetchMock.mock.calls.filter(([path, init]) => path === `/api/pages/${page.id}` && init?.method === 'PUT');
async function openNewSpaceForm(app: VueWrapper) {
  await app.get('.sidebar-spaces-heading [aria-label="Nový prostor"]').trigger('click'); await flushPromises();
}

const space = {
  id: "space-1",
  name: "Engineering",
  icon: "code-xml",
  color: "green",
  role: "Admin",
  pinPolicy: "None",
  pinned: true,
  pageCount: 1,
  lastChangedAt: null,
};
const page = {
  id: "page-1",
  spaceId: space.id,
  parentId: null,
  title: "Přihlášení",
  version: 1,
  isPublished: true,
  contentJson: JSON.stringify(emptyDocument()),
  markdown: "",
  publishedVersion: 1,
  // The page carries the writer's right itself; the lock and the editor attach only where it is set.
  canEdit: true,
};
let wrapper: VueWrapper | undefined;
const { values: storageValues, storage: localStorageMock } = memoryStorage();
/** The sidebar opens these spaces' trees at start, as if the account had expanded them before. */
function rememberOpenedSpaces(userId: string, ...spaceIds: string[]) {
  storageValues.set(treeExpansionKey(userId), JSON.stringify({ version: 2, openedSpaces: spaceIds, collapsedPages: [] }));
}
// A wiki administrator, the one space and its one page.
const wiki: Route = (path) => {
  if (path === `/api/spaces/${space.id}/tree`) return response({ spaceId: space.id, version: 1, items: [{ ...page, sortOrder: 1024, canEdit: true }] });
  // Obsah of the space (GET /api/spaces/{id}/contents): its one page.
  if (path.startsWith(`/api/spaces/${space.id}/contents`)) {
    return response({ items: [{ id: page.id, typeId: "atlas.page", title: page.title, path: null, scope: "space", spaceId: space.id, usedCount: 0, linksIn: 0, linksOut: 0, comments: 0, updatedAt: "2026-10-27T08:00:00Z", canEdit: true }],
      summary: { types: { "atlas.page": 1 }, relations: { unused: 0, noIncoming: 1, broken: 0, attached: 0 } }, nextCursor: null });
  }
  if (path === "/api/ai/capabilities") return response({ semanticSearch: false, related: false, editorLinks: false, createSuggestions: false });
  if (path === "/api/admin/setup") return response({ pending: false });
  if (path === "/api/admin/backups") return response({ warning: null });
  if (path === "/api/auth/me")
    return response({
      id: "user-1",
      username: "admin",
      displayName: "Test Admin",
      isWikiAdmin: true,
      appearance: { layout: "connected", accent: "blue", mode: "system" },
    });
  if (path.startsWith("/api/analytics/activity?")) return response({ revisions: 0, pages: 0, days: [], authors: [] });
  if (path.endsWith('/tags')) return response({ tags: [] });
  if (path.startsWith('/api/tags?')) return response({ items: [] });
  if (path === "/api/spaces") return response([space]);
  if (path === `/api/spaces/${space.id}/pages`) return response([page]);
  if (path === `/api/spaces/${space.id}/page-templates`) return response([]);
  if (path === `/api/pages/${page.id}`) return response(page);
};
// The search filters: a space FilterMenu and OptionPills groups (Upraveno, Obsah, Řazení) found by their label.
function searchPillOf(app: VueWrapper, group: string, value: string) {
  return app.findAll(".search-filters .option-pills").find(each => each.get(".option-pills-label").text() === group)!.get(`[data-value="${value}"]`);
}
const searchPill = (app: VueWrapper, group: string, value: string) => searchPillOf(app, group, value).trigger("click");
async function pickSearchSpace(app: VueWrapper, id: string) {
  await app.get("[data-search-space]").trigger("click");
  if (vi.isFakeTimers()) await vi.advanceTimersByTimeAsync(40); else await new Promise(resolve => setTimeout(resolve, 40));
  await flushPromises();
  document.querySelector<HTMLElement>(`.filter-menu-panel [data-value="${id}"]`)!.click();
  await flushPromises();
}
function requestMock(custom?: Route) {
  return routedFetch(custom, wiki, sessionRoutes, quietRoutes);
}
async function mountApp(fetchMock: ReturnType<typeof requestMock>) {
  vi.stubGlobal("localStorage", localStorageMock);
  wrapper = mountShell({ url: "/", fetch: fetchMock });
  await flushPromises();
  await flushPromises();
  return wrapper;
}
// Views behind defineAsyncComponent load as their own chunks, cold on their first use in this file (half a
// second idle, over a second under CPU load). Wait for those imports themselves, never for a wall-clock budget
// such as vi.waitFor's default 1 s, then let the rendered view and its mocked requests settle.
async function settleLazyViews() { await vi.dynamicImportSettled(); await flushPromises(); await flushPromises(); }
// App's editor loader waits (up to 1.5 s) for code highlighting, whose language chunks load cold on first use
// (slowest with the editor source). Settle it once so opening a page takes only the flushes these tests use.
beforeAll(() => codeBlockHighlightingReady);
// The settings are chunks in three levels: the host, the global and the space surface, and the pages the tests below
// open (the access section and the page templates of a space; the landing pages come with their surface). The cold
// imports together hold what the one settings chunk did, 2-2.6 s idle and up to 4.5 s under CPU load, most of a
// test's 5 s budget, in whichever test opens settings first. Import them once up front.
beforeAll(() => Promise.all([
  import("./components/settings/SettingsHost.vue"),
  import("./components/settings/global/GlobalSettings.vue"),
  import("./components/settings/space/SpaceSettings.vue"),
  import("./components/settings/space/SpaceAccessPage.vue"),
  import("./components/PageTemplatesPage.vue"),
]));
beforeAll(() => import("./components/assistant/AssistantPanel.vue"));
afterEach(() => {
  wrapper?.unmount();
  wrapper = undefined;
  resetBrowser();
  storageValues.clear();
});

describe("navigation responses belong to the authenticated account", () => {
  it("renders no ai surface while every capability is false", async () => {
    const fetchMock = requestMock();
    const app = await mountApp(fetchMock);
    await app.get('.sidebar-space-link').trigger('click'); await flushPromises();
    await app.get('.tree-page').trigger('click'); await flushPromises();
    // Loaded with the session, once, and all false: AI-24 says there is then no AI element at all.
    expect(fetchMock.mock.calls.filter(([url]) => String(url) === "/api/ai/capabilities")).toHaveLength(1);
    expect(app.element.querySelectorAll('[data-ai-glyph], [class*="atlas-ai-"], [data-ai-surface]')).toHaveLength(0);
    expect([...app.element.querySelectorAll('*')].some(el => [...el.attributes].some(a => a.name.startsWith('data-ai-')))).toBe(false);
    expect(app.text()).not.toContain("Významová shoda");
    expect(fetchMock.mock.calls.some(([url]) => /\/api\/(assist|admin\/ai)/.test(String(url)))).toBe(false);
  });

  it("shows collaborators in the page bar and removes them when the session disconnects", async () => {
    const app = await mountApp(requestMock());
    await app.get('.sidebar-space-link').trigger('click'); await flushPromises();
    await app.get('.tree-page').trigger('click'); await flushPromises();
    const editor = await startEditing(app);
    editor.vm.$emit('collaborationPeers', [{ clientId: 23, name: 'Jana Nováková', color: '#5274bd', cursorAvailable: true }]);
    await flushPromises();
    expect(app.get('.document-actions [data-client-id="23"]').attributes('aria-label')).toContain('Jana Nováková');
    editor.vm.$emit('collaborationStatus', { enabled: true, canSave: false, connection: 'reconnecting', pendingChanges: 0, projectionSave: 'current', error: null, generation: 'session' });
    await flushPromises();
    expect(app.find('.document-actions [data-client-id="23"]').exists()).toBe(false);
  });
  it("opens a tree reference with an anchor at that place, whether it is a heading or a requirement block", async () => {
    const reference = { id: "ref-1", spaceId: space.id, parentId: null, type: "reference", title: "Požadavek", version: 0, isPublished: true, sortOrder: 2048, canEdit: true,
      targetId: page.id, targetTypeId: "atlas.page", anchor: "req-1" };
    const app = await mountApp(requestMock(path => path === `/api/spaces/${space.id}/tree`
      ? Promise.resolve(response({ spaceId: space.id, version: 1, items: [{ ...page, sortOrder: 1024, canEdit: true }, reference] })) : undefined));
    await app.get('.sidebar-space-link').trigger('click'); await flushPromises();
    await app.get('.tree-page').trigger('click'); await flushPromises();
    const reveal = vi.spyOn(app.getComponent(DocumentEditor).vm.$.exposed as { revealBlock(id: string): void }, "revealBlock");
    await app.get('[data-tree-item="ref-1"]').trigger('click'); await flushPromises();
    expect(reveal).toHaveBeenCalledWith("req-1");
  });
  it("shows the server backup warning beside Správa wiki for an administrator", async () => {
    const app = await mountApp(requestMock(path => path === "/api/admin/backups" ? Promise.resolve(response({ warning: "Zálohování není nastavené." })) : undefined));
    const settings = app.get(".account .settings-nav");
    expect(settings.attributes("aria-label")).toBe("Správa wiki: Zálohování není nastavené.");
    expect(settings.get(".sr-only").text()).toBe("Správa wiki");
    expect(settings.findAll("svg")).toHaveLength(2);
  });

  it("refreshes the administrator backup warning when the window regains focus", async () => {
    let reads = 0;
    const fetchMock = requestMock(path => path === "/api/admin/backups"
      ? Promise.resolve(response({ warning: ++reads === 1 ? null : "Poslední záloha selhala." }))
      : undefined);
    const app = await mountApp(fetchMock);
    expect(app.get(".account .settings-nav").attributes("aria-label")).toBeUndefined();
    window.dispatchEvent(new Event("focus"));
    await flushPromises();
    expect(app.get(".account .settings-nav").attributes("aria-label")).toContain("Poslední záloha selhala.");
    expect(reads).toBe(2);
  });

  it("never polls administrator backup health on a timer", async () => {
    vi.useFakeTimers({ toFake: ["setInterval", "clearInterval", "setTimeout", "clearTimeout"] });
    let reads = 0;
    await mountApp(requestMock(path => path === "/api/admin/backups"
      ? Promise.resolve(response({ warning: ++reads === 1 ? null : "Záloha je po termínu." }))
      : undefined));
    expect(reads).toBe(1);
    await vi.advanceTimersByTimeAsync(5 * 60_000);
    await flushPromises();
    expect(reads).toBe(1);
  });

  it("never requests or displays backup health for a normal user", async () => {
    const fetchMock = requestMock(path => path === "/api/auth/me" ? Promise.resolve(response({ id: "reader", username: "reader", displayName: "Reader", isWikiAdmin: false, appearance: { layout: "connected", accent: "blue", mode: "system" } })) : undefined);
    const app = await mountApp(fetchMock);
    expect(fetchMock.mock.calls.some(([path]) => String(path) === "/api/admin/backups")).toBe(false);
    expect(app.find(".account .settings-nav").exists()).toBe(false);
  });
  it('shows progress while a page request is pending and removes it after arrival', async () => {
    let resolvePage!: (value: Response) => void;
    const app = await mountApp(requestMock(path => path === `/api/pages/${page.id}`
      ? new Promise(resolve => { resolvePage = resolve; }) : undefined));
    await app.get('.sidebar-space-link').trigger('click'); await flushPromises();
    await app.get('.tree-page').trigger('click');
    expect(app.get('.workspace-bar').attributes('aria-busy')).toBe('true');
    resolvePage(response(page)); await flushPromises();
    expect(app.get('.workspace-bar').attributes('aria-busy')).toBe('false');
    expect(titleOf(app)).toBe(page.title);
  });

  it('keeps local reading selection actions available without AI', async () => {
    const app = await mountApp(requestMock());
    await app.get('.sidebar-space-link').trigger('click'); await flushPromises();
    // A pressed link has focus in the browser (jsdom's click does not give it), so the opened page does not move focus to its heading.
    (app.get('.tree-page').element as HTMLElement).focus();
    await app.get('.tree-page').trigger('click'); await flushPromises();
    const editor = app.getComponent(DocumentEditor);
    expect(editor.props('readonlySelectionEnabled')).toBe(true);
    editor.vm.$emit('readonlySelection', { text: 'VPN client', anchorRect: new DOMRect(200, 200, 160, 20) });
    // The bar loads with the first selection.
    await vi.waitFor(() => expect(document.querySelector('[data-readonly-find]')).not.toBeNull());
    expect(document.querySelector('[data-readonly-explain]')).toBeNull();
    (document.querySelector('[data-readonly-find]') as HTMLElement).click();
    await flushPromises();
    expect((app.get('.omnisearch-input input').element as HTMLInputElement).value).toBe('VPN client');
  });

  // AI is extra (owner, 2026-10-28): with the agent but no answers from the wiki, the selection offers no AI actions it could not run.
  it('offers no AI selection actions while nothing can answer, and offers them once answers are there', async () => {
    let assistant = false;
    const app = await mountApp(requestMock(path => path === '/api/ai/capabilities' ? Promise.resolve(response({ assistant, agent: true })) : undefined));
    await app.get('.sidebar-space-link').trigger('click'); await flushPromises();
    (app.get('.tree-page').element as HTMLElement).focus();
    await app.get('.tree-page').trigger('click'); await flushPromises();
    const editor = app.getComponent(DocumentEditor);
    editor.vm.$emit('readonlySelection', { text: 'VPN client', anchorRect: new DOMRect(200, 200, 160, 20) });
    await vi.waitFor(() => expect(document.querySelector('[data-readonly-find]')).not.toBeNull());
    expect(document.querySelector('[data-readonly-explain]')).toBeNull();
    assistant = true;
    const { useAiCapabilities } = await import('./composables/useAiCapabilities');
    await useAiCapabilities().refresh();
    editor.vm.$emit('readonlySelection', { text: 'VPN client', anchorRect: new DOMRect(200, 200, 160, 20) });
    await flushPromises();
    expect(document.querySelector('[data-readonly-explain]')).not.toBeNull();
  });

  it('updates the page heading and navigation icon after saving without editing article content', async () => {
    const decoration = { revisionId: 'revision-2', icon: 'database', color: 'violet-3' };
    const fetchMock = requestMock((path, init) => path.endsWith('/decoration') && init?.method === 'PUT' ? Promise.resolve(response({ decoration })) : undefined);
    const app = await mountApp(fetchMock);
    await app.get('.sidebar-space-link').trigger('click'); await flushPromises();
    await app.get('.tree-page').trigger('click'); await flushPromises();
    await app.get('.page-icon-edit').trigger('click'); await vi.dynamicImportSettled(); await flushPromises();
    await app.get('[data-appearance-icons] [data-value="database"]').trigger('click');
    await app.get('[data-appearance-more-colors]').trigger('click'); await flushPromises();
    await app.get('[data-appearance-colors] [data-value="violet-3"]').trigger('click');
    await app.get('.page-icon-save').trigger('click'); await flushPromises();
    expect(app.get('.document-header .document-title [data-icon="database"]').attributes('data-color')).toBe('violet-3');
    expect(app.get('.tree-page [data-icon="database"]').attributes('data-color')).toBe('violet-3');
    expect(app.getComponent(DocumentEditor).props('readonly')).toBe(false);
    expect(fetchMock.mock.calls.some(([path, init]) => path === `/api/pages/${page.id}` && init?.method === 'PUT')).toBe(false);
  });
  it('Up at the start of the text goes to the title field (3.6: the title of a writer is a field, not a heading)', async () => {
    const app = await mountApp(requestMock());
    await app.get('.sidebar-space-link').trigger('click'); await flushPromises();
    await app.get('.tree-page').trigger('click'); await flushPromises();
    await startEditing(app);
    const leave = new Event(MARKDOWN_LEAVE_START_EVENT, { cancelable: true });
    window.dispatchEvent(leave);
    expect(leave.defaultPrevented).toBe(true);
    expect(document.activeElement).toBe(app.get('.document-header .document-title-text').element);
  });
  it('opens page-tag results without requiring a text query and restores the tag through history', async () => {
    vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout"] });
    const tag = { id: '00000000-0000-0000-0000-000000000001', name: 'Integrace' };
    const fetchMock = requestMock((path) => {
      if (path === `/api/pages/${page.id}/tags`) return Promise.resolve(response({ tags: [tag] }));
      if (path.startsWith(`/api/tags/${tag.id}/pages?`)) return Promise.resolve(response({ items: [{ ...page, spaceName: space.name, snippet: '' }], hasMore: false }));
      return undefined;
    });
    const app = await mountApp(fetchMock);
    await app.get('.sidebar-space-link').trigger('click'); await flushPromises();
    await app.get('.tree-page').trigger('click'); await flushPromises();
    await app.get('.page-tag-link').trigger('click'); await flushPromises();
    await vi.advanceTimersByTimeAsync(200); await flushPromises();
    expect(app.get('.selected-search-tag').text()).toContain('Integrace');
    expect(fetchMock.mock.calls.some(([path]) => String(path) === `/api/tags/${tag.id}/pages?q=&limit=50`)).toBe(true);
    const address = location.href;
    expect(new URL(address).searchParams.get('tag')).toBe(tag.id);
    // A result row owns no CSS animation or stagger of its own; the arrival tracker rises it (plan O15).
    expect(app.get('#search-result-0').attributes('style') ?? '').not.toContain('search-result-delay');
    await app.get('#search-result-0').trigger('click'); await flushPromises();
    history.replaceState(null, '', address);
    window.dispatchEvent(new PopStateEvent('popstate')); await flushPromises();
    await vi.advanceTimersByTimeAsync(200); await flushPromises();
    expect(app.get('.selected-search-tag').text()).toContain('Integrace');
    await app.get('.selected-search-tag').trigger('click'); await flushPromises();
    expect(new URL(location.href).searchParams.has('tag')).toBe(false);
    expect(app.find('.selected-search-tag').exists()).toBe(false);
  });
  it('finds tags in the main search and selects one to browse matching pages', async () => {
    vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout"] });
    const tag = { id: '00000000-0000-0000-0000-000000000001', name: 'Integrace' };
    const fetchMock = requestMock(path => {
      if (path.startsWith('/api/tags?')) return Promise.resolve(response({ items: [tag] }));
      if (path.startsWith('/api/search?') || path.startsWith('/api/tags/' + tag.id)) return Promise.resolve(response({ items: [], hasMore: false }));
      return undefined;
    });
    const app = await mountApp(fetchMock);
    await app.get('.search-launch').trigger('click');
    await app.get('.omnisearch-input input').setValue('integrace');
    await vi.advanceTimersByTimeAsync(200); await flushPromises();
    await app.get('.tag-search-suggestions .tag-chip').trigger('click'); await flushPromises();
    await vi.advanceTimersByTimeAsync(200); await flushPromises();
    expect(app.get('.selected-search-tag').text()).toContain('Integrace');
    expect((app.get('.omnisearch-input input').element as HTMLInputElement).value).toBe('');
  });
  it('exposes navigation disclosure controls and their expanded state', async () => {
    const app = await mountApp(requestMock());
    expect(app.get('.desktop-sidebar-toggle').attributes('aria-controls')).toBe('wiki-navigation');
    const mobile = app.get('.mobile-menu');
    expect(mobile.attributes('aria-controls')).toBe('wiki-navigation');
    expect(mobile.attributes('aria-expanded')).toBe('false');
    await mobile.trigger('click');
    expect(mobile.attributes('aria-expanded')).toBe('true');
  });
  it('remembers sidebar width per account and restores the default on double click', async () => {
    const app = await mountApp(requestMock());
    const resize = app.get('[role="separator"][aria-label="Šířka boční navigace"]');
    await resize.trigger('keydown', { key: 'ArrowRight' });
    expect(storageValues.get('atlas:reading:user-1:sidebar-width')).toBe('288');
    expect(app.get('.sidebar').attributes('style')).toContain('--atlas-sidebar-width: 288px');
    app.unmount();
    const restored = await mountApp(requestMock());
    expect(restored.get('[role="separator"]').attributes('aria-valuenow')).toBe('288');
    await restored.get('[role="separator"]').trigger('dblclick');
    // Refresh 2.5 widens the default rail to 272 px.
    expect(storageValues.get('atlas:reading:user-1:sidebar-width')).toBe('272');
  });
  it('validates saved sidebar widths and ignores another account preferences', async () => {
    storageValues.set('atlas:reading:another-user:sidebar-width', '440');
    storageValues.set('atlas:reading:user-1:sidebar-width', 'NaN');
    const app = await mountApp(requestMock());
    expect(app.get('[role="separator"]').attributes('aria-valuenow')).toBe('272');
    app.unmount();
    storageValues.set('atlas:reading:user-1:sidebar-width', '100000');
    const bounded = await mountApp(requestMock());
    // The preferred width is capped to leave 640 px of workspace on this viewport.
    expect(bounded.get('[role="separator"]').attributes('aria-valuenow')).toBe(String(Math.min(480, window.innerWidth - 640)));
  });
  it("closes the mobile navigation with Escape and restores focus", async () => {
    const app = await mountApp(requestMock());
    const trigger = app.get('.mobile-menu[aria-label="Zobrazit boční navigaci"]');
    await trigger.trigger('click'); await flushPromises();
    expect(app.get('.sidebar').classes()).toContain('mobile-open');
    document.activeElement!.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true }));
    await flushPromises();
    expect(app.get('.sidebar').classes()).not.toContain('mobile-open');
    expect(document.activeElement).toBe(trigger.element);
  });
  it("filters commands without accents, executes with Enter and never searches their text", async () => {
    const fetchMock = requestMock();
    const app = await mountApp(fetchMock);
    await app.get('.search-launch').trigger('click');
    const input = app.get('.search-dialog input[role="combobox"]');
    await input.setValue('> oblibene');
    expect(app.get('#command-results').text()).toContain('Oblíbené stránky');
    expect(app.findAll('#command-results [role="option"]')).toHaveLength(1);
    expect(input.attributes('aria-controls')).toBe('command-results');
    await input.trigger('keydown', { key: 'Enter' }); await flushPromises();
    expect(location.hash).toBe('#/library/favorites');
    expect((app.get('.search-dialog').element as HTMLDialogElement).open).toBe(false);
    expect(fetchMock.mock.calls.some(([path]) => String(path).startsWith('/api/search?'))).toBe(false);
  });
  it("opens commands directly with Ctrl Shift P and leaves an empty match inert", async () => {
    const app = await mountApp(requestMock());
    window.dispatchEvent(new KeyboardEvent('keydown', { key: 'P', ctrlKey: true, shiftKey: true, cancelable: true }));
    await flushPromises();
    const input = app.get('.search-dialog input[role="combobox"]');
    expect(document.activeElement).toBe(input.element);
    expect(app.get('#command-results').isVisible()).toBe(true);
    await input.setValue('> neexistujici prikaz');
    expect(app.get('.command-empty').text()).toContain('Žádný příkaz');
    expect(input.attributes('aria-activedescendant')).toBeUndefined();
    await input.trigger('keydown', { key: 'Enter' });
    expect(location.hash).toBe('');
  });
  it("tells same-named spaces apart in the palette and offers a new space to those who may create one", async () => {
    const twin = { ...space, id: "bbbb-2222" };
    const app = await mountApp(requestMock(path => path === "/api/spaces" ? response([space, twin]) : undefined));
    window.dispatchEvent(new KeyboardEvent('keydown', { key: 'P', ctrlKey: true, shiftKey: true, cancelable: true }));
    await flushPromises();
    const input = app.get('.search-dialog input[role="combobox"]');
    await input.setValue('> prejit do prostoru engineering');
    const details = app.findAll('#command-results small').map(item => item.text());
    expect(details).toHaveLength(2);
    expect(new Set(details).size).toBe(2);
    expect(details.join(' ')).not.toMatch(/č. /);
    await input.setValue('> novy prostor');
    expect(app.get('#command-results').text()).toContain('Nový prostor');
  });
  it("preserves the mounted document and account preference when changing reading width", async () => {
    storageValues.set('atlas:reading:user-1:width', 'comfortable');
    const app = await mountApp(requestMock());
    await app.get('.sidebar-space-link').trigger('click'); await flushPromises();
    await app.get('.tree-page').trigger('click'); await flushPromises();
    await settleLazyViews(); expect(app.findComponent(DocumentEditor).exists()).toBe(true);
    const editor = app.findComponent(DocumentEditor).element;
    await app.get('.search-launch').trigger('click');
    const input = app.get('.search-dialog input[role="combobox"]');
    await input.setValue('> siroky'); await input.trigger('keydown', { key: 'Enter' }); await flushPromises();
    expect(app.get('.atlas').attributes('data-reading-width')).toBe('wide');
    expect(app.findComponent(DocumentEditor).element).toBe(editor);
    expect(storageValues.get('atlas:reading:user-1:width')).toBe('wide');
    app.unmount();
    const restored = await mountApp(requestMock());
    expect(restored.get('.atlas').attributes('data-reading-width')).toBe('wide');
  });
  it("offers only readable page commands to a reader", async () => {
    const app = await mountApp(requestMock(path => {
      if (path === '/api/auth/me') return Promise.resolve(response({ id: 'reader', username: 'reader', displayName: 'Reader', isWikiAdmin: false, appearance: { layout: 'connected', accent: 'blue', mode: 'system' } }));
      if (path === '/api/spaces') return Promise.resolve(response([{ ...space, role: 'Reader' }]));
      // The page's own canEdit decides the write commands, so a reader's tree and page say false.
      if (path === `/api/spaces/${space.id}/tree`) return Promise.resolve(response({ spaceId: space.id, version: 1, items: [{ ...page, sortOrder: 1024, canEdit: false }] }));
      if (path.startsWith(`/api/pages/${page.id}?`) || path === `/api/pages/${page.id}`) return Promise.resolve(response({ ...page, canEdit: false }));
    }));
    await app.get('.sidebar-space-link').trigger('click'); await flushPromises();
    await app.get('.tree-page').trigger('click'); await flushPromises();
    await app.get('.search-launch').trigger('click');
    await app.get('.search-dialog input[role="combobox"]').setValue('>');
    const commands = app.get('#command-results').text();
    expect(commands).toContain('Kopírovat odkaz');
    expect(commands).not.toMatch(/Upravit stránku|Nová stránka|Historie stránky|Uspořádat stránku|Publikovat/);
  });
  it("keeps layout preferences isolated between accounts", async () => {
    storageValues.set('atlas:reading:user-1:width', 'comfortable');
    storageValues.set('atlas:reading:user-1:sidebar', 'collapsed');
    const app = await mountApp(requestMock(path => path === '/api/auth/me'
      ? Promise.resolve(response({ id: 'another-user', username: 'another', displayName: 'Another', isWikiAdmin: true, appearance: { layout: 'connected', accent: 'blue', mode: 'system' } })) : undefined));
    // Another account starts from the default reading width (wide), not user-1's comfortable.
    expect(app.get('.atlas').attributes('data-reading-width')).toBe('wide');
    expect(app.get('.atlas').attributes('data-sidebar-collapsed')).toBe('false');
    await app.get('.desktop-sidebar-toggle').trigger('click');
    expect(storageValues.get('atlas:reading:another-user:sidebar')).toBe('collapsed');
    expect(storageValues.get('atlas:reading:user-1:width')).toBe('comfortable');
  });
  it("offers a selectable link when clipboard access from a command fails", async () => {
    const app = await mountApp(requestMock());
    await app.get('.sidebar-space-link').trigger('click'); await flushPromises();
    await app.get('.tree-page').trigger('click'); await flushPromises();
    await app.get('.search-launch').trigger('click');
    const input = app.get('.search-dialog input[role="combobox"]');
    await input.setValue('> kopirovat'); await input.trigger('keydown', { key: 'Enter' }); await flushPromises();
    // The manual-copy popover is portalled out of the wrapper.
    await new Promise(resolve => setTimeout(resolve, 40));
    const fallback = document.querySelector<HTMLInputElement>('input[aria-label="Odkaz na stránku"]');
    expect(fallback).not.toBeNull();
    expect(document.activeElement).toBe(fallback);
    expect(fallback!.value).toContain('#/pages/page-1');
  });
  it("runs a command only after the folding search has closed and restored its focus", async () => {
    const app = await mountApp(requestMock());
    await app.get('.sidebar-space-link').trigger('click'); await flushPromises();
    await app.get('.tree-page').trigger('click'); await flushPromises();
    await app.get('.search-launch').trigger('click');
    const input = app.get('.search-dialog input[role="combobox"]');
    await input.setValue('> kopirovat');
    // The fold keeps the dialog modal until it finishes; AppDialog then focuses its opener.
    const folds: EventTarget[] = [];
    Object.defineProperty(HTMLElement.prototype, 'animate', { configurable: true, writable: true, value: () => {
      const fold = Object.assign(new EventTarget(), { cancel() {}, finished: new Promise(() => {}) });
      folds.push(fold);
      return fold;
    } });
    try {
      await input.trigger('keydown', { key: 'Enter' }); await flushPromises();
      expect(folds.length).toBeGreaterThan(0);
      expect((app.get('.search-dialog').element as HTMLDialogElement).open).toBe(true);
      expect(document.querySelector('input[aria-label="Odkaz na stránku"]')).toBeNull();
      folds.forEach(fold => fold.dispatchEvent(new Event('finish'))); await flushPromises();
      await new Promise(resolve => setTimeout(resolve, 40));
      expect((app.get('.search-dialog').element as HTMLDialogElement).open).toBe(false);
      const fallback = document.querySelector<HTMLInputElement>('input[aria-label="Odkaz na stránku"]');
      expect(fallback).not.toBeNull();
      expect(document.activeElement).toBe(fallback);
    } finally { Reflect.deleteProperty(HTMLElement.prototype, 'animate'); }
  });
  it("ignores command shortcuts while another modal is open", async () => {
    const app = await mountApp(requestMock());
    const other = document.createElement('dialog');
    other.setAttribute('open', '');
    document.body.append(other);
    try {
      window.dispatchEvent(new KeyboardEvent('keydown', { key: 'P', ctrlKey: true, shiftKey: true, cancelable: true }));
      await flushPromises();
      expect(app.find('dialog.search-dialog[open]').exists()).toBe(false);
    } finally { other.remove(); }
  });
  it("does not offer favorite commands before personal state is available", async () => {
    const app = await mountApp(requestMock(path => path.endsWith('/personal-state') ? new Promise(() => {}) : undefined));
    await app.get('.sidebar-space-link').trigger('click'); await flushPromises();
    await app.get('.tree-page').trigger('click'); await flushPromises();
    await app.get('.search-launch').trigger('click');
    await app.get('.search-dialog input[role="combobox"]').setValue('> pridat do oblibenych');
    expect(app.findAll('#command-results [role="option"]')).toHaveLength(0);
    await app.get('[aria-label="Další akce stránky"]').trigger('click'); await settleMenu();
    expect(menuItems().find(item => item.textContent?.includes('Přidat do oblíbených'))!.hasAttribute('disabled')).toBe(true);
  });
  it("pops the star the reader just chose as a favourite, and not the one that arrives with the page", async () => {
    const animations: { el: Element; frames: Keyframe[] }[] = [];
    Object.defineProperty(Element.prototype, "animate", { configurable: true, value(this: Element, frames: Keyframe[]) { animations.push({ el: this, frames }); return { finished: Promise.resolve(), cancel() {} }; } });
    try {
      const app = await mountApp(requestMock(path => {
        if (path.endsWith("/personal-state")) return Promise.resolve(response({ isFavorite: false, visitedAt: null }));
        if (path.endsWith("/favorite")) return Promise.resolve(response({ isFavorite: true, visitedAt: null }));
      }));
      await app.get(".sidebar-space-link").trigger("click"); await flushPromises();
      await app.get(".tree-page").trigger("click"); await flushPromises();
      expect(animations.some(({ el }) => el.closest(".favorite-button"))).toBe(false);
      animations.length = 0;
      await app.get(".favorite-button").trigger("click"); await flushPromises();
      expect(app.get(".favorite-button").attributes("aria-pressed")).toBe("true");
      const pop = animations.find(({ el }) => el.closest(".favorite-button"));
      expect(pop?.frames.map(frame => frame.transform)).toEqual(["scale(.8)", "scale(1.12)", "none"]);
    } finally { Reflect.deleteProperty(Element.prototype, "animate"); }
  });
  // 3.6-B: the page's own changes ask nothing on the way out; a checkpoint goes instead, and the shared session holds them until then.
  it("leaving a changed document asks nothing, neither the shared dialog nor the browser: the changes go as a checkpoint", async () => {
    const fetchMock = requestMock();
    const app = await mountApp(fetchMock);
    await app.get('.sidebar-space-link').trigger('click'); await flushPromises();
    await app.get('.tree-page').trigger('click'); await flushPromises();
    const editor = await startEditing(app);
    editor.vm.$emit('change'); await flushPromises();
    const confirm = vi.spyOn(window, 'confirm');
    await app.get('.sidebar-space-link').trigger('click'); await flushPromises(); await flushPromises();
    expect(confirm).not.toHaveBeenCalled();
    expect(leaveButton('Zůstat')).toBeUndefined();
    expect(puts(fetchMock)).toHaveLength(1);
    expect(location.hash).toBe('#/spaces/space-1');
    confirm.mockRestore();
  });
  // U2-2: back, forward and a typed address leave the same way, with the checkpoint and without a question.
  it.each(["back", "typed"])("a %s navigation leaves a changed document without asking, with a checkpoint", async kind => {
    const fetchMock = requestMock();
    const app = await mountApp(fetchMock);
    await app.get('.sidebar-space-link').trigger('click'); await flushPromises();
    await app.get('.tree-page').trigger('click'); await flushPromises();
    const editor = await startEditing(app);
    editor.vm.$emit('change'); await flushPromises();
    if (kind === "back") { history.back(); await new Promise(resolve => setTimeout(resolve, 20)); }
    else { location.hash = '#/spaces/space-1'; await new Promise(resolve => setTimeout(resolve, 20)); }
    await flushPromises();
    expect(leaveButton('Zůstat')).toBeUndefined();
    expect(puts(fetchMock)).toHaveLength(1);
    expect(location.hash).not.toBe('#/pages/page-1');
  });
  // U2-7, U2-8: the editor's actions sit in named groups, the requirement blocks together; a row is named by its label.
  it("groups the editor's actions in the page menu and names each row by its visible label", async () => {
    const app = await mountApp(requestMock());
    await app.get('.sidebar-space-link').trigger('click'); await flushPromises();
    await app.get('.tree-page').trigger('click'); await flushPromises();
    await startEditing(app);
    const action = (id: string, label: string, icon: string | null = null, group?: string) => ({ id, label, description: `Popis: ${label}`, icon, enabled: true, run: () => true, ...(group ? { group } : {}) });
    // C3-4: the menu groups by the action's own group; the editor's groups (content) insert, Atlas's plugins name theirs.
    app.getComponent(DocumentEditor).vm.$emit('pluginActions', [action('pages.search', 'Odkaz na stránku'), action('requirements.summary', 'Souhrn nasazení', 'activity', 'Nasazení'),
      action('toc.insert', 'Obsah dokumentu', 'outline', 'content'), action('requirements.catalog', 'Požadavek z katalogu', 'book-open', 'Nasazení'),
      action('requirements.insert', 'Požadavek', 'list-checks', 'Nasazení'), action('editor-tools.search', 'Najít a nahradit', 'search')]);
    await flushPromises();
    await app.get('[aria-label="Další akce stránky"]').trigger('click'); await settleMenu();
    const groups = [...document.querySelectorAll<HTMLElement>('[role=menu] [role=group]')];
    expect(groups.map(group => group.getAttribute('aria-label'))).toEqual(['Export', 'Stránka', 'Vložit', 'Nasazení', 'Ukončení']);
    expect(groups[1]!.textContent).toContain('Najít a nahradit');
    const rows = (group: HTMLElement) => [...group.querySelectorAll<HTMLElement>('[role=menuitem]')];
    expect(rows(groups[2]!).map(row => row.textContent?.trim())).toEqual(['Odkaz na stránku', 'Obsah dokumentu']);
    expect(rows(groups[3]!).map(row => row.textContent?.trim()).slice(0, 3)).toEqual(['Požadavek', 'Požadavek z katalogu', 'Souhrn nasazení']);
    const link = rows(groups[2]!)[0]!;
    expect(link.getAttribute('aria-label')).toBe('Odkaz na stránku');
    expect(link.getAttribute('aria-description')).toBe('Popis: Odkaz na stránku');
    expect(link.querySelector('svg')).not.toBeNull();
  });
  it("announces offline immediately in the document header and clears it on reconnect", async () => {
    const app = await mountApp(requestMock());
    await app.get('.sidebar-space-link').trigger('click'); await flushPromises();
    await app.get('.tree-page').trigger('click'); await flushPromises();
    // The shared session owns the notice while it exists; this is the page without one (a reader's, or a session that gave up).
    app.getComponent(DocumentEditor).vm.$emit('collaborationStatus', { enabled: false, canSave: false, connection: 'local' });
    window.dispatchEvent(new Event('offline'));
    await flushPromises();
    expect(app.get('.document-status').text()).toContain('Bez připojení');
    expect(app.get('.document-status').attributes('role')).toBe('status');
    window.dispatchEvent(new Event('online'));
    await flushPromises();
    expect(app.get('.document-status').text()).not.toContain('Bez připojení');
  });
  // 3.6-B: the status slot keeps the revision state; what happens to the changes is the save state's to say (SaveState).
  it("keeps the header status stable while a session retries offline; the save state says the changes wait, and they are saved once online and synced", async () => {
    vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout"] });
    const fetchMock = requestMock();
    const app = await mountApp(fetchMock);
    await app.get('.sidebar-space-link').trigger('click'); await flushPromises();
    await app.get('.tree-page').trigger('click'); await flushPromises();
    const editor = await startEditing(app);
    editor.vm.$emit('change'); await flushPromises();
    const saveState = () => app.get('[data-page-save-state]').text();
    const offline = { ...synced, canSave: false, connection: 'offline', pendingChanges: 1 };
    for (let retry = 0; retry < 3; retry++) {
      editor.vm.$emit('collaborationStatus', { ...offline });
      await flushPromises();
      // The editor's own connection notice owns the offline state; the host header stays silent.
      expect(app.get('.document-status').text()).toBe('');
      expect(app.get('.document-status').attributes('aria-live')).toBe('polite');
      // Offline the editor is read-only: the state says typing is paused instead of promising to save it.
      expect(saveState()).toContain('Offline: psaní je pozastavené');
      await vi.advanceTimersByTimeAsync(1500);
      expect(puts(fetchMock)).toHaveLength(0);
    }
    // The unacknowledged update exists only in this tab: closing it asks.
    const unsent = new Event('beforeunload', { cancelable: true });
    window.dispatchEvent(unsent);
    expect(unsent.defaultPrevented).toBe(true);
    editor.vm.$emit('collaborationStatus', { ...offline, connection: 'online' }); await flushPromises();
    expect(saveState()).toContain('Ukládám');
    expect(puts(fetchMock)).toHaveLength(0);
    editor.vm.$emit('collaborationStatus', { ...synced }); await flushPromises();
    await vi.advanceTimersByTimeAsync(1500); await flushPromises();
    expect(puts(fetchMock)).toHaveLength(1);
    expect(saveState()).toContain('Uloženo');
    expect(app.get('.document-status').text()).toBe('Publikováno');
    const acknowledged = new Event('beforeunload', { cancelable: true });
    window.dispatchEvent(acknowledged);
    expect(acknowledged.defaultPrevented).toBe(false);
  });
  // UX open blink: the revision state stands from the first frame; only a join slower than a second takes its place, in the header.
  it("shows the revision state while the session connects and says a join slower than a second in the same chip", async () => {
    vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout"] });
    const app = await mountApp(requestMock());
    await app.get('.sidebar-space-link').trigger('click'); await flushPromises();
    await app.get('.tree-page').trigger('click'); await flushPromises();
    const editor = await startEditing(app, false);
    editor.vm.$emit('collaborationStatus', { ...synced, canSave: false, connection: 'connecting' }); await flushPromises();
    expect(app.get('.document-status').text()).toBe('Publikováno');
    await vi.advanceTimersByTimeAsync(900); await flushPromises();
    expect(app.get('.document-status').text()).toBe('Publikováno');
    await vi.advanceTimersByTimeAsync(200); await flushPromises();
    expect(app.get('.document-status').text()).toBe('Připojujeme společné úpravy…');
    editor.vm.$emit('collaborationStatus', { ...synced }); await flushPromises();
    expect(app.get('.document-status').text()).toBe('Publikováno');
  });
  // The easing of the status slot's width (documentStatusMotion) went with the running states it eased between.
  it("uses native space links and intercepts only plain clicks", async () => {
    const app = await mountApp(requestMock());
    const link = app.get('.sidebar-space-link');
    expect(new URL(link.attributes('href')!).hash).toBe('#/spaces/space-1');
    const modified = new MouseEvent('click', { ctrlKey: true, bubbles: true, cancelable: true });
    let intercepted: boolean | undefined;
    // Observe the app's decision before suppressing jsdom's same-tab default
    // navigation: a real browser opens a Ctrl-click in a separate tab.
    document.addEventListener('click', event => {
      intercepted = event.defaultPrevented;
      event.preventDefault();
    }, { once: true });
    link.element.dispatchEvent(modified);
    await flushPromises();
    expect(intercepted).toBe(false);
    expect(location.hash).toBe('');
    await link.trigger('click');
    await flushPromises();
    expect(location.hash).toBe('#/spaces/space-1');
    expect(app.get('.space-page-table a').attributes('href')).toContain('#/pages/page-1');
  });
  it("keeps advanced search filters collapsed without losing an active scope", async () => {
    const app = await mountApp(requestMock());
    await app.get('.search-launch').trigger('click');
    const toggle = app.get('button[aria-controls="search-filters"]');
    expect(toggle.attributes('aria-expanded')).toBe('false');
    expect(app.get('#search-filters').isVisible()).toBe(false);
    await toggle.trigger('click');
    await pickSearchSpace(app, space.id);
    await toggle.trigger('click');
    expect(app.get('#search-filters').isVisible()).toBe(false);
    expect(app.get('.search-active-filters').text()).toContain(space.name);
    await app.get('.search-active-filters button').trigger('click');
    expect(app.get("[data-search-space]").text()).toContain("Všechny");
  });
  const reader = { id: "user-reader", username: "reader", displayName: "Reader", isWikiAdmin: false, appearance: { layout: "connected", accent: "blue", mode: "system" } };
  async function switchToReader(app: VueWrapper) {
    // Sign-out lives in the profile, not in the sidebar.
    expect(app.find('aside [data-profile-logout]').exists()).toBe(false);
    // The profile is a lazy chunk. The first test to open it imports ProfilePage and its settings components
    // cold (about 0.6 s idle, over 1 s under CPU load), so wait for that import itself rather than polling
    // against waitFor's 1 s wall-clock budget.
    await app.get('.profile-launch').trigger('click'); await settleLazyViews();
    expect(app.find('[data-profile-logout]').exists()).toBe(true);
    await app.get('[data-profile-logout]').trigger('click'); await flushPromises();
    await app.get('input[autocomplete="username"]').setValue('reader');
    await app.get('input[autocomplete="current-password"]').setValue('test-password');
    await app.get('input[autocomplete="username"]').element.closest('form')!.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }));
    await flushPromises(); await flushPromises();
  }
  it("ignores a former admin tree even if its version exceeds the newly signed-in reader tree", async () => {
    let currentReader = false, resolveOld!: (response: Response) => void;
    const fetchMock = requestMock((path) => {
      if (path === '/api/auth/logout') return Promise.resolve(new Response(null, { status: 204 }));
      if (path === '/api/auth/methods') return Promise.resolve(response({ builtinEnabled: true, providers: [] }));
      if (path === '/api/auth/login') { currentReader = true; return Promise.resolve(response({ ...reader, id: "user-1" })); }
      if (path === '/api/spaces') return Promise.resolve(response([{ ...space, role: currentReader ? 'Reader' : 'Admin' }]));
      if (path === `/api/spaces/${space.id}/tree`) return currentReader
        ? Promise.resolve(response({ spaceId: space.id, version: 2, items: [{ ...page, title: 'Published reader page', canEdit: false }] }))
        : new Promise(resolve => { resolveOld = resolve; });
    });
    // The sidebar loads the trees of the spaces the account keeps expanded on sign-in.
    rememberOpenedSpaces("user-1", space.id);
    const app = await mountApp(fetchMock);
    expect(resolveOld).toBeTypeOf('function');
    await switchToReader(app);
    await app.get('.sidebar-space-link').trigger('click'); await flushPromises();
    expect(app.get('.spaces-scroll').text()).toContain('Published reader page');
    resolveOld(response({ spaceId: space.id, version: 99, items: [{ ...page, title: 'PRIVATE ADMIN DRAFT', isPublished: false, canEdit: true }] }));
    await flushPromises();
    expect(app.text()).not.toContain('PRIVATE ADMIN DRAFT');
    expect(app.get('.spaces-scroll').text()).toContain('Published reader page');
  });
  it("ignores an old access refresh after a new session of the same account loads its spaces", async () => {
    let currentReader = false, spaceRequests = 0, resolveOld!: (response: Response) => void;
    const fetchMock = requestMock((path) => {
      if (path === '/api/auth/logout') return Promise.resolve(new Response(null, { status: 204 }));
      if (path === '/api/auth/methods') return Promise.resolve(response({ builtinEnabled: true, providers: [] }));
      if (path === '/api/auth/login') { currentReader = true; return Promise.resolve(response({ ...reader, id: "user-1" })); }
      if (path === '/api/spaces') {
        if (++spaceRequests === 2) return new Promise(resolve => { resolveOld = resolve; });
        return Promise.resolve(response([{ ...space, role: currentReader ? 'Reader' : 'Admin' }]));
      }
      if (path === `/api/spaces/${space.id}/access`) return Promise.resolve(response({ users: [], groups: [], grants: [] }));
    });
    const app = await mountApp(fetchMock);
    await app.get('.sidebar-space-link').trigger('click'); await flushPromises();
    await app.get('[aria-label^="Akce prostoru"][aria-expanded]').trigger('click');
    await settleMenu(); menuItems().find(item => item.textContent === 'Přístupy')!.click(); await flushPromises();
    // Přístupy is a section of Nastavení prostoru; its change callback refreshes the spaces.
    expect(location.hash).toBe(`#/spaces/${space.id}/settings/access`);
    await settleLazyViews(); expect(app.findComponent({ name: 'SettingsHost' }).exists()).toBe(true);
    app.getComponent({ name: 'SettingsHost' }).vm.$emit('accessChanged'); await flushPromises();
    expect(resolveOld).toBeTypeOf('function');
    await switchToReader(app);
    resolveOld(response([{ ...space, role: 'Admin' }, { ...space, id: 'secret-space', name: 'PRIVATE ADMIN SPACE' }]));
    await flushPromises();
    expect(app.text()).not.toContain('PRIVATE ADMIN SPACE');
    await app.get('.sidebar-space-link').trigger('click'); await flushPromises();
    expect(menuItems().some(item => item.textContent === 'Přístupy')).toBe(false);
  });
  it("does not carry one account's tree expansion into the next login", async () => {
    let currentReader = false;
    const parent = { ...page, id: "page-parent", title: "Parent", parentId: null };
    const child = { ...page, id: "page-child", title: "Child", parentId: parent.id };
    const fetchMock = requestMock(path => {
      if (path === "/api/auth/logout") return Promise.resolve(new Response(null, { status: 204 }));
      if (path === "/api/auth/methods") return Promise.resolve(response({ builtinEnabled: true, providers: [] }));
      if (path === "/api/auth/login") { currentReader = true; return Promise.resolve(response(reader)); }
      if (path === "/api/spaces") return Promise.resolve(response([{ ...space, role: currentReader ? "Reader" : "Admin" }]));
      if (path === `/api/spaces/${space.id}/tree`) return Promise.resolve(response({ spaceId: space.id, version: 1, items: [
        { ...parent, sortOrder: 1024, canEdit: !currentReader }, { ...child, sortOrder: 2048, canEdit: !currentReader },
      ] }));
    });
    const app = await mountApp(fetchMock);
    await app.get(".sidebar-space-link").trigger("click"); await flushPromises();
    await app.get('[aria-label="Sbalit Parent"]').trigger("click");
    await switchToReader(app);
    // The next account starts with its own sidebar: nothing expanded until it opens the space.
    expect(app.find('[aria-label="Sbalit Parent"]').exists()).toBe(false);
    await app.get(".sidebar-space-link").trigger("click"); await flushPromises();
    expect(app.get('[aria-label="Sbalit Parent"]').attributes("aria-expanded")).toBe("true");
    expect(app.find('[aria-label="Rozbalit Parent"]').exists()).toBe(false);
  });
  it("asks before the tab closes while a view holds unsaved work outside the page (a comment being typed, a board's queue)", async () => {
    await mountApp(requestMock());
    const closing = () => { const event = new Event("beforeunload", { cancelable: true }); window.dispatchEvent(event); return event.defaultPrevented; };
    expect(closing()).toBe(false);
    const release = holdUnsavedWork(() => true);
    onTestFinished(release);
    expect(closing()).toBe(true);
    release();
    expect(closing()).toBe(false);
  });
  it("forgets the previous account's drafts typed outside the page on sign-out, so the next account never sees them", async () => {
    const fetchMock = requestMock(path => {
      if (path === "/api/auth/logout") return Promise.resolve(new Response(null, { status: 204 }));
      if (path === "/api/auth/methods") return Promise.resolve(response({ builtinEnabled: true, providers: [] }));
      if (path === "/api/auth/login") return Promise.resolve(response(reader));
    });
    const app = await mountApp(fetchMock);
    onTestFinished(() => drafts.clear());
    draftKeeper()(`comment:${page.id}`, "PRIVATE COMMENT DRAFT");
    expect(drafts.size).toBe(1);
    await switchToReader(app);
    expect(drafts.size).toBe(0);
  });
  it("shows none of the previous account's personal state or backup warning to the next account", async () => {
    const second = { id: "user-2", username: "second", displayName: "Second Admin", isWikiAdmin: true, appearance: { layout: "connected", accent: "blue", mode: "system" } };
    let signedInAsSecond = false, resolveSecondBackups!: (value: Response) => void;
    const fetchMock = requestMock(path => {
      if (path === "/api/auth/logout") return Promise.resolve(new Response(null, { status: 204 }));
      if (path === "/api/auth/methods") return Promise.resolve(response({ builtinEnabled: true, providers: [] }));
      if (path === "/api/auth/login") { signedInAsSecond = true; return Promise.resolve(response(second)); }
      if (path === "/api/admin/backups") return signedInAsSecond
        ? new Promise(resolve => { resolveSecondBackups = resolve; })
        : Promise.resolve(response({ warning: "FIRST ACCOUNT BACKUP WARNING" }));
      if (path.endsWith("/personal-state")) return signedInAsSecond
        ? new Promise(() => {})
        : Promise.resolve(response({ isFavorite: true, visitedAt: "2026-09-01T10:00:00Z" }));
    });
    const app = await mountApp(fetchMock);
    expect(app.get(".account .settings-nav").attributes("aria-label")).toContain("FIRST ACCOUNT BACKUP WARNING");
    await app.get(".sidebar-space-link").trigger("click"); await flushPromises();
    await app.get(".tree-page").trigger("click"); await flushPromises();
    expect(app.get(".favorite-button").attributes("aria-pressed")).toBe("true");

    await switchToReader(app);
    // The second administrator's own backup check is still running: the first account's
    // warning must already be gone, not wait for that answer, the timer or a focus event.
    expect(resolveSecondBackups).toBeTypeOf("function");
    expect(app.get(".account .settings-nav").attributes("aria-label")).toBeUndefined();
    expect(app.text()).not.toContain("FIRST ACCOUNT BACKUP WARNING");
    resolveSecondBackups(response({ warning: null })); await flushPromises(); await flushPromises();
    expect(app.get(".profile-launch").text()).toContain("Second Admin");
    expect(location.hash).toBe("");
    expect(app.find(".favorite-button").exists()).toBe(false);

    // Same page, new account: its favorite state is unknown until its own request answers.
    await app.get(".sidebar-space-link").trigger("click"); await flushPromises();
    await app.get(".tree-page").trigger("click"); await flushPromises();
    expect(app.get(".favorite-button").attributes("aria-pressed")).toBe("false");
    expect(app.get(".favorite-button").attributes("disabled")).toBeDefined();
    expect(app.find(".account .settings-nav").attributes("aria-label")).toBeUndefined();
  });
  it("opens the profile on the first click while the data behind a fresh sign-in still loads", async () => {
    let signedIn = false;
    const fetchMock = requestMock(path => {
      if (path === "/api/auth/logout") return Promise.resolve(new Response(null, { status: 204 }));
      if (path === "/api/auth/methods") return Promise.resolve(response({ builtinEnabled: true, providers: [] }));
      if (path === "/api/auth/login") { signedIn = true; return Promise.resolve(response({ ...reader, id: "user-1" })); }
      if (path === "/api/spaces" && signedIn) return new Promise(() => {});
    });
    const app = await mountApp(fetchMock);
    await switchToReader(app);
    expect(app.get(".profile-launch").text()).toContain(reader.displayName);
    await app.get(".profile-launch").trigger("click"); await flushPromises();
    expect(location.hash).toBe("#/profile/account");
  });
  it("ignores a tree response from a previous login of the same account", async () => {
    let relogged = false, treeRequests = 0, resolveOld!: (response: Response) => void;
    const sameAccount = { ...reader, id: "user-1" };
    const fresh = { ...page, title: "Fresh authorized page", canEdit: false };
    const fetchMock = requestMock(path => {
      if (path === "/api/auth/logout") return Promise.resolve(new Response(null, { status: 204 }));
      if (path === "/api/auth/methods") return Promise.resolve(response({ builtinEnabled: true, providers: [] }));
      if (path === "/api/auth/login") { relogged = true; return Promise.resolve(response(sameAccount)); }
      if (path === "/api/spaces") return Promise.resolve(response([{ ...space, role: relogged ? "Reader" : "Admin" }]));
      if (path === `/api/spaces/${space.id}/tree`) {
        if (++treeRequests === 1) return new Promise(resolve => { resolveOld = resolve; });
        return Promise.resolve(response({ spaceId: space.id, version: 1, items: [fresh] }));
      }
    });
    rememberOpenedSpaces("user-1", space.id);
    const app = await mountApp(fetchMock);
    expect(resolveOld).toBeTypeOf("function");
    await switchToReader(app);
    expect(app.get(".spaces-scroll").text()).toContain(fresh.title);
    resolveOld(response({ spaceId: space.id, version: 99, items: [{ ...page, title: "STALE PRIVATE TITLE", canEdit: true }] }));
    await flushPromises();
    expect(app.get(".spaces-scroll").text()).toContain(fresh.title);
    expect(app.text()).not.toContain("STALE PRIVATE TITLE");
  });
});

describe("page navigation editing intent", () => {
  it("restores a collapsed branch for the same account after reload", async () => {
    const parent = { ...page, id: "page-parent", title: "Parent", parentId: null };
    const child = { ...page, id: "page-child", title: "Child", parentId: parent.id };
    const fetchMock = requestMock(path => path === `/api/spaces/${space.id}/tree`
      ? Promise.resolve(response({ spaceId: space.id, version: 1, items: [
        { ...parent, sortOrder: 1024, canEdit: true }, { ...child, sortOrder: 2048, canEdit: true },
      ] })) : undefined);
    const first = await mountApp(fetchMock);
    await first.get(".sidebar-space-link").trigger("click"); await flushPromises();
    await first.get('[aria-label="Sbalit Parent"]').trigger("click");
    first.unmount(); wrapper = undefined;

    const reloaded = await mountApp(fetchMock);
    // The space stays expanded for the account, so its tree loads without opening the space.
    expect(reloaded.get(".sidebar-space-link").text()).toBe("Engineering");
    expect(reloaded.get('[aria-label="Rozbalit Parent"]').attributes("aria-expanded")).toBe("false");
    expect(reloaded.findAll(".tree-page").some(button => button.text() === child.title)).toBe(false);
  });
  it("lists required pins first, adds the open unpinned space and remembers expanded spaces after reload", async () => {
    const ops = { ...space, id: "space-2", name: "Provoz", pinned: false };
    const rules = { ...space, id: "space-3", name: "Zásady", pinPolicy: "Required", pinned: true };
    const archive = { ...space, id: "space-4", name: "Archiv", pinPolicy: "Default", pinned: true };
    const opsPage = { ...page, id: "page-ops", spaceId: ops.id, title: "Provozní příručka" };
    const fetchMock = requestMock(path => {
      if (path === "/api/spaces") return Promise.resolve(response([archive, space, rules, ops]));
      if (path === `/api/spaces/${ops.id}/tree`) return Promise.resolve(response({ spaceId: ops.id, version: 1, items: [{ ...opsPage, sortOrder: 1024, canEdit: true }] }));
    });
    const names = (app: VueWrapper) => app.findAll(".sidebar-space-name").map(item => item.text());
    const row = (app: VueWrapper, id: string) => app.get(`[data-sidebar-space="${id}"]`);
    const first = await mountApp(fetchMock);
    expect(names(first)).toEqual(["Zásady", "Archiv", "Engineering"]);
    expect(row(first, rules.id).get(".sidebar-space-link").text()).toContain("připnuto správcem");
    // Knihovna lists every space; the sidebar has no second link to the same overview.
    expect(first.find(".sidebar-all-spaces").exists()).toBe(false);
    // Nothing is expanded yet, so no tree is requested.
    expect(fetchMock.mock.calls.some(([path]) => String(path).endsWith("/tree"))).toBe(false);

    history.pushState(null, "", `#/spaces/${ops.id}`); window.dispatchEvent(new PopStateEvent("popstate")); await flushPromises();
    expect(names(first)).toEqual(["Zásady", "Archiv", "Engineering", "Provoz"]);
    expect(row(first, ops.id).get(".sidebar-space-caption").text()).toContain("Otevřený prostor");
    expect(row(first, ops.id).get(".sidebar-space-pin").attributes("aria-label")).toBe("Připnout prostor Provoz");
    expect(row(first, ops.id).get(".sidebar-space-link").attributes("aria-current")).toBe("page");
    expect(row(first, ops.id).text()).toContain(opsPage.title);

    // Pressing the arrow of a folded space asks for its tree a moment ahead of the click, once; pointing at it asks for nothing.
    const treeRequests = () => fetchMock.mock.calls.filter(([path]) => String(path).endsWith(`/api/spaces/${space.id}/tree`)).length;
    await row(first, space.id).get(".tree-toggle").trigger("pointerenter"); await flushPromises();
    expect(treeRequests()).toBe(0);
    await row(first, space.id).get(".tree-toggle").trigger("pointerdown"); await flushPromises();
    expect(treeRequests()).toBe(1);
    await row(first, space.id).get(".tree-toggle").trigger("click"); await flushPromises();
    expect(treeRequests()).toBe(1);
    expect(row(first, space.id).get(".tree-toggle").attributes("aria-expanded")).toBe("true");
    expect(row(first, space.id).text()).toContain(page.title);
    await first.get("a.breadcrumb-root").trigger("click"); await flushPromises();
    expect(names(first)).toEqual(["Zásady", "Archiv", "Engineering"]);
    first.unmount(); wrapper = undefined;

    const reloaded = await mountApp(fetchMock); await flushPromises();
    expect(row(reloaded, space.id).get(".tree-toggle").attributes("aria-expanded")).toBe("true");
    expect(row(reloaded, space.id).text()).toContain(page.title);
    expect(row(reloaded, rules.id).get(".tree-toggle").attributes("aria-expanded")).toBe("false");
    expect(reloaded.find(`[data-sidebar-space="${ops.id}"]`).exists()).toBe(false);
  });
  it("pins the open space at once and takes back an unpin the server refuses as required", async () => {
    const ops = { ...space, id: "space-2", name: "Provoz", pinned: false };
    const shared = { ...space, pinPolicy: "Default" };
    let finishPin!: (value: Response) => void, spaceReads = 0;
    const fetchMock = requestMock((path, init) => {
      if (path === "/api/spaces") return Promise.resolve(response(++spaceReads === 1 ? [shared, ops] : [{ ...shared, pinPolicy: "Required" }, { ...ops, pinned: true }]));
      if (path === `/api/spaces/${ops.id}/tree`) return Promise.resolve(response({ spaceId: ops.id, version: 1, items: [] }));
      if (path === `/api/me/spaces/${ops.id}/pin` && init?.method === "PUT") return new Promise(resolve => { finishPin = resolve; });
      if (path === `/api/me/spaces/${space.id}/pin` && init?.method === "PUT")
        return Promise.resolve(response({ detail: "Prostor je připnutý pro všechny.", code: "space_pin_required" }, 409));
    });
    const app = await mountApp(fetchMock);
    history.pushState(null, "", `#/spaces/${ops.id}`); window.dispatchEvent(new PopStateEvent("popstate")); await flushPromises();
    await app.get(`[data-sidebar-space="${ops.id}"] .sidebar-space-pin`).trigger("click"); await flushPromises();
    // Pinned before the server answers: the open space joins the pinned ones.
    expect(app.find(`[data-sidebar-space="${ops.id}"] .sidebar-space-caption`).exists()).toBe(false);
    const request = fetchMock.mock.calls.find(([path]) => String(path) === `/api/me/spaces/${ops.id}/pin`)!;
    expect(JSON.parse(request[1]!.body as string)).toEqual({ pinned: true });
    finishPin(response({ pinned: true })); await flushPromises();
    expect(app.find(`[data-sidebar-space="${ops.id}"] .sidebar-space-caption`).exists()).toBe(false);

    await app.get(`[aria-label="Další akce prostoru ${space.name}"]`).trigger("click"); await settleMenu();
    menuItems().find(item => item.textContent?.includes("Odepnout"))!.click(); await flushPromises(); await flushPromises();
    // Refused: the space is back in the sidebar, the list is reloaded and now shows the administrator's lock.
    await vi.waitFor(() => expect(document.body.textContent).toContain("odepnout ho nejde"));
    expect(spaceReads).toBe(2);
    expect(app.get(`[data-sidebar-space="${space.id}"] .sidebar-space-link`).text()).toContain("připnuto správcem");
  });
  it("keeps the open space's row where it is when pinning moves the space up the list", async () => {
    // Pinned spaces come first, by name; the open unpinned one is last. Pinned from its caption, „Provoz“ moves above
    // „Zásady“: the row went out of the reader's view with it (2.6.1). The list scrolls by as much as the row moved.
    const ops = { ...space, id: "space-2", name: "Provoz", pinned: false };
    const rules = { ...space, id: "space-3", name: "Zásady", pinned: true };
    const fetchMock = requestMock((path, init) => {
      if (path === "/api/spaces") return Promise.resolve(response([rules, ops]));
      if (path === `/api/spaces/${ops.id}/tree`) return Promise.resolve(response({ spaceId: ops.id, version: 1, items: [] }));
      if (path === `/api/me/spaces/${ops.id}/pin` && init?.method === "PUT") return Promise.resolve(response({ pinned: true }));
    });
    const app = await mountApp(fetchMock);
    history.pushState(null, "", `#/spaces/${ops.id}`); window.dispatchEvent(new PopStateEvent("popstate")); await flushPromises();
    const names = () => app.findAll(".sidebar-space-name").map(item => item.text());
    expect(names()).toEqual(["Zásady", "Provoz"]);
    // Every item of the list is 40 px under the one before it.
    vi.spyOn(Element.prototype, "getBoundingClientRect").mockImplementation(function (this: Element) {
      const item = this.closest("[data-sidebar-space]");
      const top = item ? [...item.parentElement!.children].indexOf(item) * 40 : 0;
      return { x: 0, y: top, top, left: 0, right: 0, bottom: top, width: 0, height: 0, toJSON: () => ({}) } as DOMRect;
    });
    let scrolled = 100;
    Object.defineProperty(app.get(".spaces-scroll").element, "scrollTop", { configurable: true, get: () => scrolled, set: (value: number) => { scrolled = value; } });
    await app.get(`[data-sidebar-space="${ops.id}"] .sidebar-space-pin`).trigger("click"); await flushPromises();
    expect(names()).toEqual(["Provoz", "Zásady"]);
    expect(scrolled).toBe(60);
  });
  it("offers pinning for everyone in the space menu only to someone who administers the space", async () => {
    const reader = { id: "reader", username: "reader", displayName: "Reader", isWikiAdmin: false, appearance: { layout: "connected", accent: "blue", mode: "system" } };
    const docs = { ...space, id: "space-2", name: "Dokumentace", role: "Reader" };
    const app = await mountApp(requestMock(path => {
      if (path === "/api/auth/me") return Promise.resolve(response(reader));
      if (path === "/api/spaces") return Promise.resolve(response([space, docs]));
    }));
    // The space Admin finds the pin policy where the space's own actions are.
    await app.get(`[aria-label="Další akce prostoru ${space.name}"]`).trigger("click"); await settleMenu();
    menuItems().find(item => item.textContent?.includes("Připnutí pro všechny"))!.click(); await flushPromises();
    expect(location.hash).toBe(`#/spaces/${space.id}/settings/general`);
    // Settings take over the rail (refresh 2.5); the way back restores the space navigation.
    await app.get(".settings-back").trigger("click"); await flushPromises();
    // A reader of the other space gets no such item.
    await app.get(`[aria-label="Další akce prostoru ${docs.name}"]`).trigger("click"); await settleMenu();
    expect(menuItems().some(item => item.textContent?.includes("Připnutí pro všechny"))).toBe(false);
  });
  it("reveals a search result inside a collapsed branch using the fresh tree", async () => {
    vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout"] });
    const parent = { ...page, id: "page-parent", title: "Parent", parentId: null };
    const child = { ...page, id: "page-child", title: "Child", parentId: parent.id };
    const fetchMock = requestMock(path => {
      if (path === `/api/spaces/${space.id}/tree`) return Promise.resolve(response({ spaceId: space.id, version: 1, items: [
        { ...parent, sortOrder: 1024, canEdit: true },
        { ...child, sortOrder: 2048, canEdit: true },
      ] }));
      if (path === `/api/pages/${child.id}`) return Promise.resolve(response(child));
      if (path.startsWith("/api/search?")) return Promise.resolve(response({ items: [{ ...child, spaceName: space.name, snippet: "" }], spaces: [], hasMore: false }));
    });
    const app = await mountApp(fetchMock);
    await app.get(".sidebar-space-link").trigger("click"); await flushPromises();
    await app.get('[aria-label="Sbalit Parent"]').trigger("click");
    expect(app.findAll(".tree-page").some(button => button.text() === child.title)).toBe(false);
    await app.get(".search-launch").trigger("click");
    await app.get(".omnisearch-input input").setValue("Child");
    await vi.advanceTimersByTimeAsync(180); await flushPromises();
    await app.get("#search-result-0").trigger("click"); await flushPromises();
    expect(app.get('[aria-label="Sbalit Parent"]').attributes("aria-expanded")).toBe("true");
    const active = app.findAll(".tree-page").find(button => button.text() === child.title)!;
    expect(active.attributes("aria-current")).toBe("page");
  });
  it("shows the page being opened by its tree title and opens it", async () => {
    const parent = { ...page, id: "page-parent", title: "Parent page" };
    let resolveChild!: (value: Response) => void;
    const fetchMock = requestMock((path) => {
      if (path === `/api/spaces/${space.id}/tree`)
        return Promise.resolve(response({
          spaceId: space.id,
          version: 1,
          items: [
            { ...parent, sortOrder: 1024, canEdit: true },
            { ...page, sortOrder: 2048, canEdit: true },
          ],
        }));
      if (path === `/api/pages/${parent.id}`)
        return Promise.resolve(response(parent));
      if (path === `/api/pages/${page.id}`)
        return new Promise((resolve) => { resolveChild = resolve; });
    });
    const app = await mountApp(fetchMock);
    await app.get(".sidebar-space-link").trigger("click");
    await flushPromises();
    const treePage = (title: string) => app.findAll(".tree-page").find(button => button.text() === title)!;

    await treePage(parent.title).trigger("click");
    await flushPromises();
    expect(titleOf(app)).toBe(parent.title);

    await treePage(page.title).trigger("click");
    // A page that is slow to load stands in the left page's place, titled from the tree (3.6-G N4), once the defer token has passed (a
    // quick one swaps with no frame of it): no editor of the left page remains to take an intent to write, and none reaches the page that arrives.
    expect(app.findComponent(DocumentEditor).exists()).toBe(true);
    await new Promise(settle => setTimeout(settle, 300));
    await flushPromises();
    expect(app.get("[data-page-arrival]").text()).toBe(page.title);
    expect(app.findComponent(DocumentEditor).exists()).toBe(false);
    resolveChild(response(page));
    await flushPromises();

    expect(titleOf(app)).toBe(page.title);
    expect(app.getComponent(DocumentEditor).props("readonly")).toBe(false);
  });
});

describe("omnisearch request lifecycle", () => {
  it("flags only the full pass of the omnisearch with the space it was opened in for missed queries", async () => {
    vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout"] });
    // The prefix preview runs only beside the meaning search (UX SE3-1); without it one request per query.
    useAiCapabilities().stop(); onTestFinished(() => useAiCapabilities().stop());
    const meaning: Route = path => path === '/api/ai/capabilities' ? Promise.resolve(response({ semanticSearch: true })) : undefined;
    const fetchMock = requestMock(path => path.startsWith("/api/search?") ? Promise.resolve(response({ items: [], hasMore: false })) : meaning(path));
    const app = await mountApp(fetchMock);
    await app.get(".sidebar-space-link").trigger("click"); await flushPromises();
    await app.get(".search-launch").trigger("click");
    await app.get(".omnisearch-input input").setValue("dovolená");
    await vi.advanceTimersByTimeAsync(180); await flushPromises();
    const searches = fetchMock.mock.calls.map(([path]) => new URL(String(path), location.origin)).filter(url => url.pathname === "/api/search");
    expect(searches.map(url => url.searchParams.has("fuzzy"))).toEqual([true, false]);
    expect(searches.map(url => url.searchParams.get("recordMiss"))).toEqual([null, "true"]);
    expect(searches[1]!.searchParams.get("contextSpaceId")).toBe(space.id);
  });
  it("gives a result row the full title as its hover hint only while the row cuts the title short", async () => {
    vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout"] });
    const long = { ...page, id: "long", title: "A very long title of a page that does not fit the palette", spaceName: space.name, snippet: "" };
    const short = { ...page, id: "short", title: "Short", spaceName: space.name, snippet: "" };
    const fetchMock = requestMock(path => path.startsWith("/api/search?") ? Promise.resolve(response({ items: [long, short], hasMore: false })) : undefined);
    const app = await mountApp(fetchMock);
    await app.get(".search-launch").trigger("click");
    await app.get(".omnisearch-input input").setValue("title"); await vi.advanceTimersByTimeAsync(180); await flushPromises();
    const rows = app.findAll("a.search-result");
    Object.defineProperty(rows[0]!.get(".search-result-title").element, "scrollWidth", { configurable: true, value: 400 });
    Object.defineProperty(rows[0]!.get(".search-result-title").element, "clientWidth", { configurable: true, value: 200 });
    await rows[0]!.trigger("pointerenter"); await rows[1]!.trigger("pointerenter");
    expect(rows[0]!.attributes("title")).toBe(long.title);
    expect(rows[1]!.attributes("title")).toBeUndefined();
  });
  it("debounces the search from the second character even before any result is shown", async () => {
    vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout"] });
    const fetchMock = requestMock(path => path.startsWith('/api/search?') ? Promise.resolve(response({ items: [], hasMore: false })) : undefined);
    const app = await mountApp(fetchMock);
    await app.get('.search-launch').trigger('click');
    const input = app.get('.omnisearch-input input');
    const searches = () => fetchMock.mock.calls.filter(([path]) => String(path).startsWith('/api/search?')).length;
    await input.setValue('gu'); await vi.advanceTimersByTimeAsync(30);
    await input.setValue('gui'); await vi.advanceTimersByTimeAsync(30);
    await input.setValue('guid'); await vi.advanceTimersByTimeAsync(100); await flushPromises();
    expect(searches()).toBe(2); // the first key at once, the keys typed behind it as one trailing request
  });
  it("requests fifty results per batch and keeps the selected result when more are appended", async () => {
    vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout"] });
    const firstBatch = Array.from({ length: 50 }, (_, index) => ({ ...page, id: `batch-${index}`, title: `Guide ${index}`, spaceName: space.name, snippet: 'Useful search context.' }));
    const fetchMock = requestMock(path => path.startsWith('/api/search?') ? Promise.resolve(response(
      path.includes('offset=50') ? { items: [{ ...firstBatch[0], id: 'next', title: 'Next guide' }], hasMore: false, nextOffset: null }
        : { items: firstBatch, hasMore: true, nextOffset: 50 },
    )) : undefined);
    const app = await mountApp(fetchMock);
    await app.get('.search-launch').trigger('click');
    const input = app.get('.omnisearch-input input');
    await input.setValue('guide'); await vi.advanceTimersByTimeAsync(180); await flushPromises();
    const paths = () => fetchMock.mock.calls.map(([path]) => String(path)).filter(path => path.startsWith('/api/search?'));
    expect(paths().every(path => new URL(path, location.origin).searchParams.get('limit') === '50')).toBe(true);
    expect(app.get('.search-summary-count').text()).toContain('50');
    await input.trigger('keydown', { key: 'PageDown' });
    expect(input.attributes('aria-activedescendant')).toBe('search-result-8');
    const selected = app.get('#search-result-8').element;
    await app.get('.search-footer .search-load-more').trigger('click'); await flushPromises();
    expect(app.findAll('#search-results [role=option]')).toHaveLength(51);
    expect(app.get('#search-result-8').element).toBe(selected);
    expect(app.get('#search-result-8').attributes('aria-selected')).toBe('true');
    expect(app.get('.search-summary-count').text()).toContain('51');
    expect(new URL(paths().at(-1)!, location.origin).searchParams.get('offset')).toBe('50');
    await input.trigger('keydown', { key: 'PageUp' });
    expect(input.attributes('aria-activedescendant')).toBe('search-result-0');
  });
  it("shows prefix results immediately and preserves keyboard selection when fuzzy results arrive", async () => {
    vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout"] });
    // The prefix preview runs only beside the meaning search (UX SE3-1); without it one request per query.
    useAiCapabilities().stop(); onTestFinished(() => useAiCapabilities().stop());
    const meaning: Route = path => path === '/api/ai/capabilities' ? Promise.resolve(response({ semanticSearch: true })) : undefined;
    const first = { ...page, id: 'first', title: 'Mračena', spaceName: space.name, snippet: '' };
    const second = { ...first, id: 'second', title: 'Mrakomorová' };
    const fuzzy = { ...first, id: 'fuzzy', title: 'Similar result' };
    let finishFull!: (value: Response) => void;
    const fetchMock = requestMock(path => {
      if (path.startsWith('/api/search?')) return path.includes('fuzzy=false')
        ? Promise.resolve(response({ items: [first, second], hasMore: true, nextOffset: 2 }))
        : new Promise(resolve => { finishFull = resolve; });
      if (path.startsWith('/api/integrations/youtrack/search?')) return Promise.resolve(response([]));
      return meaning(path);
    });
    const app = await mountApp(fetchMock);
    await app.get('.search-launch').trigger('click');
    await app.get('.omnisearch-input input').setValue('MRA');
    await vi.advanceTimersByTimeAsync(0); await flushPromises();
    expect(app.get('#search-results').text()).toContain('Mračena');
    expect(fetchMock.mock.calls.filter(([path]) => String(path).startsWith('/api/search?'))).toHaveLength(1);
    expect(app.find('.search-load-more').exists()).toBe(false);
    await app.get('.omnisearch-input input').trigger('keydown', { key: 'ArrowDown' });
    await app.get('.omnisearch-input input').trigger('keydown', { key: 'ArrowDown' });
    await vi.advanceTimersByTimeAsync(180); await flushPromises();
    finishFull(response({ items: [fuzzy, first, second], hasMore: false }));
    await flushPromises();
    expect(app.get('#search-results').text()).toContain('Similar result');
    // The selected row holds its place; what the full pass adds goes below it.
    expect(app.get('#search-result-1').attributes('aria-selected')).toBe('true');
    expect(app.get('#search-result-1').text()).toContain('Mrakomorová');
    expect(app.get('#search-result-2').text()).toContain('Similar result');
    expect(app.get('.search-source-status').text()).toContain('hotovo');
  });

  it("keeps prefix results if enrichment fails and cancels an old enrichment on the next keystroke", async () => {
    vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout"] });
    // The prefix preview runs only beside the meaning search (UX SE3-1); without it one request per query.
    useAiCapabilities().stop(); onTestFinished(() => useAiCapabilities().stop());
    const meaning: Route = path => path === '/api/ai/capabilities' ? Promise.resolve(response({ semanticSearch: true })) : undefined;
    let finishOld!: (value: Response) => void;
    let oldSignal: AbortSignal | undefined;
    const app = await mountApp(requestMock((path, init) => {
      if (path.startsWith('/api/search?')) {
        if (path.includes('fuzzy=false')) return Promise.resolve(response({ items: [{ ...page, spaceName: space.name, title: path.includes('second') ? 'Second match' : 'First match', snippet: '' }], hasMore: false }));
        if (path.includes('first')) { oldSignal = init?.signal as AbortSignal; return new Promise(resolve => { finishOld = resolve; }); }
        return Promise.resolve(response({ title: 'Enrichment unavailable' }, 503));
      }
      if (path.startsWith('/api/integrations/youtrack/search?')) return Promise.resolve(response([]));
      return meaning(path);
    }));
    await app.get('.search-launch').trigger('click');
    await app.get('.omnisearch-input input').setValue('first');
    await vi.advanceTimersByTimeAsync(180); await flushPromises();
    expect(app.get('#search-results').text()).toContain('First match');
    await app.get('.omnisearch-input input').setValue('second');
    await vi.advanceTimersByTimeAsync(180); await flushPromises();
    expect(oldSignal?.aborted).toBe(true);
    expect(app.get('#search-results').text()).toContain('Second match');
    expect(app.get('[role="alert"]').text()).toContain('Enrichment unavailable');
    finishOld(response({ items: [{ ...page, spaceName: space.name, title: 'Stale enrichment', snippet: '' }], hasMore: false }));
    await flushPromises();
    expect(app.get('#search-results').text()).not.toContain('Stale enrichment');
    expect(app.get('#search-results').text()).toContain('Second match');
  });

  it("keeps the shown results in place while the next keystroke loads and never opens a stale one", async () => {
    vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout"] });
    let finishSecond!: (value: Response) => void;
    const app = await mountApp(requestMock(path => {
      if (path.startsWith('/api/search?') && path.includes('q=second')) return new Promise(resolve => { finishSecond = resolve; });
      if (path.startsWith('/api/search?')) return Promise.resolve(response({ items: [{ ...page, spaceName: space.name, title: 'First match', snippet: '' }], hasMore: false }));
      if (path.startsWith('/api/integrations/youtrack/search?')) return Promise.resolve(response([]));
    }));
    await app.get('.search-launch').trigger('click');
    await app.get('.omnisearch-input input').setValue('first');
    await vi.advanceTimersByTimeAsync(180); await flushPromises();
    await app.get('.omnisearch-input input').setValue('second');
    // While results are on screen the next request waits out a short debounce (one request per burst of keys), the first is immediate.
    await vi.advanceTimersByTimeAsync(60); await flushPromises();
    expect(finishSecond).toBeUndefined();
    await vi.advanceTimersByTimeAsync(20); await flushPromises();
    expect(finishSecond).toBeTypeOf('function');
    expect(app.get('#search-results').text()).toContain('First match');
    expect(app.get('#search-results').attributes('data-stale')).toBeDefined();
    expect(app.find('.search-skeleton').exists()).toBe(false);
    const opened = location.hash;
    await app.get('.omnisearch-input input').trigger('keydown', { key: 'Enter' });
    expect(location.hash).toBe(opened);
    finishSecond(response({ items: [{ ...page, spaceName: space.name, title: 'Second match', snippet: '' }], hasMore: false }));
    await flushPromises();
    // The Enter pressed while the query was loading is not lost: the first result of the answered query opens.
    expect(location.hash).toBe(`#/pages/${page.id}`);
    expect(app.get('#search-results').attributes('data-stale')).toBeUndefined();
  });

  it("drops an Enter remembered for a query the reader has typed past", async () => {
    vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout"] });
    const fetchMock = requestMock(path => path.startsWith('/api/search?') && path.includes('q=second') ? new Promise(() => {})
      : path.startsWith('/api/search?') ? Promise.resolve(response({ items: [{ ...page, spaceName: space.name, title: 'First match', snippet: '' }], hasMore: false })) : undefined);
    const app = await mountApp(fetchMock);
    await app.get('.search-launch').trigger('click');
    const input = app.get('.omnisearch-input input');
    await input.setValue('first'); await vi.advanceTimersByTimeAsync(180); await flushPromises();
    await input.setValue('second'); await vi.advanceTimersByTimeAsync(80); await flushPromises();
    const opened = location.hash;
    await input.trigger('keydown', { key: 'Enter' });
    await input.setValue('third'); await vi.advanceTimersByTimeAsync(80); await flushPromises();
    expect(location.hash).toBe(opened);
    await vi.advanceTimersByTimeAsync(1000); await flushPromises();
    expect(location.hash).toBe(opened);
  });

  it("lists only YouTrack issues placed on readable pages, after the page content, and never asks YouTrack itself", async () => {
    vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout"] });
    const issue = { key: 'APP-12', title: page.title, url: 'https://tracker.test/issue/APP-12', state: 'Open' };
    const asked: string[] = [];
    const app = await mountApp(requestMock(path => {
      asked.push(path);
      if (path.startsWith('/api/search?')) return Promise.resolve(response({ items: [{ ...page, spaceName: space.name, snippet: '', widgets: [{ kind: 'youtrack', ...issue, detail: '', headingId: null }] }], hasMore: false }));
      return undefined;
    }));
    await app.get('.search-launch').trigger('click');
    await app.get('.omnisearch-input input').setValue(page.title);
    await vi.advanceTimersByTimeAsync(200); await flushPromises();
    const groups = app.findAll('.search-group').map(group => group.attributes('data-kind'));
    expect(groups.indexOf('youtrack')).toBeGreaterThan(groups.indexOf('pages'));
    expect(app.findAll('a[href="https://tracker.test/issue/APP-12"]')).toHaveLength(1);
    expect(app.get('#search-results a').attributes('aria-selected')).toBe('true');
    expect(app.get('#search-results a').attributes('href')).not.toContain('tracker.test');
    expect(asked.some(path => path.includes('/integrations/youtrack/search'))).toBe(false);
  });

  it("cancels pending search passes on close and accepts the same query in a fresh search", async () => {
    vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout"] });
    const fetchMock = requestMock((path) =>
      path.startsWith("/api/search?")
        ? Promise.resolve(response({ items: [], hasMore: false }))
        : undefined,
    );
    const app = await mountApp(fetchMock);
    await app.get(".search-launch").trigger("click");
    await app.get(".omnisearch-input input").setValue("prihlaseni");
    await app.get(".search-top [data-slot=button]").trigger("click");
    await vi.advanceTimersByTimeAsync(300);
    expect(
      fetchMock.mock.calls.filter(([url]) =>
        String(url).startsWith("/api/search?"),
      ),
    ).toHaveLength(0);
    await app.get(".search-launch").trigger("click");
    expect((app.get(".omnisearch-input input").element as HTMLInputElement).value).toBe("");
    await app.get(".omnisearch-input input").setValue("prihlaseni");
    await vi.advanceTimersByTimeAsync(180);
    await flushPromises();
    expect(
      fetchMock.mock.calls.filter(([url]) =>
        String(url).startsWith("/api/search?"),
      ),
    ).toHaveLength(1); // one pass without the meaning search (UX SE3-1)
    expect(app.get(".search-dialog").text()).toContain("Žádné odpovídající stránky");
  });

  it("does not show stale results even if the transport resolves after cancellation", async () => {
    vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout"] });
    let resolveFirst!: (value: Response) => void;
    let firstSignal: AbortSignal | undefined;
    const fetchMock = requestMock((path, init) => {
      if (path.includes("/api/search?q=first")) {
        firstSignal = init?.signal as AbortSignal;
        return new Promise((resolve) => {
          resolveFirst = resolve;
        });
      }
      if (path.includes("/api/search?q=second"))
        return Promise.resolve(
          response({
            items: [
              {
                id: "new",
                spaceId: space.id,
                spaceName: space.name,
                title: "New result",
                snippet: "Correct result",
              },
            ],
            hasMore: false,
          }),
        );
    });
    const app = await mountApp(fetchMock);
    await app.get(".search-launch").trigger("click");
    await app.get(".omnisearch-input input").setValue("first");
    await vi.advanceTimersByTimeAsync(180);
    await app.get(".omnisearch-input input").setValue("second");
    await vi.advanceTimersByTimeAsync(180);
    await flushPromises();
    expect(firstSignal?.aborted).toBe(true);
    resolveFirst(
      response({
        items: [
          {
            id: "old",
            spaceId: space.id,
            spaceName: space.name,
            title: "Stale result",
            snippet: "Old content",
          },
        ],
        hasMore: false,
      }),
    );
    await flushPromises();
    expect(app.get("#search-results").text()).toContain("New result");
    expect(app.get("#search-results").text()).not.toContain("Stale result");
  });
});

describe("omnisearch keyboard and failure feedback", () => {
  it("reserves the host search shortcut before an editor can also open its link popup", async () => {
    const app = await mountApp(requestMock());
    const editorShortcut = vi.fn();
    document.addEventListener("keydown", editorShortcut);
    try {
      app.get(".search-launch").element.dispatchEvent(new KeyboardEvent("keydown", { key: "k", ctrlKey: true, bubbles: true, cancelable: true }));
      await flushPromises();
      expect(app.get(".search-dialog").attributes("open")).toBeDefined();
      expect(editorShortcut).not.toHaveBeenCalled();
    } finally { document.removeEventListener("keydown", editorShortcut); }
  });
  it("closes search on Escape before a background editor popup can consume the key", async () => {
    const app = await mountApp(requestMock());
    await app.get(".search-launch").trigger("click");
    const backgroundPopup = vi.fn((event: KeyboardEvent) => { if (event.key === "Escape") event.preventDefault(); });
    document.addEventListener("keydown", backgroundPopup);
    try {
      await app.get(".omnisearch-input input").trigger("keydown", { key: "Escape" });
      expect(app.get(".search-dialog").attributes("open")).toBeUndefined();
      expect(backgroundPopup).not.toHaveBeenCalled();
    } finally { document.removeEventListener("keydown", backgroundPopup); }
  });
  it("leaves composing text and native filter Escape to their owner before dialog cancellation", async () => {
    const app = await mountApp(requestMock());
    await app.get(".search-launch").trigger("click");
    await app.get(".omnisearch-input input").trigger("keydown", { key: "Escape", isComposing: true });
    expect(app.get(".search-dialog").attributes("open")).toBeDefined();
    expect(app.get(".search-dialog").attributes("open")).toBeDefined();
    await app.get(".search-dialog").trigger("cancel");
    expect(app.get(".search-dialog").attributes("open")).toBeUndefined();
  });
  it.each([false, true])("restores result selection and scroll after fresh authorization on Back, already open=%s", async alreadyOpen => {
    vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout"] });
    const fetchMock = requestMock(path => path.startsWith("/api/search?") ? Promise.resolve(response({
      items: [{ ...page, id: "another", title: "Another page", spaceName: space.name, snippet: "" }, { ...page, spaceName: space.name, snippet: "" }], hasMore: false,
    })) : undefined);
    const app = await mountApp(fetchMock);
    if (alreadyOpen) {
      history.replaceState(null, "", `/#/pages/${page.id}`);
      window.dispatchEvent(new PopStateEvent("popstate")); await flushPromises();
    }
    await app.get(".search-launch").trigger("click");
    await app.get(".omnisearch-input input").setValue("pristup");
    await vi.advanceTimersByTimeAsync(180); await flushPromises();
    await app.get(".omnisearch-input input").trigger("keydown", { key: "ArrowDown" });
    await app.get(".omnisearch-input input").trigger("keydown", { key: "ArrowDown" });
    const content = app.get(".search-content").element;
    content.scrollTop = 320;
    const searchAddress = location.href;
    let savedState: unknown;
    const originalPush = history.pushState.bind(history);
    const push = vi.spyOn(history, "pushState").mockImplementation((state, unused, url) => {
      savedState = history.state; originalPush(state, unused, url);
    });
    await app.get('#search-result-1').trigger("click"); await flushPromises();
    push.mockRestore();
    content.scrollTop = 0;
    history.replaceState(savedState, "", searchAddress);
    window.dispatchEvent(new PopStateEvent("popstate")); await flushPromises();
    await vi.advanceTimersByTimeAsync(180); await flushPromises();
    expect(fetchMock.mock.calls.filter(([url]) => String(url).startsWith("/api/search?") && !String(url).includes('fuzzy=false'))).toHaveLength(2);
    expect(app.get('#search-result-1').attributes("aria-selected")).toBe("true");
    expect(content.scrollTop).toBe(320);
  });
  function foundPages() {
    return {
      items: [
        {
          id: page.id,
          spaceId: space.id,
          spaceName: space.name,
          title: page.title,
          snippet: "Týmový postup",
        },
      ],
      hasMore: false,
    };
  }

  it("keeps result focus in the combobox and does not requery an already open dialog on Ctrl+K", async () => {
    vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout"] });
    const fetchMock = requestMock((path) =>
      path.startsWith("/api/search?")
        ? Promise.resolve(response(foundPages()))
        : undefined,
    );
    const app = await mountApp(fetchMock);
    await app.get(".search-launch").trigger("click");
    const input = app.get(".omnisearch-input input");
    await input.setValue("prihlaseni");
    await vi.advanceTimersByTimeAsync(180);
    await flushPromises();
    await input.trigger("keydown", { key: "k", ctrlKey: true });
    await vi.advanceTimersByTimeAsync(180);
    await flushPromises();
    expect(
      fetchMock.mock.calls.filter(([url]) =>
        String(url).startsWith("/api/search?"),
      ),
    ).toHaveLength(1); // one pass without the meaning search (UX SE3-1)
    expect(document.activeElement).toBe(input.element);
    expect(app.get('[role="option"]').attributes("tabindex")).toBe("0");
  });

  it("does not open a result when Enter commits an IME composition", async () => {
    vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout"] });
    const fetchMock = requestMock((path) =>
      path.startsWith("/api/search?")
        ? Promise.resolve(response(foundPages()))
        : undefined,
    );
    const app = await mountApp(fetchMock);
    await app.get(".search-launch").trigger("click");
    const input = app.get(".omnisearch-input input");
    await input.setValue("prihlaseni");
    await vi.advanceTimersByTimeAsync(180);
    await flushPromises();
    await input.trigger("keydown", { key: "Enter", isComposing: true });
    expect(
      fetchMock.mock.calls.filter(([url]) =>
        String(url).startsWith("/api/pages/"),
      ),
    ).toHaveLength(0);
    expect(app.get(".search-dialog").attributes("open")).toBeDefined();
  });

  it("keeps the original view when the dialog closes before a result finishes opening", async () => {
    vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout"] });
    let resolvePage!: (value: Response) => void;
    const fetchMock = requestMock((path) => {
      if (path.startsWith("/api/search?"))
        return Promise.resolve(response(foundPages()));
      if (path === `/api/pages/${page.id}`)
        return new Promise((resolve) => {
          resolvePage = resolve;
        });
    });
    const app = await mountApp(fetchMock);
    await app.get(".search-launch").trigger("click");
    const input = app.get(".omnisearch-input input");
    await input.setValue("prihlaseni");
    await vi.advanceTimersByTimeAsync(180);
    await flushPromises();
    await input.trigger("keydown", { key: "Enter" });
    expect(app.findAll('.search-dialog [role="status"]').map(status => status.text()).join(' ')).toContain(
      "Otevírání stránky",
    );
    await app.get(".search-top [data-slot=button]").trigger("click");
    resolvePage(response(page));
    await flushPromises();
    expect(app.find(".document-header").exists()).toBe(false);
    await settleLazyViews(); expect(app.find("[data-home-dashboard]").exists()).toBe(true);
    expect(location.hash).toBe("");
  });

  it("reports a failed result-open inside the active modal and lets the user refresh the search", async () => {
    vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout"] });
    const fetchMock = requestMock((path) => {
      if (path.startsWith("/api/search?"))
        return Promise.resolve(response(foundPages()));
      if (path === `/api/pages/${page.id}`)
        return Promise.resolve(
          response({ detail: "Tato stránka už není dostupná." }, 403),
        );
    });
    const app = await mountApp(fetchMock);
    await app.get(".search-launch").trigger("click");
    const input = app.get(".omnisearch-input input");
    await input.setValue("prihlaseni");
    await vi.advanceTimersByTimeAsync(180);
    await flushPromises();
    await input.trigger("keydown", { key: "Enter" });
    await flushPromises();
    expect(app.get('.search-dialog [role="alert"]').text()).toContain(
      "Tato stránka už není dostupná.",
    );
    expect(app.get(".search-dialog").attributes("open")).toBeDefined();
    await app.get(".search-retry").trigger("click");
    await vi.advanceTimersByTimeAsync(180);
    await flushPromises();
    expect(
      fetchMock.mock.calls.filter(([url]) =>
        String(url).startsWith("/api/search?"),
      ),
    ).toHaveLength(2); // one pass per search without the meaning search (UX SE3-1)
    expect(app.find('.search-dialog [role="alert"]').exists()).toBe(false);
  });
});

describe("publish while editing", () => {
  it.each([true, false])("announces publication only after the server confirms it (success=%s)", async success => {
    let finish!: (value: Response) => void;
    const published = new Promise<Response>(resolve => { finish = resolve; });
    // The page is saved but not published: that is when Publikovat shows (nothing publishes while the version is the published one).
    const fetchMock = requestMock((path, init) => {
      if (path === `/api/pages/${page.id}` && init?.method === 'PUT')
        return Promise.resolve(response({ ...page, version: 2 }));
      if (path === `/api/pages/${page.id}`) return Promise.resolve(response({ ...page, version: 2 }));
      if (path === `/api/pages/${page.id}/publish`) return published;
    });
    const app = await mountApp(fetchMock);
    await app.get('.sidebar-space-link').trigger('click'); await flushPromises();
    await app.get('.tree-page').trigger('click'); await flushPromises();
    // Loading an already published page is not a fresh confirmation.
    expect(app.find('.document-status [data-confirmed="true"]').exists()).toBe(false);
    const editor = await startEditing(app);
    editor.vm.$emit('change');
    await flushPromises();
    await app.get('.document-primary-actions .publish-button').trigger('click');
    await flushPromises();
    expect(app.get('.publish-button').attributes('aria-busy')).toBe('true');
    expect(app.get('.publish-button').text()).toBe('Publikování…');
    expect(app.find('.document-status [data-confirmed="true"]').exists()).toBe(false);
    finish(success ? response({ ...page, version: 2, publishedVersion: 2 }) : response({ error: 'Publication failed' }, 500));
    await flushPromises(); await flushPromises();
    expect(app.find('.document-status [data-confirmed="true"]').exists()).toBe(success);
    if (success) {
      expect(app.get('.document-status').text()).toBe('Stránka byla publikována.');
      expect(app.find('.publish-button').exists()).toBe(false);
    } else {
      expect(app.get('.publish-button').attributes('aria-busy')).toBe('false');
      expect(app.get('.publish-button').text()).toBe('Zveřejnit změny');
      expect(app.get('.document-status').attributes('data-tone')).toBe('error');
    }
  });
  // Access is changed in Nastavení prostoru now, never over an open document: a space administrator who
  // gives up the role there leaves the settings they may no longer open, and the spaces are re-read.
  it("leaves space settings after an own access downgrade reported by the settings surface", async () => {
    let role: "Admin" | "Reader" = "Admin";
    const fetchMock = requestMock((path) => {
      if (path === "/api/auth/me")
        return Promise.resolve(response({ id: "user-1", username: "member", displayName: "Space Admin", isWikiAdmin: false, appearance: { layout: "connected", accent: "blue", mode: "system" } }));
      if (path === "/api/spaces") return Promise.resolve(response([{ ...space, role }]));
      if (path === "/api/vaults" || path === "/api/vault-locations") return Promise.resolve(response([]));
      if (path === `/api/spaces/${space.id}/access`) return Promise.resolve(response({ users: [], groups: [], grants: [] }));
    });
    const app = await mountApp(fetchMock);
    history.pushState(null, "", `#/spaces/${space.id}/settings/access`);
    window.dispatchEvent(new PopStateEvent("popstate")); await flushPromises();
    await settleLazyViews(); expect(app.findComponent({ name: "SettingsHost" }).exists()).toBe(true);
    role = "Reader";
    app.getComponent({ name: "SettingsHost" }).vm.$emit("accessChanged");
    await flushPromises(); await flushPromises();
    expect(location.hash).toBe("#/");
    expect(app.findComponent({ name: "SettingsHost" }).exists()).toBe(false);
  });

});

describe("omnisearch filters and continuation", () => {
  it("lets Tab reach the active result and arrows move focus through result links", async () => {
    vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout"] });
    const fetchMock = requestMock(path => path.startsWith("/api/search?") ? Promise.resolve(response({ items: [{ ...page, snippet: "First" }, { ...page, id: "second", title: "Second", snippet: "Next" }], hasMore: false })) : undefined);
    const app = await mountApp(fetchMock); await app.get(".search-launch").trigger("click");
    await app.get(".omnisearch-input input").setValue("pristupy");
    await vi.advanceTimersByTimeAsync(180); await flushPromises();
    const links = app.findAll("#search-results a");
    expect(links[0]!.attributes("tabindex")).toBe("0");
    (links[0]!.element as HTMLElement).focus();
    await links[0]!.trigger("keydown", { key: "ArrowDown" });
    expect(document.activeElement).toBe(links[1]!.element);
    expect(links[1]!.attributes("tabindex")).toBe("0");
    expect(links[0]!.attributes("tabindex")).toBe("-1");
  });
  it("leaves a changed document for a space from the omnisearch without asking", async () => {
    vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout"] });
    const fetchMock = requestMock(path => path.startsWith("/api/search?") ? Promise.resolve(response({ items: [], spaces: [space], hasMore: false })) : undefined);
    const app = await mountApp(fetchMock);
    await app.get(".sidebar-space-link").trigger("click"); await flushPromises();
    await app.get(".tree-page").trigger("click"); await flushPromises();
    const editor = await startEditing(app);
    editor.vm.$emit("change"); await flushPromises();
    await app.get(".search-launch").trigger("click");
    await app.get(".omnisearch-input input").setValue("Engineering");
    await vi.advanceTimersByTimeAsync(180); await flushPromises();
    await app.get("#search-results a").trigger("click"); await flushPromises();
    expect(leaveButton("Zůstat")).toBeUndefined();
    expect(puts(fetchMock)).toHaveLength(1);
    expect(app.get(".search-dialog").attributes("open")).toBeUndefined();
    expect(location.hash).toBe("#/spaces/space-1");
  });
  it("provides a clean document URL for a modifier-click without navigating the active search", async () => {
    vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout"] });
    const fetchMock = requestMock(path => path.startsWith("/api/search?") ? Promise.resolve(response({ items: [{ ...page, spaceName: space.name, snippet: "Postup", headingId: "pristupy" }], hasMore: false })) : undefined);
    const app = await mountApp(fetchMock);
    await app.get(".search-launch").trigger("click");
    await app.get(".omnisearch-input input").setValue("pristupy");
    await vi.advanceTimersByTimeAsync(180); await flushPromises();
    const result = app.get("#search-results a");
    const href = result.attributes("href");
    expect(href).toBeTruthy();
    const target = new URL(href!);
    expect(target.search).toBe(""); expect(target.hash).toBe("#/pages/page-1?heading=pristupy");
    const before = location.href;
    await result.trigger("click", { ctrlKey: true }); await flushPromises();
    expect(location.href).toBe(before);
    expect(app.get(".search-dialog").attributes("open")).toBeDefined();
  });

  it("prevents native anchor navigation on repeated Enter while a result is opening", async () => {
    vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout"] });
    const fetchMock = requestMock(path => path.startsWith("/api/search?") ? Promise.resolve(response({ items: [{ ...page, spaceName: space.name, snippet: "" }], hasMore: false }))
      : path === `/api/pages/${page.id}` ? new Promise(() => {}) : undefined);
    const app = await mountApp(fetchMock);
    await app.get(".search-launch").trigger("click");
    await app.get(".omnisearch-input input").setValue("pristup");
    await vi.advanceTimersByTimeAsync(180); await flushPromises();
    const result = app.get('#search-result-0');
    await result.trigger("keydown", { key: "Enter" }); await flushPromises();
    expect(app.text()).toContain("Otevírání stránky…");
    const repeated = new KeyboardEvent("keydown", { key: "Enter", bubbles: true, cancelable: true });
    result.element.dispatchEvent(repeated);
    expect(repeated.defaultPrevented).toBe(true);
    expect(fetchMock.mock.calls.filter(([url]) => String(url).split("?")[0] === `/api/pages/${page.id}`)).toHaveLength(1);
  });
  it("shows accessible recent suggestions only while a fresh search is open and exposes real result links", async () => {
    const fetchMock = requestMock(path => path.startsWith("/api/me/library/recent") ? Promise.resolve(response({ items: [{ ...page, spaceName: space.name }], hasMore: false })) : undefined);
    const app = await mountApp(fetchMock);
    expect(app.find(".search-suggestions").exists()).toBe(false);
    await app.get(".search-launch").trigger("click"); await flushPromises();
    expect(app.get(`.search-suggestions a[href$="#/pages/${page.id}"]`).text()).toContain(page.title);
    await app.get(".search-top [data-slot=button]").trigger("click"); await flushPromises();
    expect(app.find(".search-suggestions").exists()).toBe(false);
  });
  it("preserves content, last-change and sort filters across requests, pagination and history, then resets a new search", async () => {
    vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout"] });
    const fetchMock = requestMock(path => path.startsWith("/api/search?") ? Promise.resolve(response({ items: [], spaces: [], hasMore: true, nextOffset: 20 })) : undefined);
    const app = await mountApp(fetchMock);
    await app.get(".search-launch").trigger("click");
    await app.get(".omnisearch-input input").setValue("pristup");
    await searchPill(app, "Obsah", "drafts");
    await searchPill(app, "Upraveno", "week");
    await searchPill(app, "Řazení", "published");
    await vi.advanceTimersByTimeAsync(180); await flushPromises();
    const url = new URL(location.href);
    expect(url.searchParams.get("content")).toBe("drafts");
    expect(url.searchParams.get("changedWithin")).toBe("week");
    expect(url.searchParams.get("sort")).toBe("published");
    await app.get(".search-load-more").trigger("click"); await flushPromises();
    const lastRequest = String(fetchMock.mock.calls.filter(([path]) => String(path).startsWith("/api/search?")).at(-1)![0]);
    expect(lastRequest).toContain("content=drafts"); expect(lastRequest).toContain("changedWithin=week"); expect(lastRequest).toContain("sort=published"); expect(lastRequest).toContain("offset=20");
    await app.get(".search-top [data-slot=button]").trigger("click");
    history.replaceState(null, "", url); window.dispatchEvent(new PopStateEvent("popstate")); await flushPromises();
    expect(searchPillOf(app, "Obsah", "drafts").attributes("data-state")).toBe("checked");
    expect(searchPillOf(app, "Upraveno", "week").attributes("data-state")).toBe("checked");
    await app.get(".search-top [data-slot=button]").trigger("click");
    await app.get(".search-launch").trigger("click");
    expect((app.get(".omnisearch-input input").element as HTMLInputElement).value).toBe("");
    expect(searchPillOf(app, "Obsah", "all").attributes("data-state")).toBe("checked");
    expect(searchPillOf(app, "Upraveno", "any").attributes("data-state")).toBe("checked");
  });
  it("records query and scope in a shareable URL without adding a history entry per character", async () => {
    vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout"] });
    const fetchMock = requestMock(path => path.startsWith("/api/search?") ? Promise.resolve(response({ items: [], spaces: [], hasMore: false, nextOffset: null })) : undefined);
    const app = await mountApp(fetchMock); const pushes = vi.spyOn(history, "pushState");
    await app.get(".search-launch").trigger("click");
    await app.get(".omnisearch-input input").setValue("pris");
    await app.get(".omnisearch-input input").setValue("pristup");
    await pickSearchSpace(app, space.id);
    await vi.advanceTimersByTimeAsync(180); await flushPromises();
    expect(pushes).toHaveBeenCalledTimes(1);
    const url = new URL(location.href); expect(url.searchParams.get("q")).toBe("pristup"); expect(url.searchParams.get("scope")).toBe(space.id);
    expect(fetchMock.mock.calls.some(([path]) => String(path).includes(`spaceId=${space.id}`))).toBe(true);
  });
  it("appends another results page and resets pagination when the filter changes", async () => {
    vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout"] });
    const fetchMock = requestMock(path => {
      if (!path.startsWith("/api/search?")) return;
      const params = new URL(path, location.origin).searchParams;
      return Promise.resolve(response(params.has("offset") ? { items: [{ id: "next", title: "Next article", snippet: "", spaceId: space.id, spaceName: space.name }], hasMore: false, nextOffset: null } : { items: [{ id: "first", title: "First article", snippet: "", spaceId: space.id, spaceName: space.name }], hasMore: true, nextOffset: 1 }));
    });
    const app = await mountApp(fetchMock); await app.get(".search-launch").trigger("click"); await app.get(".omnisearch-input input").setValue("article");
    await vi.advanceTimersByTimeAsync(180); await flushPromises();
    await app.get(".search-load-more").trigger("click"); await flushPromises();
    expect(app.findAll(".search-result")).toHaveLength(2); expect(app.find(".search-load-more").exists()).toBe(false);
    await pickSearchSpace(app, space.id); await vi.advanceTimersByTimeAsync(180); await flushPromises();
    expect(app.findAll(".search-result")).toHaveLength(1); expect(app.get(".search-result").text()).toContain("First article");
  });
});

describe("calmer search overlay", () => {
  it("reveals secondary filters on request and keeps their effect visible after collapsing", async () => {
    const app = await mountApp(requestMock());
    await app.get(".search-launch").trigger("click");
    const filters = app.get("#search-filters");
    expect(filters.attributes("aria-hidden")).toBe("true");
    await app.get(".search-filter-toggle").trigger("click"); await flushPromises();
    await searchPill(app, "Upraveno", "week");
    await app.get(".search-filter-toggle").trigger("click"); await flushPromises();
    expect(filters.attributes("aria-hidden")).toBe("true");
    expect(app.get(".search-filter-toggle [data-slot=badge]").text()).toBe("1");
    const chip = app.get('[aria-label="Zrušit filtr poslední změny"]');
    expect(chip.text()).toContain("7 dní");
    await chip.trigger("click");
    expect(app.find(".search-filter-toggle [data-slot=badge]").exists()).toBe(false);
  });
  it("shows a clear button inside the field only while the query is non-empty and it empties the query", async () => {
    const app = await mountApp(requestMock());
    await app.get(".search-launch").trigger("click");
    expect(app.find(".search-clear").exists()).toBe(false);
    const input = app.get(".omnisearch-input input");
    await input.setValue("pristup");
    const clear = app.get(".search-clear");
    expect(clear.attributes("aria-label")).toBe("Vymazat dotaz");
    await clear.trigger("click");
    expect((input.element as HTMLInputElement).value).toBe("");
    expect(app.find(".search-clear").exists()).toBe(false);
    expect(document.activeElement).toBe(input.element);
  });
  it("renders an active filter as a real badge button with an icon, not a text ×", async () => {
    const app = await mountApp(requestMock());
    await app.get(".search-launch").trigger("click");
    await pickSearchSpace(app, space.id);
    const chip = app.get(".search-active-filters [data-slot=badge]");
    expect(chip.element.tagName).toBe("BUTTON");
    expect(chip.find("svg").exists()).toBe(true);
    expect(chip.text()).not.toContain("×");
  });
  it("shows a count badge on the Filtry toggle once a filter is active", async () => {
    const app = await mountApp(requestMock());
    await app.get(".search-launch").trigger("click");
    const toggle = app.get(".search-filter-toggle");
    expect(toggle.attributes("data-variant")).toBe("outline");
    expect(toggle.find("[data-slot=badge]").exists()).toBe(false);
    await pickSearchSpace(app, space.id);
    expect(app.get(".search-filter-toggle [data-slot=badge]").text()).toBe("1");
  });
  it("keeps one Zrušit filtry button, inside the expanded filter panel", async () => {
    const app = await mountApp(requestMock());
    await app.get(".search-launch").trigger("click");
    await pickSearchSpace(app, space.id);
    expect(app.findAll("button").filter(b => b.text() === "Zrušit filtry" && b.isVisible())).toHaveLength(0);
    await app.get(".search-filter-toggle").trigger("click");
    const resetButtons = app.get("#search-filters").findAll("button").filter(b => b.text() === "Zrušit filtry");
    expect(resetButtons).toHaveLength(1);
    expect(resetButtons[0]!.attributes("data-variant")).toBe("ghost");
    await resetButtons[0]!.trigger("click");
    expect(app.get("[data-search-space]").text()).toContain("Všechny");
  });
  it("hides the explanatory sentence and the result count from sighted users but keeps them for assistive tech", async () => {
    vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout"] });
    const fetchMock = requestMock(path => path.startsWith("/api/search?") ? Promise.resolve(response({ items: [{ ...page, spaceName: space.name, snippet: "" }], hasMore: false })) : undefined);
    const app = await mountApp(fetchMock);
    await app.get(".search-launch").trigger("click");
    expect(app.get("#search-hint").classes()).toContain("visually-hidden");
    await app.get(".omnisearch-input input").setValue("pristup");
    await vi.advanceTimersByTimeAsync(180); await flushPromises();
    const status = app.findAll('[role="status"]').find(el => el.text().includes("Zobrazené výsledky"));
    expect(status).toBeTruthy();
    expect(status!.classes()).toContain("visually-hidden");
  });
  it("gives the footer keyboard hints as real kbd elements", async () => {
    const app = await mountApp(requestMock());
    await app.get(".search-launch").trigger("click");
    const footer = app.get(".search-footer");
    expect(footer.findAll("kbd").length).toBeGreaterThanOrEqual(3);
  });
  it("cleans markdown syntax out of a result snippet before it is highlighted", async () => {
    vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout"] });
    const fetchMock = requestMock(path => path.startsWith("/api/search?") ? Promise.resolve(response({ items: [{ ...page, spaceName: space.name, snippet: "… --- Naposledy **tip** ověřeno" }], hasMore: false })) : undefined);
    const app = await mountApp(fetchMock);
    await app.get(".search-launch").trigger("click");
    await app.get(".omnisearch-input input").setValue("tip");
    await vi.advanceTimersByTimeAsync(180); await flushPromises();
    expect(app.get(".search-snippet").text()).toBe("… Naposledy tip ověřeno");
  });
});

describe("one dialog chrome", () => {
  it("gives the search overlay a plain title and close instead of a brand mark", async () => {
    const app = await mountApp(requestMock());
    await app.get(".search-launch").trigger("click");
    const header = app.get(".search-dialog .app-dialog-header");
    expect(header.get("h2").text()).toBe("Hledat ve wiki");
    expect(app.get(".search-dialog").attributes("aria-labelledby")).toBe(header.get("h2").attributes("id"));
    expect(app.get(".search-dialog").attributes("data-size")).toBe("wide");
    expect(header.find(".brand-mark").exists()).toBe(false);
    expect(header.find('button[aria-label="Zavřít"] svg').exists()).toBe(true);
    expect(header.get('[aria-label="Režim hledání"]').text()).toContain('Příkazy');
    expect(app.get('.search-footer').text()).toContain('Esc');
    await header.get('button[aria-label="Zavřít"]').trigger("click");
    expect(app.get(".search-dialog").attributes("open")).toBeUndefined();
  });
  it("opens new page creation as a routed full-page wizard", async () => {
    const app = await mountApp(requestMock());
    await app.get(".sidebar-space-link").trigger("click"); await flushPromises();
    await app.get(".new-page-button").trigger("click"); await flushPromises();
    await settleLazyViews(); expect(app.find(".page-creation-flow").exists()).toBe(true);
    expect(location.hash).toBe("#/spaces/space-1/new-page");
    expect(app.find('.progressive-flow[aria-label="Nová stránka"]').exists()).toBe(true);
    await app.get("[data-flow-cancel]").trigger("click"); await flushPromises();
    expect(app.find(".page-creation-flow").exists()).toBe(false);
    expect(location.hash).toBe("#/spaces/space-1");
  });
  it("opens Kdo má přístup as a routed view of the page and checkpoints a changed draft on the way, without asking", async () => {
    const me = { userId: "user-1", displayName: "Test Admin", username: "admin", role: "Reader", isWikiAdmin: false, canReadPublished: true, canReadDraft: false, canEdit: false,
      lastViewedAt: null, lastEditedAt: null, sources: [{ kind: "space", name: "", role: "Reader", effective: true }] };
    const app = await mountApp(requestMock(path => path.startsWith(`/api/pages/${page.id}/access?`)
      ? Promise.resolve(response({ spaceId: space.id, spaceName: space.name, isPublished: true, canManage: false, myAccess: me, items: [], hasMore: false, pageTitle: page.title }))
      : undefined));
    await app.get(".sidebar-space-link").trigger("click"); await flushPromises();
    await app.get(".tree-page").trigger("click"); await flushPromises();
    const editor = await startEditing(app);
    editor.vm.$emit("change"); await flushPromises();
    await app.get('.document-actions [aria-label="Kdo má přístup"]').trigger("click"); await flushPromises();
    expect(leaveButton("Zůstat")).toBeUndefined();
    // The view is its own lazy chunk; its first import is slow under the test transform.
    await settleLazyViews(); expect(app.find("[data-page-sharing] [data-own-access]").exists()).toBe(true);
    expect(location.hash).toBe("#/pages/page-1/access");
    expect(app.findComponent(DocumentEditor).exists()).toBe(false);
    expect(app.find("dialog[open]").exists()).toBe(false);
    expect(app.get('.breadcrumb-title[aria-current="page"]').text()).toBe("Přístup");
    expect(app.get("[data-breadcrumb-page]").attributes("href")).toContain("#/pages/page-1");
    history.pushState(null, "", "#/pages/page-1");
    window.dispatchEvent(new PopStateEvent("popstate")); await flushPromises();
    expect(app.find("[data-page-sharing]").exists()).toBe(false);
    await settleLazyViews(); expect(app.findComponent(DocumentEditor).exists()).toBe(true);
    expect(app.getComponent(DocumentEditor).props("readonly")).toBe(false);
  });
});

describe("settings routes", () => {
  const settingsApi = (path: string) => {
    if (path === '/api/vaults' || path === '/api/vault-locations') return Promise.resolve(response([]));
    if (path.startsWith('/api/admin/access-audit')) return Promise.resolve(response({ items: [], hasMore: false, nextBeforeId: null, coverageStartedAt: '2026-09-01T00:00:00Z' }));
    return undefined;
  };
  it('redirects the old #/settings link to the overview of Správa wiki', async () => {
    const app = await mountApp(requestMock(settingsApi));
    history.pushState(null, '', '#/settings');
    window.dispatchEvent(new PopStateEvent('popstate')); await flushPromises();
    expect(location.hash).toBe('#/settings/security/overview');
    await settleLazyViews(); expect(document.querySelector('[data-settings-host][data-scope="global"]')).not.toBeNull();
    expect(app.get('.account .settings-nav').attributes('aria-current')).toBe('page');
    expect(app.get('.breadcrumb-title[aria-current="page"]').text()).toBe('Správa wiki');
    expect(app.get('.settings-sidebar-title').text()).toBe('Správa wiki');
  });
  it('opens Správa wiki from the sidebar at its overview', async () => {
    const app = await mountApp(requestMock(settingsApi));
    await app.get('.account .settings-nav').trigger('click'); await flushPromises();
    expect(location.hash).toBe('#/settings/security/overview');
    await settleLazyViews(); expect(document.querySelector('[data-settings-host]')).not.toBeNull();
  });
  it('keeps a reader out of global settings and of the settings of a space they do not administer', async () => {
    const app = await mountApp(requestMock(path => {
      if (path === '/api/auth/me') return Promise.resolve(response({ id: 'reader', username: 'reader', displayName: 'Reader', isWikiAdmin: false, appearance: { layout: 'connected', accent: 'blue', mode: 'system' } }));
      if (path === '/api/spaces') return Promise.resolve(response([{ ...space, role: 'Reader' }]));
      return settingsApi(path);
    }));
    for (const hash of ['#/settings/people/accounts', `#/spaces/${space.id}/settings/vaults`]) {
      history.pushState(null, '', hash);
      window.dispatchEvent(new PopStateEvent('popstate')); await flushPromises();
      expect(location.hash, hash).toBe('#/');
      expect(app.find('[data-settings-host]').exists()).toBe(false);
    }
  });
});

describe("page templates", () => {
  it('opens space settings as a page and its Šablony stránek section', async () => {
    const app = await mountApp(requestMock(path => {
      if (path === '/api/vaults' || path === '/api/vault-locations') return Promise.resolve(response([]));
      if (path === `/api/spaces/${space.id}/settings`) return Promise.resolve(response({ ...space, description: '', version: '1' }));
      return undefined;
    }));
    await app.get('.sidebar-space-link').trigger('click'); await flushPromises();
    await app.get(`[aria-label="Akce prostoru: ${space.name}"]`).trigger('click'); await settleMenu();
    menuItems().find(item => item.textContent?.includes('Nastavení prostoru'))!.click(); await flushPromises();
    expect(location.hash).toBe('#/spaces/space-1/settings/general');
    const templatesLink = () => document.querySelector<HTMLAnchorElement>('#atlas-settings-rail a[href="#/spaces/space-1/settings/templates"]');
    await settleLazyViews(); expect(templatesLink()).not.toBeNull();
    expect(app.get('.breadcrumb-title[aria-current="page"]').text()).toBe(`${space.name} · Nastavení prostoru`);
    templatesLink()!.click(); await flushPromises();
    await settleLazyViews(); expect(document.querySelector('[data-settings-host] .page-templates-page')).not.toBeNull();
    expect(location.hash).toBe('#/spaces/space-1/settings/templates');
  });
  it('redirects the former template list address to Nastavení prostoru', async () => {
    await mountApp(requestMock(path => {
      if (path === '/api/vaults' || path === '/api/vault-locations') return Promise.resolve(response([]));
      return undefined;
    }));
    history.pushState(null, '', '#/spaces/space-1/page-templates');
    window.dispatchEvent(new PopStateEvent('popstate')); await flushPromises();
    expect(location.hash).toBe('#/spaces/space-1/settings/templates');
    await settleLazyViews(); expect(document.querySelector('[data-settings-host] .page-templates-page')).not.toBeNull();
  });
  it('shows a page the account cannot open as unavailable at its own address, not the previous page', async () => {
    const app = await mountApp(requestMock(path => path === '/api/pages/hidden' ? Promise.resolve(response({ title: 'Stránka nebyla nalezena.', status: 404 }, 404)) : undefined));
    await app.get('.sidebar-space-link').trigger('click'); await flushPromises();
    await app.get('.tree-page').trigger('click'); await flushPromises();
    history.pushState(null, '', '#/pages/hidden');
    window.dispatchEvent(new PopStateEvent('popstate')); await flushPromises();
    expect(location.hash).toBe('#/pages/hidden');
    expect(app.find('[data-page-unavailable]').text()).toContain('Stránka není dostupná');
    expect(app.find('.document-body').exists()).toBe(false);
  });

  it('restores the new-page route through browser navigation', async () => {
    const app = await mountApp(requestMock());
    history.pushState(null, '', '#/spaces/space-1/new-page');
    window.dispatchEvent(new PopStateEvent('popstate')); await flushPromises();
    await settleLazyViews(); expect(app.find('.page-creation-flow').exists()).toBe(true);
    expect(app.get('.breadcrumb-title[aria-current="page"]').text()).toBe('Nová stránka');
  });

  it('does not expose template administration to a writer', async () => {
    const app = await mountApp(requestMock(path => {
      if (path === '/api/spaces') return Promise.resolve(response([{ ...space, role: 'Writer' }]));
      if (path === '/api/auth/me') return Promise.resolve(response({ id: 'writer', displayName: 'Writer', isWikiAdmin: false, appearance: { layout: 'connected', accent: 'blue', mode: 'system' } }));
    }));
    history.pushState(null, '', '#/spaces/space-1/page-templates');
    window.dispatchEvent(new PopStateEvent('popstate')); await flushPromises();
    expect(app.find('.page-templates-page').exists()).toBe(false);
    await app.get('.sidebar-space-link').trigger('click'); await flushPromises();
    await app.get('.tree-page').trigger('click'); await flushPromises();
    await app.get('[aria-label="Další akce stránky"]').trigger('click'); await settleMenu();
    expect(menuItems().some(item => item.textContent?.includes('Používat jako šablonu'))).toBe(false);
  });

  it.each([
    { template: 'contacts', heading: 'Kontaktní osoby', detail: 'Zastupování' },
    { template: 'deployment', heading: 'Postup nasazení', detail: 'Rollback' },
    { template: 'runbook', heading: 'Kdy postup použít', detail: 'Ověření výsledku' },
  ])('creates an editable $template draft from the wizard', async ({ template, heading, detail }) => {
    let created = { ...page, id: 'created', isPublished: false, publishedVersion: null as number | null };
    const fetchMock = requestMock((path, init) => {
      if (path === '/api/pages' && init?.method === 'POST') {
        created = { ...created, ...JSON.parse(String(init.body)) };
        return Promise.resolve(response(created));
      }
      if (path === '/api/pages/created') return Promise.resolve(response(created));
    });
    const app = await mountApp(fetchMock);
    await app.get('.sidebar-space-link').trigger('click'); await flushPromises();
    await app.get('.tree-page').trigger('click'); await flushPromises();
    await app.get('.new-page-button').trigger('click'); await flushPromises();
    await settleLazyViews(); expect(app.find(".page-creation-flow").exists()).toBe(true);
    await app.get(`[data-value="${template}"]`).trigger('click'); await flushPromises();
    await settleLazyViews(); expect(app.find('.page-creation-flow input[required]').exists()).toBe(true);
    await app.get('.page-creation-flow input[required]').setValue('Dokumentace týmu');
    await app.get('[data-flow-next]').trigger('click');
    await settleLazyViews(); expect(created.title).toBe('Dokumentace týmu');
    expect(created.spaceId).toBe(space.id);
    expect(created.parentId).toBe(page.id);
    expect(created.markdown).toContain(`## ${heading}`);
    expect(created.markdown).toContain(detail);
    expect(created.contentJson).toContain(heading);
    expect(created.contentJson).toContain(detail);
    expect(app.getComponent(DocumentEditor).props('value')).toEqual(JSON.parse(created.contentJson));
    expect(app.getComponent(DocumentEditor).props('readonly')).toBe(false);
    expect(fetchMock.mock.calls.some(([path]) => String(path).endsWith('/publish'))).toBe(false);
  });

  it('preserves a wizard draft when navigation is declined', async () => {
    const app = await mountApp(requestMock());
    await app.get('.sidebar-space-link').trigger('click'); await flushPromises();
    await app.get('.new-page-button').trigger('click'); await flushPromises();
    await settleLazyViews(); expect(app.find(".page-creation-flow").exists()).toBe(true);
    await app.get('[data-flow-next]').trigger('click'); await flushPromises();
    await app.get('.page-creation-flow input[required]').setValue('Vlastní název');
    const confirm = vi.spyOn(window, 'confirm');
    await app.get('.breadcrumb-root').trigger('click'); await flushPromises();
    // The shared UnsavedChangesDialog asks, never the browser's confirm.
    expect(confirm).not.toHaveBeenCalled();
    const stay = [...document.querySelectorAll<HTMLButtonElement>('dialog[open] button')].find(button => button.textContent?.includes('Zůstat'));
    expect(stay).toBeDefined();
    stay!.click(); await flushPromises();
    expect(location.hash).toBe('#/spaces/space-1/new-page');
    expect((app.get('.page-creation-flow input[required]').element as HTMLInputElement).value).toBe('Vlastní název');
    confirm.mockRestore();
  });

  it('marks the existing page as a template while keeping its normal editor', async () => {
    const fetchMock = requestMock((path, init) => path === `/api/pages/${page.id}/template` && init?.method === 'PUT' ? Promise.resolve(response({ ...page, isTemplate: true })) : undefined);
    const app = await mountApp(fetchMock);
    await app.get('.sidebar-space-link').trigger('click'); await flushPromises();
    await app.get('.tree-page').trigger('click'); await flushPromises();
    await app.get('[aria-label="Další akce stránky"]').trigger('click'); await settleMenu();
    const action = menuItems().find(item => item.textContent?.includes('Používat jako šablonu'));
    expect(action).toBeDefined();
    action!.click(); await flushPromises();
    expect(fetchMock.mock.calls.some(([path, init]) => path === `/api/pages/${page.id}/template` && init?.method === 'PUT' && JSON.parse(String(init.body)).isTemplate === true)).toBe(true);
    expect(app.findComponent(DocumentEditor).exists()).toBe(true);
    expect(app.text()).toContain('Šablona prostoru');
    expect(fetchMock.mock.calls.some(([path, init]) => String(path).includes('/page-templates') && init?.method === 'POST')).toBe(false);
  });
});
describe("shell navigation unification", () => {
  it("identifies the current destination and names each space's page tree", async () => {
    const app = await mountApp(requestMock());
    const library = app.findAll('aside .nav-link').find(item => item.text() === 'Knihovna')!;
    // The wiki root is Domů: the root crumb is current, Knihovna is a place of its own.
    expect(library.attributes('aria-current')).toBeUndefined();
    await library.trigger('click'); await flushPromises();
    expect(library.attributes('aria-current')).toBe('page');
    await app.get('.sidebar-space-link').trigger('click'); await flushPromises();
    expect(library.attributes('aria-current')).toBeUndefined();
    expect(app.get('.sidebar-space-link').attributes('aria-current')).toBe('page');
    expect(app.get('[role="tree"]').attributes('aria-label')).toBe('Stránky prostoru Engineering');
    await app.get('.tree-page').trigger('click'); await flushPromises();
    expect(app.get('.sidebar-space-link').attributes('aria-current')).toBeUndefined();
    expect(app.get('.tree-page').attributes('aria-current')).toBe('page');
  });
  it("gives the profile the rail to itself when it is opened from Správa wiki or the library, and returns where it was opened", async () => {
    const app = await mountApp(requestMock());
    // Settings replace the rail (refresh 2.5), so Knihovna is looked up again after leaving them.
    const library = () => app.findAll('aside .nav-link').find(item => item.text() === 'Knihovna')!;
    await app.get('.sidebar-space-link').trigger('click'); await flushPromises();
    await app.get('.account .settings-nav').trigger('click'); await flushPromises();
    expect(app.get('.account .settings-nav').attributes('aria-current')).toBe('page');
    await app.get('.profile-launch').trigger('click'); await settleLazyViews();
    // The profile is the account itself: its rail ends with Odhlásit se (board 12) and nothing switches to another place.
    expect(app.find('.account').exists()).toBe(false);
    expect(app.find('.settings-scope-tabs').exists()).toBe(false);
    expect(app.get('.settings-sidebar-title').text()).toBe('Profil');
    // It no longer lies over the settings it was opened from: only its own shell teleports sections into the rail.
    expect(app.findComponent({ name: 'SettingsHost' }).exists()).toBe(false);
    expect(app.findAll('#atlas-settings-rail .settings-rail-outside')).toHaveLength(1);
    // The way back says where it leads and opens Správa wiki again at the address the profile was opened from.
    expect(app.get('.settings-back').text()).toBe('Zpět do správy wiki');
    await app.get('.settings-back').trigger('click'); await settleLazyViews();
    expect(location.hash).toBe('#/settings/security/overview');
    expect(app.find('#profile-heading').exists()).toBe(false);
    expect(app.get('.account .settings-nav').attributes('aria-current')).toBe('page');
    // Správa wiki still returns to the space the gear was pressed in: the way through the profile did not replace that.
    await app.get('.settings-back').trigger('click'); await flushPromises();
    expect(location.hash).toBe('#/spaces/space-1');
    await library().trigger('click'); await flushPromises();
    await app.get('.profile-launch').trigger('click'); await flushPromises();
    // The profile takes the rail too; the way back returns to Knihovna, which is current again.
    expect(app.find('aside .nav-link.sidebar-library').exists()).toBe(false);
  });
  it("sets the wiki navigation aside while settings hold the rail and puts the same one back", async () => {
    const app = await mountApp(requestMock());
    await app.get('.sidebar-space-link').trigger('click'); await flushPromises();
    const tree = app.get('aside [role="tree"]').element, library = app.get('aside .nav-link.sidebar-library').element;
    const order = () => [...app.get('aside').element.children].map(child => child.className);
    const before = order();
    await app.get('.account .settings-nav').trigger('click'); await flushPromises();
    // Out of the document, not merely hidden: the rail has the sidebar to itself.
    expect(app.find('aside [role="tree"]').exists()).toBe(false);
    expect(app.find('aside .nav-link.sidebar-library').exists()).toBe(false);
    expect(app.find('#atlas-settings-rail').exists()).toBe(true);
    await app.get('.settings-back').trigger('click'); await flushPromises();
    // Správa wiki returns to the space the gear was pressed in.
    expect(location.hash).toBe('#/spaces/space-1');
    // The same elements return (rebuilding the pinned spaces' trees was the bulk of the way back), in their place.
    expect(app.get('aside [role="tree"]').element).toBe(tree);
    expect(app.get('aside .nav-link.sidebar-library').element).toBe(library);
    expect(app.find('#atlas-settings-rail').exists()).toBe(false);
    expect(order()).toEqual(before);
  });
  it("holds the entry screen back during the first session check only, not when the connection is verified again", async () => {
    const held: Array<(value: Response) => void> = [];
    const app = await mountApp(requestMock((path) => {
      if (path === "/api/auth/me") return Promise.resolve(response({ title: "Nepřihlášeno." }, 401));
      if (path === "/api/setup/status") return new Promise<Response>(resolve => { held.push(resolve); });
    }));
    // In flight: the screen is rendered, and its style keeps it unpainted until the defer has passed.
    expect(app.get(".entry-shell").attributes("data-connecting")).toBe("true");
    expect(app.text()).toContain("Připojujeme se k vaší wiki");
    held.shift()!(response({ databaseReady: false, requiresSetup: false, bootstrapTokenRequired: true }));
    await flushPromises();
    expect(app.get(".entry-shell").attributes("data-connecting")).toBeUndefined();
    await app.findAll(".entry-card button").find(button => button.text() === "Ověřit znovu")!.trigger("click");
    expect(app.text()).toContain("Připojujeme se k vaší wiki");
    expect(app.get(".entry-shell").attributes("data-connecting")).toBeUndefined();
    held.shift()!(response({ databaseReady: false, requiresSetup: false, bootstrapTokenRequired: true }));
    await flushPromises();
  });
  it("shows the configured wiki name as the sidebar brand and the document title, falling back when unset", async () => {
    const named = await mountApp(requestMock((path) => {
      if (path === "/api/setup/status") return Promise.resolve(response({ databaseReady: true, requiresSetup: false, bootstrapTokenRequired: true, workspaceName: "Acme Wiki" }));
    }));
    expect(named.get(".brand .brand-name").text()).toBe("Acme Wiki");
    expect(document.title).toBe("Acme Wiki");
    named.unmount(); wrapper = undefined;
    const fallback = await mountApp(requestMock((path) => {
      if (path === "/api/setup/status") return Promise.resolve(response({ databaseReady: true, requiresSetup: false, bootstrapTokenRequired: true, workspaceName: "" }));
    }));
    expect(fallback.get(".brand .brand-name").text()).toBe("Atlas");
    expect(document.title).toBe("Atlas");
  });
  it("shows the wiki name chosen in the setup wizard right after setup, without a reload", async () => {
    let installed = false;
    const app = await mountApp(requestMock((path, init) => {
      if (path === "/api/setup/status") return Promise.resolve(response({ databaseReady: true, requiresSetup: !installed, bootstrapTokenRequired: true, workspaceName: installed ? "Acme Znalosti" : null }));
      if (path === "/api/auth/me" && !installed) return Promise.resolve(response({ title: "Nepřihlášeno." }, 401));
      if (path === "/api/setup/complete" && init?.method === "POST") {
        installed = true;
        return Promise.resolve(response({ id: "user-1", username: "admin", displayName: "Test Admin", isWikiAdmin: true, appearance: { layout: "connected", accent: "ocean", mode: "system" } }));
      }
    }));
    await settleLazyViews();
    await app.get('input[autocomplete="organization"]').setValue("Acme Znalosti");
    await app.get("form").trigger("submit"); await settleLazyViews();
    await app.get('input[autocomplete="name"]').setValue("Test Admin");
    await app.get('input[autocomplete="username"]').setValue("admin");
    await app.get('input[autocomplete="new-password"]').setValue("test-password");
    await app.get('input[autocomplete="off"]').setValue("bootstrap-token");
    await app.get("form").trigger("submit"); await settleLazyViews();
    expect(app.get(".brand .brand-name").text()).toBe("Acme Znalosti");
    expect(document.title).toBe("Acme Znalosti");
  });
  it("makes the breadcrumb root a link to Knihovna at #/, shown on every route, and never renders a trailing separator", async () => {
    const app = await mountApp(requestMock());
    const root = app.get("a.breadcrumb-root");
    expect(new URL(root.attributes("href")!, location.origin).hash).toBe("#/");
    await app.get(".sidebar-space-link").trigger("click"); await flushPromises();
    expect(app.find("a.breadcrumb-root").exists()).toBe(true);
    await app.get(".tree-page").trigger("click"); await flushPromises();
    expect(app.find("a.breadcrumb-root").exists()).toBe(true);
    const crumbs = app.get(".breadcrumbs").findAll(".breadcrumb-separator, .breadcrumb-space, .breadcrumb-title");
    expect(crumbs.at(-1)!.classes()).not.toContain("breadcrumb-separator");
    await app.get("a.breadcrumb-root").trigger("click"); await flushPromises();
    expect(location.hash).toBe("#/");
  });
  it("presents the breadcrumb as a navigation landmark with list semantics", async () => {
    const app = await mountApp(requestMock());
    const nav = app.get("nav.breadcrumb-trail");
    expect(nav.attributes("aria-label")).toBeTruthy();
    expect(nav.get("ol").findAll("li").length).toBeGreaterThan(0);
  });
  it("marks the terminal crumb with aria-current, not only a class", async () => {
    const app = await mountApp(requestMock());
    // The wiki root is Domů: the root crumb is the current one; Knihovna is a crumb of its own.
    expect(app.get("a.breadcrumb-root").attributes("aria-current")).toBe("page");
    await app.findAll("aside .nav-link").find(item => item.text() === "Knihovna")!.trigger("click"); await flushPromises();
    expect(app.get("a.breadcrumb-root").attributes("aria-current")).toBeUndefined();
    expect(app.get('.breadcrumb-title[aria-current="page"]').text()).toBe("Knihovna");
    await app.get(".sidebar-space-link").trigger("click"); await flushPromises();
    expect(app.get("a.breadcrumb-root").attributes("aria-current")).toBeUndefined();
    const space = app.findAll("a.breadcrumb-space").at(-1)!;
    expect(space.attributes("aria-current")).toBe("page");
    await app.get(".tree-page").trigger("click"); await flushPromises();
    const title = app.get(".breadcrumb-title");
    expect(title.attributes("aria-current")).toBe("page");
    expect(app.findAll('.breadcrumbs [aria-current="page"]')).toHaveLength(1);
  });
  it("hides the crumb separators from assistive technology", async () => {
    const app = await mountApp(requestMock());
    await app.get(".sidebar-space-link").trigger("click"); await flushPromises();
    const separators = app.findAll(".breadcrumb-separator");
    expect(separators.length).toBeGreaterThan(0);
    for (const separator of separators)
      expect(separator.attributes("aria-hidden")).toBe("true");
  });
  it("lands on Domů at the wiki root and opens Knihovna with all spaces as its first tab", async () => {
    const fetchMock = requestMock();
    const app = await mountApp(fetchMock);
    const library = () => app.findAll("aside .nav-link").find((item) => item.text() === "Knihovna")!;
    // The root is Domů, the home dashboard (4.0), reached by the logo and the root crumb; the sidebar stays as it was.
    await settleLazyViews(); expect(app.find("[data-home-dashboard]").exists()).toBe(true);
    expect(fetchMock.mock.calls.some(([path]) => String(path).startsWith("/api/me/changes?filter=all"))).toBe(true);
    expect(app.findAll("aside .nav-link").filter((item) => item.text() === "Knihovna")).toHaveLength(1);
    expect(app.findAll("aside .nav-link").map(item => item.text())).not.toContain("Domů");
    expect(app.find("aside").text()).not.toContain("Všechny prostory");
    await library().trigger("click"); await flushPromises();
    // The parts are links with the board's names; Prostory carries the number of spaces (refresh 2.5).
    expect(app.findAll(".library-tabs a").map(tab => tab.attributes("data-library-part"))).toEqual(["spaces", "personal", "shared", "recent", "favorites", "drafts", "updates", "contacts", "notifications"]);
    expect(app.findAll(".library-tabs a > span:first-child").map(tab => tab.text())).toEqual(["Prostory", "Můj prostor", "Sdíleno se mnou", "Nedávné", "Oblíbené", "Moje koncepty", "Poslední publikace", "Kontakty", "Oznámení"]);
    expect(app.get('.library-tabs [aria-current="page"]').attributes("data-library-part")).toBe("spaces");
    await settleLazyViews(); expect(app.find(".space-directory").exists()).toBe(true);
    expect(library().attributes("aria-current")).toBe("page");
    // Nothing opened yet: the empty recent list leads back to the spaces.
    await app.get('.library-tabs [data-library-part="recent"]').trigger("click");
    await settleLazyViews(); expect(app.find(".library-start").exists()).toBe(true);
    // The personal lists have their own sidebar entries since refresh 2.5: Nedávné is current, not Knihovna.
    expect(library().attributes("aria-current")).toBeUndefined();
    expect(app.get('aside [data-library-tab="recent"]').attributes("aria-current")).toBe("page");
    await app.findAll(".library-start button").find(item => item.text().includes("Procházet prostory"))!.trigger("click");
    await flushPromises();
    expect(location.hash).toBe("#/library/spaces");
    // Knihovna in the sidebar always opens the spaces.
    await app.get('.library-tabs [data-library-part="favorites"]').trigger("click");
    await library().trigger("click");
    await flushPromises();
    expect(location.hash).toBe("#/library/spaces");
  });
  it("offers Nový prostor on Prostory only", async () => {
    const app = await mountApp(requestMock());
    await app.findAll("aside .nav-link").find(item => item.text() === "Knihovna")!.trigger("click");
    await settleLazyViews();
    expect(app.find("[data-space-create]").exists()).toBe(true);
    await app.get('.library-tabs [data-library-part="recent"]').trigger("click");
    await settleLazyViews();
    expect(app.find("[data-space-create]").exists()).toBe(false);
  });
  it("returns to sign-in with the session-ended message when any request is refused as signed out", async () => {
    let ended = false;
    const app = await mountApp(requestMock((path) => {
      if (ended && (path === "/api/auth/me" || path.startsWith("/api/me/library/"))) return Promise.resolve(response({ title: "Unauthorized" }, 401));
      if (path === "/api/auth/methods") return Promise.resolve(response({ builtinEnabled: true, providers: [] }));
    }));
    await app.findAll("aside .nav-link").find(item => item.text() === "Knihovna")!.trigger("click"); await flushPromises();
    await app.get('.library-tabs [data-library-part="recent"]').trigger("click");
    await settleLazyViews();
    ended = true;
    await app.get('.library-tabs [data-library-part="favorites"]').trigger("click");
    await settleLazyViews();
    // The favorites list asks the server, which no longer knows the session.
    await vi.waitFor(() => expect(app.text()).toContain("Vaše přihlášení skončilo"));
    expect(app.find(".library-tabs").exists()).toBe(false);
  });
  it("returns to sign-in when the live connection is refused as signed out (UX1-1)", async () => {
    let ended = false;
    const app = await mountApp(requestMock((path) => {
      if (ended && path === "/api/auth/me") return Promise.resolve(response({ title: "Unauthorized" }, 401));
      if (path === "/api/auth/methods") return Promise.resolve(response({ builtinEnabled: true, providers: [] }));
    }));
    ended = true;
    // What liveConnection.ts reports when negotiate answers 401: the board's hub, never an api() request.
    await checkSessionAfterLiveFailure(Object.assign(new Error("Unauthorized"), { statusCode: 401 }));
    await vi.waitFor(() => expect(app.text()).toContain("Vaše přihlášení skončilo"));
    expect(app.find(".library-tabs").exists()).toBe(false);
  });
  it("keeps a single search launcher visible on the home page", async () => {
    const app = await mountApp(requestMock());
    expect(app.findAll(".search-launch, .home-search")).toHaveLength(1);
  });
  it("opens Nový prostor as a full-screen view with its own address", async () => {
    const app = await mountApp(requestMock());
    await openNewSpaceForm(app);
    expect(location.hash).toBe("#/new-space");
    expect(app.find("dialog[open]").exists()).toBe(false);
    await settleLazyViews(); expect(app.find(".workspace-content .space-creation").exists()).toBe(true);
    const view = app.get(".workspace-content .space-creation");
    expect(view.get(".settings-page-heading h2").text()).toBe("Nový prostor");
    expect(view.find('input[name="name"]').exists()).toBe(true);
    expect(app.get('.breadcrumb-title[aria-current="page"]').text()).toBe("Nový prostor");
  });
  it("asks before discarding a named Nový prostor and returns focus to its trigger", async () => {
    const app = await mountApp(requestMock());
    await openNewSpaceForm(app);
    await settleLazyViews(); expect(app.find('.space-creation input[name="name"]').exists()).toBe(true);
    await app.get('.space-creation input[name="name"]').setValue("Rozepsaný prostor"); await flushPromises();
    await app.get(".space-creation [data-space-cancel]").trigger("click"); await flushPromises();
    expect(location.hash).toBe("#/new-space");
    const discard = [...document.querySelectorAll<HTMLButtonElement>("dialog[open] button")].find((button) => button.textContent?.trim() === "Zahodit");
    discard!.click(); await flushPromises();
    expect(location.hash).toBe("#/");
    expect(app.find(".space-creation").exists()).toBe(false);
    expect(document.activeElement).toBe(app.get('.sidebar-spaces-heading [aria-label="Nový prostor"]').element);
  });
  it("moves the space export action into the space menu, disabled once the space has no pages", async () => {
    const app = await mountApp(requestMock((path) => path === `/api/spaces/${space.id}/tree` ? Promise.resolve(response({ spaceId: space.id, version: 1, items: [] })) : undefined));
    await app.get(".sidebar-space-link").trigger("click"); await flushPromises();
    expect(app.find(".export-space").exists()).toBe(false);
    const spaceTrigger = app.get('[aria-label^="Akce prostoru"][aria-expanded]');
    await spaceTrigger.trigger("click"); await settleMenu();
    const exportButton = menuItems().find((item) => item.textContent!.includes("Exportovat prostor"))!;
    expect(exportButton.hasAttribute("disabled")).toBe(true);
  });
  it("moves the space actions ellipsis onto the breadcrumb as a labelled menu trigger next to the space link", async () => {
    const app = await mountApp(requestMock());
    await app.get(".sidebar-space-link").trigger("click"); await flushPromises();
    expect(app.find(".bar-actions .action-disclosure").exists()).toBe(false);
    const trigger = app.get('[aria-label^="Akce prostoru"][aria-expanded]');
    expect(trigger.find('svg[data-icon="chevron-down"]').exists()).toBe(true);
  });
  it("keeps the breadcrumb space crumb a real link, with a separate menu trigger named for the space (finding 7, WCAG 2.5.3)", async () => {
    const app = await mountApp(requestMock());
    await app.get(".sidebar-space-link").trigger("click"); await flushPromises();
    const crumb = app.get(`.breadcrumbs a.breadcrumb-space[href*="#/spaces/${space.id}"]`);
    expect(crumb.text()).toContain(space.name);
    const trigger = app.get('.breadcrumbs [aria-haspopup="menu"]');
    expect(trigger.attributes("aria-label")).toBe(`Akce prostoru: ${space.name}`);
    expect(trigger.attributes("title")).toBe(space.name);
    expect(trigger.text()).not.toContain(space.name);
  });
  it("finds a nested location and shows its full path when creating a page", async () => {
    const parent = { ...page, id: "parent", title: "P".repeat(80) };
    const child = { ...page, id: "child", parentId: "parent", title: "Child page" };
    const app = await mountApp(requestMock((path) => path === `/api/spaces/${space.id}/tree` ? Promise.resolve(response({ spaceId: space.id, version: 1, items: [{ ...parent, canEdit: true }, { ...child, canEdit: true }] })) : undefined));
    await app.get(".sidebar-space-link").trigger("click"); await flushPromises();
    await app.get(".new-page-button").trigger("click"); await flushPromises();
    await settleLazyViews(); expect(app.find('.page-creation-flow').exists()).toBe(true);
    await app.get('[data-flow-next]').trigger('click'); await flushPromises();
    await app.get('.page-creation-flow input[type=search], .page-creation-flow .location-search input').setValue('Child');
    const target = app.get('.page-creation-flow [data-page-id="child"]');
    expect(target.text()).toContain(parent.title + ' / Child page');
    await target.trigger('click');
    expect(target.attributes('aria-selected')).toBe('true');
    expect(app.get('.location-selection').text()).toContain(parent.title + ' / Child page');
  });
  it("keeps the page actions in the top bar and shows the last change under the title (user feedback 2026-09-08)", async () => {
    // The page answer carries its current revision's start; no history request is needed for the line.
    const app = await mountApp(requestMock((path) => path === `/api/pages/${page.id}`
      ? Promise.resolve(response({ ...page, activeFrom: "2026-09-07T21:41:00Z", canEdit: true }))
      : undefined));
    await app.get(".sidebar-space-link").trigger("click"); await flushPromises();
    await app.get(".tree-page").trigger("click"); await flushPromises();
    expect(app.find(".document-header .document-actions").exists()).toBe(false);
    const bar = app.get("header .bar-actions .document-actions");
    // The lock is the writer's switch now; an unlocked page offers no Upravit (a locked one does, beside the lock).
    expect(bar.find("[data-edit-lock]").exists()).toBe(true);
    expect(bar.findAll("button").some((button) => button.text() === "Upravit")).toBe(false);
    const time = app.get(".document-meta .document-updated time");
    expect(time.attributes("datetime")).toBe("2026-09-07T21:41:00Z");
    expect(time.text()).toContain("Upraveno");
  });
  it("lists the editor's plugin actions in the page menu while editing instead of a separate insert row", async () => {
    const app = await mountApp(requestMock());
    await app.get(".sidebar-space-link").trigger("click"); await flushPromises();
    await app.get(".tree-page").trigger("click"); await flushPromises();
    await startEditing(app);
    expect(app.find(".md-plugin-toolbar").exists()).toBe(false);
    // The collaboration join never settles in these shell tests, so the editor stays
    // read-only here; DocumentEditor.test.ts covers the real emission. Drive the contract.
    await settleLazyViews(); expect(app.findComponent(DocumentEditor).exists()).toBe(true);
    const pluginActions = [
      { id: "attachments.insert", label: "Příloha", icon: "paperclip", enabled: true, run: () => true },
      { id: "editor-tools.search", label: "Hledat a nahradit", icon: "search", enabled: false, run: () => true },
    ];
    await app.get('[aria-label="Další akce stránky"]').trigger("click"); await settleMenu();
    // The editor's `:key` carries the collaboration lease generation, so it can
    // remount between the emission and the assertion -- taking the reported
    // actions with it. Re-emit on whichever instance is currently mounted until
    // the menu lists them. The contract under test is "what the editor reports
    // appears in the page menu", not "one emission on one instance survives".
    await vi.waitFor(() => {
      app.findComponent(DocumentEditor).vm.$emit("pluginActions", pluginActions);
      expect(menuItems().map((button) => button.textContent))
        .toEqual(expect.arrayContaining(["Příloha", "Hledat a nahradit"]));
    }, { timeout: 5000 });
    const items = menuItems();
    const attach = items.find((button) => button.textContent === "Příloha")!;
    expect(attach.querySelector('svg[data-icon="paperclip"]')).not.toBeNull();
    expect(items.find((button) => button.textContent === "Hledat a nahradit")!.hasAttribute("disabled")).toBe(true);
  });
});

describe('grouped ocean search', () => {
  it('groups pages before headings and follows the visible order with the keyboard', async () => {
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] });
    const fetchMock = requestMock(path => path.startsWith('/api/search?') ? Promise.resolve(response({ items: [
      { ...page, spaceName: space.name, snippet: 'Connect securely', headingId: 'vpn', headingTitle: 'VPN access' },
      { ...page, id: 'page-2', title: 'VPN guide', spaceName: space.name, snippet: 'Overview' },
    ], spaces: [space], hasMore: false })) : undefined);
    const app = await mountApp(fetchMock);
    await app.get('.search-launch').trigger('click');
    const input = app.get('.omnisearch-input input');
    await input.setValue('VPN'); await vi.advanceTimersByTimeAsync(180); await flushPromises();
    const groups = app.findAll('#search-results [role=group]');
    expect(groups.map(group => group.attributes('aria-label'))).toEqual(['Stránky', 'Nadpisy', 'Prostory']);
    expect(app.findAll('[role=status]').some(status => status.text() === 'Zobrazené výsledky: 3')).toBe(true);
    expect(app.get('#search-result-0').text()).toContain('VPN guide');
    expect(input.attributes('aria-activedescendant')).toBe('search-result-0');
    // The first row is preselected, so one ↓ goes on to the second (F13).
    await input.trigger('keydown', { key: 'ArrowDown' });
    expect(input.attributes('aria-activedescendant')).toBe('search-result-1');
    expect(app.get('#search-result-1').attributes('href')).toContain('heading=vpn');
    await input.trigger('keydown', { key: 'Enter' }); await flushPromises();
    expect(location.hash).toContain('heading=vpn');
    expect(app.get('.search-dialog').attributes('open')).toBeUndefined();
  });

  it('navigates recent suggestions from the input and opens the selected page', async () => {
    const fetchMock = requestMock(path => path.startsWith('/api/me/library/recent') ? Promise.resolve(response({ items: [
      { ...page, id: 'other-page', title: 'Other page', spaceName: space.name },
      { ...page, spaceName: space.name },
    ], hasMore: false })) : undefined);
    const app = await mountApp(fetchMock);
    await app.get('.search-launch').trigger('click'); await flushPromises();
    const input = app.get('.omnisearch-input input');
    await input.setValue(' '); await flushPromises();
    expect(input.attributes('aria-expanded')).toBe('true');
    await input.trigger('keydown', { key: 'ArrowDown' });
    await input.trigger('keydown', { key: 'ArrowDown' });
    const selected = document.getElementById(input.attributes('aria-activedescendant')!);
    expect(selected?.textContent).toContain(page.title);
    await input.trigger('keydown', { key: 'Enter' }); await flushPromises();
    expect(location.hash).toBe('#/pages/page-1');
    expect(app.get('.search-dialog').attributes('open')).toBeUndefined();
  });

  it('preselects the first recent page, so Enter in the empty field opens it (F13)', async () => {
    const fetchMock = requestMock(path => path.startsWith('/api/me/library/recent') ? Promise.resolve(response({ items: [
      { ...page, spaceName: space.name },
      { ...page, id: 'other-page', title: 'Other page', spaceName: space.name },
    ], hasMore: false })) : undefined);
    const app = await mountApp(fetchMock);
    await app.get('.search-launch').trigger('click'); await flushPromises();
    const input = app.get('.omnisearch-input input');
    expect(input.attributes('aria-activedescendant')).toBe('suggested-result-0');
    await input.trigger('keydown', { key: 'Enter' }); await flushPromises();
    expect(location.hash).toBe('#/pages/page-1');
  });

  it('keeps an ↓ pressed before the answer, and Home and End walk the rows from the field (F13)', async () => {
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] });
    const answers: (() => void)[] = [];
    const fetchMock = requestMock(path => path.startsWith('/api/search?') ? new Promise<Response>(resolve => answers.push(() => resolve(response({ items: [
      { ...page, title: 'VPN first', spaceName: space.name, snippet: 'One' },
      { ...page, id: 'page-2', title: 'VPN second', spaceName: space.name, snippet: 'Two' },
      { ...page, id: 'page-3', title: 'VPN third', spaceName: space.name, snippet: 'Three' },
    ], hasMore: false })))) : undefined);
    const app = await mountApp(fetchMock);
    await app.get('.search-launch').trigger('click');
    const input = app.get('.omnisearch-input input');
    await input.setValue('VPN'); await vi.advanceTimersByTimeAsync(180);
    expect(answers.length).toBeGreaterThan(0);
    await input.trigger('keydown', { key: 'ArrowDown' });
    answers.splice(0).forEach(answer => answer()); await flushPromises(); await vi.advanceTimersByTimeAsync(0); await flushPromises();
    expect(input.attributes('aria-activedescendant')).toBe('search-result-1');
    await input.trigger('keydown', { key: 'End' });
    expect(input.attributes('aria-activedescendant')).toBe('search-result-2');
    await input.trigger('keydown', { key: 'Home' });
    expect(input.attributes('aria-activedescendant')).toBe('search-result-0');
  });
});

describe('embedded widgets in wiki search', () => {
  it('keeps contacts together, shows saved issue state and navigates to the contact section', async () => {
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] });
    const fetchMock = requestMock(path => path.startsWith('/api/search?') ? Promise.resolve(response({ items: [{
      ...page, spaceName: space.name, snippet: 'Ocean support', widgets: [
        { kind: 'contact', title: 'Jana Nová', detail: 'Support · Ocean · jana@example.cz · +420 123 456 789', headingId: 'team' },
        { kind: 'youtrack', title: 'Ocean login', detail: 'OC-8', key: 'OC-8', state: 'Open', url: 'https://tracker.example/issue/OC-8', headingId: null },
      ],
    }], hasMore: false })) : undefined);
    const app = await mountApp(fetchMock);
    await app.get('.search-launch').trigger('click');
    const input = app.get('.omnisearch-input input');
    await input.setValue('Ocean'); await vi.advanceTimersByTimeAsync(180); await flushPromises();
    expect(app.get('[role=group][aria-label="Kontakty"]').text()).toContain('jana@example.cz');
    const issue = app.get('[role=group][aria-label="YouTrack"] a');
    expect(issue.attributes('href')).toBe('https://tracker.example/issue/OC-8');
    expect(issue.attributes('target')).toBe('_blank');
    expect(issue.text()).toContain('OC-8'); expect(issue.text()).toContain('Open');
    // Content leads, YouTrack comes last even when an issue title starts with the query (owner 2026-11-05): Stránky, Kontakty, YouTrack.
    expect(app.get('#search-result-0').text()).toContain(page.title);
    expect(app.get('#search-result-1').text()).toContain('Jana Nová');
    expect(app.get('#search-result-2').text()).toContain('OC-8');
    await input.trigger('keydown', { key: 'ArrowDown' });
    expect(input.attributes('aria-activedescendant')).toBe('search-result-1');
    await input.trigger('keydown', { key: 'Enter' }); await flushPromises();
    expect(location.hash).toContain('heading=team');
  });
});

describe('grouped pagination selection', () => {
  it('keeps the chosen heading selected when a later page inserts an article before it', async () => {
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] });
    const fetchMock = requestMock(path => path.startsWith('/api/search?') ? Promise.resolve(response({ items: path.includes('offset=2') ? [
      { ...page, id: 'page-3', title: 'Later article', spaceName: space.name, snippet: '' },
    ] : [
      { ...page, id: 'page-2', title: 'First article', spaceName: space.name, snippet: '' },
      { ...page, spaceName: space.name, snippet: '', headingId: 'chosen', headingTitle: 'Chosen heading' },
    ], hasMore: !path.includes('offset=2'), nextOffset: path.includes('offset=2') ? null : 2 })) : undefined);
    const app = await mountApp(fetchMock); await app.get('.search-launch').trigger('click');
    const input = app.get('.omnisearch-input input'); await input.setValue('article');
    await vi.advanceTimersByTimeAsync(180); await flushPromises();
    await input.trigger('keydown', { key: 'ArrowDown' });
    await input.trigger('keydown', { key: 'ArrowDown' });
    await app.get('.search-load-more').trigger('click'); await flushPromises();
    const selected = document.getElementById(input.attributes('aria-activedescendant')!);
    expect(selected?.textContent).toContain('Chosen heading');
    expect(selected?.getAttribute('href')).toContain('heading=chosen');
  });
});

describe('space draft approval navigation', () => {
  it('shows the approval tab only to wiki administrators', async () => {
    const regular = requestMock(path => path === '/api/auth/me' ? Promise.resolve(response({ id: 'writer', username: 'writer', displayName: 'Writer', isWikiAdmin: false, appearance: { layout: 'connected', accent: 'blue', mode: 'system' } })) : undefined);
    const app = await mountApp(regular);
    await app.get('.sidebar-space-link').trigger('click');
    await flushPromises();
    expect(app.text()).not.toContain('Ke schválení');
  });

  it('opens the selected space approval queue for wiki administrators', async () => {
    const fetchMock = requestMock(path => path.startsWith('/api/spaces/space-1/drafts?') ? Promise.resolve(response({ items: [], total: 0 })) : undefined);
    const app = await mountApp(fetchMock);
    await app.get('.sidebar-space-link').trigger('click');
    await flushPromises();
    await app.get('button[data-space-approval-tab]').trigger('click');
    await flushPromises();
    // The approval view is a lazy chunk: the editor's renderer stays out of the shell's eager set.
    await settleLazyViews(); expect(app.text()).toContain('Žádné koncepty ke schválení');
    expect(fetchMock.mock.calls.some(([path]) => String(path).startsWith('/api/spaces/space-1/drafts?'))).toBe(true);
  });
});

describe("accepted model edit audit", () => {
  it("marks only the saved revision containing an accepted model edit", async () => {
    const bodies: Record<string, unknown>[] = [];
    const fetchMock = requestMock((path, init) => {
      if (path === `/api/pages/${page.id}` && init?.method === "PUT") {
        bodies.push(JSON.parse(String(init.body)) as Record<string, unknown>);
        return Promise.resolve(response({ ...page, version: bodies.length + 1 }));
      }
      return undefined;
    });
    const app = await mountApp(fetchMock);
    await app.get('.sidebar-space-link').trigger('click'); await flushPromises();
    await app.get('.tree-page').trigger('click'); await flushPromises();
    vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout"] });
    const editor = await startEditing(app);
    editor.vm.$emit('aiAssistedApplied'); editor.vm.$emit('change'); await flushPromises();
    // The checkpoint goes 1.5 s after the last change.
    await vi.advanceTimersByTimeAsync(1500); await flushPromises();
    expect(bodies).toHaveLength(1);
    expect(bodies[0]!.aiAssisted).toBe(true);
    editor.vm.$emit('change'); await flushPromises();
    await vi.advanceTimersByTimeAsync(1500); await flushPromises();
    expect(bodies).toHaveLength(2);
    expect(bodies[1]!.aiAssisted).toBeUndefined();
  });
});

describe("unapplied OpenAPI YAML draft guards", () => {
  async function editingWithYamlDraft(fetchMock = requestMock()) {
    const app = await mountApp(fetchMock);
    await app.get('.sidebar-space-link').trigger('click'); await flushPromises();
    await app.get('.tree-page').trigger('click'); await flushPromises();
    const editor = await startEditing(app);
    editor.vm.$emit('detailDraft', true);
    await flushPromises();
    // The exposed object App calls through its template ref; replaced so no real confirmation opens.
    const exposed = (editor.vm.$ as unknown as { exposed: { resolveDetailDraft: () => Promise<boolean>; discardDetailDraft: () => void } }).exposed;
    const resolveDetailDraft = vi.fn(async () => false);
    const discardDetailDraft = vi.fn();
    exposed.resolveDetailDraft = resolveDetailDraft;
    exposed.discardDetailDraft = discardDetailDraft;
    return { app, editor, resolveDetailDraft, discardDetailDraft };
  }
  const saved = (fetchMock: ReturnType<typeof requestMock>) => fetchMock.mock.calls.some(([path, init]) => path === `/api/pages/${page.id}` && init?.method === 'PUT');

  // 3.6-0, rule 2: the page asks nothing for its own changes, but a component's unconfirmed input asks for itself.
  it("treats the draft as unsaved work for leaving the page and closing the window: the editor asks about it, nothing else does", async () => {
    const { app, resolveDetailDraft, discardDetailDraft } = await editingWithYamlDraft();
    const unload = new Event('beforeunload', { cancelable: true });
    window.dispatchEvent(unload);
    expect(unload.defaultPrevented).toBe(true);
    await app.get('.search-launch').trigger('click');
    const input = app.get('.search-dialog input[role="combobox"]');
    await input.setValue('> knihovna'); await input.trigger('keydown', { key: 'Enter' }); await flushPromises();
    // The detail asked about its own draft (here: the reader stays); the shared leave dialog is not involved.
    expect(resolveDetailDraft).toHaveBeenCalledTimes(1);
    expect(leaveButton('Zůstat')).toBeUndefined();
    expect(location.hash).toBe('#/pages/page-1');
    expect(discardDetailDraft).not.toHaveBeenCalled();
    expect(app.getComponent(DocumentEditor).props('readonly')).toBe(false);
  });

  it("opens the history without asking while a YAML draft is unapplied; the question comes with the restore", async () => {
    const { app } = await editingWithYamlDraft(requestMock(path => path.includes('/history') ? Promise.resolve(response({ pageId: page.id, currentVersion: 1, items: [], nextBeforeVersion: null })) : undefined));
    const confirm = vi.spyOn(window, 'confirm').mockReturnValue(false);
    await app.get('[aria-label="Další akce stránky"]').trigger('click'); await settleMenu();
    menuItems().find(item => item.textContent?.includes('Historie'))!.click();
    // The panel itself, not every chunk the app prefetches meanwhile (vi.dynamicImportSettled took them all cold, most of the 5 s).
    await vi.waitFor(() => expect(app.findComponent({ name: 'PageHistoryPanel' }).exists()).toBe(true));
    expect(confirm).not.toHaveBeenCalled();
    confirm.mockRestore();
  });

  it("checkpoints the page without touching the unapplied draft, which is not part of the page", async () => {
    vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout"] });
    const fetchMock = requestMock((path, init) => path === `/api/pages/${page.id}` && init?.method === 'PUT' ? Promise.resolve(response({ ...page, version: 2 })) : undefined);
    const { app, editor, resolveDetailDraft } = await editingWithYamlDraft(fetchMock);
    editor.vm.$emit('change'); await flushPromises();
    await vi.advanceTimersByTimeAsync(1500); await flushPromises();
    expect(saved(fetchMock)).toBe(true);
    expect(resolveDetailDraft).not.toHaveBeenCalled();
    expect(fetchMock.mock.calls.some(([path]) => String(path).endsWith('/publish'))).toBe(false);
    expect(app.getComponent(DocumentEditor).props('readonly')).toBe(false);
  });
});

describe("links here under a published page (R2)", () => {
  const linksHere = { linksHere: { items: [{ id: "page-2", spaceId: space.id, spaceName: space.name, title: "Onboarding IT" }], total: 1 } };
  const connectionsUrl = `/api/pages/${page.id}/connections?limit=50`;
  const withLinks = (extra?: (path: string) => Promise<Response> | undefined) => requestMock(path => path === connectionsUrl ? Promise.resolve(response(linksHere)) : extra?.(path));
  const settle = settleLazyViews;

  it("shows links here while reading and keeps them while editing", async () => {
    const fetchMock = withLinks();
    const app = await mountApp(fetchMock);
    await app.get('.sidebar-space-link').trigger('click'); await flushPromises();
    await app.get('.tree-page').trigger('click'); await settle();
    const section = app.get('.page-connections');
    expect(section.get('.connections-graph-center').text()).toContain(page.title);
    const node = section.get('.connections-graph-node');
    expect(node.text()).toContain('Odkazuje sem');
    expect(node.text()).toContain('Onboarding IT');
    expect(new URL(node.attributes('href')!).hash).toBe('#/pages/page-2');
    // The footer follows the document body, and the whole page is requested once for it.
    expect(app.get('.document-body').element.nextElementSibling).toBe(section.element);
    expect(fetchMock.mock.calls.filter(([path]) => String(path) === connectionsUrl)).toHaveLength(1);

    // Editing hides nothing of the page's surroundings any more (3.6-F).
    await startEditing(app, false); await settle();
    expect(app.find('.page-connections').exists()).toBe(true);
  });

  it("hides related pages on capability loss and reloads fresh ones on recovery without hiding backlinks", async () => {
    useAiCapabilities().stop();
    let available = true;
    let reads = 0;
    const fetchMock = requestMock(path => {
      if (path === "/api/ai/capabilities") return Promise.resolve(response({ related: available }));
      if (path === connectionsUrl) {
        reads++;
        return Promise.resolve(response({ ...linksHere, related: { items: [{ id: "related-page", spaceId: space.id, spaceName: space.name, title: reads === 1 ? "Původní podklady" : "Nové podklady" }] } }));
      }
    });
    try {
      const app = await mountApp(fetchMock);
      await app.get('.sidebar-space-link').trigger('click'); await flushPromises();
      await app.get('.tree-page').trigger('click'); await settle();
      expect(app.get('.connections-tags a[title^="Podobné tématem"]').text()).toContain('Původní podklady');
      available = false;
      await useAiCapabilities().refresh(); await flushPromises();
      expect(app.find('.connections-tags a[title^="Podobné tématem"]').exists()).toBe(false);
      expect(app.get('.connections-graph-node').text()).toContain('Onboarding IT');
      expect(reads).toBe(1);
      available = true;
      await useAiCapabilities().refresh(); await flushPromises();
      expect(app.get('.connections-tags a[title^="Podobné tématem"]').text()).toContain('Nové podklady');
      expect(reads).toBe(2);
    } finally { useAiCapabilities().stop(); }
  });
  it("shows nothing when the endpoint fails", async () => {
    const app = await mountApp(requestMock());
    await app.get('.sidebar-space-link').trigger('click'); await flushPromises();
    await app.get('.tree-page').trigger('click'); await settle();
    expect(app.find('.page-connections').exists()).toBe(false);
    expect(titleOf(app)).toBe(page.title);
  });

  it("is not part of the public share or the history preview", () => {
    for (const file of ["components/sharing/PublicSharedPage.vue", "components/HistoryRevisionPreview.vue", "components/PageHistoryPanel.vue"]) {
      expect(readFileSync(resolve(__dirname, file), "utf8"), file).not.toContain("PageConnections");
    }
  });
});

describe("omnisearch meaning matches", () => {
  const hit = { id: "page-1", spaceId: space.id, spaceName: space.name, title: "Přihlášení", snippet: "Heslo se mění v profilu.", isDraft: false, changedAt: null };
  const meaning = { id: "page-2", spaceId: space.id, spaceName: space.name, title: "Zapomenuté heslo", snippet: "Obnovení přístupu přes e-mail.", headingTitle: "Obnovení", headingId: "obnoveni", semantic: true };
  const withPassage = { ...hit, passage: { text: "Heslo se mění v profilu.", headingTitle: "Profil", headingId: "profil" } };
  async function search(capability: boolean, body: Record<string, unknown>, text = "pristup") {
    useAiCapabilities().stop();
    vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout"] });
    const fetchMock = requestMock(path => {
      if (path === "/api/ai/capabilities") return Promise.resolve(response({ semanticSearch: capability, related: false, editorLinks: false, createSuggestions: false }));
      if (path.startsWith("/api/search?")) return Promise.resolve(response({ hasMore: false, ...body }));
      return undefined;
    });
    const app = await mountApp(fetchMock);
    await app.get(".search-launch").trigger("click");
    await app.get(".search-dialog input[role=\"combobox\"]").setValue(text);
    await vi.advanceTimersByTimeAsync(400); await flushPromises();
    return { app, fetchMock };
  }
  afterEach(() => { useAiCapabilities().stop(); });

  it("shows the semantic source chip, the badge and the passage only with the capability", async () => {
    const { app } = await search(true, { items: [withPassage, meaning], semantic: true });
    expect(app.get("[data-semantic-source]").text()).toBe("Významové hledání · hotovo");
    const rows = app.findAll(".search-result");
    expect(rows).toHaveLength(2);
    expect(rows[0]!.find("[data-semantic-badge]").exists()).toBe(false);
    expect(rows[0]!.get("[data-search-passage]").text()).toContain("Profil");
    expect(rows[0]!.attributes("href")!.endsWith("#/pages/page-1?heading=profil")).toBe(true);
    expect(rows[1]!.get("[data-semantic-badge]").text()).toBe("Významová shoda");
    expect(rows[1]!.attributes("href")!.endsWith("#/pages/page-2?heading=obnoveni")).toBe(true);
    expect(app.findAll("[data-ai-glyph]")).toHaveLength(0);
    expect(app.get('[data-kind="pages"]').findAll(".search-result")).toHaveLength(2);
    expect(app.find('[data-kind="headings"]').exists()).toBe(false);
  });

  it("looks exactly like today when the search response is not semantic", async () => {
    const plain = { items: [hit, { ...hit, id: "page-3", title: "Sekce", headingId: "sekce", headingTitle: "Sekce" }] };
    const lexical = await search(true, plain);
    const withCapability = lexical.app.get("#search-results").html();
    const status = lexical.app.get(".search-source-status").text();
    expect(status).not.toContain("Významové");
    expect(withCapability).not.toContain("data-semantic");
    expect(withCapability).not.toContain("search-passage");
    lexical.app.unmount(); wrapper = undefined; document.body.innerHTML = "";
    useAiCapabilities().stop();
    const without = await search(false, { ...plain, semantic: true, items: plain.items });
    expect(without.app.get("#search-results").html()).toBe(withCapability);
    expect(without.app.find("[data-semantic-source]").exists()).toBe(false);
  });

  it("renders a semantic response as plain rows when the capability is off", async () => {
    const { app } = await search(false, { items: [meaning], semantic: true });
    expect(app.find("[data-semantic-source]").exists()).toBe(false);
    expect(app.find("[data-semantic-badge]").exists()).toBe(false);
    expect(app.find("[data-search-passage]").exists()).toBe(false);
  });

  // T1: the server answered with partial matches when no page had every word, and the list said nothing about it.
  it("says when the results match only some of the words, and asks for the next page of that same result", async () => {
    const { app, fetchMock } = await search(false, { items: [hit], relaxed: true, hasMore: true, nextOffset: 50 }, "heslo serveru");
    expect(app.get("[data-search-relaxed]").text()).toBe("Nenašli jsme všechna slova dotazu, ukazujeme částečné shody.");
    await app.get(".search-load-more").trigger("click"); await flushPromises();
    const next = fetchMock.mock.calls.map(([path]) => String(path)).find(path => path.startsWith("/api/search?") && path.includes("offset=50"));
    expect(next).toContain("relaxed=true");
  });

  it("offers did-you-mean without AI and searches it on click", async () => {
    const { app, fetchMock } = await search(false, { items: [], didYouMean: "heslo" }, "hesl");
    expect(app.text()).toContain("Žádné odpovídající stránky");
    const button = app.get("[data-did-you-mean]");
    expect(button.text()).toBe("„heslo“?");
    expect(app.get(".search-did-you-mean").text()).toBe("Měli jste na mysli: „heslo“?");
    await button.trigger("click");
    await vi.advanceTimersByTimeAsync(400); await flushPromises();
    expect((app.get(".search-dialog input[role=\"combobox\"]").element as HTMLInputElement).value).toBe("heslo");
    expect(fetchMock.mock.calls.some(([path]) => String(path).startsWith("/api/search?q=heslo"))).toBe(true);
  });
});

describe("assistant entries (phase 3 W10)", () => {
  const caps = (assistant: boolean) => ({ semanticSearch: false, related: false, editorLinks: false, createSuggestions: false, answer: assistant, assistant });
  const source = { number: 1, type: "page", pageId: "page-1", spaceId: space.id, spaceName: space.name, title: "Přihlášení", headingTitle: "Profil", headingId: "profil", cited: true, changed: false };
  const answer = (id: string) => ({
    id, conversationId: "c1", role: "assistant", state: "answered", question: "Jak se přihlásit?", sentences: [{ text: "Přes profil.", sources: [1] }], sources: [source],
    provenance: { location: "company", provider: "lm", model: "m" }, createdAt: "2026-10-05T10:00:00Z",
  });
  afterEach(() => { useAiCapabilities().stop(); useAssistant().reset(); useWorkspacePanel().reset(); });

  it("renders no assistant entry while the capabilities are false", async () => {
    useAiCapabilities().stop();
    const fetchMock = requestMock();
    const app = await mountApp(fetchMock);
    await app.get(".sidebar-space-link").trigger("click"); await flushPromises();
    await app.get(".tree-page").trigger("click"); await flushPromises();
    expect(app.find("[data-assistant-launch]").exists()).toBe(false);
    await app.get(".search-launch").trigger("click");
    await app.get(".search-dialog input[role=\"combobox\"]").setValue("> ");
    expect(app.get("#command-results").text()).not.toMatch(/Asistent|Zeptat se na tuto stránku/);
    expect(app.find("[data-assistant-panel], [data-assistant-header]").exists()).toBe(false);
    expect(fetchMock.mock.calls.some(([url]) => /\/api\/(ai\/(conversations|messages)|me\/ai-preferences)/.test(String(url)))).toBe(false);
  });

  it("keeps the panel open while navigating through a citation", async () => {
    useAiCapabilities().stop();
    const fetchMock = requestMock((path, init) => {
      if (path === "/api/ai/capabilities") return Promise.resolve(response(caps(true)));
      if (path === "/api/me/ai-preferences") return Promise.resolve(response({ editorSuggestions: "on", disabledSuggestionTypes: [], version: null, assistantScope: "context" }));
      if (path === "/api/ai/conversations" && init?.method === "POST") return Promise.resolve(response({ conversationId: "c1", messageId: "m1", state: "preparing", sources: [source] }));
      if (path === "/api/ai/messages/m1/complete") return Promise.resolve(response(answer("m1")));
      return undefined;
    });
    const app = await mountApp(fetchMock);
    await app.get(".sidebar-space-link").trigger("click"); await flushPromises();
    await app.get(".tree-page").trigger("click"); await flushPromises();
    // Search is the common entry; the sidebar has its own Asistent entry since refresh 2.5, and a published page
    // keeps its contextual assistant action.
    expect(app.find("[data-assistant-launch=page]").exists()).toBe(true);
    expect(app.find("[data-assistant-launch=sidebar]").exists()).toBe(true);
    await app.get("[data-assistant-launch=page]").trigger("click");
    await settleLazyViews();
    expect(app.get("[role=complementary]").attributes("aria-label")).toBe("Asistent");
    const field = app.get("[data-assistant-composer] textarea");
    await field.setValue("Jak se přihlásit?");
    await field.trigger("keydown", { key: "Enter" });
    await flushPromises(); await flushPromises();
    expect(app.get("[data-ai-body]").text()).toContain("Přes profil.");
    await app.get("[data-ai-provenance-count]").trigger("click");
    await flushPromises();
    document.querySelector<HTMLAnchorElement>('[data-ai-provenance-popover] [data-source-chip="1"]')!.click();
    await flushPromises();
    expect(location.hash).toBe("#/pages/page-1?heading=profil");
    expect(app.find("[role=complementary] [data-assistant-panel]").exists()).toBe(true);
    expect(app.get("[data-ai-body]").text()).toContain("Přes profil.");
  });
});

describe("search answers (phase 3 W11)", () => {
  const source = { number: 1, type: "page", pageId: page.id, spaceId: space.id, spaceName: space.name, title: page.title, cited: true, changed: false };
  const answer = { id: "a1", conversationId: "c1", role: "assistant", state: "answered", question: "Jak se přihlásit?", sentences: [{ text: "Přes profil.", sources: [1] }], sources: [source], createdAt: "2026-10-05T10:00:00Z" };
  async function setup(enabled = true, preference: "ask" | "auto" = "ask") {
    const fetchMock = requestMock((path, init) => {
      if (path === "/api/ai/capabilities") return Promise.resolve(response({ answer: enabled, assistant: enabled }));
      if (path === "/api/me/ai-preferences") return Promise.resolve(response({ searchAnswer: preference, assistantScope: "context" }));
      if (path.startsWith("/api/search?")) return Promise.resolve(response({ items: [{ ...page, spaceName: space.name, snippet: "" }], hasMore: false }));
      if (path === "/api/ai/conversations" && init?.method === "POST") return Promise.resolve(response({ conversationId: "c1", messageId: "a1", state: "preparing", sources: [source] }));
      if (path === "/api/ai/messages/a1/complete") return Promise.resolve(response(answer));
      if (path === "/api/ai/conversations/c1/continue") return Promise.resolve(response({ id: "c1", available: true, canContinue: true, outdated: false, messages: [{ ...answer, id: "q1", role: "user", sentences: [], sources: [] }, answer] }));
    });
    const app = await mountApp(fetchMock); await useAiCapabilities().refresh(); await flushPromises();
    await app.get(".search-launch").trigger("click");
    return { app, fetchMock, input: app.get(".omnisearch-input input") };
  }
  afterEach(() => { useAiCapabilities().stop(); useAssistant().reset(); useWorkspacePanel().reset(); });
  it("reveals the answer pane only for a relevant query or the explicit question mode", async () => {
    const { app, input, fetchMock } = await setup();
    expect(app.find(".search-answer-pane").exists()).toBe(false);
    expect(app.find("[data-search-ask-mode]").exists()).toBe(true);
    await input.setValue("VPN"); await flushPromises();
    expect(app.find(".search-answer-pane").exists()).toBe(true);
    await input.setValue(""); await flushPromises();
    expect(app.find(".search-answer-pane").exists()).toBe(false);
    await app.get("[data-search-ask-mode]").trigger("click"); await flushPromises();
    expect(app.find(".search-answer-pane").exists()).toBe(true);
    expect(fetchMock.mock.calls.some(([path]) => String(path) === "/api/ai/conversations")).toBe(false);
  });

  it("switches search modes explicitly, keeps the question selection and never asks on a mode switch", async () => {
    vi.useFakeTimers(); const { app, input, fetchMock } = await setup(true, "auto");
    await input.setValue("VPN klient"); await vi.advanceTimersByTimeAsync(200); await flushPromises();
    const field = input.element as HTMLInputElement; field.focus(); field.setSelectionRange(2, 6, "backward");
    await app.get("[data-search-ask-mode]").trigger("click"); await flushPromises();
    expect(field.value).toBe("? VPN klient");
    expect([field.selectionStart, field.selectionEnd, field.selectionDirection]).toEqual([4, 8, "backward"]);
    expect(document.activeElement).toBe(field);
    expect(app.get("[data-search-ask-mode]").attributes("aria-pressed")).toBe("true");
    expect(field.placeholder).toContain("Zeptejte");
    await vi.advanceTimersByTimeAsync(1200); await flushPromises();
    expect(fetchMock.mock.calls.some(([path]) => String(path) === "/api/ai/conversations")).toBe(false);
    await app.get(".search-modes button:first-child").trigger("click"); await flushPromises();
    expect(field.value).toBe("VPN klient");
    expect([field.selectionStart, field.selectionEnd, field.selectionDirection]).toEqual([2, 6, "backward"]);
    expect(app.get("[data-search-shortcut-hint]").text()).toContain("F6");
    await input.trigger("keydown", { key: "ArrowDown" });
    await app.get("[data-search-ask-mode]").trigger("click"); await flushPromises();
    await input.trigger("keydown", { key: "Enter" }); await flushPromises();
    expect(fetchMock.mock.calls.filter(([path]) => String(path) === "/api/ai/conversations")).toHaveLength(1);
    await input.setValue("? Jak opravím VPN klient?");
    await vi.advanceTimersByTimeAsync(900); await flushPromises();
    expect(fetchMock.mock.calls.filter(([path]) => String(path) === "/api/ai/conversations")).toHaveLength(2);
  });
  it.each([false, true])("leaves native Tab navigation available in search, capability=%s", async enabled => {
    const { app, input } = await setup(enabled);
    for (const options of [{}, { shiftKey: true }, { ctrlKey: true }, { isComposing: true }]) {
      const event = new KeyboardEvent("keydown", { key: "Tab", bubbles: true, cancelable: true, ...options });
      input.element.dispatchEvent(event); await flushPromises();
      expect(event.defaultPrevented).toBe(false);
    }
    expect(app.find("[data-search-shortcut-hint]").exists()).toBe(enabled);
    if (enabled) {
      await input.setValue("> ");
      const event = new KeyboardEvent("keydown", { key: "Tab", bubbles: true, cancelable: true });
      input.element.dispatchEvent(event);
      expect(event.defaultPrevented).toBe(false);
      expect(app.find("[data-search-shortcut-hint]").exists()).toBe(false);
    }
  });
  // Tab in a typed field takes the first result (P8): the scope and Filtry after it are reached by their own key.
  it("Ctrl+Shift+F in the search lands on Filtry, whatever is typed, and not in the command mode", async () => {
    const { app, input } = await setup();
    await input.setValue("VPN");
    const event = new KeyboardEvent("keydown", { key: "F", ctrlKey: true, shiftKey: true, bubbles: true, cancelable: true });
    input.element.dispatchEvent(event);
    expect(event.defaultPrevented).toBe(true);
    expect(document.activeElement).toBe(app.get(".search-filter-toggle").element);
    expect(app.get(".search-filter-toggle").attributes("aria-keyshortcuts")).toBe("Control+Shift+F");
    await input.setValue("> ");
    const command = new KeyboardEvent("keydown", { key: "F", ctrlKey: true, shiftKey: true, bubbles: true, cancelable: true });
    input.element.dispatchEvent(command);
    expect(command.defaultPrevented).toBe(false);
  });
  it("enter still opens the selected result while an answer is offered", async () => {
    vi.useFakeTimers(); const { app, input, fetchMock } = await setup();
    await input.setValue("Jak se přihlásit?"); await vi.advanceTimersByTimeAsync(200); await flushPromises();
    expect(app.find("[data-ai-offer]").exists()).toBe(true);
    await input.trigger("keydown", { key: "Enter" }); await flushPromises();
    expect(app.find(".search-dialog[open]").exists()).toBe(false);
    expect(fetchMock.mock.calls.some(([path]) => String(path) === "/api/ai/conversations")).toBe(false);
  });
  it("question mark and ctrl enter ask, while arrows explicitly choose a result", async () => {
    vi.useFakeTimers(); const { app, input, fetchMock } = await setup();
    await input.setValue("? Jak se přihlásit?"); await vi.advanceTimersByTimeAsync(200); await flushPromises();
    await input.trigger("keydown", { key: "Enter" }); await flushPromises();
    expect(app.get("[data-ai-body]").text()).toContain("Přes profil.");
    expect(new URL(String(fetchMock.mock.calls.find(([path]) => String(path).startsWith("/api/search?"))![0]), location.origin).searchParams.get("q")).toBe("Jak se přihlásit?");
    await input.setValue("VPN klient"); await input.trigger("keydown", { key: "Enter", ctrlKey: true }); await flushPromises();
    expect((input.element as HTMLInputElement).value).toBe("? VPN klient");
    await vi.advanceTimersByTimeAsync(200); await input.trigger("keydown", { key: "ArrowDown" });
    await input.trigger("keydown", { key: "Enter" }); await flushPromises();
    expect(app.find(".search-dialog[open]").exists()).toBe(false);
  });
  it("ctrl enter during the automatic answer neither starts a second conversation nor searches again", async () => {
    vi.useFakeTimers(); const { input, fetchMock } = await setup(true, "auto");
    await input.setValue("VPN klient"); await vi.advanceTimersByTimeAsync(800); await flushPromises();
    const count = (match: (path: string) => boolean) => fetchMock.mock.calls.filter(([path]) => match(String(path))).length;
    const conversations = () => count(path => path === "/api/ai/conversations"), searches = () => count(path => path.startsWith("/api/search?"));
    expect(conversations()).toBe(1);
    const before = searches();
    await input.trigger("keydown", { key: "Enter", ctrlKey: true }); await vi.advanceTimersByTimeAsync(800); await flushPromises();
    expect((input.element as HTMLInputElement).value).toBe("? VPN klient");
    expect(conversations()).toBe(1);
    expect(searches()).toBe(before);
  });
  it("keeps the citation preview in the modal and closes it before search on Escape", async () => {
    const { app, input } = await setup();
    await input.setValue("Jak se přihlásit?");
    await vi.waitFor(() => expect(app.find("[data-search-ask]").exists()).toBe(true));
    await app.get("[data-search-ask]").trigger("click"); await flushPromises();
    const dialog = app.get(".search-dialog").element as HTMLDialogElement;
    const citation = app.get("[data-cite='1']");
    (citation.element as HTMLElement).focus();
    await citation.trigger("click"); await flushPromises();
    const preview = document.querySelector<HTMLElement>("[data-ai-citation-preview]")!;
    expect(preview.closest("dialog")).toBe(dialog);
    await vi.waitFor(() => expect(preview.contains(document.activeElement)).toBe(true));
    preview.dispatchEvent(new KeyboardEvent("keydown", { key: "Escape", bubbles: true, cancelable: true }));
    await flushPromises();
    expect(document.querySelector("[data-ai-citation-preview][data-state='open']")).toBeNull();
    expect(app.get(".search-dialog").attributes("open")).toBeDefined();
    expect(app.find("[data-ai-feedback]").exists()).toBe(true);
    await vi.waitFor(() => expect(document.activeElement).toBe(citation.element));
    await citation.trigger("keydown", { key: "Escape" });
    expect(app.get(".search-dialog").attributes("open")).toBeUndefined();
  });
  it("continues in the assistant without a new completion", async () => {
    vi.useFakeTimers(); const { app, input, fetchMock } = await setup();
    await input.setValue("Jak se přihlásit?"); await vi.advanceTimersByTimeAsync(200); await flushPromises();
    await app.get("[data-search-ask]").trigger("click"); await flushPromises();
    const count = fetchMock.mock.calls.filter(([path]) => String(path).endsWith("/complete")).length;
    await app.get("[data-search-continue]").trigger("click"); await flushPromises();
    expect(useAssistant().open.value).toBe(true); expect(useAssistant().conversationId.value).toBe("c1");
    expect(app.find(".search-dialog[open]").exists()).toBe(false);
    expect(fetchMock.mock.calls.filter(([path]) => String(path).endsWith("/complete"))).toHaveLength(count);
  });
  it("focuses the assistant composer only after the search modal has finished folding", async () => {
    const folds: Array<Animation & EventTarget> = [];
    Object.defineProperty(Element.prototype, "animate", { configurable: true, value: vi.fn(function(this: Element) {
      const animation = Object.assign(new EventTarget(), { cancel: vi.fn(), finished: Promise.resolve() }) as unknown as Animation & EventTarget;
      if (this.matches(".search-dialog")) folds.push(animation);
      return animation;
    }) });
    try {
      vi.useFakeTimers(); const { app, input } = await setup();
      await input.setValue("Jak se přihlásit?"); await vi.advanceTimersByTimeAsync(200); await flushPromises();
      await app.get("[data-search-ask]").trigger("click"); await flushPromises();
      await app.get("[data-search-continue]").trigger("click"); await flushPromises();
      const dialog = app.get(".search-dialog").element as HTMLDialogElement;
      const composer = app.get("[data-assistant-composer] textarea").element;
      expect(dialog.open).toBe(true);
      expect(dialog.hasAttribute("inert")).toBe(true);
      expect(document.activeElement).not.toBe(composer);
      folds.at(-1)!.dispatchEvent(new Event("finish")); await flushPromises();
      expect(dialog.open).toBe(false);
      expect(document.activeElement).toBe(composer);
    } finally { Reflect.deleteProperty(Element.prototype, "animate"); }
  });
  it("keeps the two modes and all AI requests absent without the answer capability", async () => {
    vi.useFakeTimers(); const { app, input, fetchMock } = await setup(false);
    await input.setValue("Jak se přihlásit?"); await vi.advanceTimersByTimeAsync(200); await flushPromises();
    expect(app.find("[data-search-answer], [data-search-ask-mode]").exists()).toBe(false);
    expect(app.get(".search-modes").findAll("button")).toHaveLength(2);
    expect(fetchMock.mock.calls.some(([path]) => String(path).includes("/ai/conversations") || String(path) === "/api/me/ai-preferences")).toBe(false);
  });
});
