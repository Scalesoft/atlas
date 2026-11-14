import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import { reactive } from "vue";
import { clearCsrf } from "../../../../api";
import { mountPage, problem } from "../testing";
import AiProfileFields from "./AiProfileFields.vue";
import AiProfileSection from "./AiProfileSection.vue";
import { changeProvider, modelCatalogRequest, draftFromView, keyWillBeDropped, validateDraft, type AiProfileView, type ProfileKind } from "./aiAdmin";
import { createAiAdminMock } from "./aiAdminMock";
import { useAiMock } from "../../../../testing/aiTesting";

let wrapper: VueWrapper | undefined;
const blank = (kind: ProfileKind = "embeddings"): AiProfileView => createAiAdminMock().handle("GET", `/admin/ai/profiles/${kind}`).body as AiProfileView;
const radio = (view: VueWrapper, value: string) => view.get(`[data-value="${value}"] [role="radio"]`);
async function advanced(view: VueWrapper) {
  await view.get(".settings-section-header.is-toggle").trigger("click");
  await flushPromises();
}
async function section(kind: ProfileKind, options: Parameters<typeof useAiMock>[0] = {}, extra?: Parameters<typeof useAiMock>[1]) {
  const harness = useAiMock(options, extra);
  const mounted = mountPage(AiProfileSection, { kind }, { hash: `#/settings/general/modules/ai/${kind}` });
  wrapper = mounted.wrapper;
  await flushPromises();
  return { ...harness, ...mounted, view: mounted.wrapper };
}

beforeEach(() => clearCsrf());
afterEach(() => {
  wrapper?.unmount();
  wrapper = undefined;
  vi.unstubAllGlobals();
  document.body.innerHTML = "";
  history.replaceState(null, "", "/");
});

describe("AiProfileFields", () => {
  it("offers a reranker only LM Studio and OpenAI and hides embedding-only and chat-only fields", async () => {
    const view = blank("rerank");
    const draft = reactive({ ...draftFromView(view), providerId: "lmstudio" as const, baseUrl: "http://lm.test/v1", model: "bge-reranker-v2-m3" });
    wrapper = mount(AiProfileFields, { props: { kind: "rerank", draft, view }, attachTo: document.body });
    expect(view.timeoutSeconds).toBe(10);
    expect(["lmstudio", "openai"].every(id => wrapper!.find(`[data-value="${id}"]`).exists())).toBe(true);
    expect(["gateway", "anthropic"].some(id => wrapper!.find(`[data-value="${id}"]`).exists())).toBe(false);
    await advanced(wrapper);
    for (const hidden of ["data-ai-query-prefix", "data-ai-document-prefix", "data-ai-model-version", "data-ai-temperature", "data-ai-native-schema"]) expect(wrapper.find(`[${hidden}]`).exists(), hidden).toBe(false);
    expect(wrapper.text()).not.toContain("Přemýšlení");
    expect(wrapper.text()).not.toContain("výstupních tokenů");
    expect(wrapper.find("[data-ai-timeout]").exists()).toBe(true);
  });
  it("lists models for the draft connection without changing a manual model and ignores stale catalog responses", async () => {
    let resolve!: (value: Response) => void;
    vi.stubGlobal("fetch", vi.fn((url: string) => url.endsWith("/csrf") ? Promise.resolve(Response.json({ token: "csrf" })) : new Promise<Response>(done => { resolve = done; })));
    const view = blank("chat");
    const draft = reactive({ ...draftFromView(view), providerId: "gateway" as const, baseUrl: "http://gateway.test/v1", model: "manual-model" });
    wrapper = mount(AiProfileFields, { props: { kind: "chat", draft, view }, attachTo: document.body });
    expect(vi.mocked(fetch).mock.calls.some(([url]) => String(url).endsWith("/models"))).toBe(false);
    expect(wrapper.get("[data-ai-models-load]").text()).toBe("Vybrat model");
    await wrapper.get("[data-ai-model]").trigger("focus");
    await wrapper.get("[data-ai-model]").trigger("focus");
    await flushPromises();
    expect(vi.mocked(fetch).mock.calls.filter(([url]) => String(url).endsWith("/models"))).toHaveLength(1);
    expect(wrapper.get("[data-ai-models-load]").text()).toBe("Načítání modelů…");
    expect(JSON.parse(vi.mocked(fetch).mock.calls.at(-1)![1]!.body as string)).toEqual({ provider: "gateway", location: "company", baseUrl: "http://gateway.test/v1", apiKey: null, clearApiKey: false });
    draft.baseUrl = "http://other.test/v1";
    await flushPromises();
    resolve(Response.json({ ok: true, code: null, models: [{ id: "stale", name: "Old" }] }));
    await flushPromises();
    expect(draft.model).toBe("manual-model");
    expect(wrapper.find('[data-value="stale"]').exists()).toBe(false);
    await wrapper.get("[data-ai-models-load]").trigger("click");
    await flushPromises();
    resolve(Response.json({ ok: true, code: null, models: [{ id: "fresh", name: "Available model" }] }));
    await flushPromises();
    expect(draft.model).toBe("manual-model");
    expect(wrapper.get("[data-ai-models-load]").text()).toBe("Obnovit nabídku");
    await wrapper.get('[data-value="fresh"][role="radio"]').trigger("click");
    expect(draft.model).toBe("fresh");
    await wrapper.get("[data-ai-models-load]").trigger("click");
    await flushPromises();
    resolve(Response.json({ ok: false, code: "timeout", models: [] }));
    await flushPromises();
    expect(wrapper.get("[data-ai-models-note]").text()).toContain("ručně");
    await wrapper.get("[data-ai-model]").setValue("manual-after-failure");
    expect(draft.model).toBe("manual-after-failure");
  });

  it("asks for a cloud key before discovering models and keeps the manual model", async () => {
    vi.stubGlobal("fetch", vi.fn((url: string) => url.endsWith("/csrf") ? Promise.resolve(Response.json({ token: "csrf" })) : Promise.resolve(Response.json({ ok: true, code: null, models: [] }))));
    const view = blank("chat");
    const draft = reactive({ ...draftFromView(view), providerId: "openai" as const, model: "manual-model" });
    wrapper = mount(AiProfileFields, { props: { kind: "chat", draft, view }, attachTo: document.body });
    await wrapper.get("[data-ai-model]").trigger("focus");
    await flushPromises();
    expect(wrapper.get("[data-ai-models-note]").text()).toContain("klíč API");
    expect(vi.mocked(fetch).mock.calls.some(([url]) => String(url).endsWith("/models"))).toBe(false);
    draft.apiKey = "test-key";
    await wrapper.get("[data-ai-model]").trigger("focus");
    await flushPromises();
    expect(vi.mocked(fetch).mock.calls.filter(([url]) => String(url).endsWith("/models"))).toHaveLength(1);
    expect(draft.model).toBe("manual-model");
  });

  it("hides duplicate activation and summary controls in guided setup", async () => {
    const view = blank("embeddings");
    const draft = reactive({ ...draftFromView(view), providerId: "lmstudio" as const, enabled: true });
    wrapper = mount(AiProfileFields, { props: { kind: "embeddings", draft, view, guided: true }, attachTo: document.body });
    expect(wrapper.find("[data-ai-enabled]").exists()).toBe(false);
    expect(wrapper.find("[data-ai-summary]").exists()).toBe(false);
    expect(wrapper.find("[data-ai-model]").exists()).toBe(true);
    await advanced(wrapper);
    expect(wrapper.find("[data-ai-model-version]").exists()).toBe(true);
  });

  it("reveals provider dependent fields and drops incompatible values on a provider change", async () => {
    const view = blank();
    const draft = reactive(draftFromView(view));
    wrapper = mount(AiProfileFields, { props: { kind: "embeddings", draft, view }, attachTo: document.body });
    // Nothing depends on a provider that is not chosen yet.
    expect(wrapper.find("[data-ai-url]").exists()).toBe(false);
    expect(wrapper.find("[data-ai-model]").exists()).toBe(false);

    await radio(wrapper, "lmstudio").trigger("click");
    expect((wrapper.get("[data-ai-url]").element as HTMLInputElement).value).toBe("http://127.0.0.1:1234/v1");
    await wrapper.get("[data-ai-model]").setValue("text-embedding-bge-m3");
    await advanced(wrapper);
    await wrapper.get("[data-ai-timeout]").setValue("45");
    expect(wrapper.get("[data-ai-summary]").text()).toContain("Firemní LM Studio (firemní)");
    expect(wrapper.get("[data-ai-summary]").text()).toContain("text-embedding-bge-m3");

    // OpenAI: the model and the timeout stay (they still fit), the company address goes, a key becomes required.
    await radio(wrapper, "openai").trigger("click");
    expect(draft.model).toBe("text-embedding-bge-m3");
    expect(draft.timeoutSeconds).toBe(45);
    expect(draft.baseUrl).toBe("https://api.openai.com/v1");
    expect(draft.requestDimensions).toBe(true);
    expect(wrapper.get("[data-provider-dropped]").text()).toContain("Adresa API");
    draft.enabled = true;
    expect(validateDraft(draft, view).apiKey).toContain("zadejte klíč");

    // A cloud provider never accepts a plain-http address.
    draft.baseUrl = "http://api.example.cz/v1";
    expect(validateDraft(draft, view).baseUrl).toContain("HTTPS");
    draft.baseUrl = "https://api.openai.com/v1";
    // Back to the company server: the cloud address is dropped again, request dimensions are off.
    await radio(wrapper, "lmstudio").trigger("click");
    expect(draft.baseUrl).toBe("http://127.0.0.1:1234/v1");
    expect(draft.requestDimensions).toBe(false);
    expect(draft.model).toBe("text-embedding-bge-m3");
  });

  it("offers Gateway and Anthropic only for tasks that are not embeddings", async () => {
    const embeddings = blank("embeddings");
    wrapper = mount(AiProfileFields, { props: { kind: "embeddings", draft: reactive(draftFromView(embeddings)), view: embeddings }, attachTo: document.body });
    expect(wrapper.find('[data-value="anthropic"]').exists()).toBe(false);
    expect(wrapper.find('[data-value="gateway"]').exists()).toBe(false);
    wrapper.unmount();
    const quick = blank("quick");
    wrapper = mount(AiProfileFields, { props: { kind: "quick", draft: reactive(draftFromView(quick)), view: quick }, attachTo: document.body });
    expect(wrapper.find('[data-value="anthropic"]').exists()).toBe(true);
    expect(wrapper.find('[data-value="gateway"]').exists()).toBe(true);
    await radio(wrapper, "anthropic").trigger("click");
    // Anthropic: the address is optional, the key is required; thinking defaults to none.
    expect(wrapper.get("[data-ai-default-url]").text()).toContain("https://api.anthropic.com");
    await advanced(wrapper);
    expect((wrapper.get("[data-ai-url]").element as HTMLInputElement).value).toBe("");
    expect(wrapper.get('[data-value="none"]').element).toBeTruthy();
  });

  it("never prefills the key and offers change and remove for a saved one", async () => {
    const { calls, view } = await section("embeddings", { enabled: true, configured: true }, (method, path) => {
      if (method === "GET" && path === "/admin/ai/profiles/embeddings")
        return { ...createAiAdminMock({ configured: true }).handle("GET", path).body as object, provider: "openai-compatible", location: "cloud", baseUrl: "https://api.openai.com/v1", hasApiKey: true };
      return undefined;
    });
    expect(view.find("[data-ai-key-input]").exists()).toBe(false);
    expect(view.get("[data-ai-key-saved]").text()).toContain("Uložený klíč ✓");
    expect(view.get("[data-ai-key-change]").text()).toBe("Změnit");
    expect(view.get("[data-ai-key-remove]").text()).toBe("Odstranit");
    // Every field is empty of the key, whatever the server holds.
    expect([...view.element.querySelectorAll("input")].every((input) => !String(input.value).includes("sk-"))).toBe(true);

    await view.get("[data-ai-key-change]").trigger("click");
    const input = view.get("[data-ai-key-input]");
    expect((input.element as HTMLInputElement).value).toBe("");
    expect(input.attributes("type")).toBe("password");
    await input.setValue("sk-new-key");
    await view.get("[data-save-bar-save]").trigger("click");
    await flushPromises();
    const sent = calls.find((call) => call.method === "PUT")!.body as Record<string, unknown>;
    expect(sent).toMatchObject({ apiKey: "sk-new-key", clearApiKey: false, provider: "openai-compatible", location: "cloud" });
    // Saved: the field is gone again and the typed key is not kept.
    expect(view.find("[data-ai-key-input]").exists()).toBe(false);
    expect(view.html()).not.toContain("sk-new-key");

    // An enabled cloud profile cannot lose its key; switched off, the key may go.
    await view.get("[data-ai-key-remove]").trigger("click");
    expect(view.get("[data-ai-key-removed]").text()).toContain("odstraní");
    await view.get("[data-save-bar-save]").trigger("click");
    await flushPromises();
    expect(calls.filter((call) => call.method === "PUT")).toHaveLength(1);
    expect(view.text()).toContain("zadejte klíč API");
    await view.get("[data-ai-enabled]").trigger("click");
    await view.get("[data-save-bar-save]").trigger("click");
    await flushPromises();
    const removal = calls.filter((call) => call.method === "PUT")[1]!.body as Record<string, unknown>;
    expect(removal).toMatchObject({ clearApiKey: true, apiKey: null });
  });

  it("warns that a stored key is dropped when the address moves to another host", async () => {
    const { view } = await section("embeddings", { enabled: true, configured: true }, (method, path) =>
      method === "GET" && path === "/admin/ai/profiles/embeddings"
        ? { ...createAiAdminMock({ configured: true }).handle("GET", path).body as object, hasApiKey: true }
        : undefined);
    expect(view.find("[data-ai-key-drop]").exists()).toBe(false);
    await view.get("[data-ai-url]").setValue("http://other-host.firma.example.test:1234/v1");
    expect(view.get("[data-ai-key-drop]").text()).toContain("zahodí");
  });

  it("resets the endpoint when changing protocols and requires a replacement cloud key", async () => {
    const saved: AiProfileView = { ...blank("chat"), configured: true, enabled: true, provider: "openai-compatible", location: "cloud", baseUrl: "https://models.example.com/v1", model: "company-chat", hasApiKey: true };
    const draft = reactive(draftFromView(saved));
    wrapper = mount(AiProfileFields, { props: { kind: "chat", draft, view: saved }, attachTo: document.body });
    await radio(wrapper, "anthropic").trigger("click");
    expect(modelCatalogRequest(draft)).toMatchObject({ provider: "anthropic", baseUrl: null });
    expect(draft.baseUrl).toBe("");
    expect(wrapper.get("[data-ai-key-drop]").text()).toContain("zahodí");
    expect(validateDraft(draft, saved).apiKey).toContain("zadejte klíč");
    expect(wrapper.find("[data-ai-key-saved]").exists()).toBe(false);
    expect(wrapper.find("[data-ai-key-cancel]").exists()).toBe(false);
    await wrapper.get("[data-ai-key-input]").setValue("synthetic-replacement");
    expect(wrapper.get("[data-ai-key-input]").isVisible()).toBe(true);
    expect(validateDraft(draft, saved).apiKey).toBeUndefined();
    expect(wrapper.find("[data-ai-key-drop]").exists()).toBe(false);
  });

  it("resets an Anthropic endpoint for OpenAI but retains compatible custom OpenAI-protocol endpoints", () => {
    const saved: AiProfileView = { ...blank("chat"), provider: "anthropic", location: "cloud", baseUrl: "https://anthropic-proxy.example/v1", hasApiKey: true };
    const next = changeProvider(draftFromView(saved), "openai", "chat").draft;
    expect(modelCatalogRequest(next)).toMatchObject({ provider: "openai-compatible", baseUrl: "https://api.openai.com/v1" });
    expect(keyWillBeDropped(next, saved)).toBe(true);
    const compatible = { ...saved, provider: "openai-compatible" as const, baseUrl: "https://openai-proxy.example/v1" };
    expect(modelCatalogRequest(changeProvider(draftFromView(compatible), "gateway", "chat").draft)).toMatchObject({ provider: "gateway", baseUrl: compatible.baseUrl });
  });

  it("keeps hosted keys when switching between its implicit and explicit default origin", () => {
    for (const [provider, baseUrl, nextUrl] of [["anthropic", null, "https://api.anthropic.com/v1"], ["anthropic", "https://api.anthropic.com/v1", ""], ["openai-compatible", null, "https://api.openai.com/v1"], ["openai-compatible", "https://api.openai.com/v1", ""]] as const) {
      const saved: AiProfileView = { ...blank("chat"), configured: true, enabled: true, provider, location: "cloud", baseUrl, model: "company-chat", hasApiKey: true };
      const draft = { ...draftFromView(saved), baseUrl: nextUrl };
      expect(keyWillBeDropped(draft, saved)).toBe(false);
      expect(validateDraft(draft, saved).apiKey).toBeUndefined();
      expect(validateDraft(draft, saved).baseUrl).toBeUndefined();
    }
  });

  it("saves and reloads Gateway as a company chat profile with an optional key", async () => {
    const { calls, view } = await section("chat", { enabled: true });
    expect(view.findAll('[data-ai-fields="chat"] [data-value]')).toHaveLength(4);
    await radio(view, "gateway").trigger("click");
    expect((view.get("[data-ai-url]").element as HTMLInputElement).value).toBe("http://127.0.0.1:8080/v1");
    expect(view.get("[data-ai-key-input]").attributes("placeholder")).toBe("Volitelný pro firemní server");
    await view.get("[data-ai-model]").setValue("company-chat");
    await view.get("[data-ai-enabled]").trigger("click");
    await view.get("[data-save-bar-save]").trigger("click");
    await flushPromises();
    expect(calls.find((call) => call.method === "PUT")!.body).toMatchObject({ provider: "gateway", location: "company", baseUrl: "http://127.0.0.1:8080/v1", model: "company-chat", apiKey: null, nativeJsonSchema: true, reasoningEffort: "none", dimensions: null });
    expect(radio(view, "gateway").attributes("aria-checked")).toBe("true");
    expect(view.get("[data-ai-summary]").text()).toContain("Firemní Gateway (firemní)");
    expect(view.get("[data-ai-test]").attributes("disabled")).toBeUndefined();
  });

  it("reveals and focuses a collapsed invalid advanced field when saving", async () => {
    const { calls, view } = await section("chat", { enabled: true, configured: true });
    await advanced(view);
    await view.get("[data-ai-timeout]").setValue("0");
    await advanced(view);
    expect(view.get(".settings-section-header.is-toggle").attributes("aria-expanded")).toBe("false");
    await view.get("[data-save-bar-save]").trigger("click");
    await flushPromises();
    expect(view.get(".settings-section-header.is-toggle").attributes("aria-expanded")).toBe("true");
    expect(view.get("[data-ai-timeout]").attributes("aria-invalid")).toBe("true");
    expect(document.activeElement).toBe(view.get("[data-ai-timeout]").element);
    expect(calls.some(call => call.method === "PUT")).toBe(false);
  });

  it("shows a missing provider at the primary choice and locks dependent choices while saving", async () => {
    const view = blank("chat");
    const draft = reactive({ ...draftFromView(view), enabled: true });
    wrapper = mount(AiProfileFields, { props: { kind: "chat", draft, view, errors: validateDraft(draft, view) }, attachTo: document.body });
    expect(wrapper.get("[data-provider-error]").text()).toContain("Nejdřív vyberte poskytovatele");
    expect(wrapper.find("[data-ai-model]").exists()).toBe(false);
    await radio(wrapper, "lmstudio").trigger("click");
    await wrapper.setProps({ errors: {}, disabled: true });
    await advanced(wrapper);
    expect(wrapper.get('[data-value="high"]').attributes("disabled")).toBeDefined();
    await wrapper.get('[data-value="high"]').trigger("click");
    expect(draft.reasoningEffort).toBe("none");
  });

  it("tests only the saved profile", async () => {
    const { calls, view } = await section("embeddings", { enabled: true, configured: true });
    const test = () => view.get("[data-ai-test]");
    expect(test().attributes("disabled")).toBeUndefined();
    expect(view.text()).toContain("Test spotřebuje tokeny.");

    await advanced(view);
    await view.get("[data-ai-timeout]").setValue("60");
    expect(test().attributes("disabled")).toBeDefined();
    expect(view.text()).toContain("Před testem uložte změny.");
    await test().trigger("click");
    expect(calls.some((call) => call.path.endsWith("/test"))).toBe(false);

    await view.get("[data-save-bar-save]").trigger("click");
    await flushPromises();
    expect(test().attributes("disabled")).toBeUndefined();
    await test().trigger("click");
    await flushPromises();
    const call = calls.find((item) => item.path === "/admin/ai/profiles/embeddings/test")!;
    expect(call.method).toBe("POST");
    expect(call.body).toBeUndefined();
    expect(view.get("[data-ai-test-result]").text()).toContain("Spojení funguje");
    expect(view.get("[data-ai-test-result]").text()).toContain("142 ms");
    expect(view.get("[data-ai-test-result]").text()).toMatch(/1\s024 rozměrů/);

    // A new edit makes the result stale.
    await view.get("[data-ai-timeout]").setValue("61");
    expect(view.find("[data-ai-test-result]").exists()).toBe(false);
  });

  it("shows the failure of a test as a czech sentence by code, not the upstream text", async () => {
    const { view } = await section("embeddings", { enabled: true, configured: true }, (method, path) =>
      method === "POST" && path.endsWith("/test") ? { ok: false, code: "auth", latencyMs: 80, dimensions: null, model: "x", message: "401 Unauthorized: bad key sk-123" } : undefined);
    await view.get("[data-ai-test]").trigger("click");
    await flushPromises();
    const result = view.get("[data-ai-test-result]");
    expect(result.text()).toContain("Spojení selhalo");
    expect(result.text()).toContain("Server modelu odmítl klíč nebo přístup.");
    expect(result.text()).not.toContain("Unauthorized");
  });

  it("locks model fields while an index is active and routes to the model swap", async () => {
    const { view } = await section("embeddings", { enabled: true, configured: true, activeIndex: true }, (method, path) =>
      method === "PUT" && path === "/admin/ai/profiles/embeddings" ? problem(409, "Změna modelu vyžaduje novou generaci indexu. Použijte Index → Vyměnit model.") : undefined);
    await advanced(view);
    expect(view.find("[data-ai-model]").exists()).toBe(false);
    expect(view.get("[data-ai-current-model]").text()).toContain("text-embedding");
    expect(view.find("[data-ai-provider-static]").exists()).toBe(true);
    expect(view.find('[data-value="lmstudio"] [role="radio"]').exists()).toBe(false);
    expect(view.find("[data-ai-model-version]").exists()).toBe(false);
    expect(view.get("[data-ai-current-version]").text()).toBeTruthy();
    expect((view.get("[data-ai-query-prefix]").element as HTMLInputElement).readOnly).toBe(true);
    expect(view.get("[data-ai-model-locked]").text()).toContain("Změna modelu vyžaduje novou generaci indexu");
    expect(view.get("[data-ai-swap]").attributes("href")).toBe("#/settings/general/modules/ai/index?action=replace");
    expect(view.get("[data-ai-swap]").text()).toContain("Změnit model");
    // Address, key and limits are not part of the fingerprint and stay editable.
    expect((view.get("[data-ai-url]").element as HTMLInputElement).readOnly).toBe(false);
  });

  it("sends a 409 ai_generation_required from the server to the model swap, keeping the draft", async () => {
    const { view } = await section("embeddings", { enabled: true, configured: true }, (method, path) =>
      method === "PUT" && path === "/admin/ai/profiles/embeddings"
        ? new Response(JSON.stringify({ detail: "Změna modelu vyžaduje novou generaci indexu. Použijte Index → Vyměnit model.", code: "ai_generation_required" }), { status: 409 })
        : undefined);
    await view.get("[data-ai-model]").setValue("jiny-model");
    await view.get("[data-save-bar-save]").trigger("click");
    await flushPromises();
    expect(view.get("[data-ai-generation-required]").text()).toContain("novou generaci indexu");
    expect(view.get("[data-ai-swap-error]").attributes("href")).toContain("action=replace");
    expect(view.find("[data-ai-conflict]").exists()).toBe(false);
    expect((view.get("[data-ai-model]").element as HTMLInputElement).value).toBe("jiny-model");
  });

  it("switches an already configured quick profile off with Nenastaveno and keeps its values", async () => {
    const { calls, view } = await section("quick", { enabled: true, configured: true });
    await view.get('[data-value="none"] [role="radio"]').trigger("click");
    expect(view.get("[data-ai-quick-off]").text()).toContain("profil vypne");
    expect(view.get('[data-value="chat"]').text()).toContain("Pro návrhy v editoru a při publikaci.");
    await view.get("[data-save-bar-save]").trigger("click");
    await flushPromises();
    expect(calls.find((call) => call.method === "PUT")!.body).toMatchObject({ enabled: false, model: "google/gemma-4-26b-a4b", reasoningEffort: "none", useChatProfile: false });
  });

  it("offers anthropic for chat and keeps the key write only", async () => {
    const { calls, view } = await section("chat", { enabled: true });
    expect(view.text()).toContain("Odpověď z wiki a Asistent. Stav firemní nebo cloudový uvidí uživatel u každé odpovědi.");
    expect(view.get("[data-ai-chat-note]").text()).toContain("Zastavení odpovědi nezaručuje nulovou cenu u poskytovatele");
    // Chat edits its own provider: no source cards, Anthropic is on offer, nothing is prefilled.
    expect(view.find("[data-ai-source]").exists()).toBe(false);
    await radio(view, "anthropic").trigger("click");
    expect(view.find('[data-ai-fields="chat"]').exists()).toBe(true);
    await view.get("[data-ai-model]").setValue("claude-sonnet-5-5");
    await view.get("[data-ai-key-input]").setValue("sk-ant-secret");
    expect(view.get("[data-ai-key-input]").attributes("type")).toBe("password");
    await view.get("[data-ai-enabled]").trigger("click");
    await view.get("[data-save-bar-save]").trigger("click");
    await flushPromises();
    const put = calls.find((call) => call.method === "PUT")!;
    expect(put.path).toBe("/admin/ai/profiles/chat");
    expect(put.body).toMatchObject({ provider: "anthropic", location: "cloud", model: "claude-sonnet-5-5", apiKey: "sk-ant-secret", useChatProfile: false, dimensions: null });
    // The key lived only in the request and the field: it is gone after the save and never comes back.
    expect(view.html()).not.toContain("sk-ant-secret");
    expect(view.find("[data-ai-key-input]").exists()).toBe(false);
    expect(view.get("[data-ai-key-saved]").text()).toContain("Uložený klíč");
  });

  it("lets Rychlé úlohy borrow the Chat profile and keeps its own values for later", async () => {
    const { calls, view } = await section("quick", { enabled: true, configured: true });
    await view.get("[data-ai-model]").setValue("unfinished-own-draft");
    await advanced(view);
    await view.get("[data-ai-timeout]").setValue("0");
    expect(view.get('[data-value="chat"] [role="radio"]').attributes("disabled")).toBeUndefined();
    await view.get('[data-value="chat"] [role="radio"]').trigger("click");
    // Nothing of the own provider is shown or tested; a note points to the Chat card.
    expect(view.find("[data-ai-fields]").exists()).toBe(false);
    expect(view.find("[data-ai-test]").exists()).toBe(false);
    expect(view.get("[data-ai-quick-chat]").text()).toContain("použijí profil Chat");
    expect(view.get("[data-ai-quick-chat] a").attributes("href")).toContain("/ai/chat");
    await view.get('[data-value="custom"] [role="radio"]').trigger("click");
    expect((view.get("[data-ai-model]").element as HTMLInputElement).value).toBe("unfinished-own-draft");
    await view.get('[data-value="chat"] [role="radio"]').trigger("click");
    await view.get("[data-save-bar-save]").trigger("click");
    await flushPromises();
    const put = calls.find((call) => call.method === "PUT")!;
    expect(put.body).toMatchObject({ enabled: true, useChatProfile: true, model: "google/gemma-4-26b-a4b", timeoutSeconds: 30 });
    // Saved: the choice stays on Chat after the reload of the saved view.
    expect(view.find("[data-ai-fields]").exists()).toBe(false);
    expect(view.find("[data-save-bar-save]").exists() && view.get("[data-save-bar-save]").isVisible()).toBe(false);
  });
});
