import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import SuggestionChip from "./SuggestionChip.vue";

// A suggestion is an unsaved value the person may use, dismiss or ask about (plan §4 A1). It never
// announces itself or takes focus, says its state in words, and moves only through aiMotion.
interface Call { el: Element; frames: Keyframe[]; options: KeyframeAnimationOptions }
let calls: Call[] = [];
let reduced = false;
let finish: () => void = () => {};

beforeEach(() => {
  calls = [];
  reduced = false;
  vi.mocked(window.matchMedia).mockImplementation((query: string) => ({
    matches: query.includes("prefers-reduced-motion") ? reduced : false,
    media: query,
    addEventListener: vi.fn(),
    removeEventListener: vi.fn(),
  }) as unknown as MediaQueryList);
  Element.prototype.animate = vi.fn(function (this: Element, frames: Keyframe[], options: KeyframeAnimationOptions) {
    calls.push({ el: this, frames, options });
    const finished = new Promise<void>((resolve) => { finish = resolve; });
    return { finished, cancel: vi.fn() } as unknown as Animation;
  }) as unknown as Element["animate"];
});

afterEach(() => {
  document.body.replaceChildren();
  delete (Element.prototype as Partial<Element>).animate;
});

const mountChip = (props: Record<string, unknown> = {}, slot = "IT a infrastruktura › Síť › VPN") =>
  mount(SuggestionChip, { props: { icon: "map-pin", ...props }, slots: { default: slot }, attachTo: document.body });

/** The accessible name of an element labelled by other elements (aria-labelledby), as a reader computes it. */
function accessibleName(wrapper: VueWrapper, selector: string) {
  const el = wrapper.get(selector).element;
  const ids = el.getAttribute("aria-labelledby");
  if (!ids) return el.getAttribute("aria-label") ?? el.textContent?.trim();
  return ids.split(/\s+/).map((id) => document.getElementById(id)?.textContent?.replace(/\s+/g, " ").trim()).join(" ");
}

describe("SuggestionChip", () => {
  it("renders the proposal with apply, dismiss and why actions and emits them", async () => {
    const chip = mountChip({ label: "Návrh umístění", reason: "Podobné stránky o VPN jsou v sekci Síť." });
    const group = chip.get('[role="group"]');
    expect(accessibleName(chip, '[role="group"]')).toBe("Návrh umístění: IT a infrastruktura › Síť › VPN");
    // A passive suggestion stays quiet: no live region, focus does not move to it.
    expect(chip.find("[aria-live]").exists()).toBe(false);
    expect(chip.find('[role="status"], [role="alert"]').exists()).toBe(false);
    expect(group.element.contains(document.activeElement)).toBe(false);
    // The dashed outline is drawn, not a border, so it can solidify.
    expect(chip.find("svg.atlas-suggestion-outline rect").exists()).toBe(true);

    expect(accessibleName(chip, "[data-suggestion-apply]")).toBe("Použít Návrh umístění: IT a infrastruktura › Síť › VPN");
    expect(accessibleName(chip, "[data-suggestion-dismiss]")).toBe("Zahodit Návrh umístění: IT a infrastruktura › Síť › VPN");
    expect(chip.findAll("button").every((button) => button.element.tagName === "BUTTON")).toBe(true);

    await chip.get("[data-suggestion-apply]").trigger("click");
    expect(chip.emitted("apply")).toHaveLength(1);

    await chip.get("[data-suggestion-why]").trigger("click");
    await flushPromises();
    expect(document.body.textContent).toContain("Podobné stránky o VPN jsou v sekci Síť.");
  });

  it("hides apply for an informational proposal and keeps dismiss", () => {
    const chip = mountChip({ applyLabel: null, layout: "panel", icon: "info", label: "Podobné stránky už existují. Pokračovat můžete i tak." }, "<p>Připojení k VPN</p>");
    expect(chip.find("[data-suggestion-apply]").exists()).toBe(false);
    expect(chip.find("[data-suggestion-why]").exists()).toBe(false);
    expect(chip.get("[data-suggestion-dismiss]").attributes("disabled")).toBeUndefined();
    expect(chip.get('[role="group"]').attributes("data-layout")).toBe("panel");
  });

  it("solidifies while applying and glows when applied", async () => {
    const chip = mountChip();
    const rect = () => chip.find("svg.atlas-suggestion-outline rect");
    calls = [];

    await chip.setProps({ state: "applying" });
    expect(calls.map((call) => call.el)).toEqual([rect().element]);
    expect(calls[0]!.frames.at(-1)).toMatchObject({ strokeDasharray: "11 0" });
    expect(chip.get("[data-suggestion-apply]").attributes()).toMatchObject({ disabled: "", "aria-busy": "true" });

    calls = [];
    await chip.setProps({ state: "applied" });
    expect(rect().exists()).toBe(false);
    const group = chip.get('[role="group"]').element;
    expect(calls.map((call) => call.el)).toEqual([group]);
    expect(calls[0]!.frames[0]).toMatchObject({ backgroundColor: "var(--atlas-ai-glow-color)" });
    // The state is said in words, not only by the outline and the glow.
    expect(chip.find("[data-suggestion-apply]").exists()).toBe(false);
    expect(chip.text()).toContain("Použito");
  });

  it("marks a stale proposal in text and disables apply", () => {
    const chip = mountChip({ state: "stale" });
    expect(chip.text()).toContain("Návrh už neodpovídá textu.");
    expect(chip.get("[data-suggestion-apply]").attributes("disabled")).toBeDefined();
    expect(chip.get("[data-suggestion-dismiss]").attributes("disabled")).toBeUndefined();
  });

  it("emits dismiss after the exit animation, which reduced motion turns into a short fade", async () => {
    const chip = mountChip();
    calls = [];
    await chip.get("[data-suggestion-dismiss]").trigger("click");
    const group = chip.get('[role="group"]').element;
    expect(calls.map((call) => [call.el, call.options.duration])).toEqual([[group, 160]]);
    expect(group.hasAttribute("inert")).toBe(true);
    expect(chip.emitted("dismiss")).toBeUndefined();
    finish();
    await flushPromises();
    expect(chip.emitted("dismiss")).toHaveLength(1);

    reduced = true;
    const calm = mountChip();
    calls = [];
    await calm.get("[data-suggestion-dismiss]").trigger("click");
    // Review 3.5.1, finding 12: the chip fades out (opacity only, no scale) instead of vanishing.
    expect(calls).toHaveLength(1);
    expect(calls[0]!.frames.every(frame => frame.transform === undefined)).toBe(true);
    expect(calls[0]!.frames.map(frame => frame.opacity)).toEqual([1, 0]);
    expect(calm.emitted("dismiss")).toBeUndefined();
    finish();
    await flushPromises();
    expect(calm.emitted("dismiss")).toHaveLength(1);
  });
});
