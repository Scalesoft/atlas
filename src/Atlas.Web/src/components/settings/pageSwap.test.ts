import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { enableAutoUnmount, flushPromises, mount } from "@vue/test-utils";
import { defineComponent, h, ref } from "vue";
import { usePageSwap } from "./pageSwap";

enableAutoUnmount(afterEach);

const key = ref("a");
let hooks!: ReturnType<typeof usePageSwap>;
const Host = defineComponent({ setup() { hooks = usePageSwap(key); return () => h("div"); } });
let scrollTop = 0;
const scrollTo = vi.fn();
const original = { scrollTo: window.scrollTo, scrolling: Object.getOwnPropertyDescriptor(Document.prototype, "scrollingElement") };

beforeEach(() => {
  key.value = "a";
  scrollTop = 0;
  scrollTo.mockClear();
  window.scrollTo = scrollTo as unknown as typeof window.scrollTo;
  Object.defineProperty(document, "scrollingElement", { configurable: true, get: () => ({ get scrollTop() { return scrollTop; } }) });
});
afterEach(() => {
  window.scrollTo = original.scrollTo;
  delete (document as unknown as Record<string, unknown>).scrollingElement;
});

describe("usePageSwap", () => {
  it("starts the new page at the top and leaves the old one where the reader saw it, in one swap", async () => {
    mount(Host);
    scrollTop = 600;
    key.value = "b"; // the address changes: the Transition now runs beforeLeave of the old page, then beforeEnter of the new one
    const old = document.createElement("section");
    hooks.beforeLeave(old);
    expect(old.style.translate).toBe("0px -600px");
    expect(old.hasAttribute("inert")).toBe(true);
    expect(old.getAttribute("aria-hidden")).toBe("true");
    hooks.beforeEnter();
    expect(scrollTo).toHaveBeenCalledTimes(1);
    expect(scrollTo).toHaveBeenCalledWith({ top: 0, behavior: "instant" });
  });

  it("does not touch the scroll when a skeleton is replaced by its own page, or for the first page of a visit", async () => {
    mount(Host);
    scrollTop = 80;
    hooks.beforeEnter(); // the first page: no swap
    expect(scrollTo).not.toHaveBeenCalled();
    key.value = "b";
    hooks.beforeLeave(document.createElement("section"));
    hooks.beforeEnter(); // the swap: the skeleton of the new page arrives
    expect(scrollTo).toHaveBeenCalledTimes(1);
    // The reader scrolls, the chunk arrives: the skeleton leaves and the page enters with the same hooks, and nothing is moved.
    scrollTop = 200;
    const skeleton = document.createElement("div");
    hooks.beforeLeave(skeleton);
    hooks.beforeEnter();
    expect(skeleton.style.translate).toBe("");
    expect(skeleton.hasAttribute("inert")).toBe(true);
    expect(scrollTo).toHaveBeenCalledTimes(1);
  });

  it("forgets a change of the address that did not swap a page", async () => {
    mount(Host);
    key.value = "b"; // a sub-page inside the same page: nothing leaves, nothing enters
    await flushPromises();
    scrollTop = 300;
    hooks.beforeEnter(); // a later page that is only now replacing a skeleton
    expect(scrollTo).not.toHaveBeenCalled();
  });
});
