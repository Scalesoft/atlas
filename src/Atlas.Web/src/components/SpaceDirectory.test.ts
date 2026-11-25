import { afterEach, expect, it } from "vitest";
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import { defineComponent, h, ref, type PropType } from "vue";
import type { Space } from "../api";
import SpaceDirectory from "./SpaceDirectory.vue";
import { TooltipProvider } from "./ui/tooltip";

const space = (id: string, name: string, extra: Partial<Space> = {}): Space => ({
  id, name, icon: "book-open", color: "blue", role: "Reader", pinPolicy: "None", pinned: false, pageCount: 3, lastChangedAt: "2026-09-20T08:00:00Z", ...extra,
});
const spaces = [
  space("ops", "Provoz", { description: "Servery a sítě", role: "Admin" }),
  space("dev", "Vývoj", { description: "Kód a nasazení", pinPolicy: "Default", pinned: true }),
  space("hr", "Lidé", { pinPolicy: "Required", pinned: true }),
  space("arch", "Archiv", { itemAccessOnly: true }),
];

// The app provides tooltips at its root; the host does the same and passes props through.
const Host = defineComponent({
  props: { spaces: { type: Array as PropType<Space[]>, required: true }, canCreate: Boolean, canManage: Boolean, pendingIds: Set as PropType<ReadonlySet<string>>, limit: Number },
  setup: (props) => () => h(TooltipProvider, null, () => h(SpaceDirectory, { ...props })),
});
let wrapper: VueWrapper | undefined;
function render(props: Partial<{ spaces: Space[]; canCreate: boolean; canManage: boolean; pendingIds: ReadonlySet<string>; limit: number }> = {}) {
  wrapper = mount(Host, { props: { spaces, canCreate: true, ...props }, attachTo: document.body });
  return wrapper;
}
const directory = () => wrapper!.findComponent(SpaceDirectory);
const tileIds = () => wrapper!.findAll("[data-space]").map((tile) => tile.attributes("data-space"));

afterEach(() => {
  wrapper?.unmount();
  wrapper = undefined;
  document.body.innerHTML = "";
  history.replaceState(null, "", "/");
});

it("lists pinned spaces first and locks a Required one", () => {
  const view = render();
  expect(tileIds()).toEqual(["hr", "dev", "arch", "ops"]);
  const required = view.get('[data-space="hr"]');
  expect(required.find("[data-pin-locked]").text()).toContain("Připnuto správcem");
  expect(required.find("button[aria-pressed]").exists()).toBe(false);
  expect(view.get('[data-space="arch"]').text()).toContain("jen k vybraným stránkám");
  const level = view.get('[data-space="ops"] [data-space-access-level]');
  expect(level.text()).toBe("Spravuje");
  expect(level.attributes("title")).toBe("Váš přístup: Spravuje");
  expect(view.get('[data-space="ops"]').text()).toContain("3 stránky");
});

it("moves a tile to the pinned ones as soon as it is pinned and keeps its toggle focused", async () => {
  // As the shell does: the pin shows at once, before the server answers.
  const Live = defineComponent({
    setup() {
      const list = ref(spaces);
      const pin = (target: Space, pinned: boolean) => (list.value = list.value.map((item) => (item.id === target.id ? { ...item, pinned } : item)));
      return () => h(TooltipProvider, null, () => h(SpaceDirectory, { spaces: list.value, canCreate: true, onPin: pin }));
    },
  });
  wrapper = mount(Live, { attachTo: document.body });
  const toggle = wrapper.get('[data-pin="ops"]');
  (toggle.element as HTMLElement).focus();
  await toggle.trigger("click");
  await flushPromises();
  expect(tileIds()).toEqual(["hr", "ops", "dev", "arch"]);
  expect(toggle.attributes("aria-pressed")).toBe("true");
  expect(document.activeElement).toBe(toggle.element);
  await wrapper.get('[data-pin="dev"]').trigger("click");
  await flushPromises();
  expect(tileIds()).toEqual(["hr", "ops", "arch", "dev"]);
});

it("searches name and description without diacritics and filters the pinned ones", async () => {
  const view = render();
  await view.get("[data-space-search]").setValue("SITE");
  expect(tileIds()).toEqual(["ops"]);
  expect(view.get(".space-directory-count").text()).toBe("1 z 4 prostorů");
  await view.get("[data-space-search]").setValue("");
  await view.get("[data-space-filter]").trigger("click");
  await new Promise((resolve) => setTimeout(resolve, 40));
  document.querySelector<HTMLElement>('.filter-menu-panel [data-value="pinned"]')!.click();
  await flushPromises();
  expect(tileIds()).toEqual(["hr", "dev"]);
  // Unpinned under Připnuté: the tile stays (below the pinned one) until the filter or the search changes.
  await view.setProps({ spaces: spaces.map((item) => (item.id === "dev" ? { ...item, pinned: false } : item)) });
  expect(tileIds()).toEqual(["hr", "dev"]);
  await view.get("[data-space-search]").setValue("provoz");
  expect(view.get("[data-space-no-match]").text()).toContain("Žádný připnutý prostor neodpovídá hledání „provoz“");
  await view.get("[data-space-reset]").trigger("click");
  expect(tileIds()).toHaveLength(4);
});

it("asks to pin with the negated value and holds a pending toggle", async () => {
  const view = render({ pendingIds: new Set(["arch"]) });
  await view.get('[data-pin="ops"]').trigger("click");
  await view.get('[data-pin="dev"]').trigger("click");
  const pending = view.get('[data-pin="arch"]');
  expect(pending.attributes("aria-busy")).toBe("true");
  await pending.trigger("click");
  expect(directory().emitted("pin")).toEqual([[spaces[0], true], [spaces[1], false]]);
});

it("opens a space on a plain click and leaves modified clicks to the browser", async () => {
  const view = render();
  const link = view.get('[data-space="ops"] a[href]');
  expect(link.attributes("href")).toContain("ops");
  // No interactive element nests in another: the pin toggle is a sibling of the link.
  expect(view.find("a button, button a").exists()).toBe(false);
  await link.trigger("click", { ctrlKey: true });
  expect(directory().emitted("open")).toBeUndefined();
  const plain = new MouseEvent("click", { bubbles: true, cancelable: true, button: 0 });
  link.element.dispatchEvent(plain);
  expect(plain.defaultPrevented).toBe(true);
  expect(directory().emitted("open")).toEqual([[spaces[0]]]);
});

it("explains an empty list by permission and notes the server limit", async () => {
  const view = render({ spaces: [], canCreate: false });
  expect(view.get("[data-space-empty]").text()).toContain("Zatím nemáte přístup k žádnému prostoru");
  expect(view.find("button").exists()).toBe(false);
  await view.setProps({ canCreate: true });
  await view.get("[data-space-empty] button").trigger("click");
  expect(directory().emitted("create")).toHaveLength(1);
  await view.setProps({ spaces, limit: 4 });
  expect(view.get("[data-space-limit]").text()).toContain("nejvýš 4 prostory");
});

it("gives Karty / Seznam one thumb that is placed first and then follows the choice", async () => {
  // jsdom has no layout: the choice made sits at 2 px (Karty) or 78 px (Seznam), 74 and 89 px wide.
  const left = Object.getOwnPropertyDescriptor(HTMLElement.prototype, "offsetLeft");
  const width = Object.getOwnPropertyDescriptor(HTMLElement.prototype, "offsetWidth");
  const isList = (element: HTMLElement) => element.dataset.value === "list";
  Object.defineProperty(HTMLElement.prototype, "offsetLeft", { configurable: true, get() { return isList(this) ? 78 : 2; } });
  Object.defineProperty(HTMLElement.prototype, "offsetWidth", { configurable: true, get() { return isList(this) ? 89 : 74; } });
  try {
    const view = render();
    const frame = () => new Promise<void>((done) => requestAnimationFrame(() => done()));
    await flushPromises(); await frame(); await frame(); await flushPromises();
    const switcher = view.get("[data-library-view] .option-pills-group");
    // Placed without motion first, then ready for the glide (the CSS transition hangs on data-thumb).
    expect(switcher.attributes("style")).toContain("--thumb-x: 2px");
    expect(switcher.attributes("style")).toContain("--thumb-w: 74px");
    expect(switcher.attributes("data-thumb")).toBe("ready");

    await view.get('[data-library-view] [data-value="list"]').trigger("click");
    await flushPromises(); await frame();
    expect(switcher.attributes("style")).toContain("--thumb-x: 78px");
    expect(switcher.attributes("style")).toContain("--thumb-w: 89px");
  } finally {
    if (left) Object.defineProperty(HTMLElement.prototype, "offsetLeft", left); else delete (HTMLElement.prototype as { offsetLeft?: number }).offsetLeft;
    if (width) Object.defineProperty(HTMLElement.prototype, "offsetWidth", width); else delete (HTMLElement.prototype as { offsetWidth?: number }).offsetWidth;
  }
});

it("tells someone who administers a space where it is pinned for everyone", () => {
  render();
  expect(wrapper!.find("[data-space-policy-hint]").exists()).toBe(false);
  wrapper!.unmount();
  render({ canManage: true });
  expect(wrapper!.get("[data-space-policy-hint]").text()).toContain("Obecné → Připnutí pro všechny");
});
