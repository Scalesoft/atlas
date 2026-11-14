import { afterEach, expect, it, vi } from "vitest";
import { mount, type VueWrapper } from "@vue/test-utils";
import RequirementsExample from "./RequirementsExample.vue";

let wrapper: VueWrapper | undefined;
afterEach(() => { wrapper?.unmount(); wrapper = undefined; document.body.replaceChildren(); });

it("shows requirement headings with their chips, the deployment summary over them, and why a task list is not enough", async () => {
  wrapper = mount(RequirementsExample, { attachTo: document.body });
  const editing = () => wrapper!.get("[data-example-requirements]");
  await vi.waitFor(() => expect(editing().findAll(".rq-chips")).toHaveLength(6));
  // The blocked reason is a chip of the heading line; SMS brána has only „Kde uloženo: …“ under it, which is no reason.
  expect(editing().findAll("[data-rq-chip=reason]").map(chip => [chip.element.closest("h3")!.dataset.headingId, chip.text()]))
    .toEqual([["uzis-test", "Blokuje: Žádost o testovací prostředí leží bez odpovědi; bez něj nejde napojení ověřit."]]);
  const summary = await vi.waitFor(() => editing().get("[data-requirement-summary-view]"));
  // One of the six is done.
  expect(summary.get("[data-summary-percent]").attributes("aria-valuenow")).toBe("16");
  // Without a summary block the same overview stands at the top, with the same numbers, and pins as a block.
  const auto = () => wrapper!.find("[data-example-requirements-auto] .atlas-requirement-auto-overview");
  await vi.waitFor(() => expect(auto().exists()).toBe(true));
  expect(auto().get("[data-summary-counts]").text()).toBe(summary.get("[data-summary-counts]").text());
  await auto().get("[data-summary-pin]").trigger("click");
  await vi.waitFor(() => expect(auto().exists()).toBe(false));
  expect(wrapper.findAll("[data-example-requirements-auto] [data-requirement-summary-view]")).toHaveLength(1);
  // Plan 3.4 R6: the deployment summary is the overview of kind requirements, the gallery lists the registered kinds.
  expect(wrapper.get("[data-example-overview-kinds]").text()).toContain("requirements (Stav nasazení, úrovně NONE · bez požadavků / S · jen nadpisy / M · s poznámkami)");
  expect(wrapper.text()).toContain("Proč ne seznam úkolů (taskList)");
});

it("opens the catalog's tree in the heading example, and shows the module checklist, the space's overview and waiting list on sample data", async () => {
  wrapper = mount(RequirementsExample, { attachTo: document.body });
  await vi.waitFor(() => expect(wrapper!.findAll("[data-example-section] .rq-chips")).toHaveLength(6));
  const popover = () => document.querySelector<HTMLElement>(".md-context-popover");
  const close = async () => { document.dispatchEvent(new KeyboardEvent("keydown", { key: "Escape", bubbles: true })); await vi.waitFor(() => expect(popover()).toBeNull()); };
  await wrapper.get("[data-example-open-catalog]").trigger("click");
  // The tree: the product, its three categories and a module under each (plan 3.5 C).
  await vi.waitFor(() => expect(popover()?.querySelectorAll("[data-requirement-catalog] [role=option]")).toHaveLength(7));
  await close();
  // „Nové nasazení“'s checklist: modules only, Space ticks one, the summary follows live.
  const picker = wrapper.get("[data-example-catalog-picker]");
  expect(picker.findAll("[role=option] .catalog-picker-title").map(title => title.text())).toEqual(["Portál", "Identita občana", "Mobilní aplikace"]);
  const query = picker.get<HTMLInputElement>("[data-catalog-query]");
  await query.setValue("identita");
  await query.trigger("keydown", { key: " " });
  expect(picker.get("[data-catalog-summary]").text()).toBe("Vloží se 4 požadavky ve 2 modulech, z toho 2 vstupy od zákazníka.");
  await vi.waitFor(() => expect(wrapper!.findAll("[data-example-overview] tbody tr").length).toBeGreaterThan(0));
  // „Co chybí“ first: PX2, all done, has no row.
  expect(wrapper.findAll("[data-example-overview] thead [data-group]").map(cell => cell.text())).toEqual(["Předání", "Portál pacienta › Základ"]);
  expect(wrapper.findAll("[data-example-overview] tbody th a").map(link => link.text())).toEqual(["Nemocnice Horní Lhota", "Nemocnice Dolní Ves"]);
  expect(wrapper.findAll("[data-example-waiting] [data-party]").map(group => group.get("h3").text())).toEqual(["IT nemocnice", "Jana Nováková"]);
});
