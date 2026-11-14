import { afterEach, expect, it } from "vitest";
import { mount, type VueWrapper } from "@vue/test-utils";
import BoardPickList from "./BoardPickList.vue";

let wrapper: VueWrapper | undefined;
afterEach(() => { wrapper?.unmount(); wrapper = undefined; document.body.innerHTML = ""; });

const options = [
  { value: "a", label: "Alfa", description: "první", icon: "user" },
  { value: "b", label: "Beta", description: "druhá", icon: "user" },
  { value: "c", label: "Gama", icon: "user" },
];
const show = (props: Record<string, unknown> = {}) => (wrapper = mount(BoardPickList, { attachTo: document.body, props: { options, picked: [], label: "Kontakt", ...props } }));
const box = (value: string) => wrapper!.get(`[data-pick="${value}"]`);

it("lists the results as checkboxes with their description, nothing ticked at first", () => {
  show();
  expect(wrapper!.findAll("input[type=checkbox]")).toHaveLength(3);
  expect(wrapper!.text()).toContain("Alfa");
  expect(wrapper!.text()).toContain("první");
  expect((box("a").element as HTMLInputElement).checked).toBe(false);
  expect(wrapper!.find("[data-pick-count]").exists()).toBe(false);
});

it("asks to toggle the option that is ticked, or ticked again", async () => {
  show({ picked: [options[1]] });
  expect((box("b").element as HTMLInputElement).checked).toBe(true);
  await box("a").setValue(true);
  await box("b").setValue(false);
  expect(wrapper!.emitted("toggle")).toEqual([[options[0]], [options[1]]]);
});

it("shows the basket as removable tags, in the order of the picks, however the results change", async () => {
  show({ picked: [options[2], options[0]] });
  expect(wrapper!.get("[data-pick-count]").text()).toBe("Vybráno 2 z 100");
  expect(wrapper!.findAll(".pick-tag").map(tag => tag.text())).toEqual(["Gama", "Alfa"]);
  // The picks are no longer among the results: the tags stay.
  await wrapper!.setProps({ options: [options[1]] });
  expect(wrapper!.findAll(".pick-tag")).toHaveLength(2);
  await wrapper!.get('[data-tag="c"]').trigger("click");
  expect(wrapper!.emitted("toggle")).toEqual([[options[2]]]);
  expect(wrapper!.get('[data-tag="c"]').attributes("aria-label")).toBe("Odebrat Gama");
});

it("stops offering more past the limit, but lets a ticked one be unticked", () => {
  show({ limit: 2, picked: [options[0], options[1]] });
  expect((box("c").element as HTMLInputElement).disabled).toBe(true);
  expect(wrapper!.text()).toContain("Nejvýše 2 položek");
  expect((box("a").element as HTMLInputElement).disabled).toBe(false);
  expect(wrapper!.get("[data-pick-count]").text()).toBe("Vybráno 2 z 2");
});

it("says what to do when there is nothing to show", () => {
  show({ options: [], emptyText: "Napište jméno." });
  expect(wrapper!.find("input[type=checkbox]").exists()).toBe(false);
  expect(wrapper!.get("[role=status]").text()).toBe("Napište jméno.");
});

it("binds the search text and leaves the filtering to the owner", async () => {
  show({ query: "al", "onUpdate:query": (value: string) => wrapper!.setProps({ query: value }) });
  const field = wrapper!.get("input[type=search]");
  expect((field.element as HTMLInputElement).value).toBe("al");
  await field.setValue("alfa novakova");
  expect((wrapper!.props() as { query: string }).query).toBe("alfa novakova");
  // All three results stay although none contains the text: the server decided what matches.
  expect(wrapper!.findAll("input[type=checkbox]")).toHaveLength(3);
});

it("moves between the rows with the arrow keys, from the field in and back out", async () => {
  show();
  const field = wrapper!.get("input[type=search]");
  await field.trigger("keydown", { key: "ArrowDown" });
  expect(document.activeElement).toBe(box("a").element);
  await box("a").trigger("keydown", { key: "ArrowDown" });
  expect(document.activeElement).toBe(box("b").element);
  await box("b").trigger("keydown", { key: "ArrowUp" });
  await box("a").trigger("keydown", { key: "ArrowUp" });
  expect(document.activeElement).toBe(field.element);
});

it("preselects the first result not picked yet: Enter ticks it and clears the search for the next one", async () => {
  show({ query: "a", picked: [options[0]], "onUpdate:query": (value: string) => wrapper!.setProps({ query: value }) });
  const field = wrapper!.get<HTMLInputElement>("input[type=search]");
  const enter = (init: KeyboardEventInit = {}) => {
    const event = new KeyboardEvent("keydown", { key: "Enter", bubbles: true, cancelable: true, ...init });
    field.element.dispatchEvent(event);
    return event;
  };
  expect(wrapper!.findAll("[data-first]").map(row => row.text())).toEqual([expect.stringContaining("Beta")]);
  expect(enter({ isComposing: true }).defaultPrevented).toBe(false);
  expect(enter({ shiftKey: true }).defaultPrevented).toBe(false);
  expect(wrapper!.emitted("toggle")).toBeUndefined();
  expect(enter().defaultPrevented).toBe(true);
  expect(wrapper!.emitted("toggle")).toEqual([[options[1]]]);
  expect(wrapper!.emitted("update:query")?.at(-1)).toEqual([""]);
});

it("does nothing on Enter while the owner still searches, with nothing found, or with nothing typed", async () => {
  show({ query: "al", loading: true });
  const field = wrapper!.get("input[type=search]");
  await field.trigger("keydown", { key: "Enter" });
  await wrapper!.setProps({ loading: false, options: [] });
  await field.trigger("keydown", { key: "Enter" });
  await wrapper!.setProps({ query: "", options });
  await field.trigger("keydown", { key: "Enter" });
  expect(wrapper!.emitted("toggle")).toBeUndefined();
  expect(wrapper!.emitted("confirm")).toBeUndefined();
});

it("asks to add what is ticked on Enter in the empty search (C, a name, Enter, Enter)", async () => {
  show({ query: "", picked: [options[0]] });
  await wrapper!.get("input[type=search]").trigger("keydown", { key: "Enter" });
  expect(wrapper!.emitted("confirm")).toHaveLength(1);
  expect(wrapper!.emitted("toggle")).toBeUndefined();
});

it("lets new results rise one after the other, the shared entrance (40 ms apart)", () => {
  show();
  expect(wrapper!.get(".pick-items").classes()).toContain("atlas-stagger");
});
