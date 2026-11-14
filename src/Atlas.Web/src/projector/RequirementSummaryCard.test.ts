import { afterEach, describe, expect, it, vi } from "vitest";
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import { overviewLevel, setOverviewLevel } from "../overview/overviewKinds";
import { setViewPrefs } from "./requirementViewMode";
import type { PageRequirement } from "./requirementModel";
import { requirementDefaults, type RequirementAttributes } from "./requirementTypes";
import RequirementSummaryCard from "./RequirementSummaryCard.vue";
import "./requirementsPlugin";

const item = (id: string, attrs: Partial<RequirementAttributes> = {}, ancestors: string[] = []): PageRequirement =>
  ({ id, title: id.toUpperCase(), attrs: { ...requirementDefaults, ...attrs }, ancestors });
const outline = { entries: [{ heading: "Core", level: 2 }, { block: "a" }, { heading: "NIA", level: 2 }, { block: "b" }], notes: new Map(), summary: false, blocks: 2 };

let wrapper: VueWrapper | undefined;
afterEach(() => { wrapper?.unmount(); wrapper = undefined; setOverviewLevel("requirements", "p1", "m"); setOverviewLevel("requirements", "board", "m"); localStorage.clear(); });
const card = (props: Record<string, unknown>) => (wrapper = mount(RequirementSummaryCard, { props: { show: () => {}, ...props } as never, attachTo: document.body }));

describe("the summary card's controls (only those that do something)", () => {
  it("offers „Doplnit prerekvizity“ only while one is missing, names it from the catalog and opens the detail of the block that needs it", async () => {
    const actions = [{ id: "fill-prerequisites", label: "Doplnit prerekvizity", run: () => {} }, { id: "compose-board", label: "Poskládat tabuli", run: () => {} }];
    const labels = () => wrapper!.findAll(".summary-head [data-overview-action]").map(button => button.text());
    card({ items: [item("a", { key: "a" }), item("b", { key: "b", requires: "a" })], actions });
    expect(labels()).toEqual(["Poskládat tabuli"]);
    wrapper!.unmount();
    const detail = vi.fn();
    card({ items: [item("b", { key: "b", requires: "vpn" })], actions, detail, catalogNames: async () => new Map([["vpn", "VPN přístup"]]) });
    await flushPromises();
    expect(labels()).toEqual(["Doplnit prerekvizity", "Poskládat tabuli"]);
    await wrapper!.find('[data-summary-part="lacking"]').trigger("click");
    const lacking = wrapper!.find("[data-summary-lacking-list] [data-go]");
    expect(lacking.text()).toBe("VPN přístup · 1×");
    await lacking.trigger("click");
    expect(detail).toHaveBeenCalledWith(expect.objectContaining({ id: "b" }));
  });

  it("keeps the menu's place until the menu arrives, so the level switch beside it does not move (UX open blink)", async () => {
    card({ items: [item("a")], viewPage: "p1", menuPlace: true });
    const head = () => [...wrapper!.get(".summary-actions").element.children].map(child => child.className);
    const before = head();
    expect(wrapper!.find("[data-summary-menu-place]").attributes("aria-hidden")).toBe("true");
    await wrapper!.setProps({ menu: () => {} } as never);
    expect(wrapper!.find("[data-summary-menu-place]").exists()).toBe(false);
    expect(wrapper!.find("[data-summary-menu]").exists()).toBe(true);
    // The same slots in the same order, the place taken by the menu itself.
    expect(head()).toEqual(before);
  });

  it("arrives as it stands when it is built hidden under the page it replaces (no count from zero, no fade)", async () => {
    const hidden = document.createElement("div");
    hidden.className = "editor-hydrating";
    document.body.append(hidden);
    const animate = vi.fn();
    Object.defineProperty(HTMLElement.prototype, "animate", { configurable: true, value: animate });
    try {
      wrapper = mount(RequirementSummaryCard, { props: { show: () => {}, items: [item("a", { state: "done" }), item("b")] } as never, attachTo: hidden });
      await flushPromises();
      expect(wrapper.get("[data-summary-percent]").text()).toMatch(/^50/);
      expect(animate).not.toHaveBeenCalled();
    } finally { Reflect.deleteProperty(HTMLElement.prototype, "animate"); wrapper?.unmount(); wrapper = undefined; hidden.remove(); }
  });

  it("offers the level wherever the page has a view, the order and filter only for more than one block", () => {
    card({ items: [item("a")], viewPage: "p1" });
    expect(wrapper!.find("[data-requirement-density-switch]").exists()).toBe(true);
    expect(wrapper!.find("[data-requirement-view-menus]").exists()).toBe(false);
    wrapper!.unmount();
    card({ items: [item("a"), item("b")], viewPage: "p1" });
    expect(wrapper!.find("[data-requirement-view-menus]").exists()).toBe(true);
  });

  it("has no switch, no menus and no shared choice without a page or on a catalog", async () => {
    card({ items: [item("a", { state: "blocked" }), item("b", { state: "waiting_for_input", party: "IT" })] });
    expect(wrapper!.find("[data-requirement-view-bar]").exists()).toBe(false);
    expect(wrapper!.find("[data-requirement-view-menus]").exists()).toBe(false);
    await wrapper!.find("[data-summary-all]").trigger("click");
    expect(localStorage.length).toBe(0);
    wrapper!.unmount();
    card({ items: [item("a"), item("b")], viewPage: "p1", catalog: true });
    expect(wrapper!.find("[data-requirement-view-bar]").exists()).toBe(false);
  });

  it("on NONE keeps the order and the filter (the sorted list is the way to the hidden requirements), and jumps at once without changing the level", async () => {
    setOverviewLevel("requirements", "p1", "none");
    // The jump lands the caret in the requirement, which pins it in view (requirementViewMode.ts): the reader's NONE stays.
    const show = vi.fn();
    wrapper = mount(RequirementSummaryCard, { props: { items: [item("a"), item("b"), item("c", {}, ["a"])], outline, viewPage: "p1", show } as never, attachTo: document.body });
    try {
      // The order and the filter stay: the order is the card's list, which shows the requirements NONE hides (owner 2026-11-04).
      expect((wrapper.find("[data-requirement-view-menus]").element as HTMLElement).style.visibility).toBe("");
      // The summary is as short as on the other levels: „Podle oddílů“ stays closed until it is opened.
      expect(wrapper.find("[data-summary-sections]").exists()).toBe(false);
      await wrapper.find("[data-summary-part=sections]").trigger("click");
      await flushPromises();
      await wrapper.find("[data-summary-section-show]").trigger("click");
      expect(show).toHaveBeenCalledOnce();
      expect(overviewLevel("requirements", "p1").value).toBe("none");
    } finally { wrapper.unmount(); wrapper = undefined; }
  });

  it("on a board shows its rows, chips and legend as text (no button that does nothing), without „Bez nadpisu“", async () => {
    setOverviewLevel("requirements", "board", "none");
    card({ items: [item("a", { state: "blocked" }), item("b", { state: "waiting_for_input", party: "IT" })], viewPage: "board", board: true });
    await flushPromises();
    expect(wrapper!.find("[data-summary-sections]").exists()).toBe(false);
    expect(wrapper!.findAll("[data-summary-counts] button")).toHaveLength(0);
    expect(wrapper!.findAll("[data-summary-counts] > span").length).toBeGreaterThan(0);
    await wrapper!.find("[data-summary-part=blocked]").trigger("click");
    expect(wrapper!.find(".summary-row").element.tagName).toBe("SPAN");
    await wrapper!.find("[data-summary-part=waiting]").trigger("click");
    expect(wrapper!.findAll("[data-summary-waiting] .summary-chips > *").every(chip => chip.element.tagName === "SPAN")).toBe(true);
    expect(wrapper!.findAll("[data-summary-waiting] button")).toHaveLength(0);
  });

  it("shows a block's own word for its state as an item of its own, counted in its state, and opens its blocks from the bar or the legend", async () => {
    const show = vi.fn();
    card({ show, items: [item("a", { state: "done" }), item("b", { state: "in_progress" }), item("c", { state: "in_progress", stateLabel: "test" }),
      item("d", { state: "in_progress", stateLabel: "test" }), item("e", { state: "waiting_for_input", stateLabel: "čeká na schválení" }), item("f"), item("g", { state: "not_applicable" })] });
    const legend = wrapper!.findAll("[data-summary-counts] > [data-go]");
    expect(legend.map(entry => entry.get(".summary-count-name").text())).toEqual(["hotovo", "rozpracováno", "test", "čeká na schválení", "neřešeno", "netýká se"]);
    expect(legend[2]!.text()).toContain("test 2");
    expect(legend[2]!.get(".summary-count-note").text()).toBe("jako rozpracováno");
    // The bar: a segment each, the word's hatched in its state's tone; what does not apply has none. The percent stays done of all that apply.
    expect(wrapper!.findAll("[data-summary-bar] > span").map(segment => [segment.attributes("data-tone"), segment.attributes("data-custom") !== undefined, segment.text()]))
      .toEqual([["done", false, "1"], ["in_progress", false, "1"], ["in_progress", true, "2"], ["waiting_for_input", true, "1"], ["planned", false, "1"]]);
    expect(wrapper!.get("[data-summary-percent]").attributes("aria-valuenow")).toBe("16");
    // The legend's item is the button for its segment: it opens the blocks under the legend, by party; a row jumps; one list at a time.
    expect(legend[2]!.attributes("aria-expanded")).toBe("false");
    await legend[2]!.trigger("click");
    expect(legend[2]!.attributes("aria-expanded")).toBe("true");
    expect(wrapper!.find("[data-summary-bar] [data-open]").attributes("data-custom")).toBeDefined();
    expect(wrapper!.findAll("[data-summary-state-list] [data-go]").map(row => row.text())).toEqual(["C", "D"]);
    await wrapper!.find("[data-summary-state-list] [data-go]").trigger("click");
    expect(show).toHaveBeenLastCalledWith(expect.objectContaining({ id: "c" }));
    await wrapper!.findAll("[data-summary-bar] > span")[0]!.trigger("click");
    expect(wrapper!.findAll("[data-summary-state-list] [data-go]").map(row => row.text())).toEqual(["A"]);
    expect(legend[0]!.attributes("aria-expanded")).toBe("true");
    expect(legend[2]!.attributes("aria-expanded")).toBe("false");
    await wrapper!.findAll("[data-summary-bar] > span")[0]!.trigger("click");
    expect(wrapper!.find("[data-summary-state-list]").exists()).toBe(false);
  });

  it("counts only the parties chosen in „Kdo“ and says so in the headline; the choices are every party of the page with its count", async () => {
    card({ items: [item("a", { state: "done", party: "Modelsoft" }), item("b", { party: "Modelsoft" }), item("c", { party: "IT KBN", state: "blocked" }), item("d")], viewPage: "p1" });
    setViewPrefs("p1", { parties: ["Modelsoft"] });
    await flushPromises();
    expect(wrapper!.get("[data-summary-headline]").text()).toBe("1 z 2 hotovo (50\u00a0%) · ještě nezačato 1 · jen Modelsoft");
    expect(wrapper!.find('[data-summary-part="blocked"]').exists()).toBe(false);
    expect(wrapper!.get("[data-requirement-party-filter]").text()).toContain("Kdo:");
    setViewPrefs("p1", { parties: [] });
  });

  it("says in its headline what blocks first, then done of all, what is being worked on and what has not started", () => {
    card({ items: [item("a", { state: "done" }), item("b", { state: "in_progress", stateLabel: "test" }), item("c", { state: "waiting_for_input" }), item("d"), item("e"), item("f", { state: "blocked" })] });
    expect(wrapper!.get("[data-summary-headline]").text()).toBe("Blokováno 1. 1 z 6 hotovo (16\u00a0%) · v práci 2 · ještě nezačato 2");
    wrapper!.unmount();
    card({ items: [item("a", { state: "done" }), item("b", { state: "not_applicable" })] });
    expect(wrapper!.get("[data-summary-headline]").text()).toBe("Vše hotovo · 1 z 1");
  });

  it("prints what a section waits for open", async () => {
    const article = document.createElement("article");
    article.className = "print-content";
    document.body.append(article);
    wrapper = mount(RequirementSummaryCard, { attachTo: article, props: { show: () => {},
      items: [item("a", { key: "a" }), item("b", { requires: "a" })], outline: { ...outline, entries: [{ heading: "Core", level: 2 }, { block: "a" }, { heading: "NIA", level: 2 }, { block: "b" }] } } });
    await flushPromises();
    expect(wrapper.find("[data-summary-prerequisites]").exists()).toBe(true);
    wrapper.unmount();
    wrapper = undefined;
    article.remove();
  });
});
