import { afterEach, describe, expect, it, vi } from "vitest";
import { mount, type VueWrapper } from "@vue/test-utils";
import { toast } from "vue-sonner";
import InteractionRulesExample from "./InteractionRulesExample.vue";
import { interactionDeviations, interactionSharedParts } from "../../contracts/interaction";
import { readFileSync } from "node:fs";

const states = readFileSync("../Atlas.Markdown/src/styles/states.css", "utf8") // vitest runs in src/Atlas.Web;

vi.mock("vue-sonner", () => ({ toast: Object.assign(vi.fn(), { success: vi.fn(), error: vi.fn(), warning: vi.fn(), dismiss: vi.fn() }) }));

let wrapper: VueWrapper | undefined;
afterEach(() => { wrapper?.unmount(); wrapper = undefined; vi.clearAllMocks(); });

describe("InteractionRulesExample", () => {
  it("lists the rules P1–P17 and every deviation the contract suites accept, by area", async () => {
    wrapper = mount(InteractionRulesExample, { attachTo: document.body });
    expect(wrapper.findAll(".rules-id").map(id => id.text())).toEqual(Array.from({ length: 17 }, (_, index) => `P${index + 1}`));
    const rows = Object.values(interactionDeviations).flat();
    expect(rows.length).toBeGreaterThan(0);
    expect(wrapper.findAll(".rules-table tbody tr")).toHaveLength(rows.length);
    await wrapper.get("input[type=search]").setValue(rows[0]!.element);
    expect(wrapper.findAll(".rules-table tbody tr").length).toBeLessThan(rows.length);
  });
  it("shows the one notification path with „Zpět“", async () => {
    wrapper = mount(InteractionRulesExample, { attachTo: document.body });
    const button = wrapper.findAll("button").find(candidate => candidate.text().startsWith("Smazat ukázku"))!;
    await button.trigger("click");
    const options = vi.mocked(toast.success).mock.calls[0]![1] as { action: { label: string; onClick: () => void } };
    expect(options.action.label).toBe("Zpět");
    // P7: the control stays the focused element (it becomes „Obnovit“), the keyboard never falls to <body>.
    (button.element as HTMLElement).focus();
    expect(button.text()).toBe("Obnovit ukázku");
    expect(document.activeElement).toBe(button.element);
    options.action.onClick();
    await wrapper.vm.$nextTick();
    expect(button.text().startsWith("Smazat ukázku")).toBe(true);
  });
  it("names only state tokens that states.css defines", () => {
    const text = interactionSharedParts.find(part => part.name === "Tokeny stavů")!.text;
    const names = text.match(/--state-[a-z-]+/g) ?? [];
    expect(names.length).toBeGreaterThan(0);
    for (const name of names) expect(states).toContain(`${name}:`);
  });
});
