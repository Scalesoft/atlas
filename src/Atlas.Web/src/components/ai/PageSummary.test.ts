import { afterEach, expect, it, vi } from "vitest";
import { flushPromises, mount } from "@vue/test-utils";
import PageSummary from "./PageSummary.vue";

const api = {
  preparePageSummary: vi.fn(async () => ({ conversationId: "c", messageId: "m", state: "preparing" as const, sources: [] })),
  completeMessage: vi.fn(async () => ({ id: "m", conversationId: "c", role: "assistant" as const, state: "answered" as const, sources: [], sentences: [{ text: "Souhrn stránky.", sources: [] }], createdAt: "2026-10-05T10:00:00Z" })),
  sendFeedback: vi.fn(async () => {}),
};
afterEach(() => { Reflect.deleteProperty(HTMLElement.prototype, "animate"); document.body.innerHTML = ""; });
it("keeps the summary visible but inert through its exit, then removes it", async () => {
  const finishes: Array<() => void> = [];
  Object.defineProperty(HTMLElement.prototype, "animate", { configurable: true, value: vi.fn(() => ({
    finished: new Promise<void>(resolve => finishes.push(resolve)), cancel() {},
  })) });
  const wrapper = mount(PageSummary, { attachTo: document.body, props: { page: { id: "p", version: 1 }, enabled: true, api }, global: { stubs: { transition: false, AiAnswer: { props: ["sentences"], template: '<section data-summary-content>{{ sentences?.[0]?.text }}<slot name="actions" /></section>' } } } });
  try {
    await wrapper.get("[data-page-summary-start]").trigger("click"); await flushPromises();
    expect(wrapper.get("[data-summary-content]").text()).toContain("Souhrn stránky.");
    await wrapper.findAll("button").find(button => button.text() === "Zavřít shrnutí")!.trigger("click");
    expect(wrapper.find("[data-summary-content]").exists()).toBe(true);
    expect(wrapper.get("[data-summary-content]").element.closest("[inert]")).not.toBeNull();
    finishes.forEach(finish => finish()); await flushPromises();
    expect(wrapper.find("[data-summary-content]").exists()).toBe(false);
  } finally { wrapper.unmount(); }
});