import { afterEach, expect, it, vi } from "vitest";
import { mount, flushPromises, type VueWrapper } from "@vue/test-utils";
import ModuleSettingsHost from "./ModuleSettingsHost.vue";
import { api, post } from "../../api";

vi.mock("../../api", async (original) => ({ ...(await original<object>()), api: vi.fn(), post: vi.fn() }));
// The module forms are the real settings forms; here they only report a draft through the host's guard.
vi.mock("../settings/general/OidcModule.vue", async () => {
  const { defineComponent, h } = await import("vue");
  const { useDirtyGuard } = await import("../settings/dirtyGuard");
  return {
    __esModule: true,
    default: defineComponent({
      setup() {
        const guard = useDirtyGuard();
        return () => h("div", [h("button", { onClick: () => guard && (guard.dirty.value = true) }, "Edit config"), h("button", { onClick: () => guard && (guard.dirty.value = false) }, "Cancel config")]);
      },
    }),
  };
});
vi.mock("../settings/general/YouTrackModule.vue", async () => {
  const { defineComponent, h } = await import("vue");
  return { __esModule: true, default: defineComponent({ render: () => h("p", "YouTrack settings fixture") }) };
});
vi.mock("../settings/general/BackupImportSection.vue", async () => {
  const { defineComponent, h } = await import("vue");
  return {
    __esModule: true,
    default: defineComponent({
      props: { importOnly: Boolean },
      emits: ["imported"],
      setup(props, { emit }) {
        return () => h("div", [h("p", `Import backup fixture ${props.importOnly ? "import-only" : "full"}`), h("button", { onClick: () => emit("imported", "") }, "Import fixture")]);
      },
    }),
  };
});

let wrapper: VueWrapper | undefined;
afterEach(() => {
  wrapper?.unmount();
  vi.clearAllMocks();
});
const items = [
  { id: "oidc", title: "Firemní přihlášení", description: "OIDC", settingsUi: "oidc", order: 10, showInSetup: true },
  { id: "youtrack", title: "YouTrack", description: "Úkoly", settingsUi: "youtrack", order: 20, showInSetup: true },
  { id: "hidden", title: "Mimo instalaci", description: "", settingsUi: "youtrack", order: 30, showInSetup: false },
];
async function open() {
  vi.mocked(api).mockResolvedValue({ items });
  wrapper = mount(ModuleSettingsHost, { props: { isWikiAdmin: true, setup: true } });
  await flushPromises();
  await vi.dynamicImportSettled();
  await flushPromises();
  return wrapper;
}
const next = (view: VueWrapper) => view.get("footer [data-setup-next]");
const button = (view: VueWrapper, text: string) => view.findAll("button").find((item) => item.text() === text)!;

it("hosts the module forms of the settings as setup steps, with the module title as the step heading", async () => {
  const view = await open();
  expect(api).toHaveBeenCalledWith("/admin/modules");
  expect(view.get("h1").text()).toBe("Připojte své nástroje");
  expect(view.get(".module-step-heading h2").text()).toBe("Firemní přihlášení");
  expect(view.text()).toContain("Krok 1 z 3");
  expect(view.find('[data-module="hidden"]').exists()).toBe(false);
  expect(view.classes()).toContain("settings-ui");
});

it("does not lose edits when switching a step: a draft in the module form closes the steps", async () => {
  const view = await open();
  await button(view, "Edit config").trigger("click");
  expect(next(view).attributes("disabled")).toBeDefined();
  expect(view.findAll("nav button").every((item) => item.attributes("disabled") !== undefined)).toBe(true);
  expect(view.get(".dirty-hint").text()).toContain("další modul");
  expect(view.emitted("dirty")?.at(-1)).toEqual([true]);
  await button(view, "Cancel config").trigger("click");
  await next(view).trigger("click");
  await flushPromises();
  await vi.dynamicImportSettled();
  await flushPromises();
  expect(view.text()).toContain("YouTrack settings fixture");
  expect(view.get(".module-step-heading h2").text()).toBe("YouTrack");
  expect(post).not.toHaveBeenCalled();
});

it("finishes onboarding only after server confirmation, and permits retry", async () => {
  const view = await open();
  await next(view).trigger("click");
  await flushPromises();
  await next(view).trigger("click");
  await flushPromises();
  expect(next(view).text()).toBe("Otevřít wiki");
  vi.mocked(post).mockRejectedValueOnce(new Error("Připojení selhalo"));
  await next(view).trigger("click");
  await flushPromises();
  expect(view.emitted("complete")).toBeUndefined();
  expect(view.get("[role=alert]").text()).toContain("selhalo");
  vi.mocked(post).mockResolvedValueOnce(undefined);
  await next(view).trigger("click");
  await flushPromises();
  expect(post).toHaveBeenCalledWith("/admin/setup/complete");
  expect(view.emitted("complete")).toHaveLength(1);
});

it("lets the user defer all integrations, but never skips an unsaved draft", async () => {
  const view = await open();
  await button(view, "Edit config").trigger("click");
  expect(view.get('[data-setup-finish]').attributes('disabled')).toBeDefined();
  await view.get('[data-setup-finish]').trigger('click');
  expect(post).not.toHaveBeenCalled();
  await button(view, "Cancel config").trigger("click");
  vi.mocked(post).mockResolvedValueOnce(undefined);
  await view.get('[data-setup-finish]').trigger('click');
  await flushPromises();
  expect(post).toHaveBeenCalledWith('/admin/setup/complete');
  expect(view.emitted('complete')).toHaveLength(1);
});

it("shows a distinct icon per step and exposes the steps as an ARIA tablist linked to their panels", async () => {
  const view = await open();
  const steps = view.findAll('[role="tab"]');
  expect(steps).toHaveLength(3);
  expect(view.find('[role="tablist"]').exists()).toBe(true);
  expect(steps.filter((step) => step.attributes("aria-selected") === "true")).toHaveLength(1);
  const names = steps.map((step) => step.get("svg").attributes("data-icon") ?? step.get("svg").html());
  expect(new Set(names).size).toBe(names.length);
  const controls = steps[0]!.attributes("aria-controls");
  expect(view.find(`#${controls}`).attributes("role")).toBe("tabpanel");
});

it("offers the backup import in import-only mode and passes its result on", async () => {
  const view = await open();
  await view.get('[data-module="backup-import"]').trigger("click");
  await flushPromises();
  await vi.dynamicImportSettled();
  await flushPromises();
  expect(view.text()).toContain("Import backup fixture import-only");
  expect(view.get(".module-step-heading h2").text()).toBe("Obnovit ze zálohy");
  await button(view, "Import fixture").trigger("click");
  expect(view.emitted("imported")).toEqual([[""]]);
});
