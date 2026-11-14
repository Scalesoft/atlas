import { afterEach, describe, expect, it, vi } from "vitest";
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import AiReportsSection from "./AiReportsSection.vue";

let view: VueWrapper | undefined;
afterEach(() => { view?.unmount(); });
const usage = { since: "2026-09-28T00:00:00Z", until: "2026-10-05T00:00:00Z", items: [
  { task: "editor.transform", kind: "quick", operation: "chat", calls: 3, errors: 1, inputTokens: null, outputTokens: 40, inputKnownCalls: 0, outputKnownCalls: 1, p50Ms: 100, p95Ms: 280, estimatedCost: null, costKnownCalls: 0 },
], providers: [
  { provider: "anthropic", location: "cloud", model: "claude-test", calls: 3, errors: 1, canceled: 0, inputTokens: null, outputTokens: 40, inputKnownCalls: 0, outputKnownCalls: 1, costKnownCalls: 0, estimatedCost: null, lastCallAt: "2026-10-05T00:00:00Z" },
], days: [
  { day: "2026-10-05T00:00:00", calls: 3, errors: 1, canceled: 0, inputTokens: null, outputTokens: 40, inputKnownCalls: 0, outputKnownCalls: 1, costKnownCalls: 0, estimatedCost: null },
] };
describe("AI reports", () => {
  it("keeps unknown and partial token counts explicit and offers an authorized CSV download", async () => {
    view = mount(AiReportsSection, { props: { kind: "usage", client: { usage: vi.fn(async () => usage), quality: vi.fn() } } });
    await flushPromises();
    expect(view.get("[data-ai-usage-row]").text()).toContain("Neznámé");
    expect(view.get("[data-ai-usage-row]").text()).toContain("1 ze 3");
    expect(view.get("[data-ai-export]").attributes("href")).toBe("/api/admin/ai/usage.csv");
    expect(view.find("[data-ai-glyph]").exists()).toBe(false);
    await view.setProps({ exportHref: null });
    expect(view.find("[data-ai-export]").exists()).toBe(false);
  });
  it("shows the provider, UTC call day, and compact cost coverage without hiding missing values", async () => {
    view = mount(AiReportsSection, { props: { kind: "usage", client: { usage: vi.fn(async () => usage), quality: vi.fn() } } });
    await flushPromises();
    expect(view.get("[data-ai-provider-row]").text()).toContain("Anthropic");
    expect(view.get("[data-ai-provider-row]").text()).toContain("claude-test");
    expect(view.get("[data-ai-day-row]").text()).toContain("5. října 2026");
    await view.setProps({ compact: true });
    expect(view.find("[data-ai-provider-row]").exists()).toBe(false);
    expect(view.text()).toContain("Cloud");
    expect(view.text()).toContain("0 ze 3");
    expect(view.text()).toContain("Neznámé");
    expect(view.get("[data-ai-full-report]").attributes("href")).toBe("#/settings/general/modules/ai/usage");
  });
  it("keeps sub-cent estimates visible when price and tokens are known", async () => {
    const priced = { ...usage, providers: [{ ...usage.providers[0]!, costKnownCalls: 1, estimatedCost: 0.000001 }] };
    view = mount(AiReportsSection, { props: { kind: "usage", compact: true, client: { usage: vi.fn(async () => priced), quality: vi.fn() } } });
    await flushPromises();
    expect(view.text()).toContain("0,000001 USD");
    expect(view.text()).toContain("1 ze 3");
  });
  it("recovers a failed quality read without showing private notes supplied in a stray field", async () => {
    const quality = vi.fn().mockRejectedValueOnce(new Error("offline")).mockResolvedValueOnce({
      since: usage.since, until: usage.until, items: [{ task: "search.answer", promptVersion: "answer@1", rating: "down", reason: "wrong_sources", count: 3, note: "PRIVATE-NOTE" }], interactions: [{ task: "editor.transform", kind: "shorten", outcome: "applied", count: 7 }], noSources: [{ spaceId: "space", spaceName: "Provoz", count: 4 }],
    });
    view = mount(AiReportsSection, { props: { kind: "quality", client: { usage: vi.fn(), quality } } });
    await flushPromises();
    await view.get("[role=alert] button").trigger("click");
    await flushPromises();
    expect(view.text()).toContain("Špatné zdroje");
    expect(view.text()).not.toContain("PRIVATE-NOTE");
    expect(quality).toHaveBeenCalledTimes(2);
    expect(view.text()).toContain("Přijato");
    expect(view.text()).toContain("Provoz");
  });
});
