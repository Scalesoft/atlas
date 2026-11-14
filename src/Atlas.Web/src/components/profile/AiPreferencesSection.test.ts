import { afterEach, describe, expect, it, vi } from "vitest";
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import { ApiError } from "../../api";
import type { AiPreferences, AiPreferencesApi } from "../../aiPreferences";
import AiPreferencesSection from "./AiPreferencesSection.vue";
import { useAssistant } from "../../assistant/useAssistant";

let wrapper: VueWrapper | undefined;
afterEach(() => { wrapper?.unmount(); wrapper = undefined; useAssistant().reset(); document.body.innerHTML = ""; });

const base: AiPreferences = { editorSuggestions: "on", disabledSuggestionTypes: [], version: "v1" };
function open(initial: Partial<AiPreferences> = {}, overrides: Partial<AiPreferencesApi> = {}, capabilities?: { answer?: boolean; assistant?: boolean }) {
  const api = {
    load: vi.fn(async () => ({ ...base, ...initial })),
    save: vi.fn(async (draft: Pick<AiPreferences, "editorSuggestions" | "disabledSuggestionTypes">) => ({ ...draft, version: "v2" })),
    ...overrides,
  } as unknown as AiPreferencesApi & { load: ReturnType<typeof vi.fn>; save: ReturnType<typeof vi.fn> };
  wrapper = mount(AiPreferencesSection, { props: { api, capabilities }, attachTo: document.body });
  return api;
}
const pill = (label: string) => wrapper!.findAll('[data-ai-suggestions-mode] [role="radio"], [data-ai-suggestions-mode] button').find((item) => item.text().includes(label))!;
const save = () => wrapper!.get("[data-save-bar-save]").trigger("click");

describe("AiPreferencesSection", () => {
  it('keeps preference controls locked while their snapshot is saving', async () => {
    let finish!: (value: AiPreferences) => void;
    const api = open({}, { save: vi.fn(() => new Promise<AiPreferences>(resolve => { finish = resolve; })) }, { answer: true, assistant: true });
    await flushPromises();
    await pill('Jen pravidla').trigger('click');
    await save();
    for (const item of wrapper!.findAll('[data-ai-suggestions-mode] [role=radio], [data-ai-search-answer] [role=radio], [data-ai-assistant-scope] [role=radio]')) {
      expect(item.attributes('data-disabled')).toBeDefined();
      await item.trigger('click');
    }
    expect(wrapper!.get('[data-summary="mode"]').text()).toContain('Jen pravidla');
    expect(api.save).toHaveBeenCalledTimes(1);
    finish({ ...base, editorSuggestions: 'rules', version: 'v2' }); await flushPromises();
    expect(wrapper!.get('[data-ai-suggestions-mode] [role=radio]').attributes('data-disabled')).toBeUndefined();
  });

  it('shows failed conflict reloads without hiding the existing draft', async () => {
    const api = open({}, { save: vi.fn(async () => { throw new ApiError(409, 'Conflict'); }) });
    await flushPromises(); await pill('Vypnuto').trigger('click'); await save(); await flushPromises();
    api.load.mockRejectedValueOnce(new Error('Připojení přerušeno'));
    await wrapper!.get('[data-ai-preferences-conflict] button').trigger('click'); await flushPromises();
    expect(wrapper!.text()).toContain('Předvolby se nepodařilo načíst');
    expect(wrapper!.get('[data-summary="mode"]').text()).toContain('Vypnuto');
  });
  it.each([
    [{}, "auto"],
    [{ searchAnswer: "ask" as const }, "ask"],
    [{ searchAnswer: "never" as const }, "never"],
  ])("defaults a missing answer preference to automatic and keeps an explicit choice: %j", async (initial, expected) => {
    const api = open(initial, {}, { answer: true });
    await flushPromises();
    expect(wrapper!.get(`[data-ai-search-answer] [data-value="${expected}"]`).attributes("aria-checked")).toBe("true");
    expect(wrapper!.emitted("dirty")?.at(-1)?.[0]).toBe(false);
    await pill("Jen pravidla").trigger("click");
    await save();
    await flushPromises();
    expect(api.save).toHaveBeenCalledWith({ editorSuggestions: "rules", disabledSuggestionTypes: [], searchAnswer: expected }, "v1");
  });
  it("offers rules-only suggestions to a user with suggestions enabled", async () => {
    const api = open();
    await flushPromises();
    const rules = pill("Jen pravidla");
    expect(rules).toBeDefined();
    await rules.trigger("click");
    await save();
    await flushPromises();
    expect(api.save).toHaveBeenCalledWith({ editorSuggestions: "rules", disabledSuggestionTypes: [] }, "v1");
    expect(wrapper!.text()).toContain("model");
    expect(wrapper!.text()).not.toContain("Návrhy jsou pravidlové, nevolají žádný model");
  });

  it("turning suggestions off refreshes the editor assist feature", async () => {
    const api = open();
    await flushPromises();
    expect(wrapper!.find("[data-ai-suggestion-types]").exists()).toBe(true);
    await pill("Vypnuto").trigger("click");
    await flushPromises();
    // The summary follows the draft at once; nothing is saved before the explicit Uložit.
    expect(wrapper!.get('[data-summary="mode"]').text()).toContain("Vypnuto");
    expect(api.save).not.toHaveBeenCalled();
    await save();
    await flushPromises();
    expect(api.save).toHaveBeenCalledWith({ editorSuggestions: "off", disabledSuggestionTypes: [] }, "v1");
    // The page reloads auth/me through this event: features.editorAssist is now false.
    expect(wrapper!.emitted("saved")).toHaveLength(1);
    expect(wrapper!.find("[data-save-bar-save]").exists() && wrapper!.get("[data-save-bar-save]").isVisible()).toBe(false);
  });

  it("stores unticked types as disabled, keeps types it does not know and cancels back to the saved state", async () => {
    const api = open({ disabledSuggestionTypes: ["issue", "future-type"] });
    await flushPromises();
    const boxes = () => wrapper!.findAll('[data-ai-suggestion-types] [role="checkbox"], [data-ai-suggestion-types] input[type="checkbox"]');
    expect(boxes()).toHaveLength(5);
    const emits = () => wrapper!.emitted("dirty")?.at(-1)?.[0];
    await boxes()[0]!.trigger("click");
    expect(emits()).toBe(true);
    await wrapper!.get("[data-save-bar-cancel]").trigger("click");
    expect(emits()).toBe(false);
    await boxes()[0]!.trigger("click");
    await save();
    await flushPromises();
    expect(api.save).toHaveBeenCalledWith({ editorSuggestions: "on", disabledSuggestionTypes: ["contact", "issue", "future-type"] }, "v1");
  });

  it("keeps the draft and offers a reload on a version conflict, and reads a missing disabled list as empty", async () => {
    const api = open({ disabledSuggestionTypes: undefined as unknown as string[] }, { save: vi.fn(async () => { throw new ApiError(409, "Conflict"); }) });
    await flushPromises();
    await pill("Vypnuto").trigger("click");
    await save();
    await flushPromises();
    expect(wrapper!.find("[data-ai-preferences-conflict]").exists()).toBe(true);
    expect(wrapper!.get('[data-summary="mode"]').text()).toContain("Vypnuto");
    api.load.mockResolvedValueOnce({ ...base, editorSuggestions: "on", version: "v3" });
    await wrapper!.get("[data-ai-preferences-conflict] button").trigger("click");
    await flushPromises();
    expect(wrapper!.get('[data-summary="mode"]').text()).toContain("Zapnuto");
  });

  it("shows answer and assistant preferences only with their capabilities and deletes all conversations after confirmation", async () => {
    const deleteAll = vi.fn(async () => {});
    const none = open({}, { deleteAllConversations: deleteAll });
    await flushPromises();
    // No capability: only the suggestion preferences, as before phase 3.
    expect(wrapper!.find("[data-ai-answer-preferences], [data-ai-assistant-preferences]").exists()).toBe(false);
    wrapper!.unmount();

    const onlyAnswer = open({ searchAnswer: "ask", assistantScope: "context" }, { deleteAllConversations: deleteAll }, { answer: true });
    await flushPromises();
    expect(wrapper!.find("[data-ai-search-answer]").exists()).toBe(true);
    expect(wrapper!.find("[data-ai-assistant-preferences]").exists()).toBe(false);
    wrapper!.unmount();

    const both = open({ searchAnswer: "ask", assistantScope: "context" }, { deleteAllConversations: deleteAll }, { answer: true, assistant: true });
    await flushPromises();
    expect(wrapper!.find("[data-ai-assistant-scope]").exists()).toBe(true);
    // Both choices are part of the draft: nothing is saved before Uložit, the summary follows.
    await wrapper!.get('[data-ai-search-answer] [data-value="auto"]').trigger("click");
    await wrapper!.get('[data-ai-assistant-scope] [data-value="wiki"]').trigger("click");
    expect(both.save).not.toHaveBeenCalled();
    expect(wrapper!.get('[data-summary="answer"]').text()).toContain("automaticky");
    expect(wrapper!.get('[data-summary="scope"]').text()).toContain("Celá dostupná wiki");
    await save();
    await flushPromises();
    expect(both.save).toHaveBeenCalledWith({ editorSuggestions: "on", disabledSuggestionTypes: [], searchAnswer: "auto", assistantScope: "wiki" }, "v1");

    // "Moje konverzace" only asks the page to open the panel.
    await wrapper!.get("[data-ai-open-conversations]").trigger("click");
    expect(wrapper!.emitted("openConversations")).toHaveLength(1);

    // Deleting asks first; Zrušit deletes nothing, the confirmation deletes once and says so.
    const assistant = useAssistant();
    assistant.conversationId.value = "c1";
    assistant.items.value = [{ kind: "question", key: "q1", text: "Private question" }];
    assistant.lastQuestions.value = ["Private question"];
    assistant.list.value.next = "older-conversations";
    // Maintenance is disclosed separately from ordinary assistant preferences.
    expect(wrapper!.find('[data-ai-delete-conversations]').exists()).toBe(false);
    await wrapper!.get('[data-ai-history-settings] .settings-section-header').trigger('click');
    await wrapper!.get("[data-ai-delete-conversations]").trigger("click");
    await flushPromises();
    expect(document.querySelector("dialog[open]")?.textContent).toContain("trvale smažou");
    expect(deleteAll).not.toHaveBeenCalled();
    document.querySelector<HTMLButtonElement>("dialog[open] [data-confirm-cancel]")!.click();
    await flushPromises();
    expect(deleteAll).not.toHaveBeenCalled();
    expect(assistant.conversationId.value).toBe("c1");
    await wrapper!.get("[data-ai-delete-conversations]").trigger("click");
    await flushPromises();
    document.querySelector<HTMLButtonElement>("dialog[open] [data-confirm-action]")!.click();
    await flushPromises();
    expect(deleteAll).toHaveBeenCalledTimes(1);
    expect(assistant.conversationId.value).toBeNull();
    expect(assistant.items.value).toEqual([]);
    expect(assistant.lastQuestions.value).toEqual([]);
    expect(assistant.list.value).toEqual({ items: [], next: null, loading: false, error: "" });
    expect(wrapper!.get("[data-ai-conversations-deleted]").text()).toContain("smazané");
    expect(onlyAnswer.save).not.toHaveBeenCalled();
    expect(none.save).not.toHaveBeenCalled();
    expect(wrapper!.find("[data-ai-glyph], .atlas-ai-twinkle, .atlas-ai-shimmer").exists()).toBe(false);
  });

  it("states that the password guard is always on and carries no AI glyph", async () => {
    open();
    await flushPromises();
    expect(wrapper!.get("[data-password-guard]").text()).toContain("Vždy zapnuto");
    expect(wrapper!.find("[data-ai-glyph], .atlas-ai-twinkle, .atlas-ai-shimmer").exists()).toBe(false);
  });
});
