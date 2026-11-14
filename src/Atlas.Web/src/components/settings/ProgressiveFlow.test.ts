import { afterEach, expect, it, vi } from "vitest";
import { enableAutoUnmount, flushPromises, mount } from "@vue/test-utils";
import { REDUCED_MOTION_PREVIEW } from "../../motion/aiMotion";
import { defineComponent, h, ref } from "vue";
import ProgressiveFlow from "./ProgressiveFlow.vue";
import type { FlowStep } from "./types";

enableAutoUnmount(afterEach);
const steps = (kind = false, fields = false): FlowStep[] => [
  { id: "kind", label: "Druh", title: "Vyberte druh", ready: kind, summary: kind ? "SMB" : undefined },
  { id: "fields", label: "Údaje", title: "Doplňte údaje", ready: fields },
  { id: "summary", label: "Souhrn", title: "Co se uloží", manual: true, nextLabel: "Uložit" },
  { id: "done", label: "Hotovo", title: "Uloženo", final: true },
];

// U2-14: one phase (Nová tabule: name and place) is a form with its button, not a stepper of one step.
it("shows no progress for a single step", async () => {
  const wrapper = mount(ProgressiveFlow, { props: { modelValue: "details", label: "Nová tabule", steps: [{ id: "details", label: "Název", title: "Pojmenujte tabuli", manual: true, nextLabel: "Vytvořit tabuli" }] },
    slots: { details: '<input aria-label="Název" />' }, attachTo: document.body });
  expect(wrapper.find("[data-step-chip]").exists()).toBe(false);
  expect(wrapper.find("ol").exists()).toBe(false);
  expect(wrapper.get("[data-flow-next]").text()).toBe("Vytvořit tabuli");
});

it("reveals valid dependencies without navigation or submission and chips only focus", async () => {
  const wrapper = mount(ProgressiveFlow, { props: { modelValue: "kind", label: "Ukázka", steps: steps() }, slots: { kind: '<input aria-label="Druh" />', fields: '<input aria-label="Údaje" />', summary: "Shrnutí" }, attachTo: document.body });
  expect(wrapper.find('[data-step="fields"]').exists()).toBe(false);
  expect(wrapper.text()).toContain("Údaje");
  expect(wrapper.find("[data-flow-next]").exists()).toBe(false);
  await wrapper.setProps({ steps: steps(true) });
  await flushPromises();
  expect(wrapper.find('[data-step="fields"]').exists()).toBe(true);
  expect(wrapper.find('[data-step="kind"]').exists()).toBe(true);
  await wrapper.setProps({ steps: steps(true, true) });
  await flushPromises();
  expect(wrapper.get('[data-flow-next]').text()).toBe("Uložit");
  expect(wrapper.emitted("next")).toBeUndefined();
  const updates = wrapper.emitted("update:modelValue")?.length;
  await wrapper.get('[data-step-chip="kind"]').trigger("click");
  expect(wrapper.emitted("update:modelValue")?.length).toBe(updates);
  expect(document.activeElement).toBe(wrapper.get('[data-step="kind"]').element);
  await wrapper.get('[data-flow-next]').trigger("click");
  expect(wrapper.emitted("next")?.[0]?.[0]).toMatchObject({ id: "summary" });
});

it("keeps revealed summary but prevents stale submission after an earlier input becomes invalid", async () => {
  const wrapper = mount(ProgressiveFlow, { props: { modelValue: "kind", label: "Ukázka", steps: steps(true, true) }, slots: { summary: '<button type="button">Potvrdit</button>' } });
  await wrapper.setProps({ steps: steps(true, false) });
  expect(wrapper.find('[data-step="summary"]').exists()).toBe(true);
  expect(wrapper.get('[data-step="summary"] fieldset').attributes("disabled")).toBeDefined();
  expect(wrapper.get('[data-step="summary"]').text()).toContain("Údaje");
  await wrapper.get('[data-flow-next]').trigger("click");
  expect(wrapper.emitted("next")).toBeUndefined();
  await wrapper.setProps({ modelValue: "done" });
  expect(wrapper.find('[data-step="kind"]').exists()).toBe(false);
  expect(wrapper.get('[data-step="done"]').text()).toContain("Uloženo");
});

it("keeps explicit operations as boundaries and reveals their result only when completed", async () => {
  const input: FlowStep = { id: "source", label: "Zdroj", title: "Zdroj", ready: true, manual: true, nextLabel: "Připravit náhled" };
  const review: FlowStep = { id: "review", label: "Náhled", title: "Náhled", ready: false, manual: true, nextLabel: "Importovat" };
  const wrapper = mount(ProgressiveFlow, { props: { modelValue: "source", label: "Import", steps: [input, review] } });
  await wrapper.get('[data-flow-next]').trigger("click");
  expect(wrapper.find('[data-step="review"]').exists()).toBe(false);
  await wrapper.setProps({ steps: [{ ...input, complete: true }, review], modelValue: "review" });
  await flushPromises();
  expect(wrapper.find('[data-step="review"]').exists()).toBe(true);
  // Not ready: said to be unavailable, yet in the Tab order (UX cycle 1, SH-11).
  expect(wrapper.get('[data-flow-next]').attributes("aria-disabled")).toBe("true");
  expect(wrapper.get('[data-flow-next]').attributes("disabled")).toBeUndefined();
});

it("leads a press on a step that is not ready to its first invalid field instead of going on (UX cycle 1, SH-11)", async () => {
  const step: FlowStep = { id: "details", label: "Název", title: "Název", ready: false, manual: true, nextLabel: "Vytvořit" };
  const wrapper = mount(ProgressiveFlow, { props: { modelValue: "details", label: "Nová stránka", steps: [step] }, slots: { details: '<input name="title" required />' }, attachTo: document.body });
  await wrapper.get("[data-flow-next]").trigger("click");
  expect(wrapper.emitted("next")).toBeUndefined();
  expect(document.activeElement).toBe(wrapper.get('input[name="title"]').element);
  wrapper.unmount();
});

// A default mount stubs <Transition> and <TransitionGroup>, so the cases above never run the disclosure hooks. The cases below
// mount the flow with the real transitions and a recorded Element.animate: they see which children are given a fold or an
// unfold, in which flush, and what is still in the document afterwards. What the motion looks like is the browser's to show.
afterEach(() => { Reflect.deleteProperty(Element.prototype, "animate"); document.documentElement.removeAttribute(REDUCED_MOTION_PREVIEW); vi.restoreAllMocks(); });
function motion() {
  const runs: Array<{ node: Element; frames: Keyframe[]; animation: EventTarget & { cancel: ReturnType<typeof vi.fn> } }> = [];
  Object.defineProperty(Element.prototype, "animate", { configurable: true, writable: true, value(this: Element, frames: Keyframe[]) {
    const animation = Object.assign(new EventTarget(), { cancel: vi.fn() });
    runs.push({ node: this, frames, animation });
    return animation as unknown as Animation;
  } });
  const ends = (node: Element, opacity: number) => runs.some((run) => run.node === node && run.frames.at(-1)?.opacity === opacity && run.frames.some((frame) => frame.height === "0px"));
  return {
    folds: (node: Element) => ends(node, 0),
    unfolds: (node: Element) => ends(node, 1),
    // Reduced motion: an animation of the opacity alone, and a child kept at no height (every frame) until that is cancelled.
    fades: (node: Element, to: number) => runs.some((run) => run.node === node && run.frames.at(-1)?.opacity === to && run.frames.every((frame) => !("height" in frame))),
    holds: (node: Element) => runs.some((run) => run.node === node && run.frames.every((frame) => frame.height === "0px") && !run.animation.cancel.mock.calls.length),
    folded: () => [...new Set(runs.map((run) => run.node))].filter((node) => ends(node, 0)),
    finish: async () => { for (const run of [...runs]) run.animation.dispatchEvent(new Event("finish")); await flushPromises(); },
  };
}
const live = (list: FlowStep[]) => mount(ProgressiveFlow, { attachTo: document.body, props: { modelValue: "kind", label: "Ukázka", steps: list }, slots: { summary: "Shrnutí", done: "Hotovo" }, global: { stubs: { Transition: false, TransitionGroup: false } } });
const form = (wrapper: ReturnType<typeof live>) => [...wrapper.element.children].filter((child) => child.matches(".inline-reveal-root, .progressive-actions"));

it("folds the form away while the final step unfolds and removes the steps when their fold ends", async () => {
  const recorded = motion();
  const wrapper = live(steps(true, true));
  await flushPromises();
  const leaving = form(wrapper);
  expect(leaving).toHaveLength(4);
  await wrapper.setProps({ modelValue: "done" });
  await flushPromises();
  // One flush started both halves: no animation has ended, and the form is still above the final step, out of use.
  const done = wrapper.get('[data-step="done"]').element;
  expect(leaving.every(recorded.folds)).toBe(true);
  expect(recorded.unfolds(done)).toBe(true);
  expect(leaving.every((node) => node.isConnected && node.hasAttribute("inert") && node.getAttribute("aria-hidden") === "true")).toBe(true);
  expect(leaving.at(-1)!.compareDocumentPosition(done) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
  await recorded.finish();
  expect(leaving.some((node) => node.isConnected)).toBe(false);
  expect(wrapper.findAll("[data-step]").map((step) => step.attributes("data-step"))).toEqual(["done"]);
  expect(wrapper.find("[inert]").exists()).toBe(false);
});

it("unfolds a step that joins the list midway and folds it before it goes", async () => {
  const recorded = motion();
  const wrapper = live(steps(true));
  await flushPromises();
  const list = steps(true);
  await wrapper.setProps({ steps: [list[0]!, { id: "extra", label: "Navíc", title: "Doplňte navíc", ready: false }, ...list.slice(1)] });
  await flushPromises();
  const joined = wrapper.get('[data-step="extra"]').element.closest(".inline-reveal-root")!;
  expect(recorded.unfolds(joined)).toBe(true);
  await recorded.finish();
  await wrapper.setProps({ steps: steps(true) });
  await flushPromises();
  expect(joined.isConnected && joined.hasAttribute("inert")).toBe(true);
  expect(recorded.folds(joined)).toBe(true);
  await recorded.finish();
  expect(joined.isConnected).toBe(false);
});

it("leaves nothing inert or folded when the flow goes back or is unmounted during the fold", async () => {
  const recorded = motion();
  const wrapper = live(steps(true, true));
  await flushPromises();
  await wrapper.setProps({ modelValue: "done" });
  await flushPromises();
  // Back before the fold has ended: the leaving copies go at once and the form that returns is in use.
  await wrapper.setProps({ modelValue: "summary" });
  await flushPromises();
  expect(wrapper.findAll('[data-step="kind"]')).toHaveLength(1);
  expect(wrapper.get("[data-flow-next]").element.closest("[inert]")).toBeNull();
  await recorded.finish();
  expect(wrapper.findAll("[data-step]").map((step) => step.attributes("data-step"))).toEqual(["kind", "fields", "summary"]);
  expect(wrapper.find("[inert]").exists()).toBe(false);
  // A fold keeps its last frame, so an element that was given one must not stay in the document.
  expect(recorded.folded().some((node) => node.isConnected)).toBe(false);
  await wrapper.setProps({ modelValue: "done" });
  await flushPromises();
  wrapper.unmount();
  await recorded.finish();
  expect(document.body.querySelector("[data-step], [inert]")).toBeNull();
});

// Reduced motion keeps the opacity and drops the height, so a step that unfolded beside the folding form would stand under it
// at full height until the form is removed: the flow grew by the step and then lost the form in one frame. The halves take turns.
it("under reduced motion fades the form out before the final step takes its room", async () => {
  document.documentElement.setAttribute(REDUCED_MOTION_PREVIEW, "");
  const recorded = motion();
  const wrapper = live(steps(true, true));
  await flushPromises();
  const leaving = form(wrapper);
  await wrapper.setProps({ modelValue: "done" });
  await flushPromises();
  // The final step is in the document at once, held at no height: the flow is as high as the form that fades in it.
  const done = wrapper.get('[data-step="done"]').element;
  expect(leaving.every((node) => node.isConnected && recorded.fades(node, 0))).toBe(true);
  expect(recorded.holds(done)).toBe(true);
  expect(recorded.fades(done, 1)).toBe(false);
  await recorded.finish();
  // The form is gone and the final step has its height back, fading in.
  expect(leaving.some((node) => node.isConnected)).toBe(false);
  expect(recorded.holds(done)).toBe(false);
  expect(recorded.fades(done, 1)).toBe(true);
});

it("under reduced motion removes a final step that never got its turn without showing it", async () => {
  document.documentElement.setAttribute(REDUCED_MOTION_PREVIEW, "");
  const recorded = motion();
  const wrapper = live(steps(true, true));
  await flushPromises();
  await wrapper.setProps({ modelValue: "done" });
  await flushPromises();
  const done = wrapper.get('[data-step="done"]').element;
  await wrapper.setProps({ modelValue: "summary" });
  await flushPromises();
  expect(done.isConnected).toBe(false);
  expect(recorded.fades(done, 1) || recorded.fades(done, 0)).toBe(false);
  // The form that returned waits for nothing: its leaving copies went with the final step.
  expect(form(wrapper)).toHaveLength(4);
  expect(form(wrapper).every((node) => recorded.fades(node, 1) && !recorded.holds(node))).toBe(true);
  expect(wrapper.find("[inert]").exists()).toBe(false);
});

it("under reduced motion opens a closed step only after the step that left beside it has faded", async () => {
  document.documentElement.setAttribute(REDUCED_MOTION_PREVIEW, "");
  // jsdom lays nothing out: a step has a height when its reveal is shown.
  vi.spyOn(HTMLElement.prototype, "offsetHeight", "get").mockImplementation(function (this: HTMLElement) { return this.querySelector("[data-inline-reveal]:not([hidden])") ? 200 : 0; });
  const recorded = motion();
  const list = steps(true);
  const wrapper = live([list[0]!, { id: "extra", label: "Navíc", title: "Doplňte navíc", ready: false }, ...list.slice(1)]);
  await flushPromises();
  const root = (id: string) => wrapper.get(`[data-step="${id}"]`).element.closest(".inline-reveal-root")!;
  const extra = root("extra");
  expect(wrapper.find('[data-step="fields"]').exists()).toBe(false);
  // Without „Navíc“ the next step is „Údaje“: it opens in the patch that makes „Navíc“ leave.
  await wrapper.setProps({ steps: list });
  await flushPromises();
  const fields = root("fields");
  expect(extra.isConnected && recorded.fades(extra, 0)).toBe(true);
  expect(recorded.holds(fields)).toBe(true);
  expect(recorded.holds(root("kind"))).toBe(false);
  await recorded.finish();
  expect(extra.isConnected).toBe(false);
  expect(recorded.holds(fields)).toBe(false);
  expect(recorded.fades(fields, 1)).toBe(true);
});

it("measures nothing when its owner re-renders it on a keystroke", async () => {
  const wrapper = live(steps(true, true));
  await flushPromises();
  const reads = [vi.spyOn(Element.prototype, "getBoundingClientRect"), vi.spyOn(Node.prototype, "cloneNode"), vi.spyOn(window, "getComputedStyle")];
  const typed = steps(true, true);
  typed[0]!.summary = "SMB 2";
  await wrapper.setProps({ steps: typed });
  await flushPromises();
  expect(wrapper.text()).toContain("SMB 2");
  expect(reads.map((read) => read.mock.calls.length)).toEqual([0, 0, 0]);
});

// UX4-4: Esc or Zrušit closes the flow and focus goes back to the button that opened it, not to the page heading.
it("returns focus to its opener when cancelled", async () => {
  const Host = defineComponent({ setup() {
    const open = ref(false);
    return () => h("div", [h("button", { id: "opener", onClick: () => (open.value = true) }, "Pozvat"),
      open.value ? h(ProgressiveFlow, { modelValue: "kind", label: "Pozvat", steps: steps(), autofocus: true, onCancel: () => (open.value = false) }, { kind: () => h("input", { "aria-label": "Druh" }) }) : null]);
  } });
  const wrapper = mount(Host, { attachTo: document.body });
  const opener = wrapper.get("#opener").element as HTMLElement;
  opener.focus();
  await opener.click();
  await flushPromises();
  await wrapper.get("[data-flow-cancel]").trigger("click");
  await flushPromises();
  expect(wrapper.find(".progressive-flow").exists()).toBe(false);
  expect(document.activeElement).toBe(opener);
});
