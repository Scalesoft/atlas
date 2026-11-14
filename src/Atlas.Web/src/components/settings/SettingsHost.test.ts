import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from "vitest";
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import { defineComponent, h } from "vue";

// The surfaces are lazy components created once per module and keep what they loaded, so each test mocks its surfaces
// afresh, resets the modules afterwards and imports the host inside the test: none depends on another one having run.
let wrapper: VueWrapper | undefined;
/** A promise the test settles when its chunk is to arrive, like a slow network. */
const gate = () => {
  let open!: () => void;
  const opened = new Promise<void>((resolve) => { open = resolve; });
  return { opened, open };
};
/** The global surface as a stub that shows the path of its route, once `until` has been settled. */
function mockGlobalSurface(until: Promise<void> = Promise.resolve()) {
  vi.doMock("./global/GlobalSettings.vue", async () => {
    await until;
    const { useSettingsRoute } = await import("./useSettingsRoute");
    return { __esModule: true, default: defineComponent({ setup() { const router = useSettingsRoute(); return () => h("p", { "data-surface": router.route.value.path }); } }) };
  });
}
async function host(scope: "global" | "space") {
  const { default: SettingsHost } = await import("./SettingsHost.vue");
  return mount(SettingsHost, { props: { scope, spaceId: scope === "space" ? "provoz" : undefined, userId: "u-admin", isWikiAdmin: true, spaces: [] }, attachTo: document.body });
}
const settled = async () => { await flushPromises(); await vi.dynamicImportSettled(); await flushPromises(); };

// The host's cold first import (its SFC transform and the settings modules behind it) fell into the first test's 5 s budget and timed
// it out in 3 of 3 web suites run beside the editor suite. Transformed once here; the reset keeps every test on fresh modules.
beforeAll(async () => { await import("./SettingsHost.vue"); vi.resetModules(); });

beforeEach(() => {
  vi.stubGlobal("fetch", vi.fn(async () => Response.json({ globalSecurityAdmin: false, managesAllVaults: false, vaultManagerSpaces: [], vaults: [] })));
});
afterEach(() => {
  wrapper?.unmount();
  wrapper = undefined;
  vi.doUnmock("./global/GlobalSettings.vue");
  vi.doUnmock("./space/SpaceSettings.vue");
  vi.resetModules();
  vi.unstubAllGlobals();
  document.body.innerHTML = "";
  history.replaceState(null, "", "/");
});

describe("SettingsHost", () => {
  // The host owns the router and the leave guard, so a lazy surface must neither lose a navigation made while it loads nor leave a hole.
  it("shows the loading placeholder until the surface arrives, which then opens the address the user moved to meanwhile", async () => {
    const arrival = gate();
    mockGlobalSurface(arrival.opened);
    history.replaceState(null, "", "/#/settings/security/overview");
    wrapper = await host("global");
    await flushPromises();
    expect(wrapper.find('[role="status"]').exists()).toBe(true);
    expect(wrapper.find("[data-surface]").exists()).toBe(false);

    location.hash = "#/settings/security/networks";
    // jsdom delivers hashchange as a task, like a browser after a clicked link.
    await new Promise((resolve) => setTimeout(resolve, 0));
    arrival.open();
    await settled();
    expect(wrapper.find('[role="status"]').exists()).toBe(false);
    expect(wrapper.get("[data-surface]").attributes("data-surface")).toBe("security/networks");
  });

  // The application mounts a new host each time settings are opened or the scope changes. A surface created per host would
  // start unresolved again and show the placeholder and wait for an import() task, although its chunk is long loaded.
  it("renders a surface that was loaded before at once when settings are opened again", async () => {
    mockGlobalSurface();
    history.replaceState(null, "", "/#/settings/security/overview");
    wrapper = await host("global");
    await settled();
    expect(wrapper.find("[data-surface]").exists()).toBe(true);
    wrapper.unmount();

    wrapper = await host("global");
    expect(wrapper.find("[data-surface]").exists()).toBe(true);
    expect(wrapper.find('[role="status"]').exists()).toBe(false);
  });

  // Without this the whole settings area would stay blank and say nothing; only the application's own controls would remain.
  it("says so and offers a reload when the chunk of a surface cannot be loaded", async () => {
    // The space surface never arrives: the device is offline, or a deployment replaced the hashed files while the tab was open.
    vi.doMock("./space/SpaceSettings.vue", async () => { throw new Error("Failed to fetch dynamically imported module"); });
    const reload = vi.fn();
    vi.stubGlobal("location", { ...window.location, reload });
    history.replaceState(null, "", "/#/spaces/provoz/settings/general");
    wrapper = await host("space");
    await settled();
    expect(wrapper.find('[role="status"]').exists()).toBe(false);
    const alert = wrapper.get('[role="alert"]');
    expect(alert.text()).toContain("Nastavení se nepodařilo načíst");
    await alert.get("button").trigger("click");
    expect(alert.get("button").text()).toBe("Obnovit stránku");
    expect(reload).toHaveBeenCalledTimes(1);
  });

  // UX-N-5: a save the page did not finish (an invalid field, a fresh sign-in it asks for) left the question open with no reason.
  it("leaves after a save in the leave question, and steps aside for the page's reason when the save did not finish", async () => {
    let finishes = false;
    vi.doMock("./global/GlobalSettings.vue", async () => {
      const { onMounted, ref } = await import("vue");
      const { useSettingsRoute } = await import("./useSettingsRoute");
      const { useDraftGuard } = await import("./dirtyGuard");
      return { __esModule: true, default: defineComponent({ setup() {
        const router = useSettingsRoute();
        const dirty = ref(false);
        onMounted(() => { dirty.value = true; });
        useDraftGuard(dirty, { save: () => { if (finishes) dirty.value = false; } });
        return () => h("p", { "data-surface": router.route.value.path });
      } }) };
    });
    history.replaceState(null, "", "/#/settings/security/overview");
    wrapper = await host("global");
    await settled();
    const leave = async () => {
      location.hash = "#/settings/security/networks";
      await new Promise((resolve) => setTimeout(resolve, 0));
      await flushPromises();
      const save = [...document.querySelectorAll<HTMLButtonElement>("dialog[open] button")].find((button) => button.textContent === "Uložit")!;
      save.click();
      await flushPromises();
    };
    await leave();
    expect(document.querySelector("dialog[open]")).toBeNull();
    expect(wrapper.get("[data-surface]").attributes("data-surface")).toBe("security/overview");

    finishes = true;
    await leave();
    expect(wrapper.get("[data-surface]").attributes("data-surface")).toBe("security/networks");
  });
});
