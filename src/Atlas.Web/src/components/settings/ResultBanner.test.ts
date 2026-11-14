import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { enableAutoUnmount, flushPromises, mount } from "@vue/test-utils";
import { defineComponent, h, nextTick, ref } from "vue";
import ResultBanner from "./ResultBanner.vue";
import * as disclosure from "./disclosure";

enableAutoUnmount(afterEach);

const message = ref("První");
const shown = ref(true);
// An owner that keys its banner per message, as ResourceAccess, PageAccessPanel and PageShareLinks do.
const Owner = defineComponent({ setup: () => () => h("div", { class: "owner" }, shown.value ? [h(ResultBanner, { key: message.value, message: message.value })] : []) });
const mountOwner = () => mount(Owner, { attachTo: document.body, global: { stubs: { transition: false } } });
let height = 52;
const originalHeight = Object.getOwnPropertyDescriptor(HTMLElement.prototype, "offsetHeight");
const originalRect = HTMLElement.prototype.getBoundingClientRect;

beforeEach(() => {
  message.value = "První";
  shown.value = true;
  height = 52;
  HTMLElement.prototype.getBoundingClientRect = function (this: HTMLElement) {
    const visible = this.classList.contains("result-banner") ? height : 0;
    return { top: 0, left: 0, right: 100, bottom: visible, width: 100, height: visible, x: 0, y: 0, toJSON: () => ({}) } as DOMRect;
  };
});
afterEach(() => {
  vi.restoreAllMocks();
  HTMLElement.prototype.getBoundingClientRect = originalRect;
  if (originalHeight) Object.defineProperty(HTMLElement.prototype, "offsetHeight", originalHeight);
});

describe("ResultBanner", () => {
  it("unfolds when it arrives", async () => {
    const unfold = vi.spyOn(disclosure.disclosureHooks, "onEnter");
    mountOwner();
    await flushPromises();
    expect(unfold).toHaveBeenCalledTimes(1);
  });

  it("does not unfold again when a new message takes the place of the one that was there", async () => {
    const unfold = vi.spyOn(disclosure.disclosureHooks, "onEnter");
    const wrapper = mountOwner();
    await flushPromises();
    expect(unfold).toHaveBeenCalledTimes(1);
    message.value = "Druhá"; // a new key: the old banner goes and the new one comes in the same update
    await nextTick();
    await flushPromises();
    expect(wrapper.text()).toContain("Druhá");
    expect(wrapper.findAll("[data-result-banner]")).toHaveLength(1);
    expect(unfold).toHaveBeenCalledTimes(1);
  });

  it("unfolds a banner that arrives after the previous one has been gone for a while", async () => {
    const unfold = vi.spyOn(disclosure.disclosureHooks, "onEnter");
    const wrapper = mountOwner();
    await flushPromises();
    shown.value = false;
    await flushPromises();
    expect(wrapper.find("[data-result-banner]").exists()).toBe(false);
    shown.value = true;
    await flushPromises();
    expect(unfold).toHaveBeenCalledTimes(2);
  });

  it("still unfolds a banner that takes the place of one that had folded away", async () => {
    const unfold = vi.spyOn(disclosure.disclosureHooks, "onEnter");
    mountOwner();
    await flushPromises();
    height = 2; // the first one has folded to its border before its owner takes it out
    message.value = "Druhá";
    await nextTick();
    await flushPromises();
    expect(unfold).toHaveBeenCalledTimes(2);
  });
});
