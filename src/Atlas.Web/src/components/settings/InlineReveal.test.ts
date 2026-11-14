import { afterEach, expect, it, vi } from "vitest";
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import InlineReveal from "./InlineReveal.vue";
import ChoiceCards from "./ChoiceCards.vue";

let wrapper: VueWrapper | undefined;
afterEach(() => { wrapper?.unmount(); document.body.replaceChildren(); vi.restoreAllMocks(); vi.unstubAllGlobals(); });

function animations() {
  const created: Array<EventTarget & { cancel: ReturnType<typeof vi.fn> }> = [];
  const animate = vi.fn(() => {
    const next = Object.assign(new EventTarget(), { cancel: vi.fn() });
    created.push(next);
    return next as unknown as Animation;
  });
  vi.stubGlobal("ResizeObserver", class { observe() {} disconnect() {} });
  Object.defineProperty(Element.prototype, "animate", { configurable: true, writable: true, value: animate });
  return { created, animate, cleanup: () => Reflect.deleteProperty(Element.prototype, "animate") };
}

it("reverses at the displayed height and opacity, ignores stale completion, then releases the clip", async () => {
  const motion = animations();
  try {
    wrapper = mount(InlineReveal, { attachTo: document.body, props: { open: false }, slots: { default: '<input aria-label="Detail" />' } });
    await wrapper.setProps({ open: true }); await flushPromises();
    const node = wrapper.get<HTMLElement>("[data-inline-reveal]").element;
    vi.spyOn(node, "getBoundingClientRect").mockReturnValue(new DOMRect(0, 0, 400, 42));
    node.style.opacity = ".4";
    await wrapper.setProps({ open: false }); await flushPromises();
    expect((motion.animate.mock.calls[1] as unknown as [Keyframe[]])[0][0]).toMatchObject({ height: "42px", opacity: "0.4" });
    expect(node.hasAttribute("inert")).toBe(true);
    await wrapper.setProps({ open: true }); await flushPromises();
    expect((motion.animate.mock.calls[2] as unknown as [Keyframe[]])[0][0]).toMatchObject({ height: "42px", opacity: "0.4" });
    motion.created[1]!.dispatchEvent(new Event("finish")); await flushPromises();
    expect(wrapper.find("input").exists()).toBe(true);
    expect(node.hasAttribute("inert")).toBe(false);
    expect(node.classList.contains("is-settled")).toBe(false);
    motion.created[2]!.dispatchEvent(new Event("finish")); await flushPromises();
    expect(node.classList.contains("is-settled")).toBe(true);
  } finally { motion.cleanup(); }
});

it("retargets an in-progress opening when nested or loaded content grows", async () => {
  const motion = animations();
  let resize: () => void = () => {};
  vi.stubGlobal("ResizeObserver", class { constructor(callback: () => void) { resize = callback; } observe() {} disconnect() {} });
  try {
    wrapper = mount(InlineReveal, { props: { open: false, keepMounted: true }, slots: { default: "<p>Async result</p>" } });
    await flushPromises();
    const inner = wrapper.get<HTMLElement>(".inline-reveal-inner").element;
    const height = vi.spyOn(inner, "getBoundingClientRect").mockReturnValue(new DOMRect(0, 0, 400, 80));
    await wrapper.setProps({ open: true }); await flushPromises();
    height.mockReturnValue(new DOMRect(0, 0, 400, 180));
    resize();
    expect(motion.created[0]!.cancel).toHaveBeenCalled();
    expect((motion.animate.mock.calls[1] as unknown as [Keyframe[]])[0][1]).toMatchObject({ height: "180px", opacity: 1 });
  } finally { motion.cleanup(); }
});

it("keeps draft fields mounted while closed, restores the trigger, and uses only fade with reduced motion", async () => {
  const motion = animations();
  try {
    const trigger = document.createElement("button");
    trigger.setAttribute("aria-controls", "detail");
    document.body.append(trigger);
    document.body.setAttribute("data-atlas-reduced-motion", "");
    wrapper = mount(InlineReveal, { attachTo: document.body, props: { id: "detail", open: true, keepMounted: true }, slots: { default: "<input />" } });
    const input = wrapper.get<HTMLInputElement>("input").element;
    input.value = "Unsaved draft"; input.focus();
    await wrapper.setProps({ open: false }); await flushPromises();
    expect(document.activeElement).toBe(trigger);
    expect((motion.animate.mock.calls[0] as unknown as [Keyframe[]])[0]).toEqual([{ opacity: "1" }, { opacity: 0 }]);
    motion.created[0]!.dispatchEvent(new Event("finish")); await flushPromises();
    expect(wrapper.get("[data-inline-reveal]").attributes("hidden")).toBe("until-found");
    expect(wrapper.get<HTMLInputElement>("input").element.value).toBe("Unsaved draft");
  } finally { document.body.removeAttribute("data-atlas-reduced-motion"); motion.cleanup(); }
});

it("restores a choice controller even when its parent removes aria-controls while collapsing", async () => {
  wrapper = mount({
    components: { ChoiceCards },
    data: () => ({ choice: "custom", options: [{ value: "default", label: "Default" }, { value: "custom", label: "Custom" }] }),
    template: '<ChoiceCards v-model="choice" label="Mode" :options="options" :reveal-for="[\'custom\']"><template #reveal><input aria-label="Custom detail" /></template></ChoiceCards>',
  }, { attachTo: document.body });
  await flushPromises();
  const original = wrapper.get('[aria-controls]').element;
  wrapper.get<HTMLInputElement>('input[aria-label="Custom detail"]').element.focus();
  await wrapper.setData({ choice: "default" }); await flushPromises();
  expect(original.hasAttribute("aria-controls")).toBe(false);
  expect(document.activeElement).toBe(original);
});
