import { describe, expect, it } from "vitest";
import { mount } from "@vue/test-utils";
import { nextTick } from "vue";
import SearchableTags from "./SearchableTags.vue";

function mountCompact() {
  return mount(SearchableTags, {
    props: { options: [{ value: "jana", label: "Jana" }, { value: "petr", label: "Petr" }], label: "Kdo", compact: true },
    attachTo: document.body,
  });
}

describe("SearchableTags same names (P8)", () => {
  it("reads two people of one name apart, and Tab adds the first match and keeps the focus in the field", async () => {
    const wrapper = mount(SearchableTags, {
      props: { options: [{ value: "a", label: "Jan Novák" }, { value: "b", label: "Jan Novák" }, { value: "c", label: "Petr", description: "Provoz" }], label: "Kdo" },
      attachTo: document.body,
    });
    const input = wrapper.get("input");
    await input.setValue("jan");
    expect(wrapper.findAll("[data-suggestion]").map((pill) => pill.text())).toEqual(["Jan Novák1. z 2", "Jan Novák2. z 2"]);
    input.element.focus();
    const tab = new KeyboardEvent("keydown", { key: "Tab", bubbles: true, cancelable: true });
    input.element.dispatchEvent(tab);
    await nextTick();
    expect(wrapper.emitted("update:modelValue")).toEqual([[["a"]]]);
    expect(tab.defaultPrevented).toBe(true);
    expect(document.activeElement).toBe(input.element);
    wrapper.unmount();
  });

  // These pills show no description, and in an audit filter a count: neither may hide what tells two people apart.
  it("tells twins apart by their own description beside the badge, also on the chosen tag", async () => {
    const wrapper = mount(SearchableTags, {
      props: { options: [{ value: "a", label: "Jan Novák", description: "jan@a.cz", badge: "12" }, { value: "b", label: "Jan Novák", description: "jan@b.cz", badge: "3" }], modelValue: ["b"], label: "Kdo" },
      attachTo: document.body,
    });
    expect(wrapper.findAll("[data-suggestion]").map((pill) => pill.text())).toEqual(["Jan Novák12 · jan@a.cz"]);
    expect(wrapper.get('[data-tag="b"]').text()).toBe("Jan Novákjan@b.cz");
    expect(wrapper.get('[data-tag="b"]').attributes("aria-label")).toBe("Odebrat Jan Novák (jan@b.cz)");
    wrapper.unmount();
  });
});

describe("SearchableTags keys", () => {
  it("preselects the first match: Enter adds it, a composing or modified Enter does not, arrows walk the matches", async () => {
    const wrapper = mount(SearchableTags, {
      props: { options: [{ value: "jana", label: "Jana" }, { value: "jan", label: "Jan" }, { value: "petr", label: "Petr" }], label: "Kdo" },
      attachTo: document.body,
    });
    const input = wrapper.get("input");
    await input.setValue("ja");
    expect(wrapper.findAll("[data-first]").map((pill) => pill.attributes("data-suggestion"))).toEqual(["jana"]);
    await input.trigger("keydown", { key: "Enter", isComposing: true });
    await input.trigger("keydown", { key: "Enter", ctrlKey: true });
    expect(wrapper.emitted("update:modelValue")).toBeUndefined();
    await input.trigger("keydown", { key: "ArrowDown" });
    expect(document.activeElement).toBe(wrapper.get('[data-suggestion="jana"]').element);
    await wrapper.get('[data-suggestion="jana"]').trigger("keydown", { key: "ArrowRight" });
    expect(document.activeElement).toBe(wrapper.get('[data-suggestion="jan"]').element);
    await wrapper.get('[data-suggestion="jan"]').trigger("keydown", { key: "ArrowLeft" });
    await wrapper.get('[data-suggestion="jana"]').trigger("keydown", { key: "ArrowUp" });
    expect(document.activeElement).toBe(input.element);
    await input.trigger("keydown", { key: "Enter" });
    expect(wrapper.emitted("update:modelValue")).toEqual([[["jana"]]]);
    wrapper.unmount();
  });
});

describe("compact SearchableTags disclosure", () => {
  it("closes on outside focus while preserving the query, then resumes on focus", async () => {
    const wrapper = mountCompact();
    const input = wrapper.get("input");
    input.element.focus();
    await input.setValue("Jan");
    expect(wrapper.find('[data-suggestion="jana"]').exists()).toBe(true);
    const outside = document.createElement("button");
    document.body.append(outside);
    outside.focus();
    await nextTick();
    expect(wrapper.find('[role="group"]').exists()).toBe(false);
    expect(input.element.value).toBe("Jan");
    input.element.focus();
    await nextTick();
    expect(wrapper.find('[data-suggestion="jana"]').exists()).toBe(true);
    wrapper.unmount();
    outside.remove();
  });

  it("dismisses with Escape without selecting hidden results, and reopens when typing", async () => {
    const wrapper = mountCompact();
    const input = wrapper.get("input");
    input.element.focus();
    await input.setValue("Ja");
    wrapper.get<HTMLButtonElement>('[data-suggestion="jana"]').element.focus();
    let surroundingEscapes = 0;
    const host = wrapper.element.parentElement!;
    const onKey = (event: KeyboardEvent) => { if (event.key === "Escape") surroundingEscapes++; };
    host.addEventListener("keydown", onKey);
    await wrapper.get('[data-suggestion="jana"]').trigger("keydown", { key: "Escape" });
    expect(document.activeElement).toBe(input.element);
    expect(wrapper.find('[role="group"]').exists()).toBe(false);
    expect(input.element.value).toBe("Ja");
    expect(surroundingEscapes).toBe(0);
    await input.trigger("keydown", { key: "Enter" });
    expect(wrapper.emitted("update:modelValue")).toBeUndefined();
    // The first Esc in the field empties it and stays; the next one goes on to the surroundings (P3).
    await input.trigger("keydown", { key: "Escape" });
    expect(input.element.value).toBe("");
    expect(surroundingEscapes).toBe(0);
    await input.trigger("keydown", { key: "Escape" });
    expect(surroundingEscapes).toBe(1);
    await input.setValue("Jan");
    expect(wrapper.find('[data-suggestion="jana"]').exists()).toBe(true);
    host.removeEventListener("keydown", onKey);
    wrapper.unmount();
  });
});
