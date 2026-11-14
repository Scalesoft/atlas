import { afterEach, expect, it } from "vitest";
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import { h } from "vue";
import ActionDisclosure from "./ActionDisclosure.vue";
import ActionMenu from "./ActionMenu.vue";
import SidebarSpace from "./SidebarSpace.vue";
import SpaceMenuItems from "./SpaceMenuItems.vue";
import { helpSections } from "../keyboard/shortcuts";

let wrapper: VueWrapper | undefined;
afterEach(() => { wrapper?.unmount(); wrapper = undefined; document.body.replaceChildren(); });
const settle = () => new Promise(resolve => setTimeout(resolve, 40));
const rows = () => [...document.querySelectorAll<HTMLElement>("[role=menu] [role^=menuitem]")];
const labels = () => rows().map(row => row.textContent!.trim());

// 3.6 review F12: every menu opens with its first action lit, whether it opens by a click or a key.
it("lights the first action of an ActionDisclosure menu opened by a click", async () => {
  wrapper = mount({ components: { ActionDisclosure, SpaceMenuItems }, template: '<ActionDisclosure label="Akce"><SpaceMenuItems can-write can-manage boards /></ActionDisclosure>' }, { attachTo: document.body });
  await wrapper.get("[aria-expanded]").trigger("click"); await settle();
  expect(document.activeElement).toBe(rows()[0]);
  expect(rows()[0]!.hasAttribute("data-highlighted")).toBe(true);
});

it("lights the first enabled action of an ActionMenu (the tree row's menu) when it opens", async () => {
  const items = [{ id: "a", label: "Otevřít", disabled: true }, { id: "b", label: "Přejmenovat" }, { id: "c", label: "Přesunout do koše…", destructive: true }];
  wrapper = mount(ActionMenu, { attachTo: document.body, props: { items, label: "Akce", itemAttr: "data-x", actions: true, open: true }, slots: { default: () => h("button", "x") } });
  await flushPromises(); await settle();
  expect(document.activeElement).toBe(rows()[1]);
  expect(rows()[2]!.classList).toContain("board-menu-destructive");
});

// 3.6 review F22: the breadcrumb's and the sidebar's space menu are the same list.
it("offers the same space actions in the breadcrumb's list and in the sidebar's", async () => {
  const props = { canWrite: true, canManage: true, boards: true };
  wrapper = mount({ components: { ActionDisclosure, SpaceMenuItems }, template: '<ActionDisclosure label="Akce prostoru: Provoz"><SpaceMenuItems v-bind="$attrs" /></ActionDisclosure>', inheritAttrs: false }, { attachTo: document.body, attrs: props });
  await wrapper.get("[aria-expanded]").trigger("click"); await settle();
  const breadcrumb = labels();
  wrapper.unmount(); document.body.replaceChildren();

  const space = { id: "s", name: "Provoz", icon: "book", color: "blue", role: "Admin" as const, pinPolicy: "None" as const, pinned: false, pageCount: 0, lastChangedAt: null };
  wrapper = mount(SidebarSpace, { props: { space, pages: [], expanded: false, ...props }, attachTo: document.body });
  await wrapper.get('[aria-label="Další akce prostoru Provoz"]').trigger("click"); await settle();
  // The sidebar adds only what concerns its tree (Najít ve stromu, Sbalit vše, Odepnout), none of which an empty, unpinned space has.
  expect(labels()).toEqual(breadcrumb);
  expect(breadcrumb).toEqual(["Nová stránka", "Nová tabule", "Nastavení prostoru", "Šablony stránek", "Přístupy", "Připnutí pro všechny…", "Koš stránek", "Exportovat prostor"]);
  rows().find(row => row.textContent?.includes("Šablony"))!.click(); await settle();
  expect(wrapper.emitted("templates")).toHaveLength(1);
});

// 3.6 review F20: AI is extra.
it("leaves out Ctrl J and the AI wording in the keyboard help while no assistant can answer", () => {
  const editor = [{ keys: "Alt Enter", label: "Co s tím jde dělat, s návrhy AI nahoře", group: "Editor" }, { keys: "Ctrl Space", label: "Co s tím jde dělat: nabídka akcí (kontakt, text, AI)", group: "Editor" }];
  const text = (ai: boolean) => JSON.stringify(helpSections(editor, [], [], ai));
  expect(text(true)).toContain("Asistent");
  expect(text(true)).toContain("AI");
  expect(text(false)).not.toContain("Asistent");
  expect(text(false)).not.toMatch(/AI/);
  expect(text(false)).toContain("Ctrl Space");
});
