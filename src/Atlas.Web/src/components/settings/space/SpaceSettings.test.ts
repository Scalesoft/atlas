import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import { defineComponent, h, ref } from "vue";
import type { Space } from "../../../api";
import { provideSettingsContext } from "../context";
import { createDirtyGuard, provideDirtyGuard } from "../dirtyGuard";
import { createSettingsFlash, provideSettingsFlash } from "../flash";
import { provideSettingsRoute } from "../useSettingsRoute";
import { createVaultRegistry, provideVaultRegistry } from "../vaultRegistry";
import SpaceSettings from "./SpaceSettings.vue";

const any = ref(false);
vi.mock("../../../composables/useAiCapabilities", () => ({ useAiCapabilities: () => ({ any }) }));
// The chunk of this page never arrives (offline, or replaced by a deployment); no other test opens it.
vi.mock("./SpaceSharingPage.vue", async () => { throw new Error("Failed to fetch dynamically imported module"); });

const spaces: Space[] = [{ id: "provoz", name: "Provoz", icon: "book-open", color: "blue", role: "Admin", pinPolicy: "None", pinned: false, pageCount: 0, lastChangedAt: null }];
let wrapper: VueWrapper | undefined;
function open(section: string, mcpEnabled = false, isWikiAdmin = false) {
  history.replaceState(null, "", `/#/spaces/provoz/settings/${section}`);
  const Host = defineComponent({
    setup() {
      const router = provideSettingsRoute("#/spaces/provoz/settings");
      provideSettingsContext({ userId: "u", isWikiAdmin, spaces, backupWarning: null, version: undefined, mcpEnabled, imported: vi.fn(), warning: vi.fn(), openPage: vi.fn(), newTemplate: vi.fn(), newPage: vi.fn(), spaceChanged: vi.fn(), spaceTrashed: vi.fn(), spacesChanged: vi.fn(), accessChanged: vi.fn() });
      provideDirtyGuard(createDirtyGuard());
      provideSettingsFlash(createSettingsFlash(router));
      provideVaultRegistry(createVaultRegistry());
      return () => h(SpaceSettings, { spaceId: "provoz" });
    },
  });
  wrapper = mount(Host, { attachTo: document.body });
}
// A page is a lazy chunk, loaded the first time a section shows it: wait for it where a test reads the page itself.
async function settle() { await flushPromises(); await vi.dynamicImportSettled(); await flushPromises(); }
beforeEach(() => {
  any.value = false;
  vi.stubGlobal("fetch", vi.fn(async (input: string) => {
    const path = String(input);
    if (path.includes("ai-settings")) return Response.json({ included: true, version: "v1" });
    if (path.includes("search-gaps")) return Response.json({ items: [], minimumUsers: 3, retentionDays: 90 });
    return Response.json([]);
  }));
});
afterEach(() => { wrapper?.unmount(); wrapper = undefined; vi.unstubAllGlobals(); document.body.innerHTML = ""; });
const navItem = (path: string) => wrapper!.find(`[data-path="${path}"]`);

describe("SpaceSettings AI section", () => {
  it("hides the ai section without an ai capability", async () => {
    open("search-gaps");
    await flushPromises();
    expect(navItem("ai").exists()).toBe(false);
    // The rule-based gaps section works without AI and stays.
    expect(navItem("search-gaps").exists()).toBe(true);
    wrapper!.unmount();
    open("ai");
    await flushPromises();
    expect(wrapper!.text()).toContain("Tato sekce neexistuje");
    expect(wrapper!.find("[data-space-ai-included]").exists()).toBe(false);
  });

  it("offers AI a znalosti next to Nenalezené dotazy while an ai capability exists", async () => {
    any.value = true;
    open("ai");
    await settle();
    expect(navItem("ai").exists()).toBe(true);
    expect(navItem("search-gaps").exists()).toBe(true);
    expect(wrapper!.find("[data-space-ai-included]").exists()).toBe(true);
  });

  it("offers the section with MCP only, and hides it with neither", async () => {
    any.value = false;
    open("ai", true);
    await settle();
    expect(navItem("ai").exists()).toBe(true);
    expect(wrapper!.find("[data-space-ai-included]").exists()).toBe(true);
    expect(wrapper!.text()).toContain("Zahrnout prostor do nástrojů MCP");
    expect(wrapper!.text()).not.toContain("související stránky");
    wrapper!.unmount();
    open("search-gaps", false);
    await flushPromises();
    expect(navItem("ai").exists()).toBe(false);
  });

  it("names both AI and MCP when both are on", async () => {
    any.value = true;
    open("ai", true);
    await settle();
    expect(wrapper!.text()).toContain("Zahrnout prostor do AI a nástrojů MCP");
  });
});

describe("SpaceSettings cross link", () => {
  it("offers Správa wiki to a wiki administrator only", async () => {
    open("search-gaps", false, true);
    await settle();
    const link = wrapper!.get("[data-global-settings]");
    expect(link.text()).toBe("Správa wiki");
    expect(link.attributes("href")).toBe("#/settings/security/overview");
    wrapper!.unmount();
    open("search-gaps");
    await settle();
    expect(wrapper!.find("[data-global-settings]").exists()).toBe(false);
  });

  it("gives a space writer Stav obsahu only, and nothing of the administrator's sections", async () => {
    spaces[0]!.role = "Writer";
    try {
      open("content-health");
      await settle();
      const links = () => wrapper!.findAll("nav a").map((link) => link.attributes("href"));
      expect(links()).toEqual(["#/spaces/provoz/settings/content-health"]);
      expect(wrapper!.text()).toContain("Stav obsahu");
      wrapper!.unmount();
      open("access");
      await settle();
      // A direct link to an administrator's section shows nothing of it.
      expect(wrapper!.text()).not.toContain("Pozvat");
      expect(wrapper!.findAll("nav a").map((link) => link.attributes("href"))).toEqual(["#/spaces/provoz/settings/content-health"]);
    } finally { spaces[0]!.role = "Admin"; }
  });
});

describe("SpaceSettings pages", () => {
  // A page that cannot be loaded must not leave the content blank: the shell and its navigation stay and the reload is offered.
  it("says so inside the shell and keeps the section navigation when the chunk of a page cannot be loaded", async () => {
    open("sharing");
    await settle();
    const alert = wrapper!.get('.settings-shell-content [role="alert"]');
    expect(alert.text()).toContain("Nastavení se nepodařilo načíst");
    expect(alert.get("button").text()).toBe("Obnovit stránku");
    expect(wrapper!.find('[role="status"]').exists()).toBe(false);
    expect(navItem("sharing").exists()).toBe(true);
    expect(navItem("general").exists()).toBe(true);
  });
});
