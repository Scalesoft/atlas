import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import { clearCsrf } from "../../../../api";
import { mountPage } from "../testing";
import AiIndexFlow from "./AiIndexFlow.vue";
import AiIndexSection from "./AiIndexSection.vue";
import { indexClientOver, mockIndexClient } from "./aiAdminClient";
import { createAiAdminMock, type AiAdminMock } from "./aiAdminMock";
import type { AiProfileView, GenerationMode, GenerationView } from "./aiAdmin";
import { useAiMock } from "../../../../testing/aiTesting";
import { createFakeLiveFeed } from "../../../../live/fakeLiveFeed";
import { liveFeedSourceKey } from "../../../../live/liveFeed";

let wrapper: VueWrapper | undefined;
const settle = async () => {
  await flushPromises();
  await new Promise((resolve) => setTimeout(resolve, 0));
  await flushPromises();
};
const stage = (view: VueWrapper) => view.get("[data-step]").attributes("data-step");
const next = (view: VueWrapper) => view.get("[data-flow-next]");
const profile = (mock: AiAdminMock) => mock.handle("GET", "/admin/ai/profiles/embeddings").body as AiProfileView;

/** The flow itself, over the mock client. */
async function flow(mode: GenerationMode, options: Parameters<typeof createAiAdminMock>[0], arrange?: (mock: AiAdminMock) => void) {
  const mock = createAiAdminMock({ enabled: true, configured: true, ...options });
  arrange?.(mock);
  const client = mockIndexClient(mock.handle);
  const building = ((await client.status()).building ?? null) as GenerationView | null;
  const feed = createFakeLiveFeed();
  wrapper = mount(AiIndexFlow, { props: { mode, profile: profile(mock), building, client }, attachTo: document.body, global: { provide: { [liveFeedSourceKey as symbol]: feed.source } } });
  await settle();
  return { mock, client, feed, view: wrapper, emitted: () => wrapper!.emitted() };
}

beforeEach(() => clearCsrf());
afterEach(() => {
  vi.useRealTimers();
  wrapper?.unmount();
  wrapper = undefined;
  vi.unstubAllGlobals();
  document.body.innerHTML = "";
  history.replaceState(null, "", "/");
});

describe("AiIndexFlow", () => {
  it("pins a selected saved embedding profile without sending an editable profile or activating it", async () => {
    // A slow pace: the flow reads the status once more as its live feed subscribes, and the build must still be going after it.
    const mock = createAiAdminMock({ enabled: true, configured: true, activeIndex: true, building: true, step: 100 });
    const active = profile(mock);
    const selected = { id: "saved-embedding", name: "Záložní hledání", version: "saved-v1", isActive: false,
      profile: { ...active, model: "other-embedding", version: "saved-v1", modelLockedByIndex: false } };
    const generation = { ...mock.state.building!, model: "other-embedding" } as GenerationView;
    const requests: Array<{ method: string; path: string; body: unknown }> = [];
    const client = indexClientOver(async <T,>(method: string, path: string, body?: unknown) => {
      requests.push({ method, path, body });
      return generation as T;
    });
    wrapper = mount(AiIndexFlow, { props: { mode: "replace", profile: active, savedProfile: selected,
      savedActiveVersion: active.version, client }, attachTo: document.body });
    await settle();
    expect(wrapper.text()).toContain("Záložní hledání");
    expect(wrapper.find("[data-ai-fields]").exists()).toBe(false);
    await next(wrapper).trigger("click");
    await settle();
    expect(requests).toEqual([{ method: "POST", path: "/admin/ai/index/generations", body: {
      mode: "replace", savedProfileId: "saved-embedding", expectedSavedVersion: "saved-v1", expectedProfileVersion: active.version,
    } }]);
    expect(stage(wrapper)).toBe("build");
  });

  it("resumes at the build step when a build is running", async () => {
    // A slow pace: the flow reads the status once more as its live feed subscribes, and the build must still be going after it.
    const mock = createAiAdminMock({ enabled: true, configured: true, activeIndex: true, building: true, step: 100 });
    useAiMock({ enabled: true, configured: true, activeIndex: true, building: true });
    const mounted = mountPage(AiIndexSection, { client: mockIndexClient(mock.handle) }, { hash: "#/settings/general/modules/ai/index" });
    wrapper = mounted.wrapper;
    await settle();

    // The section does not show the list of generations: the flow opens where the work is.
    expect(wrapper.find("[data-ai-index-flow]").exists()).toBe(true);
    expect(stage(wrapper)).toBe("build");
    expect(wrapper.get("[data-ai-build-figures]").text()).toMatch(/z 1\s480 stránek/);
    expect(wrapper.get("[data-ai-build]").text()).toContain("Staví se");
    expect(next(wrapper).attributes("disabled")).toBeDefined();
    expect(wrapper.get("[data-ai-flow-leave]").text()).toContain("stavba pokračuje");
    // The profile step is already done and says which generation it belongs to.
    expect(wrapper.get('[data-step-chip="profile"]').attributes("aria-label")).toContain("Změnit");

    await wrapper.get("[data-ai-flow-leave]").trigger("click");
    await settle();
    expect(wrapper.find("[data-ai-resume]").text()).toContain("Pokračovat ve stavbě");
    expect(wrapper.find("[data-ai-building]").exists()).toBe(true);
  });

  it("walks Profil to Stavba for a model swap and sends the new profile as a candidate", async () => {
    const { mock, view } = await flow("replace", { activeIndex: true });
    expect(stage(view)).toBe("profile");
    // The same fields as Embeddingy, but not locked: the new model is a candidate.
    expect((view.get("[data-ai-model]").element as HTMLInputElement).readOnly).toBe(false);
    await view.get("[data-ai-model]").setValue("");
    await next(view).trigger("click");
    await settle();
    expect(mock.calls.some((call) => call.path.endsWith("/generations"))).toBe(false);
    expect(view.text()).toContain("Pro zapnutí profilu vyplňte model.");

    await view.get("[data-ai-model]").setValue("text-embedding-3-large");
    await next(view).trigger("click");
    await settle();
    const start = mock.calls.find((call) => call.path === "/admin/ai/index/generations")!;
    expect(start.body).toMatchObject({ mode: "replace", profile: { model: "text-embedding-3-large", provider: "openai-compatible", dimensions: 1024 } });
    expect(stage(view)).toBe("build");
    expect(mock.state.building?.model).toBe("text-embedding-3-large");
  });

  it("follows the build when the index job's run changes, never on a timer, and offers a refresh when the feed is lost", async () => {
    try {
      const { client, feed, view } = await flow("rebuild", { activeIndex: true, building: true });
      vi.useFakeTimers({ toFake: ["setTimeout", "setInterval", "Date"] });
      expect(stage(view)).toBe("build");
      expect(feed.count("jobs")).toBe(1);
      const status = vi.spyOn(client, "status");
      await vi.advanceTimersByTimeAsync(10 * 60_000);
      expect(status).not.toHaveBeenCalled();
      feed.emit("jobs", { runId: 1, jobId: "backups.zip", state: "running", spaceId: null });
      await flushPromises();
      expect(status).not.toHaveBeenCalled();
      feed.emit("jobs", { runId: 2, jobId: "ai-index.index", state: "running", spaceId: null });
      await flushPromises();
      expect(status).toHaveBeenCalledTimes(1);
      feed.fail("jobs");
      await flushPromises();
      expect(view.find("[data-live-update-note]").exists()).toBe(true);
    } finally {
      vi.useRealTimers();
    }
  });

  it("activates only after a passing verification and returns to it on a conflict", async () => {
    const { mock, view, emitted } = await flow("rebuild", { activeIndex: true, building: true });
    expect(stage(view)).toBe("build");
    // Incomplete: neither Další nor the verification is possible.
    expect(next(view).attributes("disabled")).toBeDefined();
    mock.complete();
    // The jobs feed is not waited for; the flow reads the status the same way when the section reloads.
    wrapper!.unmount();
    const client = mockIndexClient(mock.handle);
    const building = (await client.status()).building;
    wrapper = mount(AiIndexFlow, { props: { mode: "rebuild", profile: profile(mock), building, client }, attachTo: document.body });
    await settle();
    const second = wrapper;
    expect(stage(second)).toBe("verify");
    expect(next(second).attributes("disabled")).toBeDefined();
    await second.get("[data-ai-verify]").trigger("click");
    await settle();
    expect(second.get("[data-ai-verification]").text()).toContain("Index jde aktivovat");
    expect(second.get("[data-ai-verification]").text()).toContain("46 z 50");
    expect(next(second).attributes("disabled")).toBeUndefined();
    await next(second).trigger("click");
    await settle();

    expect(stage(second)).toBe("activate");
    expect(second.get("[data-ai-activate-impact]").text()).toContain("Hledání a související stránky začnou používat nový index");
    expect(second.get("[data-ai-activate-impact]").text()).toContain("Starý index se smaže po 24 hodinách");
    expect(next(second).text()).toContain("Aktivovat index");

    // Somebody changed the generation meanwhile: the CAS fails, nothing is switched, the flow goes back to the verification.
    mock.bumpGenerationVersion();
    await next(second).trigger("click");
    await settle();
    expect(second.emitted("done")).toBeUndefined();
    expect(stage(second)).toBe("verify");
    expect(second.find("[data-ai-flow-conflict]").exists()).toBe(true);
    expect(second.find("[data-ai-verification]").exists()).toBe(false);
    expect(mock.state.active?.state).toBe("Active");

    await second.get("[data-ai-verify]").trigger("click");
    await settle();
    await next(second).trigger("click");
    await settle();
    await next(second).trigger("click");
    await settle();
    const activation = mock.calls.filter((call) => call.path.endsWith("/activate"));
    expect(activation).toHaveLength(2);
    expect(second.emitted("done")![0]![0]).toContain("Nový index je aktivní");
    expect(emitted()).toBeDefined();
  });

  it("cancels a build after confirmation", async () => {
    const { mock, view } = await flow("rebuild", { activeIndex: true, building: true });
    expect(view.get("[data-flow-cancel]").text()).toContain("Zrušit stavbu");
    await view.get("[data-flow-cancel]").trigger("click");
    await settle();
    // The question comes first; nothing is cancelled yet.
    expect(mock.calls.some((call) => call.path.endsWith("/cancel"))).toBe(false);
    const dialog = document.querySelector("dialog[open]")!;
    expect(dialog.textContent).toContain("Aktivní index a hledání zůstanou beze změny");
    (dialog.querySelector("[data-confirm-cancel]") as HTMLButtonElement).click();
    await settle();
    expect(view.emitted("done")).toBeUndefined();
    expect(mock.state.building).not.toBeNull();

    await view.get("[data-flow-cancel]").trigger("click");
    await settle();
    (document.querySelector("dialog[open] [data-confirm-action]") as HTMLButtonElement).click();
    await settle();
    expect(mock.calls.filter((call) => call.path.endsWith("/cancel"))).toHaveLength(1);
    expect(mock.state.building).toBeNull();
    expect(mock.state.active?.state).toBe("Active");
    expect(view.emitted("done")![0]![0]).toContain("Stavba byla zrušena");
  });

  it("builds the first index from the saved profile", async () => {
    const { mock, view } = await flow("build", {});
    expect(view.get("[data-ai-flow-summary]").text()).toContain("text-embedding-bge-m3");
    expect(view.find("[data-ai-fields]").exists()).toBe(false);
    expect(view.get("[data-flow-next]").text()).toContain("Spustit stavbu");
    await next(view).trigger("click");
    await settle();
    expect(mock.calls.find((call) => call.path === "/admin/ai/index/generations")!.body).toEqual({ mode: "build" });
    expect(stage(view)).toBe("build");
  });
});

describe("AiIndexSection", () => {
  it("retains the index and offers model settings when a saved selection was deleted", async () => {
    const mock = createAiAdminMock({ enabled: true, configured: true, activeIndex: true });
    useAiMock({}, (method, path) => method === "GET" && path.endsWith("/saved/deleted")
      ? new Response(JSON.stringify({ detail: "Not found" }), { status: 404 }) : undefined);
    wrapper = mountPage(AiIndexSection, { client: mockIndexClient(mock.handle), profileLoader: async () => profile(mock) },
      { hash: "#/settings/general/modules/ai/index?action=replace&savedProfileId=deleted&savedVersion=v1" }).wrapper;
    await settle();
    expect(wrapper.find("[data-ai-index-missing]").exists()).toBe(false);
    expect(wrapper.get('[data-ai-generation-state="Active"]').text()).toContain("Aktivní");
    expect(wrapper.get("[data-ai-saved-selection-error]").text()).toContain("Vybraný profil už není k dispozici");
    expect(wrapper.get("[data-ai-saved-selection-error] a").attributes("href")).toContain("/embeddings");
    expect(wrapper.find("[data-ai-index-flow]").exists()).toBe(false);
  });

  it("loads the exact saved profile from the replacement link and rejects a changed saved version", async () => {
    const options = { enabled: true, configured: true, activeIndex: true };
    const mock = createAiAdminMock(options);
    const active = profile(mock);
    const saved = { id: "saved-embedding", name: "Připravená záloha", version: "saved-v1", isActive: false,
      profile: { ...active, model: "backup-embedding", version: "saved-v1", modelLockedByIndex: false } };
    useAiMock(options, (method, path) => method === "GET" && path.endsWith("/saved/saved-embedding") ? saved : undefined);
    wrapper = mountPage(AiIndexSection, { client: mockIndexClient(mock.handle), profileLoader: async () => active },
      { hash: "#/settings/general/modules/ai/index?action=replace&savedProfileId=saved-embedding&savedVersion=saved-v1&activeVersion=active-v1" }).wrapper;
    await settle();
    expect(wrapper.get("[data-ai-saved-index-profile]").text()).toContain("Připravená záloha");
    expect(wrapper.text()).toContain("backup-embedding");
    expect(wrapper.find("[data-ai-fields]").exists()).toBe(false);
    wrapper.unmount();
    wrapper = mountPage(AiIndexSection, { client: mockIndexClient(mock.handle), profileLoader: async () => active },
      { hash: "#/settings/general/modules/ai/index?action=replace&savedProfileId=saved-embedding&savedVersion=old&activeVersion=active-v1" }).wrapper;
    await settle();
    expect(wrapper.text()).toContain("Uložený profil se mezitím změnil");
    expect(wrapper.find("[data-ai-index-flow]").exists()).toBe(false);
    expect(mock.calls.some(call => call.method === "POST")).toBe(false);
  });

  it.each([260, 0])("resumes a complete %i-page candidate at verification without running it", async (pages) => {
    // A slow pace: the flow reads the status once more as its live feed subscribes, and the build must still be going after it.
    const mock = createAiAdminMock({ enabled: true, configured: true, activeIndex: true, building: true, step: 100 });
    mock.complete();
    Object.assign(mock.state.building!, { eligiblePages: pages, indexedPages: pages });
    const mounted = mountPage(AiIndexSection, { client: mockIndexClient(mock.handle), profileLoader: async () => profile(mock) }, { hash: "#/settings/general/modules/ai/index" });
    wrapper = mounted.wrapper;
    await settle();

    expect(stage(wrapper)).toBe("verify");
    expect(wrapper.get("[data-ai-verify]").attributes("disabled")).toBeUndefined();
    expect(next(wrapper).attributes("disabled")).toBeDefined();
    expect(mock.calls.some((call) => call.path.endsWith("/verify") || call.path.endsWith("/activate"))).toBe(false);
    await wrapper.get("[data-ai-flow-leave]").trigger("click");
    await settle();
    expect(wrapper.get('[data-ai-generation-state="Building"]').text()).toContain("Připraveno k ověření");
    expect(wrapper.get("[data-ai-building]").text()).not.toContain("Staví se");
    expect(wrapper.get("[data-ai-resume]").text()).toContain("Pokračovat k ověření");
    await wrapper.get("[data-ai-resume]").trigger("click");
    await settle();
    expect(stage(wrapper)).toBe("verify");
  });

  it("keeps a 100-percent candidate in the build step while a failed task is pending", async () => {
    // A slow pace: the flow reads the status once more as its live feed subscribes, and the build must still be going after it.
    const mock = createAiAdminMock({ enabled: true, configured: true, activeIndex: true, building: true, step: 100 });
    mock.complete();
    const client = mockIndexClient(mock.handle);
    const status = await client.status();
    Object.assign(status.building!, { pendingTasks: 1, failingTasks: 1 });
    const mounted = mountPage(AiIndexSection, { client: { ...client, status: async () => status }, profileLoader: async () => profile(mock) }, { hash: "#/settings/general/modules/ai/index" });
    wrapper = mounted.wrapper;
    await settle();

    expect(stage(wrapper)).toBe("build");
    expect(next(wrapper).attributes("disabled")).toBeDefined();
    await wrapper.get("[data-ai-flow-leave]").trigger("click");
    await settle();
    expect(wrapper.get('[data-ai-generation-state="Building"]').text()).toContain("Staví se");
    expect(wrapper.get("[data-ai-resume]").text()).toContain("Pokračovat ve stavbě");
  });

  it("opens the model swap from the link of a locked Embeddingy profile and offers the right actions", async () => {
    const mock = createAiAdminMock({ enabled: true, configured: true, activeIndex: true });
    useAiMock({ enabled: true, configured: true, activeIndex: true });
    const mounted = mountPage(AiIndexSection, { client: mockIndexClient(mock.handle) }, { hash: "#/settings/general/modules/ai/index?action=replace" });
    wrapper = mounted.wrapper;
    await settle();
    expect(wrapper.get("[data-ai-index-flow]").attributes("data-ai-index-flow")).toBe("replace");
    expect(wrapper.text()).toContain("Výměna modelu");
    await wrapper.get("[data-flow-cancel]").trigger("click");
    await settle();
    expect(wrapper.find("[data-ai-rebuild-action]").exists()).toBe(true);
    expect(wrapper.get("[data-ai-replace-action]").text()).toContain("Vyměnit model…");
    expect(wrapper.find("[data-ai-build-action]").exists()).toBe(false);
  });

  it("offers Postavit index first and says so when the server has no index endpoints yet", async () => {
    const mock = createAiAdminMock({ enabled: true, configured: true });
    useAiMock({ enabled: true, configured: true });
    const mounted = mountPage(AiIndexSection, { client: mockIndexClient(mock.handle) }, { hash: "#/settings/general/modules/ai/index" });
    wrapper = mounted.wrapper;
    await settle();
    expect(wrapper.get("[data-ai-build-action]").text()).toContain("Postavit index");
    expect(wrapper.get('[data-ai-generation-state="none"]').text()).toContain("Žádný aktivní index");
    wrapper.unmount();

    const older = createAiAdminMock({ enabled: true, configured: true, withoutLaterPackages: true });
    useAiMock({ enabled: true, configured: true, withoutLaterPackages: true });
    const second = mountPage(AiIndexSection, { client: mockIndexClient(older.handle) }, { hash: "#/settings/general/modules/ai/index" });
    wrapper = second.wrapper;
    await settle();
    expect(wrapper.find("[data-ai-index-missing]").exists()).toBe(true);
  });
});
