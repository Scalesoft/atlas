import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";

// The icon pack is shared module state (iconPack.ts), so every case starts from a pack nobody has requested. The
// catalog is the real one unless a case makes its request fail; a throwing factory runs again on each import
// (see iconPack.test.ts).
const pack = { catalogFailures: 0 };
let wrapper: VueWrapper;

beforeEach(() => {
  vi.resetModules();
  pack.catalogFailures = 0;
  vi.doMock("../../assets/icon-pack/catalog.json", () => {
    if (pack.catalogFailures > 0) {
      pack.catalogFailures--;
      throw new Error("Failed to fetch dynamically imported module");
    }
    return vi.importActual("../../assets/icon-pack/catalog.json");
  });
});
afterEach(() => {
  wrapper.unmount();
  vi.restoreAllMocks();
  localStorage.clear();
});

async function picker(icon = "server") {
  const AppearancePicker = (await import("./AppearancePicker.vue")).default;
  wrapper = mount(AppearancePicker, {
    attachTo: document.body,
    props: { icon, color: "teal-2", "onUpdate:icon": (value: string) => wrapper.setProps({ icon: value }) },
  });
  await flushPromises();
  return { ...(await import("../../appearance")), ...(await import("./appearanceIcons")), ...(await import("../../iconPack")) };
}
const more = () => wrapper.get<HTMLElement>("[data-appearance-more-icons]");
const quick = () => wrapper.findAll("[data-appearance-icons] [role=radio]");
const search = () => wrapper.get("[data-appearance-search]");
const options = () => wrapper.findAll("[data-appearance-results] [role=option]");
/** The browser and the pack arrive through real dynamic imports. */
const browserShown = () => vi.waitFor(() => { search(); });

describe("AppearancePicker", () => {
  it("offers the common icons and counts the rest on the disclosure without loading the pack", async () => {
    const { appearanceIcons, iconPackCatalog, iconPackCount, iconPackMarkup, quickAppearanceIconIds } = await picker();
    expect(quick().map((item) => item.attributes("data-value"))).toEqual([...quickAppearanceIconIds]);
    expect(more().text()).toBe(`Další ikony (${(appearanceIcons.length + iconPackCount).toLocaleString("cs-CZ")})`);
    expect(more().attributes("aria-expanded")).toBe("false");
    expect(wrapper.find("[data-appearance-search]").exists()).toBe(false);
    expect(iconPackCatalog.value).toBeNull();
    expect(iconPackMarkup.value).toBeNull();
  });

  it("opens the icon browser on the search, takes its choice and closes back to the common icons", async () => {
    const { appearanceIcons, quickAppearanceIconIds } = await picker();
    more().element.focus();
    await more().trigger("click");
    await browserShown();
    expect(more().attributes("aria-expanded")).toBe("true");
    expect(more().text()).toBe("Méně ikon");
    expect(document.getElementById(more().attributes("aria-controls")!)?.contains(search().element)).toBe(true);
    expect(options()).toHaveLength(appearanceIcons.length);
    await vi.waitFor(() => expect(document.activeElement).toBe(search().element));

    // An icon of the pack: it joins the common ones in the first place and the preview draws it.
    await search().setValue("rocket");
    await wrapper.get('[data-appearance-results] [data-value="rocket"]').trigger("click");
    await flushPromises();
    expect(wrapper.emitted("update:icon")).toEqual([["rocket"]]);
    expect(quick()[0]!.attributes("data-value")).toBe("rocket");
    expect(wrapper.find('[data-appearance-preview] svg[data-icon="rocket"] path').exists()).toBe(true);

    await more().trigger("click");
    await flushPromises();
    expect(more().attributes("aria-expanded")).toBe("false");
    expect(document.activeElement).toBe(more().element);
    expect(wrapper.find("[data-appearance-search]").exists()).toBe(false);
    expect(quick()).toHaveLength(quickAppearanceIconIds.length);
  });

  it("says when the other icons cannot be loaded, keeps the common ones usable and retries in place", async () => {
    pack.catalogFailures = 2;
    const { appearanceIcons } = await picker();
    await more().trigger("click");
    const failure = await vi.waitFor(() => wrapper.get('[role="alert"]'));
    expect(failure.text()).toContain("Další ikony se nepodařilo načíst");
    expect(wrapper.find("[data-appearance-search]").exists()).toBe(false);

    await wrapper.get('[data-appearance-icons] [data-value="star"]').trigger("click");
    expect(wrapper.emitted("update:icon")).toEqual([["star"]]);

    await failure.get("button").trigger("click");
    await browserShown();
    expect(wrapper.find('[role="alert"]').exists()).toBe(false);
    expect(options()).toHaveLength(appearanceIcons.length);
    await vi.waitFor(() => expect(document.activeElement).toBe(search().element));
  });

  // Pointing at or focusing the disclosure only warms the chunks: it is a hint, so a failure there says nothing to the shell.
  it("does not flag the build as outdated when the prefetch on pointing or focusing fails, and opening still loads", async () => {
    pack.catalogFailures = 100;
    await picker();
    const { buildOutdated } = await import("../../lazyComponent");
    more().element.focus();
    await more().trigger("pointerenter");
    // The prefetch has asked for the catalog and that request has failed: the mocked module counts its failures down.
    await vi.waitFor(() => expect(pack.catalogFailures).toBeLessThan(100));
    await flushPromises();
    expect(buildOutdated.value).toBe(false);
    expect(wrapper.find('[role="alert"]').exists()).toBe(false);

    pack.catalogFailures = 0;
    await more().trigger("click");
    await browserShown();
    expect(options()).toHaveLength(87);
    expect(buildOutdated.value).toBe(false);
  });

  it("keeps a stored pack icon in the first common place and names it in Czech in the preview", async () => {
    const { iconPackCatalog, quickAppearanceIconIds } = await picker("rocket");
    await vi.waitFor(() => expect(iconPackCatalog.value).not.toBeNull());
    await flushPromises();
    const label = iconPackCatalog.value!.icons.find((item) => item.id === "rocket")!.label;
    expect(quick()).toHaveLength(quickAppearanceIconIds.length);
    expect(quick()[0]!.attributes("data-value")).toBe("rocket");
    expect(quick()[0]!.attributes("aria-checked")).toBe("true");
    expect(quick()[0]!.attributes("aria-label")).toBe(label);
    expect(wrapper.get("[data-appearance-preview]").text()).toContain(`${label} · `);
    expect(more().attributes("aria-expanded")).toBe("false");
  });
});
