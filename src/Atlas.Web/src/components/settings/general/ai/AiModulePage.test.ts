import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { flushPromises, type VueWrapper } from "@vue/test-utils";
import { clearCsrf } from "../../../../api";
import { mountPage, problem } from "../testing";
import AiModulePage from "./AiModulePage.vue";
import { useAiMock } from "../../../../testing/aiTesting";

let wrapper: VueWrapper | undefined;
async function open(part: string = "overview") {
  const mounted = mountPage(AiModulePage, { part }, { hash: `#/settings/general/modules/ai${part ? `/${part}` : ""}` });
  wrapper = mounted.wrapper;
  await flushPromises();
  await vi.dynamicImportSettled();
  await flushPromises();
  return mounted;
}
const confirmButton = () => document.querySelector<HTMLButtonElement>("dialog[open] [data-confirm-action]");

beforeEach(() => clearCsrf());
afterEach(() => {
  wrapper?.unmount();
  wrapper = undefined;
  vi.unstubAllGlobals();
  document.body.innerHTML = "";
  history.replaceState(null, "", "/");
});

describe("Administrace → AI", () => {
  it("edits only selected AI groups and saves all limits in one CAS request, retaining a conflicting draft", async () => {
    const selected = { groupId: "g-ops", groupName: "Provoz", searchAnswerDailyLimit: null, chatAnswerDailyLimit: null, pageSummaryDailyLimit: null, agentDailyLimit: null, toolsEnabled: true };
    const { calls } = useAiMock({}, (method, path) => {
      if (path === "/admin/groups") return [{ id: "g-ops", name: "Provoz" }, { id: "g-dev", name: "Vývoj" }];
      if (path === "/admin/ai/access") return method === "GET" ? { groups: [selected], version: "a1" } : problem(409, "Konflikt");
    });
    const { wrapper: view, guard } = await open("access");
    expect(view.findAll("[data-ai-access-group]")).toHaveLength(1);
    await view.get("[data-ai-add-group]").trigger("click");
    await view.get('[data-value="g-dev"][role="radio"]').trigger("click");
    expect(view.findAll("[data-ai-access-group]")).toHaveLength(2);
    const row = view.get('[data-ai-access-group="g-dev"]');
    await row.get("[data-ai-group-limits]").trigger("click");
    // D20: a new grant has tools; turning them off shows in the row's summary.
    expect(row.get("[data-ai-group-tools]").attributes("aria-checked")).toBe("true");
    await row.get("[data-ai-group-tools]").trigger("click");
    expect(row.text()).toContain("Bez nástrojů");
    await row.get('[data-quota="chatAnswerDailyLimit"] [data-value="custom"]').trigger("click");
    await row.get('[data-quota="chatAnswerDailyLimit"] input').setValue("0");
    await view.get("[data-save-bar-save]").trigger("click");
    await flushPromises();
    expect(calls.filter(call => call.method === "PUT")).toHaveLength(0);
    expect(view.text()).toContain("Vlastní limity musí být celá čísla od 1 do 10 000.");
    await row.get('[data-quota="chatAnswerDailyLimit"] [data-value="unlimited"]').trigger("click");
    expect(guard.dirty.value).toBe(true);
    await view.get("[data-save-bar-save]").trigger("click");
    await flushPromises();
    const saves = calls.filter(call => call.method === "PUT");
    expect(saves).toHaveLength(1);
    expect(saves[0]!.body).toEqual({ expectedVersion: "a1", groups: [
      { groupId: "g-ops", searchAnswerDailyLimit: null, chatAnswerDailyLimit: null, pageSummaryDailyLimit: null, agentDailyLimit: null, toolsEnabled: true },
      { groupId: "g-dev", searchAnswerDailyLimit: null, chatAnswerDailyLimit: 0, pageSummaryDailyLimit: null, agentDailyLimit: null, toolsEnabled: false },
    ] });
    expect(view.find("[data-ai-access-conflict]").exists()).toBe(true);
    expect(row.get('[data-quota="chatAnswerDailyLimit"] [data-value="unlimited"]').attributes("data-state")).toBe("checked");
    expect(guard.dirty.value).toBe(true);
  });

  it("shows profile states with time and a czech error text but never upstream detail", async () => {
    // The overview of the server never carries upstream text; even a stray field must not reach the screen.
    const { mock } = useAiMock({ enabled: true, configured: true, embeddingsDown: true }, (method, path) => {
      if (method !== "GET" || path !== "/admin/ai") return undefined;
      const overview = mock.handle("GET", path).body as { profiles: Record<string, unknown>[] };
      overview.profiles[0] = { ...overview.profiles[0], message: "secret upstream detail: sk-live-123", detail: "secret upstream detail" };
      return overview;
    });
    const { wrapper: view } = await open();

    const embeddings = view.get('[data-ai-tile="embeddings"]');
    expect(embeddings.text()).toMatch(/Nedostupné od /);
    expect(view.get("[data-ai-tile-error]").text()).toContain("Poslední chyba: Server modelu neodpověděl včas.");
    expect(view.get("[data-ai-tile-error]").text()).toContain("timeout");
    expect(view.get('[data-ai-tile="quick"]').text()).toContain("Dostupné");
    // The Chat tile reads like the others: state, model, since when and the last success.
    const chat = view.get('[data-ai-tile="chat"]');
    expect(chat.text()).toContain("Chat");
    expect(chat.text()).toContain("Dostupné");
    expect(chat.text()).toContain("google/gemma-4-26b-a4b");
    expect(chat.text()).toContain("Poslední úspěch");
    expect(chat.attributes("href")).toContain("/ai/chat");
    expect(view.text()).not.toContain("secret upstream");
    expect(view.text()).not.toContain("sk-live");
    expect(view.get('[data-ai-tile="index"]').text()).toContain("Nepostaven");

  });

  it("says Nenastaveno and Vypnuto, and survives a server without the overview", async () => {
    useAiMock({ enabled: false });
    const { wrapper: view } = await open();
    expect(view.get('[data-ai-tile="embeddings"]').text()).toContain("Nenastaveno");
    expect(view.get("[data-ai-switch-state]").text()).toBe("Vypnuto");
    view.unmount();

    useAiMock({ enabled: true, configured: true, withoutLaterPackages: true });
    const { wrapper: older } = await open();
    wrapper = older;
    expect(older.find("[data-ai-overview-missing]").exists()).toBe(true);
    expect(older.get("[data-ai-switch-state]").text()).toBe("Zapnuto");
  });

  it("asks before switching ai off and handles a version conflict", async () => {
    const { calls } = useAiMock({ enabled: true, configured: true });
    const { wrapper: view, guard } = await open();

    await view.get("[data-ai-switch]").trigger("click");
    expect(guard.dirty.value).toBe(true);
    expect(view.get("[data-ai-switch-impact]").text()).toContain("Vypnutí skryje všechny AI funkce; wiki funguje jako bez AI.");
    await view.get("[data-save-bar-save]").trigger("click");
    await flushPromises();
    // Nothing is sent before the question is answered.
    expect(calls.some((call) => call.method === "PUT")).toBe(false);
    expect(confirmButton()).not.toBeNull();
    confirmButton()!.click();
    await flushPromises();
    expect(calls.find((call) => call.method === "PUT")!.body).toEqual({ enabled: false, expectedVersion: "s1" });
    expect(view.get("[data-ai-switch-state]").text()).toBe("Vypnuto");
    expect(guard.dirty.value).toBe(false);

    // A stale version: the draft stays, the note offers Načíst znovu.
    view.unmount();
    useAiMock({ enabled: true, configured: true }, (method, path) => (method === "PUT" && path === "/admin/ai/switch" ? problem(409, "Nastavení změnil jiný správce.") : undefined));
    const { wrapper: second } = await open();
    wrapper = second;
    await second.get("[data-ai-switch]").trigger("click");
    await second.get("[data-save-bar-save]").trigger("click");
    await flushPromises();
    confirmButton()!.click();
    await flushPromises();
    expect(second.find("[data-ai-conflict]").exists()).toBe(true);
    expect(second.get("[data-ai-switch-state]").text()).toBe("Zapnuto");
    expect(second.get("[data-save-bar-save]").element).toBeTruthy();
  });

  it("switches ai on without asking", async () => {
    const { calls } = useAiMock({ enabled: false });
    const { wrapper: view } = await open();
    await view.get("[data-ai-switch]").trigger("click");
    await view.get("[data-save-bar-save]").trigger("click");
    await flushPromises();
    expect(confirmButton()).toBeNull();
    expect(calls.find((call) => call.method === "PUT")!.body).toMatchObject({ enabled: true });
  });

  it("saves features with the version, keeps the cloud permission to working text tasks and explains it", async () => {
    const { calls } = useAiMock({ enabled: true, configured: true });
    const { wrapper: view } = await open("features");
    expect(view.findAll("[data-ai-group]").map((group) => group.attributes("data-ai-group"))).toEqual(["search", "read", "write", "create"]);
    expect(view.find('[data-ai-feature="search.embed-query"] [data-ai-cloud]').exists()).toBe(false);
    expect(view.find("[data-ai-cloud]").exists()).toBe(false);

    await view.get('[data-ai-feature="page.related"] [data-ai-feature-switch]').trigger("click");
    await view.get("[data-save-bar-save]").trigger("click");
    await flushPromises();
    const body = calls.find((call) => call.method === "PUT")!.body as { expectedVersion: string; items: { task: string; enabled: boolean; cloudWorkingText: boolean | null }[] };
    expect(body.expectedVersion).toBe("f1");
    expect(body.items.find((item) => item.task === "page.related")).toMatchObject({ enabled: false, cloudWorkingText: null });
    expect(body.items.find((item) => item.task === "editor.link")).toMatchObject({ enabled: true, cloudWorkingText: false });
  });

  it("starts with guided setup and keeps costs directly reachable in the primary navigation", async () => {
    useAiMock({ enabled: true, configured: true });
    const { wrapper: view } = await open("nonsense");
    expect(view.find("[data-ai-setup]").exists()).toBe(true);
    expect(view.get('[data-tab="setup"]').attributes("aria-current")).toBe("page");
    expect(view.get('[data-tab="usage"]').text()).toContain("Volání a náklady");
    expect(view.get('[data-tab="usage"]').attributes("href")).toBe("#/settings/general/modules/ai/usage");
    expect(view.findAll('[aria-current="page"]')).toHaveLength(1);
  });
});

describe("module registry entry", () => {
  it("registers a type icon (never the AI glyph) and the one-word card state", async () => {
    const { moduleEntry } = await import("../moduleRegistry");
    const entry = moduleEntry("ai")!;
    expect(entry.icon).not.toBe("sparkles");
    expect(entry.parts).toBe(true);

    useAiMock({ enabled: false });
    expect((await entry.state!()).label).toBe("Vypnuto");
    useAiMock({ enabled: true });
    expect((await entry.state!()).label).toBe("Nenastaveno");
    useAiMock({ enabled: true, configured: true });
    expect((await entry.state!()).label).toBe("Dostupné");
    useAiMock({ enabled: true, configured: true, embeddingsDown: true });
    expect((await entry.state!()).label).toBe("Nedostupné");
    // Without the A3 overview the card falls back to what A1 knows.
    useAiMock({ enabled: true, configured: true, withoutLaterPackages: true });
    expect((await entry.state!()).label).toBe("Zapnuto");
  });
});
