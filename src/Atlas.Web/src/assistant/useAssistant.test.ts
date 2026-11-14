import { beforeEach, describe, expect, it, vi } from "vitest";
import { flushPromises } from "@vue/test-utils";
import { nextTick, ref } from "vue";
import { ApiError } from "../api";
import { aiUnavailableText } from "../ai/aiAction";
import type { AiConversationView, AiMessageView, AiSourceView } from "../ai/assistantApi";

const state = vi.hoisted(() => ({ caps: undefined as unknown as { value: { assistant: boolean; answer: boolean } } }));
vi.mock("../composables/useAiCapabilities", async () => {
  const { ref: makeRef } = await import("vue");
  state.caps = makeRef({ assistant: true, answer: true });
  return { useAiCapabilities: () => ({ capabilities: state.caps }) };
});
const api = vi.hoisted(() => ({
  startConversation: vi.fn(), askFollowUp: vi.fn(), completeMessage: vi.fn(), continueConversation: vi.fn(),
  listConversations: vi.fn(), deleteConversation: vi.fn(), deleteAllConversations: vi.fn(), loadAssistantScopePreference: vi.fn(),
  sendFeedback: vi.fn(),
}));
vi.mock("../ai/assistantApi", () => api);

import { useAssistant, type AssistantContext } from "./useAssistant";
import { selectionToAssistant } from "./assistantEntries";
import { AI_TEXT_LIMIT } from "../assist/aiIntents";

const pageContext: AssistantContext = { pageId: "p1", pageTitle: "VPN", spaceId: "s1", spaceName: "IT", };
const spaceContext: AssistantContext = { pageId: null, pageTitle: null, spaceId: "s1", spaceName: "IT" };
const source: AiSourceView = { number: 1, type: "page", pageId: "p1", spaceId: "s1", spaceName: "IT", title: "VPN", cited: true, changed: false };
const answered = (over: Partial<AiMessageView> = {}): AiMessageView => ({
  id: "a1", conversationId: "c1", role: "assistant", state: "answered", question: "Jak se připojit?", sentences: [{ text: "Přes klienta.", sources: [1] }],
  sources: [source], provenance: { location: "company", provider: "lm", model: "m" }, createdAt: "2026-10-05T10:00:00Z", ...over,
});

beforeEach(() => {
  vi.clearAllMocks();
  state.caps.value = { assistant: true, answer: true };
  api.loadAssistantScopePreference.mockResolvedValue("context");
  useAssistant().reset();
});

describe("useAssistant", () => {
  it("opens with the scope of the context and the user's preference", async () => {
    const assistant = useAssistant();
    assistant.openFor(pageContext);
    await flushPromises();
    expect(assistant.open.value).toBe(true);
    expect(assistant.scopeOptions.value).toEqual(["page", "space", "wiki"]);
    expect(assistant.scopeChoice.value).toBe("space");

    assistant.reset();
    assistant.openFor(spaceContext);
    expect(assistant.scopeChoice.value).toBe("space");

    assistant.reset();
    assistant.openFor(null);
    expect(assistant.scopeOptions.value).toEqual(["wiki"]);
    expect(assistant.scopeChoice.value).toBe("wiki");

    // The preference „wiki“ wins over the context while the person has not chosen a scope.
    assistant.reset();
    api.loadAssistantScopePreference.mockResolvedValue("wiki");
    assistant.openFor(pageContext);
    await flushPromises();
    expect(assistant.scopeChoice.value).toBe("wiki");
    // A scope the person chose is never replaced by the preference.
    assistant.reset();
    assistant.openFor(pageContext);
    assistant.chooseScope("space");
    await flushPromises();
    expect(assistant.scopeChoice.value).toBe("space");
  });

  it("refreshes the default for the next conversation without replacing an explicit scope", async () => {
    const assistant = useAssistant();
    assistant.openFor(pageContext);
    await flushPromises();
    assistant.chooseScope("space");
    api.loadAssistantScopePreference.mockResolvedValue("wiki");
    await assistant.refreshPreference();
    expect(assistant.scopeChoice.value).toBe("space");
    assistant.newConversation();
    expect(assistant.scopeChoice.value).toBe("wiki");
  });

  it("ignores an earlier preference load after a saved preference is refreshed", async () => {
    const assistant = useAssistant();
    let resolve!: (value: "wiki") => void;
    api.loadAssistantScopePreference.mockReturnValueOnce(new Promise(done => { resolve = done; }));
    assistant.openFor(pageContext);
    await assistant.refreshPreference();
    resolve("wiki");
    await flushPromises();
    assistant.newConversation();
    expect(assistant.scopeChoice.value).toBe("space");
  });

  it("does not open without the assistant capability", () => {
    state.caps.value = { assistant: false, answer: false };
    expect(useAssistant().openFor(pageContext)).toBe(false);
    expect(useAssistant().open.value).toBe(false);
    expect(api.loadAssistantScopePreference).not.toHaveBeenCalled();
  });

  it("continues a search conversation without asking the model again", async () => {
    const assistant = useAssistant();
    api.continueConversation.mockResolvedValue({
      id: "c1", available: true, canContinue: true, outdated: false,
      messages: [{ id: "q1", conversationId: "c1", role: "user", question: "Jak se připojit?", sentences: [], sources: [], createdAt: "2026-10-05T10:00:00Z" }, answered()],
    });
    expect(await assistant.continueFrom("c1")).toBe(true);
    expect(api.continueConversation).toHaveBeenCalledWith("c1", expect.any(AbortSignal));
    expect(api.startConversation).not.toHaveBeenCalled();
    expect(api.askFollowUp).not.toHaveBeenCalled();
    expect(api.completeMessage).not.toHaveBeenCalled();
    expect(assistant.open.value).toBe(true);
    expect(assistant.items.value.map((item) => item.kind)).toEqual(["question", "answer"]);
    const answer = assistant.items.value[1]!;
    expect(answer.kind === "answer" && answer.state).toBe("done");
    expect(assistant.readOnly.value).toBeNull();
    expect(assistant.canCompose.value).toBe(true);
  });

  it.each(["reset", "newConversation"] as const)("discards conversation loads after %s", async (action) => {
    const assistant = useAssistant();
    let resolve!: (value: AiConversationView) => void;
    api.continueConversation.mockReturnValueOnce(new Promise<AiConversationView>((done) => { resolve = done; }));
    const pending = assistant.continueFrom("old-account");
    assistant[action]();
    resolve({ id: "old-account", available: true, canContinue: true, outdated: false, messages: [answered()] });
    expect(await pending).toBe(false);
    expect(assistant.conversationId.value).toBeNull();
    expect(assistant.items.value).toEqual([]);
    expect(assistant.loading.value).toBe(false);
    expect(assistant.loadError.value).toBe("");
  });

  it("keeps the latest selected conversation and refuses questions while it is loading", async () => {
    const assistant = useAssistant();
    let resolve!: (value: AiConversationView) => void;
    api.continueConversation.mockReturnValueOnce(new Promise<AiConversationView>((done) => { resolve = done; }));
    const older = assistant.continueFrom("older");
    expect(assistant.canCompose.value).toBe(false);
    expect(await assistant.ask("A new question")).toBe(false);
    expect(api.startConversation).not.toHaveBeenCalled();
    api.continueConversation.mockResolvedValueOnce({ id: "latest", available: true, canContinue: true, outdated: false, messages: [] });
    await assistant.continueFrom("latest");
    resolve({ id: "older", available: true, canContinue: true, outdated: false, messages: [answered()] });
    expect(await older).toBe(false);
    expect(assistant.conversationId.value).toBe("latest");
    expect(assistant.items.value).toEqual([]);
    expect(assistant.canCompose.value).toBe(true);
  });

  it("does not restore an old account's conversation list or preference after reset", async () => {
    const assistant = useAssistant();
    let resolveList!: (value: unknown) => void;
    let resolvePreference!: (value: "wiki") => void;
    api.listConversations.mockReturnValueOnce(new Promise((done) => { resolveList = done; }));
    api.loadAssistantScopePreference.mockReturnValueOnce(new Promise((done) => { resolvePreference = done; }));
    assistant.openFor(pageContext);
    const pending = assistant.loadList();
    assistant.reset();
    assistant.openFor(pageContext);
    await flushPromises();
    resolveList({ items: [{ id: "old", title: "Private title" }], next: null });
    resolvePreference("wiki");
    await pending;
    await flushPromises();
    expect(assistant.list.value.items).toEqual([]);
    expect(assistant.scopeChoice.value).toBe("space");
  });

  it("makes a conversation of an incompatible profile or with withdrawn sources read-only", async () => {
    const assistant = useAssistant();
    api.continueConversation.mockResolvedValue({ id: "c1", available: true, canContinue: false, outdated: true, messages: [answered()] });
    await assistant.continueFrom("c1");
    expect(assistant.readOnly.value).toBe("outdated");
    expect(assistant.canCompose.value).toBe(false);
    api.continueConversation.mockResolvedValue({ id: "c2", available: false, canContinue: false, outdated: false, messages: [answered({ state: "withheld", sentences: [], sources: [] })] });
    await assistant.continueFrom("c2");
    expect(assistant.readOnly.value).toBe("unavailable");
    expect(assistant.items.value.some((item) => item.kind === "answer" && item.state === "withheld")).toBe(true);
  });

  it("asks, follows up in the same conversation and keeps the question when the conversation cannot take it", async () => {
    const assistant = useAssistant();
    assistant.openFor(pageContext);
    api.startConversation.mockResolvedValue({ conversationId: "c1", messageId: "a1", state: "preparing", sources: [source] });
    api.completeMessage.mockResolvedValue(answered());
    expect(await assistant.ask("Jak se připojit?")).toBe(true);
    expect(api.startConversation).toHaveBeenCalledWith({ origin: "assistant", question: "Jak se připojit?", scope: { kind: "space", spaceId: "s1" }, effort: "quick" }, expect.any(AbortSignal));
    expect(assistant.conversationId.value).toBe("c1");
    api.askFollowUp.mockRejectedValue(new ApiError(409, "x", "conversation_outdated"));
    expect(await assistant.ask("A pro Mac?")).toBe(false);
    expect(api.askFollowUp).toHaveBeenCalledWith("c1", expect.objectContaining({ question: "A pro Mac?" }), expect.any(AbortSignal));
    expect(assistant.readOnly.value).toBe("outdated");
    expect(assistant.draft.value).toBe("A pro Mac?");
    expect(assistant.items.value.filter((item) => item.kind === "question")).toHaveLength(1);
  });

  it("closes on losing the capability unless a turn is pending, which ends with the neutral sentence", async () => {
    const assistant = useAssistant();
    assistant.openFor(pageContext);
    state.caps.value = { assistant: false, answer: false };
    await nextTick();
    expect(assistant.open.value).toBe(false);

    state.caps.value = { assistant: true, answer: true };
    assistant.openFor(pageContext);
    api.startConversation.mockResolvedValue({ conversationId: "c1", messageId: "a1", state: "preparing", sources: [source] });
    let fail!: (reason: unknown) => void;
    api.completeMessage.mockReturnValue(new Promise((_, reject) => { fail = reject; }));
    const asked = assistant.ask("Jak se připojit?");
    await flushPromises();
    expect(assistant.pending.value).toBe(true);

    state.caps.value = { assistant: false, answer: false };
    await nextTick();
    expect(assistant.open.value).toBe(true);
    expect(assistant.outage.value).toBe(true);
    fail(new ApiError(503, "x", "ai_unavailable"));
    await asked;
    const answer = assistant.items.value.find((item) => item.kind === "answer");
    expect(answer?.kind === "answer" && answer.state).toBe("unavailable");
    expect(answer?.kind === "answer" && answer.text).toBe(aiUnavailableText());
    expect(assistant.canCompose.value).toBe(false);
    // The panel stays open (the sentence is on screen); closing it is final.
    expect(assistant.open.value).toBe(true);
    assistant.close();
    expect(assistant.open.value).toBe(false);
    expect(assistant.items.value).toHaveLength(0);
    expect(assistant.draft.value).toBe("");
  });

  it("names what an ask was pointed at in the question (P11), and leaves room for the typed question", async () => {
    const assistant = useAssistant();
    api.startConversation.mockResolvedValue({ conversationId: "c1", messageId: "a1", state: "preparing", sources: [source] });
    api.completeMessage.mockResolvedValue(answered());
    expect(await selectionToAssistant(pageContext, "ask", "Termín dodávky se posouvá", "Prvek tabule")).toBe(true);
    expect(await assistant.ask("Co s tím?")).toBe(true);
    expect(api.startConversation).toHaveBeenCalledWith(expect.objectContaining({ question: "Co s tím?\n\nPrvek tabule:\nTermín dodávky se posouvá" }), expect.any(AbortSignal));
    // A plain selection keeps „Výběr ze stránky“; the longest text with a long section name still leaves the person 300 characters.
    expect(await selectionToAssistant(pageContext, "ask", "x".repeat(AI_TEXT_LIMIT), `Sekce „${"Dlouhý nadpis ".repeat(20)}“`)).toBe(true);
    expect(assistant.questionLimit.value).toBeGreaterThanOrEqual(300);
  });

  it("refuses a text too long for the question before it touches the conversation", async () => {
    const assistant = useAssistant();
    api.startConversation.mockResolvedValue({ conversationId: "c1", messageId: "a1", state: "preparing", sources: [source] });
    api.completeMessage.mockResolvedValue(answered());
    assistant.openFor(pageContext);
    await assistant.ask("Jak se připojit?");
    expect(await selectionToAssistant(pageContext, "summarize", "x".repeat(1990), "Sekce „Úvod“")).toBe(false);
    expect(assistant.conversationId.value).toBe("c1");
    // Within the one limit the prompt fits, however long the section's name.
    expect(await selectionToAssistant(pageContext, "summarize", "x".repeat(AI_TEXT_LIMIT), `Sekce „${"Dlouhý nadpis ".repeat(20)}“`)).toBe(true);
  });

  it("refuses a question with a detected password and keeps it in the draft", async () => {
    const assistant = useAssistant();
    assistant.openFor(pageContext);
    expect(await assistant.ask("Nouzový účet: admin, heslo Srv#2026!rack")).toBe(false);
    expect(api.startConversation).not.toHaveBeenCalled();
    expect(assistant.notice.value).toBe("Dotaz vypadá, že obsahuje heslo. Odeberte ho před odesláním.");
    expect(assistant.items.value).toHaveLength(0);
  });
});
