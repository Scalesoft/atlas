import { afterEach, expect, it, vi } from "vitest";
import { flushPromises, mount } from "@vue/test-utils";
import AiQuotaStatus from "./AiQuotaStatus.vue";
import { runAnswerTurn } from "../../ai/answerTurn";
import { aiUsageLoaderKey, notifyAiUsageChanged } from "../../ai/quotaUsage";
import type { AiPersonalUsage } from "../../administration";
const usage = (remaining: number | null): AiPersonalUsage => ({ enabled: true, limits: [{ task: "chat.answer", effective: remaining === null ? null : 20, used: 3, remaining, resetAt: "2026-10-07T00:00:00Z", source: remaining === null ? "unlimited" : "custom", origin: "group", groupId: "g", groupName: "Provoz" }] });
afterEach(() => { vi.useRealTimers(); vi.unstubAllGlobals(); });
it("shows server-resolved remaining allowance and refreshes when a request settles without replacing newer data", async () => {
  let resolve!: (value: AiPersonalUsage) => void;
  const load = vi.fn().mockResolvedValueOnce(usage(17)).mockImplementationOnce(() => new Promise<AiPersonalUsage>(done => { resolve = done; })).mockResolvedValueOnce(usage(null));
  const wrapper = mount(AiQuotaStatus, { props: { task: "chat.answer" }, global: { provide: { [aiUsageLoaderKey as symbol]: load } } });
  await flushPromises();
  expect(wrapper.text()).toContain("Zbývá 17 z 20");
  expect(wrapper.text()).toContain("Provoz");
  expect(wrapper.text()).toContain("Obnovení");
  await runAnswerTurn({ prepare: async () => ({ state: "no_sources", conversationId: "c", messageId: "m", sources: [] }), complete: vi.fn() });
  notifyAiUsageChanged();
  await flushPromises();
  resolve(usage(16));
  await flushPromises();
  expect(wrapper.text()).toContain("Bez denního limitu");
  expect(wrapper.text()).not.toContain("Zbývá 16");
  wrapper.unmount();
  notifyAiUsageChanged();
  expect(load).toHaveBeenCalledTimes(3);
});

it("refreshes once at the server reset boundary while focused and never loops on a stale reset date", async () => {
  vi.useFakeTimers({ toFake: ["Date", "setTimeout", "clearTimeout"] });
  vi.setSystemTime(new Date("2026-10-06T23:59:59Z"));
  const load = vi.fn().mockResolvedValueOnce(usage(0)).mockResolvedValue(usage(20));
  const wrapper = mount(AiQuotaStatus, { props: { task: "chat.answer" }, global: { provide: { [aiUsageLoaderKey as symbol]: load } } });
  try {
    await flushPromises();
    await vi.advanceTimersByTimeAsync(999);
    expect(load).toHaveBeenCalledTimes(1);
    expect(wrapper.text()).toContain("Zbývá 0 z 20");
    await vi.advanceTimersByTimeAsync(1);
    expect(load).toHaveBeenCalledTimes(2);
    expect(wrapper.text()).toContain("Zbývá 20 z 20");
    await vi.advanceTimersByTimeAsync(60_000);
    expect(load).toHaveBeenCalledTimes(2);
  } finally { wrapper.unmount(); }
});

it("does not request provided snapshots and clears reset scheduling when props change or the component unmounts", async () => {
  vi.useFakeTimers({ toFake: ["Date", "setTimeout", "clearTimeout"] });
  vi.setSystemTime(new Date("2026-10-06T23:59:59Z"));
  const next = usage(20);
  next.limits[0]!.resetAt = "2026-10-08T00:00:00Z";
  const load = vi.fn().mockResolvedValueOnce(usage(0)).mockResolvedValue(next);
  const wrapper = mount(AiQuotaStatus, { props: { value: usage(17) as AiPersonalUsage | undefined }, global: { provide: { [aiUsageLoaderKey as symbol]: load } } });
  try {
    await flushPromises();
    expect(load).not.toHaveBeenCalled();
    await wrapper.setProps({ value: undefined });
    await flushPromises();
    expect(load).toHaveBeenCalledTimes(1);
    await wrapper.setProps({ value: usage(17) });
    await vi.advanceTimersByTimeAsync(1000);
    expect(load).toHaveBeenCalledTimes(1);
    await wrapper.setProps({ value: undefined });
    await flushPromises();
    expect(load).toHaveBeenCalledTimes(2);
  } finally { wrapper.unmount(); }
  await vi.advanceTimersByTimeAsync(86_400_000);
  expect(load).toHaveBeenCalledTimes(2);
});

 it("keeps unlimited accounts quiet and exposes finite allowance in a compact profile disclosure", async () => {
  const wrapper = mount(AiQuotaStatus, { props: { compact: true, value: usage(null) } });
  await flushPromises();
  expect(wrapper.find("summary").exists()).toBe(false);
  expect(wrapper.text()).toBe("");
  await wrapper.setProps({ value: usage(17) });
  expect(wrapper.get("summary").attributes("aria-label")).toContain("Asistent: zbývá 17 z 20");
  expect(wrapper.get("circle[data-remaining]").attributes("stroke-dasharray")).toBe("85 100");
  await wrapper.setProps({ value: { ...usage(17), enabled: false } });
  expect(wrapper.find("summary").exists()).toBe(false);
  wrapper.unmount();
});
