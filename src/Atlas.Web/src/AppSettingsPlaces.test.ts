// The three places of settings (design 2026-10-10): Profil, Nastavení prostoru and Správa wiki. Each holds the rail
// with its own heading, way back and breadcrumb, and nothing in the rail switches between them.
import { afterEach, beforeAll, describe, expect, it, vi } from "vitest";
import { flushPromises, type VueWrapper } from "@vue/test-utils";
import { emptyDocument } from "./document";
import { globalSettingsHome } from "./settingsRoutes";
import { jsonResponse as response, mountApp, quietRoutes, resetBrowser, routedFetch, sessionRoutes, type Route } from "./testing/appHarness";

vi.mock("./components/DocumentEditor.vue", async () => { const { defineComponent, onMounted } = await import("vue"); return { __esModule: true, default: defineComponent({
  name: "DocumentEditor", props: ["value", "readonly"], emits: ["update:value", "change", "ready", "collaborationStatus"],
  setup(_, { emit, expose }) {
    onMounted(() => emit("ready"));
    expose({ focus: () => true, capture: () => ({ contentJson: JSON.stringify(emptyDocument()), markdown: "" }) });
  },
  template: '<textarea aria-label="Testovací dokument" />',
}) }; });

const appearance = { layout: "connected", accent: "blue", mode: "light" };
/** A wiki administrator, and an account whose role in each space decides what it may open. */
const admin = { id: "user-1", username: "admin", displayName: "Test Admin", isWikiAdmin: true, appearance };
const member = { id: "user-2", username: "member", displayName: "Test Member", isWikiAdmin: false, appearance };
type Account = typeof admin;
/** A space as `GET /api/spaces` lists it for the account; `more` overrides or adds fields (a description, a change time). */
function space(id: string, name: string, role = "Admin", more: Record<string, unknown> = {}) {
  return { id, name, icon: "book", color: "blue", role, pinPolicy: "None", pinned: true, pageCount: 1, lastChangedAt: null, ...more };
}
type SpaceRow = ReturnType<typeof space>;
const page = { id: "page-1", spaceId: "space-1", parentId: null, title: "Dokument", version: 1, isPublished: true, contentJson: JSON.stringify(emptyDocument()), markdown: "", publishedVersion: 1 };

let wrapper: VueWrapper | undefined;
/** Every path the shell has asked for since `start`. */
let requests: string[] = [];
/** Lazy views (the profile, the settings surfaces and their pages) are chunks: wait for the imports, then for what they render and request. */
async function settle() { await vi.dynamicImportSettled(); await flushPromises(); await flushPromises(); }
/**
 * Mounts the shell at an address. By default a wiki administrator with two spaces it administers; the one page lives
 * in space-1. `routes` answers first, so a test can add or override a request.
 */
async function start(hash = "#/pages/page-1", options: { user?: Account; spaces?: SpaceRow[]; routes?: Route } = {}) {
  const user = options.user ?? admin;
  const spaces = options.spaces ?? [space("space-1", "Tým"), space("space-2", "Provoz")];
  const wiki: Route = path => {
    if (path === "/api/auth/me") return response(user);
    if (path === "/api/spaces") return response(spaces);
    if (path === "/api/admin/setup") return response({ pending: false });
    if (path === "/api/admin/backups") return response({ warning: null });
    if (path === "/api/ai/capabilities") return response({ semanticSearch: false, related: false, editorLinks: false, createSuggestions: false });
    const tree = /^\/api\/spaces\/([^/]+)\/tree$/.exec(path)?.[1];
    if (tree) return response({ spaceId: tree, version: 1, items: tree === page.spaceId ? [{ ...page, sortOrder: 1024, canEdit: true }] : [] });
    const settings = /^\/api\/spaces\/([^/]+)\/settings$/.exec(path)?.[1];
    if (settings) return response({ ...spaces.find(item => item.id === settings), description: "", version: "1" });
    if (/^\/api\/spaces\/[^/]+\/page-templates$/.test(path)) return response([]);
    if (path === `/api/pages/${page.id}`) return response(page);
    if (path.endsWith("/tags")) return response({ tags: [] });
    if (path === "/api/profile/account") return response({ displayName: user.displayName, email: null, version: "v1", source: "builtin", canEditDisplayName: true, canEditEmail: true, currentSessionAuthenticatedAt: null, currentSessionAuthenticationMethod: null, lastSignInAt: null, lastSignInMethod: null });
    if (path === "/api/auth/methods") return response({ builtinEnabled: true, providers: [] });
    if (path === "/api/vaults" || path === "/api/vault-locations") return response([]);
  };
  requests = [];
  const record: Route = path => { requests.push(path); return undefined; };
  wrapper = mountApp({ url: "/" + hash, fetch: routedFetch(record, options.routes, wiki, sessionRoutes, quietRoutes) });
  await settle();
  return wrapper;
}
/** Arrives at an address the way the browser does (a typed address, back and forward): nothing was pressed. */
async function visit(hash: string) {
  history.pushState(null, "", hash);
  window.dispatchEvent(new PopStateEvent("popstate"));
  await settle();
}
// Action menus are portalled and exist only while open, so their items are found in the document.
const settleMenu = () => new Promise(resolve => setTimeout(resolve, 40));
const unsavedDialog = (app: VueWrapper) => app.findAll("dialog[open]").find(dialog => dialog.text().includes("Neuložené změny"))!;
const answer = (app: VueWrapper, label: string) => unsavedDialog(app).findAll("button").find(button => button.text() === label)!.trigger("click");

// The comment thread an open page loads beside it is the coldest chunk (6.5 s on its first use, it takes the editor's
// comment composer with it) and would use up the 5 s of whichever test opens a page first. The settings chunks in three
// levels, the switcher of their rail, the profile and O prostoru are cold on their first use in this file too: import them
// once up front.
beforeAll(() => Promise.all([
  import("./components/comments/CommentThread.vue"),
  import("./components/settings/SettingsHost.vue"),
  import("./components/settings/global/GlobalSettings.vue"),
  import("./components/settings/space/SpaceSettings.vue"),
  import("./components/settings/SpaceSwitcher.vue"),
  import("./components/ProfilePage.vue"),
  import("./components/library/SpaceAbout.vue"),
]));
afterEach(() => { wrapper?.unmount(); wrapper = undefined; resetBrowser(); });

describe("the rail of three places", () => {
  // The profile and Správa wiki return to the page they were opened from; space settings return to their space.
  const places: { place: string; open: (app: VueWrapper) => Promise<void>; address: string; back: string; breadcrumb: string; returns: string; view: string }[] = [
    { place: "Profil", open: app => app.get(".profile-launch").trigger("click"),
      address: "#/profile/account", back: "Zpět na stránku", breadcrumb: "Profil", returns: "#/pages/page-1", view: "textarea" },
    { place: "Správa wiki", open: app => app.get(".account .settings-nav").trigger("click"),
      address: "#/settings/security/overview", back: "Zpět do wiki", breadcrumb: "Správa wiki", returns: "#/pages/page-1", view: "textarea" },
    { place: "Nastavení prostoru", open: async app => {
        await app.get('[aria-label="Další akce prostoru Tým"]').trigger("click"); await settleMenu();
        document.querySelector<HTMLElement>("[role=menu] [data-space-menu=settings]")!.click();
      },
      address: "#/spaces/space-1/settings/general", back: "Zpět do prostoru", breadcrumb: "Tým · Nastavení prostoru", returns: "#/spaces/space-1", view: ".space-overview" },
  ];
  it.each(places)("$place opened from a page has its own heading, way back and breadcrumb", async ({ place, open, address, back, breadcrumb, returns, view }) => {
    const app = await start("#/pages/page-1");
    await open(app); await settle();
    expect(location.hash).toBe(address);
    expect(app.get(".settings-sidebar-title").text()).toBe(place);
    expect(app.get(".settings-back").text()).toBe(back);
    expect(app.get('.breadcrumb-title[aria-current="page"]').text()).toBe(breadcrumb);
    // Nothing in the rail switches between the places; its filter says that it matches section names only.
    expect(app.find(".settings-scope-tabs").exists()).toBe(false);
    expect(app.get('#atlas-settings-rail input[type="search"]').attributes("placeholder")).toBe("Hledat sekci");
    await app.get(".settings-back").trigger("click"); await settle();
    // The address alone would not show that the view it names has opened.
    expect(location.hash).toBe(returns);
    expect(app.find("#atlas-settings-rail").exists()).toBe(false);
    expect(app.get(view).isVisible()).toBe(true);
  });

  it("returns from Správa wiki to Domů when it was reached by its address, not by the gear", async () => {
    const app = await start("#/pages/page-1");
    await visit("#/settings/people/accounts");
    expect(app.get(".settings-sidebar-title").text()).toBe("Správa wiki");
    await app.get(".settings-back").trigger("click"); await settle();
    expect(location.hash).toBe("#/");
    expect(app.find("#atlas-settings-rail").exists()).toBe(false);
    expect(app.find("textarea").exists()).toBe(false);
    await vi.waitFor(() => expect(app.find("[data-home-dashboard]").exists()).toBe(true));
  });

  // The remembered address serves one return: a later arrival by address has nothing to go back to but the wiki root, Domů.
  it("returns to the page the gear was pressed on once, and to Domů after a later arrival by address", async () => {
    const app = await start("#/pages/page-1");
    await app.get(".account .settings-nav").trigger("click"); await settle();
    await app.get(".settings-back").trigger("click"); await settle();
    expect(location.hash).toBe("#/pages/page-1");
    await visit("#/settings/people/accounts");
    await app.get(".settings-back").trigger("click"); await settle();
    expect(location.hash).toBe("#/");
    await vi.waitFor(() => expect(app.find("[data-home-dashboard]").exists()).toBe(true));
  });

  it("returns from Správa wiki to the space settings whose link led there, and says so", async () => {
    // Below 1025 px, where the rail is a drawer (as it is in these tests), the surface shows its own header and actions.
    vi.stubGlobal("matchMedia", (query: string) => ({ matches: query === "(max-width: 1024px)", media: query, addEventListener: vi.fn(), removeEventListener: vi.fn() }));
    const app = await start("#/spaces/space-1/settings/templates");
    // "Správa wiki" in that header: pressed, it opens through the shell as the gear does.
    await app.get("[data-global-settings]").trigger("click"); await settle();
    expect(location.hash).toBe(globalSettingsHome);
    expect(app.get(".settings-sidebar-title").text()).toBe("Správa wiki");
    expect(app.get(".settings-back").text()).toBe("Zpět do nastavení prostoru");
    await app.get(".settings-back").trigger("click"); await settle();
    expect(location.hash).toBe("#/spaces/space-1/settings/templates");
    expect(app.get(".settings-sidebar-title").text()).toBe("Nastavení prostoru");
  });

  // A vault in Trezory is a plain link into the settings of its space. Coming back from there by the browser is an
  // arrival by address: nobody pressed "Správa wiki", so the way back is still the page of the gear.
  it("returns to the page of the gear after the browser came back from space settings a link of Správa wiki led to", async () => {
    const app = await start("#/pages/page-1");
    await app.get(".account .settings-nav").trigger("click"); await settle();
    await visit("#/spaces/space-1/settings/vaults");
    expect(app.get(".settings-sidebar-title").text()).toBe("Nastavení prostoru");
    await visit(globalSettingsHome);
    expect(app.get(".settings-back").text()).toBe("Zpět do wiki");
    // Also when that way back discards a draft of the space settings: Zahodit resumes the arrival, it presses nothing.
    await visit("#/spaces/space-1/settings/vaults");
    app.getComponent({ name: "SettingsHost" }).vm.$emit("dirty", true); await flushPromises();
    await visit(globalSettingsHome);
    await answer(app, "Zahodit"); await settle();
    expect(app.get(".settings-sidebar-title").text()).toBe("Správa wiki");
    expect(app.get(".settings-back").text()).toBe("Zpět do wiki");
    await app.get(".settings-back").trigger("click"); await settle();
    expect(location.hash).toBe("#/pages/page-1");
    expect(app.get("textarea").isVisible()).toBe(true);
  });

  // Settings of a space the account does not know have no overview to return to: the place is Správa wiki's, in words and in target.
  it("gives the settings address of an unknown space the heading and the way back of Správa wiki", async () => {
    const app = await start("#/spaces/gone/settings/general");
    expect(app.get(".settings-back").text()).toBe("Zpět do wiki");
    expect(app.get(".settings-sidebar-title").text()).toBe("Správa wiki");
    expect(app.get('.breadcrumb-title[aria-current="page"]').text()).toBe("Správa wiki");
    await app.get(".settings-back").trigger("click"); await settle();
    expect(location.hash).toBe("#/");
    await vi.waitFor(() => expect(app.find("[data-home-dashboard]").exists()).toBe(true));
  });

  // The question comes before the address moves: staying leaves the history as it was, discarding adds the way back only.
  it("asks about a settings draft before Zpět do wiki moves the address", async () => {
    const app = await start("#/pages/page-1");
    await app.get(".account .settings-nav").trigger("click"); await settle();
    app.getComponent({ name: "SettingsHost" }).vm.$emit("dirty", true); await flushPromises();
    const entries = history.length;
    await app.get(".settings-back").trigger("click"); await settle();
    await answer(app, "Zůstat"); await settle();
    expect(location.hash).toBe("#/settings/security/overview");
    expect(history.length).toBe(entries);
    await app.get(".settings-back").trigger("click"); await settle();
    await answer(app, "Zahodit"); await settle();
    expect(location.hash).toBe("#/pages/page-1");
    expect(history.length).toBe(entries + 1);
    expect(app.get("textarea").isVisible()).toBe(true);
  });

  it("asks about a settings draft before the profile takes the rail, and unmounts the settings once it does", async () => {
    const app = await start("#/settings/security/overview");
    const host = () => app.findComponent({ name: "SettingsHost" });
    host().vm.$emit("dirty", true); await flushPromises();
    await app.get(".profile-launch").trigger("click"); await settle();
    await answer(app, "Zůstat"); await settle();
    expect(location.hash).toBe("#/settings/security/overview");
    expect(host().exists()).toBe(true);
    expect(app.find(".profile-page").exists()).toBe(false);
    await app.get(".profile-launch").trigger("click"); await settle();
    await answer(app, "Zahodit"); await settle();
    expect(location.hash).toBe("#/profile/account");
    expect(app.get(".settings-sidebar-title").text()).toBe("Profil");
    expect(host().exists()).toBe(false);
  });
});

describe("the space switcher in the rail of space settings", () => {
  // The account administers two spaces and only reads the third.
  const spaces = [space("space-1", "Tým"), space("space-2", "Provoz"), space("space-3", "Archiv", "Reader")];
  const switcher = (app: VueWrapper) => app.getComponent({ name: "SpaceSwitcher" });
  const host = (app: VueWrapper) => app.getComponent({ name: "SettingsHost" });

  it.each([
    ["#/spaces/space-3/settings", "Nastavení prostoru může otevřít jen jeho správce."],
    ["#/spaces/space-3/settings/trash", "Nastavení prostoru může otevřít jen jeho správce."],
    ["#/settings", "Správu wiki může otevřít jen správce wiki."],
    ["#/settings/people", "Správu wiki může otevřít jen správce wiki."],
    ["#/new-space", "Nový prostor může založit jen správce wiki."],
  ])("sends a direct link to %s the account may not open to Knihovna and says why", async (hash, reason) => {
    const { toast } = await import("vue-sonner");
    const refused = vi.spyOn(toast, "error");
    await start(hash, { user: member, spaces });
    expect(location.hash).toBe("#/");
    expect(refused).toHaveBeenCalledWith(reason, expect.anything());
  });

  it("offers the spaces the account manages and opens the same section of the chosen one", async () => {
    const app = await start("#/spaces/space-1/settings/templates", { user: member, spaces });
    expect(switcher(app).props("current")).toMatchObject({ id: "space-1" });
    expect((switcher(app).props("spaces") as SpaceRow[]).map(item => item.id)).toEqual(["space-1", "space-2"]);
    const before = host(app).element;
    switcher(app).vm.$emit("switch", "space-2"); await settle();
    expect(location.hash).toBe("#/spaces/space-2/settings/templates");
    expect(switcher(app).props("current")).toMatchObject({ id: "space-2" });
    // Another surface: the host is a new one for space-2, not the old one given another space.
    expect(host(app).props("spaceId")).toBe("space-2");
    expect(host(app).element).not.toBe(before);
    expect(app.get('.breadcrumb-title[aria-current="page"]').text()).toBe("Provoz · Nastavení prostoru");
  });

  it("asks about a draft before it moves, and leaves behind what lay below the section", async () => {
    const app = await start("#/spaces/space-1/settings/access/people/u-1", { user: member, spaces });
    host(app).vm.$emit("dirty", true); await flushPromises();
    switcher(app).vm.$emit("switch", "space-2"); await settle();
    await answer(app, "Zůstat"); await settle();
    expect(location.hash).toBe("#/spaces/space-1/settings/access/people/u-1");
    expect(host(app).props("spaceId")).toBe("space-1");
    switcher(app).vm.$emit("switch", "space-2"); await settle();
    await answer(app, "Zahodit"); await settle();
    // The person belongs to the space that was left: the move lands on the section itself.
    expect(location.hash).toBe("#/spaces/space-2/settings/access");
    expect(host(app).props("spaceId")).toBe("space-2");
  });

  it("is in the rail of space settings only, not in the profile or Správa wiki", async () => {
    const app = await start("#/spaces/space-1/settings/general");
    const slot = () => app.find(".settings-switcher-slot"), chip = () => app.find(".settings-switcher-slot [data-space-switcher]");
    expect(chip().exists()).toBe(true);
    await app.get(".profile-launch").trigger("click"); await settle();
    expect(slot().exists()).toBe(false);
    await app.get(".settings-back").trigger("click"); await settle();
    expect(chip().exists()).toBe(true);
    await app.get(".account .settings-nav").trigger("click"); await settle();
    expect(app.get(".settings-sidebar-title").text()).toBe("Správa wiki");
    expect(slot().exists()).toBe(false);
  });

  // The test window is narrow (matchMedia answers false), so the rail is the drawer that Escape closes.
  it("closes only its own list on Escape while the drawer of a narrow screen is open", async () => {
    const app = await start("#/spaces/space-1/settings/general");
    await app.get(".mobile-menu").trigger("click"); await settle();
    const chip = app.get("button[data-space-switcher]");
    await chip.trigger("click"); await settle();
    const list = () => document.querySelector<HTMLElement>('[data-slot="popover-content"]');
    // The list is portalled out of the drawer and takes the focus, so the drawer does not see its keys.
    expect(list()!.contains(document.activeElement)).toBe(true);
    document.activeElement!.dispatchEvent(new KeyboardEvent("keydown", { key: "Escape", bubbles: true, cancelable: true }));
    await settle();
    expect(list()).toBeNull();
    expect(app.get(".sidebar").classes()).toContain("mobile-open");
    expect(document.activeElement).toBe(chip.element);
  });
});

describe("O prostoru, the tab of a space for everyone who can open it", () => {
  const template = { id: "template-1", spaceId: "space-1", parentId: null, title: "Zápis z porady", version: 1, isPublished: true, isTemplate: true };
  const routes: Route = path => {
    if (path === "/api/spaces/space-1/page-templates") return response([template]);
    if (path === `/api/pages/${template.id}`) return response({ ...page, ...template, publishedVersion: 1 });
  };
  const tabs = (app: VueWrapper) => app.findAll(".space-tabs [role=tab]").map(tab => tab.attributes("data-space-tab") ?? "approval");
  const selected = (app: VueWrapper) => app.get('.space-tabs [role=tab][aria-selected="true"]').attributes("data-space-tab");

  // A writer creates pages, which is not administering the space: the gate is the one of space settings.
  it.each([["Reader", "Čte"], ["Writer", "Upravuje"]])("tells a %s about the space and nothing about its administration", async (role, access) => {
    const app = await start("#/spaces/space-1/about", { user: member, spaces: [space("space-1", "Tým", role, { description: "Postupy a rozhodnutí týmu." })], routes });
    expect(tabs(app)).toEqual(["pages", "contacts", "about"]);
    expect(selected(app)).toBe("about");
    const about = app.get("[data-space-about]");
    expect(about.get(".space-about-description").text()).toBe("Postupy a rozhodnutí týmu.");
    expect(about.get('[data-summary="role"]').text()).toContain(access);
    expect(about.find('[data-open-template="template-1"]').exists()).toBe(true);
    expect(app.find("[data-space-about-admin]").exists()).toBe(false);
    expect(app.find("[data-space-about-link]").exists()).toBe(false);
    expect(app.find("[data-space-settings-open]").exists()).toBe(false);
    // The tab reads what the overview already holds and asks only for the templates: nothing about other people. A writer's overview
    // also counts the space's unlinked mentions (its Nepropojené zmínky tab); a reader cannot link, so it is not asked.
    expect([...new Set(requests.filter(path => path.startsWith("/api/spaces/")))].sort()).toEqual(["/api/spaces/space-1/page-templates", "/api/spaces/space-1/tree",
      ...(role === "Writer" ? ["/api/spaces/space-1/unlinked?limit=50"] : [])]);
    await app.get('[data-space-tab="pages"]').trigger("click"); await settle();
    expect(location.hash).toBe("#/spaces/space-1");
    expect(selected(app)).toBe("pages");
    expect(app.find("[data-space-about]").exists()).toBe(false);
  });

  it("leads a space administrator into the settings sections and opens a template", async () => {
    const app = await start("#/spaces/space-1", { user: member, spaces: [space("space-1", "Tým")], routes });
    expect(app.find("[data-space-settings-open]").exists()).toBe(true);
    expect(tabs(app)).toEqual(["pages", "contacts", "about"]);
    // The templates are read only while the tab is on screen.
    const reads = () => requests.filter(path => path === "/api/spaces/space-1/page-templates").length;
    expect(reads()).toBe(0);
    await app.get('[data-space-tab="about"]').trigger("click"); await settle();
    expect(reads()).toBe(1);
    expect(location.hash).toBe("#/spaces/space-1/about");
    expect(app.findAll("[data-space-about-admin] a[data-space-about-link]").map(link => link.attributes("href")))
      .toEqual(["general", "access", "sharing", "templates", "vaults"].map(section => `#/spaces/space-1/settings/${section}`));
    // The tab's address is where the shell is: the profile opened here returns here.
    await app.get(".profile-launch").trigger("click"); await settle();
    await app.get(".settings-back").trigger("click"); await settle();
    expect(location.hash).toBe("#/spaces/space-1/about");
    expect(selected(app)).toBe("about");
    await app.get('[data-open-template="template-1"]').trigger("click"); await settle();
    expect(location.hash).toBe("#/pages/template-1");
    expect(app.get("textarea").isVisible()).toBe(true);
    // Back on the tab, the list is read again: the template may have been renamed or unmarked meanwhile.
    const before = reads();
    await visit("#/spaces/space-1/about");
    expect(selected(app)).toBe("about");
    expect(reads()).toBe(before + 1);
  });

  // The tabs are views of the space that is already open: only arriving at the space reads its tree and unfolds it.
  it("moves between the tabs without reading the tree again or unfolding the space in the sidebar", async () => {
    const app = await start("#/spaces/space-1", { user: member, spaces: [space("space-1", "Tým")], routes });
    const trees = () => requests.filter(path => path === "/api/spaces/space-1/tree").length;
    const sidebarSpace = () => app.getComponent({ name: "SidebarSpace" });
    expect(trees()).toBe(1);
    expect(sidebarSpace().props("expanded")).toBe(true);
    sidebarSpace().vm.$emit("toggle"); await flushPromises();
    expect(sidebarSpace().props("expanded")).toBe(false);
    await app.get('[data-space-tab="about"]').trigger("click"); await settle();
    expect(location.hash).toBe("#/spaces/space-1/about");
    expect(selected(app)).toBe("about");
    await app.get('[data-space-tab="pages"]').trigger("click"); await settle();
    expect(location.hash).toBe("#/spaces/space-1");
    expect(selected(app)).toBe("pages");
    expect(trees()).toBe(1);
    expect(sidebarSpace().props("expanded")).toBe(false);
  });

  // The role alone does not administer a space: the menu beside its name asks the same gate as its settings do.
  it("offers no administration to an Admin of the space who has access to chosen pages only", async () => {
    const spaces = [space("space-1", "Tým", "Admin", { itemAccessOnly: true }), space("space-2", "Provoz")];
    const app = await start("#/spaces/space-1/about", { user: member, spaces });
    const spaceMenu = (name: string) => app.find(`button[aria-label="Akce prostoru: ${name}"]`);
    expect(spaceMenu("Tým").exists()).toBe(false);
    expect(app.find("[data-space-settings-open]").exists()).toBe(false);
    expect(app.get("[data-space-about]").find("[data-space-about-admin]").exists()).toBe(false);
    // The space the account does administer has the menu, and is the only one the switcher of its settings lists.
    await visit("#/spaces/space-2");
    expect(spaceMenu("Provoz").exists()).toBe(true);
    await visit("#/spaces/space-2/settings/general");
    expect((app.getComponent({ name: "SpaceSwitcher" }).props("spaces") as SpaceRow[]).map(item => item.id)).toEqual(["space-2"]);
  });
});

// 5.2 §6: Stav obsahu is every editor's of the space; canOpenSettings gives a Writer that one section and nothing else of the space settings.
describe("Stav obsahu for a space's editors", () => {
  const health: Route = path => path.startsWith("/api/spaces/space-1/content-health")
    ? response({ summary: { verified: 0, expiring: 0, expired: 0, unverified: 0, withoutSteward: 0 }, items: [], nextCursor: null, enabled: true }) : undefined;

  it("leads a Writer from the space menu into Stav obsahu, opens it by its address and refuses the other sections", async () => {
    const { toast } = await import("vue-sonner");
    const refused = vi.spyOn(toast, "error");
    const app = await start("#/spaces/space-1", { user: member, spaces: [space("space-1", "Tým", "Writer")], routes: health });
    await app.get('[aria-label="Další akce prostoru Tým"]').trigger("click"); await settleMenu();
    expect(document.querySelector("[role=menu] [data-space-menu=settings]")).toBeNull();
    document.querySelector<HTMLElement>("[role=menu] [data-space-menu=content-health]")!.click(); await settle();
    expect(location.hash).toBe("#/spaces/space-1/settings/content-health");
    expect(app.findComponent({ name: "SpaceContentHealthPage" }).exists()).toBe(true);
    expect(requests).toContain("/api/spaces/space-1/content-health?sort=priority&days=90");
    expect(refused).not.toHaveBeenCalled();
    await visit("#/spaces/space-1/settings/general");
    expect(location.hash).toBe("#/");
    expect(refused).toHaveBeenCalledWith("Nastavení prostoru může otevřít jen jeho správce.", expect.anything());
    await visit("#/spaces/space-1/settings/content-health");
    expect(location.hash).toBe("#/spaces/space-1/settings/content-health");
  });

  it("offers a Reader no Stav obsahu", async () => {
    const app = await start("#/spaces/space-1", { user: member, spaces: [space("space-1", "Tým", "Reader")], routes: health });
    expect(app.find('[aria-label="Další akce prostoru Tým"]').exists() && document.querySelector("[data-space-menu=content-health]")).toBeFalsy();
    await visit("#/spaces/space-1/settings/content-health");
    expect(location.hash).toBe("#/");
  });
});
