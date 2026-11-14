import { afterEach, describe, expect, it, vi } from "vitest";
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import { defineComponent, h } from "vue";
import { provideSettingsRoute } from "../useSettingsRoute";
import { createVaultRegistry, provideVaultRegistry } from "../vaultRegistry";
import VaultDetailPage from "./VaultDetailPage.vue";

// The parts of a vault load their own data; this page only chooses between them, so they are stand-ins with one root each.
const { part } = vi.hoisted(() => ({ part: (name: string, vue: typeof import("vue")) => vue.defineComponent({ name, setup: () => () => vue.h("div", { class: `part-${name}` }, name) }) }));
vi.mock("./VaultStatusTab.vue", async () => ({ default: part("status", await import("vue")) }));
vi.mock("./VaultAccessTab.vue", async () => ({ default: part("access", await import("vue")) }));
vi.mock("./VaultSecretsTab.vue", async () => ({ default: part("secrets", await import("vue")) }));
vi.mock("./VaultAuditTab.vue", async () => ({ default: part("audit", await import("vue")) }));
vi.mock("../requests/AccessRequestsPanel.vue", async () => ({ default: part("requests", await import("vue")) }));
vi.mock("../security/ProtectionLevelEditor.vue", async () => ({ default: part("protection", await import("vue")) }));

const vault = { id: "v1", spaceId: "provoz", name: "Provozní trezor", locationName: "Server", fileName: "provoz.kdbx", status: "unlocked", entryCount: 3 };
let wrapper: VueWrapper | undefined;
afterEach(() => { wrapper?.unmount(); wrapper = undefined; });

function open(tab: string) {
  history.replaceState(null, "", "/#/spaces/provoz/settings/vaults/v1/" + tab);
  const registry = createVaultRegistry();
  registry.replace(vault as never);
  const Host = defineComponent({
    props: { tab: { type: String, required: true } },
    setup(props) {
      provideSettingsRoute("#/spaces/provoz/settings");
      provideVaultRegistry(registry);
      return () => h(VaultDetailPage, { spaceId: "provoz", vaultId: "v1", tab: props.tab });
    },
  });
  // Test Utils stubs Transition by default; the part swap is what is under test.
  wrapper = mount(Host, { props: { tab }, attachTo: document.body, global: { stubs: { transition: false } } });
  return wrapper;
}
const frames = () => new Promise<void>((resolve) => requestAnimationFrame(() => requestAnimationFrame(() => resolve())));

describe("vault detail", () => {
  it("swaps the part with a leave and an enter instead of cutting it over", async () => {
    const host = open("status");
    await flushPromises();
    expect(host.find(".part-status").exists()).toBe(true);
    await host.setProps({ tab: "audit" });
    // The part that is leaving carries the step's leave class while the next one waits (mode out-in).
    expect(host.find(".part-status").classes()).toContain("st-step-leave-active");
    expect(host.find(".part-audit").exists()).toBe(false);
    await frames(); await flushPromises(); await frames();
    expect(host.find(".part-status").exists()).toBe(false);
    expect(host.find(".part-audit").exists()).toBe(true);
  });
});

