import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import { h, nextTick, reactive } from "vue";
import AiAnswer from "./AiAnswer.vue";
import { provenance, sentences, source, sources, withProviders } from "../dev/ai/aiSampleData";
import { aiUnavailableText } from "../../ai/aiAction";

let wrapper: VueWrapper | undefined;
let animations: { el: Element; frames: Keyframe[] }[] = [];
beforeEach(() => {
  animations = [];
  Element.prototype.animate = vi.fn(function (this: Element, frames: Keyframe[]) {
    animations.push({ el: this, frames });
    return { finished: Promise.resolve(), cancel: vi.fn() } as unknown as Animation;
  }) as unknown as Element["animate"];
});
afterEach(() => {
  wrapper?.unmount(); wrapper = undefined;
  document.body.replaceChildren();
  delete (Element.prototype as Partial<Element>).animate;
  vi.useRealTimers();
});

const mountAnswer = (props: Record<string, unknown>, slots: Record<string, () => unknown> = {}) => {
  wrapper = mount(withProviders(AiAnswer, { sentences, sources, provenance, ...props }, slots), { attachTo: document.body });
  return wrapper;
};
const glyphs = () => document.querySelectorAll("[data-ai-glyph]");

describe("AiAnswer states", () => {
  it("renders every state of the shared state model with its text and role", async () => {
    // offer: the dashed outline and the slot
    mountAnswer({ state: "offer", sentences: [], sources: [], provenance: null }, { offer: () => h("button", { "data-ask": "" }, "Zeptat se asistenta") });
    expect(document.querySelector("[data-ai-offer] [data-ask]")).not.toBeNull();
    expect(document.querySelector(".atlas-suggestion-outline")).not.toBeNull();
    expect(document.querySelector("[data-ai-answer]")!.getAttribute("data-state")).toBe("offer");
    wrapper!.unmount();

    // retrieving: the one status line (AiStatus): the card's glyph searches, the verb shimmers in a polite status, Zastavit
    mountAnswer({ state: "retrieving", sentences: [], sources: [], provenance: null });
    const retrieving = document.querySelector<HTMLElement>("[data-ai-status='retrieving']")!;
    expect(retrieving.querySelector("[role='status']")!.textContent).toContain("Hledám ve wiki…");
    expect(document.querySelector("[data-ai-answer] [data-ai-glyph][data-state='searching']")).not.toBeNull();
    expect(retrieving.querySelector(".atlas-ai-shimmer")).not.toBeNull();
    document.querySelector<HTMLButtonElement>("[data-ai-stop]")!.click();
    expect(wrapper!.findComponent(AiAnswer).emitted("stop")).toHaveLength(1);
    wrapper!.unmount();

    // preparing: the verb and what is known, the writing glyph and Zastavit
    mountAnswer({ state: "preparing", sentences: [], provenance: null });
    expect(document.querySelector("[data-ai-status='preparing'] [role='status']")!.textContent).toContain("Píšu odpověď · 3 zdroje");
    expect(document.querySelector("[data-ai-answer] [data-ai-glyph][data-state='writing']")).not.toBeNull();
    document.querySelector<HTMLButtonElement>("[data-ai-stop]")!.click();
    expect(wrapper!.findComponent(AiAnswer).emitted("stop")).toHaveLength(1);
    expect(document.querySelectorAll("[data-ai-sentence]")).toHaveLength(0);
    wrapper!.unmount();

    // the assistant (full) lists the steps beside its avatar glyph
    mountAnswer({ state: "preparing", variant: "full", sentences: [], provenance: null, scope: { kind: "page", pageId: "p" } });
    const steps = [...document.querySelectorAll("[data-ai-status='preparing'] [data-step]")].map((step) => [step.getAttribute("data-step"), step.textContent!.trim()]);
    expect(steps).toEqual([["done", "Čtu stránku3 zdroje"], ["now", "Píšu odpověď"]]);
    expect(document.querySelector("[data-ai-avatar] [data-ai-glyph][data-state='writing']")).not.toBeNull();
    expect(document.querySelector("[data-ai-status='preparing'] [data-ai-stop]")).not.toBeNull();
    wrapper!.unmount();

    // done: sentences, sources, provenance, feedback and the notice, and no loop
    mountAnswer({ state: "done" });
    expect(document.querySelectorAll("[data-ai-sentence]")).toHaveLength(3);
    expect(document.querySelector("[data-ai-provenance]")!.textContent).toContain("Firemní model");
    expect(document.querySelector("[data-ai-provenance]")!.textContent).toContain("3 zdroje");
    expect(document.querySelector("[data-ai-feedback]")).not.toBeNull();
    expect(document.body.textContent).toContain("Odpověď vytvořila AI z uvedených zdrojů. Ověřte důležité údaje.");
    expect(document.querySelector(".atlas-ai-shimmer, .atlas-ai-twinkle")).toBeNull();
    expect(document.querySelector("code")!.textContent).toBe("FortiClient");
    wrapper!.unmount();

    // no_sources: EmptyState with Zkusit jiný dotaz
    mountAnswer({ state: "no_sources", sentences: [], sources: [], provenance: null });
    expect(document.querySelector(".st-empty")!.getAttribute("role")).toBe("status");
    expect(document.querySelector(".st-empty")!.textContent).toContain("V dostupných stránkách odpověď není.");
    document.querySelector<HTMLButtonElement>("[data-ai-try-other]")!.click();
    expect(wrapper!.findComponent(AiAnswer).emitted()["try-other"]).toHaveLength(1);
    wrapper!.unmount();

    // error: an alert and Zkusit znovu
    mountAnswer({ state: "error" });
    expect(document.querySelector("[role='alert']")!.textContent).toContain("Odpověď se nepodařilo připravit.");
    [...document.querySelectorAll("button")].find((b) => b.textContent!.includes("Zkusit znovu"))!.click();
    expect(wrapper!.findComponent(AiAnswer).emitted("retry")).toHaveLength(1);
    wrapper!.unmount();

    // the text-only states
    for (const [state, text, marker] of [
      ["unavailable", aiUnavailableText(), "data-ai-unavailable"],
      ["limit", "Limit využití asistenta byl pro dnešek dosažen.", "data-ai-limit"],
      ["withheld", "Tato odpověď už není dostupná.", "data-ai-withheld"],
    ] as const) {
      mountAnswer({ state });
      const line = document.querySelector(`[${marker}]`)!;
      expect(line.textContent).toBe(text);
      expect(line.getAttribute("role")).toBe("status");
      expect(document.querySelectorAll("[data-ai-sentence], [data-ai-sources]")).toHaveLength(0);
      wrapper!.unmount();
    }
  });

  it("draws the AI glyph only while there is model output or a model task on screen", () => {
    for (const state of ["unavailable", "error", "limit", "withheld", "no_sources"] as const) {
      mountAnswer({ state, sentences: [], sources: [], provenance: null });
      expect(glyphs().length, state).toBe(0);
      wrapper!.unmount();
    }
    for (const state of ["offer", "retrieving", "preparing", "done"] as const) {
      mountAnswer({ state, sentences: state === "done" ? sentences : [], provenance: state === "done" ? provenance : null });
      expect(glyphs().length, state).toBeGreaterThan(0);
      wrapper!.unmount();
    }
  });

  it("is a card in compact and a plain message with a hidden heading in full", () => {
    mountAnswer({ state: "done", variant: "compact", headingLevel: 2 });
    expect(document.querySelector(".atlas-suggestion-outline")).not.toBeNull();
    expect(document.querySelector("h2")!.textContent).toContain("Odpověď z wiki");
    wrapper!.unmount();
    mountAnswer({ state: "done", variant: "full", headingLevel: 4, title: "Odpověď asistenta" });
    expect(document.querySelector(".atlas-suggestion-outline")).toBeNull();
    const heading = document.querySelector("h4")!;
    expect(heading.textContent).toBe("Odpověď asistenta");
    expect(heading.classList.contains("visually-hidden")).toBe(true);
  });
});

describe("AiAnswer content", () => {
  it("draws citations only from the sources list and never renders html", () => {
    mountAnswer({
      state: "done",
      sources: [source(1)],
      sentences: [{ text: "<b>Tučně</b> [7] a <img src=x onerror=alert(1)> podle [1].", sources: [1, 7] }],
    });
    // exactly one citation: the one that names a source; [7] and the numbers in the text are only text
    expect([...document.querySelectorAll("[data-cite]")].map((c) => c.getAttribute("data-cite"))).toEqual(["1"]);
    const sentence = document.querySelector("[data-ai-sentence]")!;
    expect(sentence.querySelector("b, img")).toBeNull();
    expect(sentence.textContent).toContain("<b>Tučně</b> [7] a <img src=x onerror=alert(1)> podle [1].");
    expect(document.querySelector("[data-cite='1']")!.getAttribute("aria-label")).toBe("Zdroj 1: Stránka 1");

    // a sentence that names no listed source has no citation at all
    wrapper!.unmount();
    mountAnswer({ state: "done", sources: [], sentences: [{ text: "Věta [1].", sources: [1] }] });
    expect(document.querySelectorAll("[data-cite]")).toHaveLength(0);
  });

  it("draws the quick overview as a lead sentence and points with bold key facts", () => {
    mountAnswer({ state: "done", format: "lead", sources: [source(1), source(2)], sentences: [
      { text: "Zakázka běží, je **z třetiny** hotová.", sources: [1] },
      { text: "Čeká se na **termín** od zákazníka.", sources: [2] },
      { text: "Chybí **DNS záznam**.", sources: [2] },
    ] });
    const items = [...document.querySelectorAll("ul.ai-answer-sentences > li[data-ai-sentence]")];
    expect(items).toHaveLength(3);
    expect(items.map((item) => item.classList.contains("is-lead"))).toEqual([true, false, false]);
    expect(items.map((item) => item.querySelector("strong")!.textContent)).toEqual(["z třetiny", "termín", "DNS záznam"]);
    expect(items[1]!.querySelector("[data-cite='2']")).not.toBeNull();
    expect(document.querySelector(".ai-answer-sentences")!.textContent).not.toContain("**");
  });

  it("lists the cited sources under a finished answer and leaves the uncited ones to its provenance", () => {
    mountAnswer({ state: "done", sources: [source(1), source(2), source(3), source(4)], sentences: [{ text: "Stav [3] a dodávka [4].", sources: [3, 4] }] });
    const chips = () => [...document.querySelectorAll(".ai-answer-sources [data-source-chip]")].map((chip) => chip.getAttribute("data-source-chip"));
    expect(chips()).toEqual(["3", "4"]);
    expect(document.querySelector("[data-ai-provenance]")!.textContent).toContain("4 zdroje");
    wrapper!.unmount();
    // an answer that cites nothing it was given keeps the whole list
    mountAnswer({ state: "done", sources: [source(1), source(2)], sentences: [{ text: "Věta.", sources: [] }] });
    expect(chips()).toEqual(["1", "2"]);
  });

  it("lights the sentence, citation and chip of a source on hover and reports it to the parent", async () => {
    mountAnswer({ state: "done" });
    const cite = document.querySelector<HTMLElement>("[data-cite='2']")!;
    cite.dispatchEvent(new MouseEvent("mouseenter"));
    await nextTick();
    expect([...document.querySelectorAll(".ai-sentence.is-hl")]).toHaveLength(2);
    expect(document.querySelector("[data-source-chip='2']")!.classList.contains("is-hl")).toBe(true);
    expect(document.querySelector("[data-source-chip='1']")!.classList.contains("is-hl")).toBe(false);
    expect(wrapper!.findComponent(AiAnswer).emitted("highlight")![0]).toEqual([2]);
    cite.dispatchEvent(new MouseEvent("mouseleave"));
    await nextTick();
    expect(document.querySelector(".ai-sentence.is-hl")).toBeNull();
    expect(wrapper!.findComponent(AiAnswer).emitted("highlight")!.at(-1)).toEqual([null]);
  });
});

describe("AiAnswer accessibility and motion", () => {
  it("keeps focus where it was and announces a finished answer once", async () => {
    const other = document.createElement("button");
    document.body.append(other);
    const live = () => document.querySelector("[data-ai-live]")!;
    const props = reactive<Record<string, unknown>>({ state: "preparing", sentences: [], sources, provenance, rating: null });
    wrapper = mount(withProviders(AiAnswer, props), { attachTo: document.body });
    other.focus();
    expect(document.activeElement).toBe(other);
    props.state = "done"; props.sentences = sentences;
    await flushPromises();
    expect(document.activeElement).toBe(other);
    expect(live().textContent).toBe("Odpověď je připravena");
    const changes: string[] = [];
    new MutationObserver(() => changes.push(live().textContent!)).observe(live(), { childList: true, characterData: true, subtree: true });
    props.rating = "up";
    await flushPromises();
    expect(changes).toEqual([]);
    expect(live().textContent).toBe("Odpověď je připravena");
    expect(document.querySelectorAll("[data-ai-live]")).toHaveLength(1);
  });

  it("reveals sentences over finished content and skips the flight with reduced motion", async () => {
    vi.useFakeTimers();
    const origin = document.createElement("div");
    origin.setAttribute("data-origin", "");
    document.body.append(origin);
    const run = async (reduced: boolean) => {
      animations = [];
      document.querySelectorAll("body > [data-atlas-motion]").forEach((ghost) => ghost.remove());
      const props = reactive<Record<string, unknown>>({ state: "retrieving", sources: [], sentences: [], provenance: null, sourceOrigin: () => origin });
      const host = document.createElement("div");
      if (reduced) host.setAttribute("data-atlas-reduced-motion", "");
      document.body.append(host);
      const view = mount(withProviders(AiAnswer, props), { attachTo: host });
      props.sources = sources; props.state = "preparing";
      await nextTick(); await nextTick();
      await vi.advanceTimersByTimeAsync(1200);
      const ghosts = animations.filter((a) => a.el.hasAttribute("data-atlas-motion")).length;
      animations = [];
      props.state = "done"; props.sentences = sentences; props.provenance = provenance;
      await nextTick(); await nextTick(); await flushPromises();
      const reveal = animations.filter((a) => a.frames.some((f) => "filter" in f || "backgroundColor" in f || "opacity" in f) && a.el.closest("[data-ai-body]"));
      const sentenceAnimations = animations.filter((a) => a.el.classList.contains("ai-sentence"));
      // the text is complete in the DOM before the first sentence moves
      expect(host.querySelectorAll("[data-ai-sentence]")).toHaveLength(3);
      view.unmount(); host.remove();
      return { ghosts, sentenceAnimations, reveal };
    };

    const normal = await run(false);
    expect(normal.ghosts).toBeGreaterThan(0);
    expect(normal.sentenceAnimations).toHaveLength(3);
    expect(normal.sentenceAnimations[0]!.frames[0]).toMatchObject({ filter: "blur(4px)" });

    const reduced = await run(true);
    expect(reduced.ghosts).toBe(0);
    // sentences still appear, but only as a fade
    expect(reduced.sentenceAnimations).toHaveLength(3);
    for (const animation of reduced.sentenceAnimations) for (const frame of animation.frames) expect(Object.keys(frame).filter((key) => key !== "offset")).toEqual(["opacity"]);
  });
});
it("finishes gathering sources before revealing a fast answer and keeps the full text available", async () => {
  vi.useFakeTimers();
  const origin = document.createElement("div"); document.body.append(origin);
  const props = reactive<Record<string, unknown>>({ state: "preparing", sources, sentences: [], sourceOrigin: () => origin });
  wrapper = mount(withProviders(AiAnswer, props), { attachTo: document.body });
  await nextTick(); await nextTick(); animations = [];
  props.state = "done"; props.sentences = sentences;
  await nextTick(); await nextTick();
  expect(document.querySelectorAll("[data-ai-sentence]")).toHaveLength(3);
  expect(animations.filter(a => a.el.classList.contains("ai-sentence"))).toHaveLength(0);
  await vi.advanceTimersByTimeAsync(900);
  expect(animations.filter(a => a.el.classList.contains("ai-sentence"))).toHaveLength(3);
});
