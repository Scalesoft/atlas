import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { defineComponent, h } from "vue";
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import { clearCsrf } from "../../../../api";
import { mountPage } from "../testing";
import type { AiOverview, AiProfileState, AiProfileStatus, AiTestResult } from "./aiAdmin";
import AiHealthStatus from "./AiHealthStatus.vue";
import { useAiAdminHealth } from "./useAiAdminHealth";
import { createFakeLiveFeed } from "../../../../live/fakeLiveFeed";
import { liveFeedSourceKey } from "../../../../live/liveFeed";

const json = (value: unknown, status = 200) => new Response(JSON.stringify(value), { status, headers: { "Content-Type": "application/json" } });
const status = (kind: "embeddings" | "quick" | "chat", state: AiProfileState): AiProfileStatus => ({
  kind, state, since: state === "Unavailable" ? "2026-10-06T10:00:00Z" : null,
  lastErrorCode: state === "Unavailable" ? "timeout" : null,
  lastSuccessAt: null, model: "demo-model", location: "company",
});
const overview = (enabled: boolean, chatState: AiProfileState): AiOverview => ({
  enabled, switchVersion: "s1",
  profiles: [status("embeddings", "Available"), status("quick", "Available"), status("chat", chatState)],
  index: null, usage7Days: { calls: 0, errors: 0, inputTokens: null, outputTokens: null, estimatedCost: null },
});
let visibility: "visible" | "hidden";
let calls: string[];
let current: AiOverview;
let testResult: AiTestResult;
let failOverview: boolean;
let holdOverview: boolean;
let holdTest: boolean;
let resolveOverview: ((response: Response) => void) | undefined;
let resolveTest: ((response: Response) => void) | undefined;
let wrappers: VueWrapper[];
let health: ReturnType<typeof useAiAdminHealth>;
const Consumer = defineComponent({ setup() { health = useAiAdminHealth(); return () => h("div"); } });
/** /hubs/live as the consumers hear it: `feed.emit("ai-admin", {})` is an AiHealthChanged. */
let feed = createFakeLiveFeed();
const consumer = () => mount(Consumer, { global: { provide: { [liveFeedSourceKey as symbol]: feed.source } } });

beforeEach(() => {
  vi.useFakeTimers();
  visibility = "visible";
  calls = [];
  feed = createFakeLiveFeed();
  current = overview(true, "Unavailable");
  testResult = { ok: true, code: null, latencyMs: 30, dimensions: null, model: "demo-model" };
  failOverview = false;
  holdOverview = false;
  holdTest = false;
  resolveOverview = undefined;
  resolveTest = undefined;
  wrappers = [];
  Object.defineProperty(document, "visibilityState", { configurable: true, get: () => visibility });
  vi.stubGlobal("fetch", vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const path = String(input);
    calls.push((init?.method ?? "GET") + " " + path);
    if (path === "/api/auth/csrf") return json({ token: "mock-csrf" });
    if (path === "/api/admin/ai" && failOverview) return json({ detail: "raw upstream secret" }, 503);
    if (path === "/api/admin/ai" && holdOverview) return new Promise<Response>(resolve => { resolveOverview = resolve; });
    if (path === "/api/admin/ai") return json(current);
    if (path === "/api/admin/ai/profiles/chat/test") {
      if (holdTest) return new Promise<Response>(resolve => { resolveTest = resolve; });
      if (testResult.ok) current = overview(true, "Available");
      return json(testResult);
    }
    throw new Error("Unexpected request " + path);
  }));
});
afterEach(() => {
  for (const wrapper of wrappers) wrapper.unmount();
  vi.unstubAllGlobals();
  vi.useRealTimers();
  clearCsrf();
});

describe("admin AI health", () => {
  it("retries the effective Chat connection for borrowed quick tasks", async () => {
    current.profiles[1] = { ...status("quick", "Unavailable"), useChatProfile: true };
    const mounted = mountPage(AiHealthStatus);
    wrappers.push(mounted.wrapper, consumer());
    await flushPromises();
    const quick = mounted.wrapper.get('[data-ai-health-profile="quick"]');
    expect(quick.text()).toContain("Používá profil Chat");
    expect(quick.get("a").attributes("href")).toContain("/ai/chat");
    await quick.get('[data-ai-health-retry="quick"]').trigger("click");
    await flushPromises();
    expect(calls.filter(call => call.startsWith("POST"))).toEqual(["POST /api/admin/ai/profiles/chat/test"]);
    expect(health.result.value).toMatchObject({ kind: "quick", ok: true });
    expect(health.failures.value).toEqual([]);
  });

  it("shares one live feed, reads when the server tells and never on a timer, keeps retry success until dismissed, then clears after the last consumer", async () => {
    wrappers.push(consumer(), consumer());
    await flushPromises();
    const reads = () => calls.filter(call => call === "GET /api/admin/ai").length;
    // The first read and the subscription's share one request.
    expect(reads()).toBe(1);
    expect(feed.count("ai-admin")).toBe(1);
    expect(health.failures.value.map(item => item.kind)).toEqual(["chat"]);

    await health.retry("chat");
    await flushPromises();
    expect(calls.filter(call => call === "POST /api/admin/ai/profiles/chat/test")).toHaveLength(1);
    expect(health.failures.value).toEqual([]);
    expect(health.result.value).toMatchObject({ kind: "chat", ok: true });
    expect(reads()).toBe(2);
    await vi.advanceTimersByTimeAsync(10 * 60_000);
    expect(reads()).toBe(2);
    feed.emit("ai-admin", {});
    await flushPromises();
    expect(reads()).toBe(3);

    wrappers[0]!.unmount();
    wrappers.shift();
    feed.emit("ai-admin", {});
    await flushPromises();
    expect(reads()).toBe(4);
    wrappers[0]!.unmount();
    wrappers.shift();
    expect(feed.count()).toBe(0);
    const before = calls.length;
    feed.emit("ai-admin", {});
    await vi.advanceTimersByTimeAsync(10 * 60_000);
    expect(calls).toHaveLength(before);
    expect(health.overview.value).toBeUndefined();
    expect(health.result.value).toBeUndefined();
  });

  it("does not let an old session clear a new session's retry state", async () => {
    const first = consumer();
    wrappers.push(first);
    await flushPromises();
    holdOverview = true;
    const oldRetry = health.retry("chat");
    await flushPromises();
    expect(resolveOverview).toBeTypeOf("function");

    first.unmount();
    wrappers.pop();
    holdOverview = false;
    const second = consumer();
    wrappers.push(second);
    await flushPromises();
    holdTest = true;
    const newRetry = health.retry("chat");
    await flushPromises();
    expect(health.retrying.value).toBe("chat");

    resolveOverview!(json(current));
    await oldRetry;
    expect(health.retrying.value).toBe("chat");

    holdTest = false;
    resolveTest!(json(testResult));
    await newRetry;
    expect(health.retrying.value).toBeNull();
  });
  it("hides outage messaging while master AI is off and keeps a failed retry helpful without raw error text", async () => {
    current = overview(false, "Unavailable");
    const mounted = mountPage(AiHealthStatus);
    wrappers.push(mounted.wrapper, consumer());
    await flushPromises();
    expect(mounted.wrapper.find("[data-ai-health-alert]").exists()).toBe(false);

    current = overview(true, "Unavailable");
    await health.refresh();
    await flushPromises();
    expect(mounted.wrapper.get("[data-ai-health-alert]").text()).toContain("odpovědi a shrnutí");
    expect(mounted.wrapper.get("[data-ai-health-alert]").text()).toContain("může spotřebovat tokeny");
    testResult = { ok: false, code: "timeout", latencyMs: 30, dimensions: null, model: null };
    await mounted.wrapper.get('[data-ai-health-retry="chat"]').trigger("click");
    await flushPromises();
    expect(mounted.wrapper.get("[data-ai-health-alert]").text()).toContain("Server modelu neodpověděl včas");
    expect(mounted.wrapper.text()).not.toContain("raw upstream secret");
    expect(health.result.value).toMatchObject({ kind: "chat", ok: false, code: "timeout" });
    current = overview(false, "Unavailable");
    await health.refresh();
    await flushPromises();
    expect(mounted.wrapper.find("[data-ai-health-alert]").exists()).toBe(false);
  });

  it("warns about a Gateway model mismatch on a working profile once, without a retry, and drops it when the names match", async () => {
    const warning = "Server Gateway obsluhuje model „qwen-s“, ale profil má nastavený „qwen-xxs“. AI funguje dál; upravte model v profilu, aby odpovídal.";
    current = overview(true, "Available");
    current.profiles[2] = { ...status("chat", "Available"), warning };
    current.profiles[1] = { ...status("quick", "Available"), useChatProfile: true, warning };
    const mounted = mountPage(AiHealthStatus);
    wrappers.push(mounted.wrapper, consumer());
    await flushPromises();
    const alert = mounted.wrapper.get("[data-ai-health-alert]");
    expect(alert.findAll("[data-ai-health-warning]").map(item => item.attributes("data-ai-health-warning"))).toEqual(["chat"]);
    expect(alert.get('[data-ai-health-warning="chat"]').text()).toContain(warning);
    expect(alert.get('[data-ai-health-warning="chat"] a').attributes("href")).toContain("/ai/chat");
    expect(alert.find("[data-ai-health-retry]").exists()).toBe(false);
    expect(health.failures.value).toEqual([]);

    current = overview(true, "Available");
    await health.refresh();
    await flushPromises();
    expect(mounted.wrapper.find("[data-ai-health-alert]").exists()).toBe(false);
  });

  it("shows a safe fetch error, asks nothing on a timer, and clears it with the next read the server's signal brings", async () => {
    failOverview = true;
    wrappers.push(consumer());
    await flushPromises();
    expect(health.error.value).toContain("Stav AI");
    expect(health.error.value).not.toContain("raw upstream secret");
    await vi.advanceTimersByTimeAsync(10 * 60_000);
    expect(calls.filter(call => call === "GET /api/admin/ai")).toHaveLength(1);
    failOverview = false;
    feed.emit("ai-admin", {});
    await flushPromises();
    expect(health.error.value).toBe("");
    expect(health.failures.value.map(item => item.kind)).toEqual(["chat"]);
  });
});
