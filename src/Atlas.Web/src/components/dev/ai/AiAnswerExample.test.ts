import { afterEach, expect, it, vi } from "vitest";
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import { TooltipProvider } from "../../ui/tooltip";
import { defineComponent, h } from "vue";
import AiAnswerExample from "./AiAnswerExample.vue";

let wrapper: VueWrapper | undefined;
afterEach(() => { wrapper?.unmount(); wrapper = undefined; document.body.replaceChildren(); vi.useRealTimers(); delete (Element.prototype as Partial<Element>).animate; });

it("plays the omnisearch scene from the offer to a finished, cited answer and shows every state in both variants", async () => {
  vi.useFakeTimers();
  Element.prototype.animate = vi.fn(() => ({ finished: Promise.resolve(), cancel: vi.fn() }) as unknown as Animation) as unknown as Element["animate"];
  const fetch = vi.spyOn(globalThis, "fetch");
  wrapper = mount(defineComponent({ render: () => h(TooltipProvider, null, { default: () => h(AiAnswerExample) }) }), { attachTo: document.body });
  const scene = () => document.querySelector("[data-ai-scene] [data-ai-answer]")!;
  expect(scene().getAttribute("data-state")).toBe("offer");
  const seen: string[] = [];
  for (let step = 0; step < 40; step++) {
    await vi.advanceTimersByTimeAsync(250);
    const state = scene().getAttribute("data-state")!;
    if (seen.at(-1) !== state) seen.push(state);
  }
  await flushPromises();
  expect(seen).toEqual(["offer", "retrieving", "preparing", "done"]);
  expect(scene().querySelectorAll("[data-ai-sentence]")).toHaveLength(3);
  expect(scene().querySelectorAll("[data-source-chip]")).toHaveLength(3);

  // hovering a citation lights the matching result row of the scene
  document.querySelector("[data-ai-scene] [data-cite='2']")!.dispatchEvent(new MouseEvent("mouseenter"));
  await flushPromises();
  expect(document.querySelector("[data-result='2']")!.classList.contains("is-hl")).toBe(true);

  // every state of the shared model, in light and dark
  for (const mode of ["light", "dark"]) {
    const surface = document.querySelector(`.aia-surface[data-mode='${mode}']`)!;
    expect([...surface.querySelectorAll("[data-aia-state]")].map((cell) => cell.getAttribute("data-aia-state"))).toEqual(
      ["offer", "retrieving", "preparing", "done", "no_sources", "unavailable", "error", "limit", "withheld"]);
  }
  expect(document.querySelector("[data-ai-narrow] [data-variant='full']")).not.toBeNull();
  expect(fetch).not.toHaveBeenCalled();
});
