import { afterEach, expect, it } from "vitest";
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import FilterMenu from "./FilterMenu.vue";

let wrapper: VueWrapper | undefined;
afterEach(() => { wrapper?.unmount(); wrapper = undefined; document.body.replaceChildren(); });

const options = ["Modelsoft", "Marek Musil", "IT KBN", "Lékárna", "Marketing", "Vedení", "Účetní", "Právník", "Dodavatel"]
  .map((label, index) => ({ value: `p${index}`, label, badge: String(index + 1) }));

it("ticks several values as checkboxes, names them on the trigger, and with a search ticks the first match by Enter", async () => {
  wrapper = mount(FilterMenu, { attachTo: document.body, props: { label: "Kdo", options, multiple: true, allLabel: "Všichni", searchLabel: "Hledat", values: ["p0", "p1"],
    "onUpdate:values": (values: string[]) => wrapper!.setProps({ values }) } });
  expect(wrapper.get("button").text()).toContain("Modelsoft +1");
  await wrapper.get("button").trigger("click");
  await new Promise(resolve => setTimeout(resolve, 40));
  await flushPromises();
  const search = document.querySelector<HTMLInputElement>(".filter-menu-panel .filter-menu-search")!;
  expect(document.querySelectorAll(".filter-menu-panel [data-filter-option]")).toHaveLength(9);
  // Without diacritics; the first match is preselected and Enter ticks it, the menu stays open.
  search.value = "lekar";
  search.dispatchEvent(new Event("input"));
  await flushPromises();
  expect([...document.querySelectorAll(".filter-menu-panel [data-filter-option]")].map(item => item.textContent?.trim())).toEqual(["Lékárna4"]);
  search.dispatchEvent(new KeyboardEvent("keydown", { key: "Enter", bubbles: true }));
  await flushPromises();
  expect((wrapper.props() as { values: string[] }).values).toEqual(["p0", "p1", "p3"]);
  expect(document.querySelector(".filter-menu-panel")).not.toBeNull();
  // A click unticks.
  search.value = "";
  search.dispatchEvent(new Event("input"));
  await flushPromises();
  document.querySelector<HTMLElement>('.filter-menu-panel [data-value="p0"]')!.click();
  await flushPromises();
  expect((wrapper.props() as { values: string[] }).values).toEqual(["p1", "p3"]);
});

it("says the all-label with nothing ticked and keeps the single choice as it was", () => {
  wrapper = mount(FilterMenu, { props: { label: "Kdo", options, multiple: true, allLabel: "Všichni" } });
  expect(wrapper.get("button").text()).toBe("Kdo:Všichni");
  wrapper.unmount();
  wrapper = mount(FilterMenu, { props: { label: "Stav", options: [{ value: "", label: "Všechny" }, ...options], modelValue: "p2" } });
  expect(wrapper.get("button").text()).toBe("Stav:IT KBN");
});

it("empties the search on the first Esc (the menu stays), takes nothing on Enter in the empty field, and tells equal names apart", async () => {
  const twins = [...options, { value: "x", label: "Vedení", badge: "10" }];
  wrapper = mount(FilterMenu, { attachTo: document.body, props: { label: "Kdo", options: twins, searchLabel: "Hledat", "onUpdate:modelValue": (value: string) => wrapper!.setProps({ modelValue: value }) } });
  await wrapper.get("button").trigger("click");
  await new Promise(resolve => setTimeout(resolve, 40));
  await flushPromises();
  expect(document.querySelectorAll(".filter-menu-twin")).toHaveLength(2);
  const search = document.querySelector<HTMLInputElement>(".filter-menu-panel .filter-menu-search")!;
  search.dispatchEvent(new KeyboardEvent("keydown", { key: "Enter", bubbles: true }));
  await flushPromises();
  expect((wrapper.props() as { modelValue: string }).modelValue ?? "").toBe("");
  search.value = "zzz";
  search.dispatchEvent(new Event("input"));
  await flushPromises();
  expect(document.querySelectorAll(".filter-menu-panel [data-filter-option]")).toHaveLength(0);
  search.dispatchEvent(new KeyboardEvent("keydown", { key: "Escape", bubbles: true, cancelable: true }));
  await flushPromises();
  expect(search.value).toBe("");
  expect(document.querySelector(".filter-menu-panel")).not.toBeNull();
});
