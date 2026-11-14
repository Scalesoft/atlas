import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { flushPromises, type VueWrapper } from "@vue/test-utils";
import { clearCsrf } from "../../../../api";
import { mountPage, problem } from "../testing";
import AiProfileSection from "./AiProfileSection.vue";
import { useAiMock } from "../../../../testing/aiTesting";
import type { AiProfileView } from "./aiAdmin";
import type { SavedAiProfile } from "./aiSavedProfiles";

let wrapper: VueWrapper | undefined;
const settle = async () => { await flushPromises(); await new Promise(resolve => setTimeout(resolve, 0)); await flushPromises(); };
const base = "/admin/ai/profiles/chat/saved";
const local: AiProfileView = { kind: "chat", configured: true, enabled: true, provider: "openai-compatible", location: "company", baseUrl: "http://localhost:1234/v1", model: "local-model", modelVersion: null, dimensions: null, queryPrefix: "", documentPrefix: "", timeoutSeconds: 30, maxConcurrency: 4, reasoningEffort: "none", temperature: null, nativeJsonSchema: true, requestDimensions: false, useChatProfile: false, inputPricePerMillion: null, outputPricePerMillion: null, hasApiKey: false, modelLockedByIndex: false, version: "active-v1" };
const saved = (id = "backup", cloud = false): SavedAiProfile => ({ id, name: cloud ? "Cloud" : "LM Studio záloha", version: "saved-v1", isActive: false, profile: { ...local, version: "saved-v1", model: id, hasApiKey: cloud, location: cloud ? "cloud" : "company", baseUrl: cloud ? "https://api.openai.com/v1" : local.baseUrl } });
function setup(items: SavedAiProfile[] = [], failSave = false, kind: "chat" | "quick" = "chat") {
  const profileBase = `/admin/ai/profiles/${kind}`;
  const savedBase = `${profileBase}/saved`;
  const { calls } = useAiMock({ enabled: true, configured: true }, (method, path, body) => {
    if (path === profileBase && method === "GET") return { ...local, kind };
    if (path === savedBase && method === "GET") return { items, activeVersion: "active-v1" };
    if ((path === savedBase && method === "POST") || (path.startsWith(savedBase + "/") && method === "PUT")) {
      if (failSave) return problem(409, "Profil mezitím změnil jiný správce.");
      const request = body as { name: string; profile: AiProfileView };
      const entry = { ...saved("new"), name: request.name, profile: { ...request.profile, version: "saved-v2", hasApiKey: false, configured: true } };
      items.push(entry); return entry;
    }
    if (path.endsWith("/activate")) return { ...items[0]!.profile, version: "active-v2" };
    if (path.endsWith("/models")) return { ok: true, code: null, models: [{ id: "scoped-model", name: "Scoped model" }] };
    return undefined;
  });
  const mounted = mountPage(AiProfileSection, { kind }, { hash: `#/settings/general/modules/ai/${kind}` });
  wrapper = mounted.wrapper;
  return { calls, ...mounted };
}
async function open() { await settle(); await wrapper!.get("[data-ai-saved-toggle]").trigger("click"); await settle(); }
beforeEach(clearCsrf);
afterEach(() => { wrapper?.unmount(); wrapper = undefined; vi.unstubAllGlobals(); document.body.innerHTML = ""; history.replaceState(null, "", "/"); });

describe("saved AI profiles", () => {
  it("creates a named profile offline without activating or testing it", async () => {
    const { calls } = setup();
    await settle();
    expect(calls.some(call => call.path === base)).toBe(false);
    await open();
    await wrapper!.get("[data-ai-saved-new]").trigger("click");
    await wrapper!.get("[data-ai-saved-name]").setValue("Záložní server");
    const editor = wrapper!.get("[data-ai-saved-editor]");
    await editor.get('[data-value="lmstudio"] [role="radio"]').trigger("click");
    await editor.get("[data-ai-model]").setValue("backup-model");
    await editor.get("[data-save-bar-save]").trigger("click");
    await settle();
    const request = calls.find(call => call.method === "POST" && call.path === base);
    expect(request?.body).toMatchObject({ name: "Záložní server", expectedVersion: null, profile: { model: "backup-model", apiKey: null, location: "company" } });
    expect(calls.some(call => /\/(activate|test)$/.test(call.path))).toBe(false);
    expect(wrapper!.text()).toContain("Záložní server");
  });

  it("requires an explicit cloud Use and sends both concurrency versions", async () => {
    const { calls } = setup([saved("cloud", true)]);
    await open();
    await wrapper!.get("[data-ai-saved-use]").trigger("click"); await settle();
    const dialog = document.querySelector("dialog[open]")!;
    expect(dialog.textContent).toContain("mimo firmu");
    expect(dialog.textContent).toContain("Souhlasy");
    expect(calls.some(call => call.path.endsWith("/activate"))).toBe(false);
    (dialog.querySelector("[data-confirm-action]") as HTMLButtonElement).click(); await settle();
    expect(calls.find(call => call.path.endsWith("/activate"))?.body).toEqual({ expectedVersion: "active-v1", expectedSavedVersion: "saved-v1" });
    expect(calls.some(call => call.path.endsWith("/test"))).toBe(false);
  });

  it("keeps one dirty guard for the active form and saved editor and delegates Save", async () => {
    const { guard, calls } = setup([saved()]); await settle();
    await wrapper!.get("[data-ai-model]").setValue("unsaved-active");
    expect(wrapper!.get("[data-ai-saved-toggle]").attributes("disabled")).toBeDefined();
    expect(guard.dirty.value).toBe(true);
    guard.discard(); await settle(); await open();
    await wrapper!.get("[data-ai-saved-edit]").trigger("click");
    await wrapper!.get("[data-ai-saved-name]").setValue("Přejmenovaná záloha"); await settle();
    expect(guard.dirty.value).toBe(true);
    expect(wrapper!.get("[data-ai-current-fields] [data-ai-model]").attributes("disabled")).toBeDefined();
    await guard.save(); await settle();
    expect(calls.find(call => call.method === "PUT")?.path).toBe(base + "/backup");
    expect(guard.dirty.value).toBe(false);
  });

  it("discovers models using only the selected saved credential context and aborts when closed", async () => {
    const { calls, guard } = setup([saved("cloud", true)]); await open();
    await wrapper!.get("[data-ai-saved-edit]").trigger("click");
    await wrapper!.get("[data-ai-saved-editor] [data-ai-models-load]").trigger("click"); await settle();
    expect(calls.find(call => call.path.endsWith("/models"))).toMatchObject({ path: base + "/cloud/models", body: { expectedSavedVersion: "saved-v1", apiKey: null } });
    const priorFetch = vi.mocked(fetch).getMockImplementation()!;
    let finish!: (response: Response) => void; let signal: AbortSignal | undefined;
    vi.mocked(fetch).mockImplementation((input, init) => {
      if (String(input).endsWith("/cloud/models")) { signal = init?.signal as AbortSignal; return new Promise(resolve => { finish = resolve; }); }
      return priorFetch(input, init);
    });
    await wrapper!.get("[data-ai-saved-editor] [data-ai-models-load]").trigger("click"); await flushPromises();
    guard.discard(); await settle();
    expect(signal?.aborted).toBe(true);
    finish(Response.json({ ok: true, models: [{ id: "late-model", name: "Late model" }] })); await settle();
    expect(wrapper!.text()).not.toContain("Late model");
    await wrapper!.get("[data-ai-saved-new]").trigger("click");
    expect(wrapper!.find("[data-ai-saved-editor] [data-ai-key-saved]").exists()).toBe(false);
    await wrapper!.get('[data-ai-saved-editor] [data-value="lmstudio"] [role="radio"]').trigger("click");
    await wrapper!.get("[data-ai-saved-editor] [data-ai-models-load]").trigger("click"); await settle();
    expect(calls.filter(call => call.path.endsWith("/models")).at(-1)).toMatchObject({ path: base + "/models", body: { apiKey: null } });
  });

  it("excludes hidden custom settings when quick tasks reuse Chat, retaining them for editing", async () => {
    const { calls } = setup([], false, "quick"); await open();
    await wrapper!.get("[data-ai-saved-new]").trigger("click");
    await wrapper!.get("[data-ai-saved-name]").setValue("Přes Chat");
    const editor = wrapper!.get("[data-ai-saved-editor]");
    await editor.get('[data-value="lmstudio"] [role="radio"]').trigger("click");
    await editor.get(".settings-section-header.is-toggle").trigger("click"); await settle();
    await editor.get("[data-ai-timeout]").setValue("0");
    await editor.get('[role="radio"][data-value="chat"]').trigger("click");
    expect(editor.find("[data-ai-timeout]").exists()).toBe(false);
    await editor.get('[role="radio"][data-value="custom"]').trigger("click");
    await editor.get(".settings-section-header.is-toggle").trigger("click"); await settle();
    expect((editor.get("[data-ai-timeout]").element as HTMLInputElement).value).toBe("0");
    await editor.get('[role="radio"][data-value="chat"]').trigger("click");
    await editor.get("[data-save-bar-save]").trigger("click"); await settle();
    expect(calls.find(call => call.method === "POST" && call.path === "/admin/ai/profiles/quick/saved")?.body).toMatchObject({ profile: { useChatProfile: true, timeoutSeconds: 30, model: "", apiKey: null } });
  });

  it("preserves the draft after a failed save but clears its entered API key", async () => {
    const { guard } = setup([saved("cloud", true)], true); await open();
    await wrapper!.get("[data-ai-saved-edit]").trigger("click");
    await wrapper!.get("[data-ai-saved-name]").setValue("Cloud změna");
    await wrapper!.get("[data-ai-saved-editor] [data-ai-key-change]").trigger("click");
    await wrapper!.get("[data-ai-saved-editor] [data-ai-key-input]").setValue("test-key-never-persisted");
    await guard.save(); await settle();
    expect((wrapper!.get("[data-ai-saved-name]").element as HTMLInputElement).value).toBe("Cloud změna");
    expect((wrapper!.get("[data-ai-saved-editor] [data-ai-key-input]").element as HTMLInputElement).value).toBe("");
    expect(guard.dirty.value).toBe(true);
    expect(wrapper!.text()).toContain("Profil mezitím změnil jiný správce.");
  });
});
