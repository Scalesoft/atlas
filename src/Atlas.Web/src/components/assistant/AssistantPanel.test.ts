import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import { ApiError } from "../../api";
import type { AiMessageView, AiSourceView } from "../../ai/assistantApi";

const state = vi.hoisted(() => ({ caps: undefined as unknown as { value: { assistant: boolean; answer: boolean; agent?: boolean; arrangePages?: boolean } }, refresh: vi.fn(async () => {}) }));
vi.mock("../../composables/useAiCapabilities", async () => {
  const { ref } = await import("vue");
  state.caps = ref({ assistant: true, answer: true });
  return { useAiCapabilities: () => ({ capabilities: state.caps, refresh: state.refresh }) };
});
const api = vi.hoisted(() => ({
  startConversation: vi.fn(), askFollowUp: vi.fn(), completeMessage: vi.fn(), continueConversation: vi.fn(),
  listConversations: vi.fn(), deleteConversation: vi.fn(), deleteAllConversations: vi.fn(), loadAssistantScopePreference: vi.fn(),
  sendFeedback: vi.fn(),
}));
vi.mock("../../ai/assistantApi", () => api);
const agent = vi.hoisted(() => ({ complete: vi.fn() }));
vi.mock("../../assistant/agentStream", async (original) => ({ ...(await original<typeof import("../../assistant/agentStream")>()), completeAgentMessage: agent.complete }));

import { SELECTION_PREFIX, toolsOnlyNotice, useAssistant, type AssistantContext } from "../../assistant/useAssistant";
import AssistantPanel from "./AssistantPanel.vue";

const context: AssistantContext = { pageId: "p1", pageTitle: "VPN", spaceId: "s1", spaceName: "IT" };
const source: AiSourceView = { number: 1, type: "page", pageId: "p1", spaceId: "s1", spaceName: "IT", title: "VPN", cited: true, changed: false };
const answered = (id: string, question: string): AiMessageView => ({
  id, conversationId: "c1", role: "assistant", state: "answered", question, sentences: [{ text: "Přes klienta.", sources: [1] }], sources: [source],
  provenance: { location: "company", provider: "lm", model: "m" }, createdAt: "2026-10-05T10:00:00Z",
});
let wrapper: VueWrapper | undefined;
let counter = 0;
function prepared() {
  counter++;
  return { conversationId: "c1", messageId: `a${counter}`, state: "preparing", sources: [source] };
}
async function mountPanel() {
  const assistant = useAssistant();
  assistant.openFor(context);
  wrapper = mount(AssistantPanel, { attachTo: document.body });
  await flushPromises();
  return { assistant, panel: wrapper };
}
const field = (panel: VueWrapper) => panel.get("textarea");
async function type(panel: VueWrapper, text: string) { await field(panel).setValue(text); }

beforeEach(() => {
  vi.clearAllMocks(); counter = 0;
  state.caps.value = { assistant: true, answer: true };
  agent.complete.mockImplementation(async (id: string) => answered(id, "otázka"));
  api.loadAssistantScopePreference.mockResolvedValue("context");
  api.startConversation.mockImplementation(async () => prepared());
  api.askFollowUp.mockImplementation(async () => prepared());
  api.completeMessage.mockImplementation(async (id: string) => answered(id, "otázka"));
  useAssistant().reset();
});
afterEach(() => { wrapper?.unmount(); wrapper = undefined; document.body.innerHTML = ""; });

describe("AssistantPanel", () => {
  it("offers the whole wiki and then a deeper search when an answer finds nothing", async () => {
    const { assistant, panel } = await mountPanel();
    const nothing = (id: string, question: string): AiMessageView => ({ ...answered(id, question), state: "no_sources", sentences: [], sources: [], provenance: null });
    api.completeMessage.mockImplementation(async (id: string) => nothing(id, "Co je OID?"));
    await type(panel, "Co je OID?");
    await field(panel).trigger("keydown", { key: "Enter" });
    await flushPromises();
    expect(panel.findAll("[data-ai-retry] button").map((button) => button.text())).toEqual(["Hledat v celé wiki", "Hledat do hloubky", "Zkusit jiný dotaz"]);

    await panel.get("[data-ai-widen]").trigger("click");
    await flushPromises();
    expect(api.askFollowUp).toHaveBeenLastCalledWith("c1", { question: "Co je OID?", scope: { kind: "wiki" }, effort: "quick" }, expect.any(AbortSignal));
    const last = panel.findAll("[data-ai-retry]").at(-1)!;
    expect(last.find("[data-ai-widen]").exists()).toBe(false);

    await last.get("[data-ai-deepen]").trigger("click");
    await flushPromises();
    expect(api.askFollowUp).toHaveBeenLastCalledWith("c1", { question: "Co je OID?", scope: { kind: "wiki" }, effort: "deep" }, expect.any(AbortSignal));
    expect(assistant.effort.value).toBe("deep");
    expect(panel.findAll("[data-ai-retry]").at(-1)!.get("[data-ai-deepen]").text()).toBe("Hledat naplno");
  });

  it("shows the chosen effort in the composer, changes it in the popover and sends it with the next question", async () => {
    const { panel } = await mountPanel();
    const pick = panel.get("[data-ai-effort-pick]");
    expect(pick.attributes("aria-label")).toBe("Náročnost odpovědi: Rychle");
    await pick.trigger("click");
    await flushPromises();
    const pop = document.body.querySelector<HTMLElement>("[data-ai-effort-pop]")!;
    const thumb = pop.querySelector<HTMLElement>("[role=slider]")!;
    expect(thumb.getAttribute("aria-valuetext")).toBe("Rychle, až 6 úryvků");
    thumb.dispatchEvent(new KeyboardEvent("keydown", { key: "End", bubbles: true }));
    await flushPromises();
    expect(thumb.getAttribute("aria-valuetext")).toBe("Naplno, až 14 úryvků");
    expect(pop.querySelectorAll(".ai-effort-spark")).toHaveLength(8);
    expect(pick.text()).toContain("Naplno");
    await type(panel, "Jak se připojit?");
    await field(panel).trigger("keydown", { key: "Enter" });
    await flushPromises();
    expect(api.startConversation).toHaveBeenCalledWith(expect.objectContaining({ effort: "max" }), expect.any(AbortSignal));
  });

  it("sends on enter, breaks lines on shift enter and stops a running answer on the first escape", async () => {
    const { assistant, panel } = await mountPanel();
    expect(panel.find("[data-ai-quota-status]").exists()).toBe(false);
    expect(panel.get("button[type=submit]").attributes("aria-label")).toBe("Odeslat otázku");
    await type(panel, "Jak se připojit?");
    await field(panel).trigger("keydown", { key: "Enter", shiftKey: true });
    expect(api.startConversation).not.toHaveBeenCalled();
    expect(field(panel).element.value).toBe("Jak se připojit?");

    let signal!: AbortSignal;
    api.completeMessage.mockImplementation((_: string, s: AbortSignal) => new Promise((_resolve, reject) => { signal = s; s.addEventListener("abort", () => reject(new DOMException("aborted", "AbortError"))); }));
    await field(panel).trigger("keydown", { key: "Enter" });
    await flushPromises();
    expect(api.startConversation).toHaveBeenCalledWith({ origin: "assistant", question: "Jak se připojit?", scope: { kind: "space", spaceId: "s1" }, effort: "quick" }, expect.any(AbortSignal));
    expect(panel.get("[data-ai-status=preparing]").text()).toContain("Píšu odpověď");
    expect(field(panel).element.value).toBe("");

    // The first Escape stops the answer and never reaches the panel that would close.
    const outer = vi.fn();
    document.body.addEventListener("keydown", outer);
    await panel.get("[data-assistant-panel]").trigger("keydown", { key: "Escape" });
    await flushPromises();
    expect(signal.aborted).toBe(true);
    expect(outer).not.toHaveBeenCalled();
    expect(assistant.pending.value).toBe(false);
    expect(panel.find("[data-ai-status]").exists()).toBe(false);
    expect(panel.findAll("[data-assistant-question]")).toHaveLength(1);
    // Nothing runs now, so the second Escape travels on.
    await panel.get("[data-assistant-panel]").trigger("keydown", { key: "Escape" });
    expect(outer).toHaveBeenCalledTimes(1);
    document.body.removeEventListener("keydown", outer);
  });

  it("brings the previous question back with arrow up in an empty field", async () => {
    const { panel } = await mountPanel();
    await type(panel, "Jak se připojit?");
    await field(panel).trigger("keydown", { key: "Enter" });
    await flushPromises();
    await field(panel).trigger("keydown", { key: "ArrowUp" });
    expect(field(panel).element.value).toBe("Jak se připojit?");
  });

  it("marks a scope change in the thread", async () => {
    const { panel } = await mountPanel();
    await type(panel, "První otázka");
    await field(panel).trigger("keydown", { key: "Enter" });
    await flushPromises();
    expect(panel.find("[data-assistant-scope-change]").exists()).toBe(false);
    await panel.get('[data-value="wiki"]').trigger("click");
    await type(panel, "Druhá otázka");
    await field(panel).trigger("keydown", { key: "Enter" });
    await flushPromises();
    expect(panel.get("[data-assistant-scope-change]").text()).toBe("Rozsah změněn na: Celá dostupná wiki");
    expect(api.askFollowUp).toHaveBeenCalledWith("c1", { question: "Druhá otázka", scope: { kind: "wiki" }, effort: "quick" }, expect.any(AbortSignal));
  });

  it("refuses to send a question with a detected password", async () => {
    const { panel } = await mountPanel();
    await type(panel, "Nouzový účet: admin, heslo Srv#2026!rack");
    await field(panel).trigger("keydown", { key: "Enter" });
    await flushPromises();
    expect(api.startConversation).not.toHaveBeenCalled();
    expect(panel.get("[data-assistant-notice]").text()).toBe("Dotaz vypadá, že obsahuje heslo. Odeberte ho před odesláním.");
    expect(field(panel).element.value).toContain("heslo");
  });

  it("shows unavailable and outdated conversations without their titles", async () => {
    const { assistant, panel } = await mountPanel();
    api.listConversations.mockResolvedValue({
      items: [
        { id: "k1", title: null, available: false, lastActivityAt: "2026-10-05T09:00:00Z" },
        { id: "k2", title: "Tajný název zdroje", available: false, lastActivityAt: "2026-10-05T08:00:00Z" },
        { id: "k3", title: "Jak se připojit", available: true, lastActivityAt: "2026-10-04T08:00:00Z" },
      ], next: null,
    });
    assistant.showList();
    await flushPromises();
    const rows = panel.findAll("[data-conversation]");
    expect(rows.map((row) => row.attributes("data-conversation"))).toEqual(["k1", "k2", "k3"]);
    expect(rows[0]!.text()).toContain("Konverzace není dostupná");
    expect(rows[1]!.text()).toContain("Konverzace není dostupná");
    expect(panel.text()).not.toContain("Tajný název zdroje");
    expect(rows[0]!.find("button").exists()).toBe(false);
    expect(rows[2]!.text()).toContain("Jak se připojit");

    api.continueConversation.mockResolvedValue({ id: "k3", available: true, canContinue: false, outdated: true, messages: [answered("a9", "Jak se připojit")] });
    await rows[2]!.get("button").trigger("click");
    await flushPromises();
    expect(panel.get("[data-assistant-readonly]").text()).toContain("Tato konverzace používá předchozí nastavení.");
    expect(panel.get("[data-assistant-readonly]").text()).toContain("Začít novou");
    expect(panel.find("[data-assistant-composer]").exists()).toBe(false);
    await panel.get("[data-assistant-readonly] button").trigger("click");
    await flushPromises();
    expect(panel.find("[data-assistant-readonly]").exists()).toBe(false);
    expect(panel.find("[data-assistant-composer]").exists()).toBe(true);
  });

  it("answers an outage mid-answer with the neutral sentence and no composer", async () => {
    const { panel } = await mountPanel();
    api.completeMessage.mockRejectedValue(new ApiError(503, "x", "ai_unavailable"));
    // The refresh that follows the 503 finds the capability gone: a real outage.
    state.refresh.mockImplementationOnce(async () => { state.caps.value = { assistant: false, answer: false }; });
    await type(panel, "Jak se připojit?");
    await field(panel).trigger("keydown", { key: "Enter" });
    await flushPromises();
    expect(panel.get("[data-ai-unavailable]").text()).toBe("Tahle funkce teď není k dispozici.");
    expect(panel.find("[data-assistant-composer]").exists()).toBe(false);
    expect(panel.find("[data-ai-body]").exists()).toBe(false);
  });

  it("AI4-1 answers a 503 of a busy model with a retry and keeps the composer while the capability stays on", async () => {
    const { panel } = await mountPanel();
    api.completeMessage.mockRejectedValueOnce(new ApiError(503, "x", "ai_unavailable"));
    await type(panel, "Jak se připojit?");
    await field(panel).trigger("keydown", { key: "Enter" });
    await flushPromises();
    expect(state.refresh).toHaveBeenCalled();
    expect(panel.find("[data-ai-unavailable]").exists()).toBe(false);
    expect(panel.find("[data-assistant-composer]").exists()).toBe(true);
    expect(panel.text()).toContain("Odpověď se nepodařilo připravit.");
  });

  it("greets an empty conversation with questions that only fill the field", async () => {
    const { panel } = await mountPanel();
    expect(panel.get("[data-assistant-intro]").text()).toContain("S čím vám pomůžu?");
    expect(panel.find("[data-assistant-intro] [data-ai-glyph]").exists()).toBe(true);
    const suggestions = panel.findAll("[data-assistant-suggestion]");
    expect(suggestions.map((s) => s.text())).toContain("Co najdu v prostoru IT?");
    await suggestions[0]!.trigger("click");
    expect(field(panel).element.value).toBe("Co najdu v prostoru IT?");
    expect(api.startConversation).not.toHaveBeenCalled();
  });

  it("sends a selection taken from the page with the next question and leaves room for it", async () => {
    const { assistant, panel } = await mountPanel();
    assistant.chooseScope("wiki");
    expect(assistant.attachSelection("Composition API v souborech SFC")).toBe(true);
    await flushPromises();
    expect(panel.get("[data-assistant-selection]").text()).toContain("Výběr: „Composition API v…“");
    expect(panel.get('[data-value="page"]').attributes("data-state")).toBe("checked");
    expect(SELECTION_PREFIX).toBe("\n\nVýběr ze stránky:\n");
    expect(Number(field(panel).attributes("maxlength"))).toBe(2000 - SELECTION_PREFIX.length - "Composition API v souborech SFC".length);
    await type(panel, "Proč ne Options API?");
    await field(panel).trigger("keydown", { key: "Enter" });
    await flushPromises();
    expect(api.startConversation).toHaveBeenCalledWith({ origin: "assistant", question: "Proč ne Options API?\n\nVýběr ze stránky:\nComposition API v souborech SFC", scope: { kind: "page", pageId: "p1", spaceId: "s1" }, effort: "quick" }, expect.any(AbortSignal));
    expect(panel.find("[data-assistant-selection]").exists()).toBe(false);
    // The recall brings back only what was typed.
    await field(panel).trigger("keydown", { key: "ArrowUp" });
    expect(field(panel).element.value).toBe("Proč ne Options API?");
  });

  it("names the attached target on its chip and in the question (P11)", async () => {
    const { assistant, panel } = await mountPanel();
    assistant.attachSelection("Úvodní text sekce", "Sekce „Úvod“");
    await flushPromises();
    expect(panel.get("[data-assistant-selection]").text()).toContain("Sekce „Úvod“: „Úvodní text sekce“");
    await type(panel, "Co to znamená?");
    await field(panel).trigger("keydown", { key: "Enter" });
    await flushPromises();
    expect(api.startConversation).toHaveBeenCalledWith(expect.objectContaining({ question: "Co to znamená?\n\nSekce „Úvod“:\nÚvodní text sekce" }), expect.any(AbortSignal));
  });

  it("removes an attached selection with its chip", async () => {
    const { assistant, panel } = await mountPanel();
    assistant.attachSelection("Vybraný text");
    await flushPromises();
    await panel.get("[data-assistant-selection] button").trigger("click");
    expect(assistant.selection.value).toBeNull();
    expect(panel.find("[data-assistant-selection]").exists()).toBe(false);
  });
});

describe("selection entries", () => {
  it("asks at once for Vysvětlit and Shrnout and only attaches for Zeptat se", async () => {
    const { selectionToAssistant } = await import("../../assistant/assistantEntries");
    const { assistant } = await mountPanel();
    expect(await selectionToAssistant(context, "ask", "Vybraný odstavec")).toBe(true);
    expect(api.startConversation).not.toHaveBeenCalled();
    expect(assistant.selection.value).toBe("Vybraný odstavec");
    await selectionToAssistant(context, "summarize", "Vybraný odstavec");
    await flushPromises();
    const [request] = api.startConversation.mock.calls.at(-1)!;
    expect(request).toMatchObject({ origin: "assistant", scope: { kind: "page", pageId: "p1", spaceId: "s1" } });
    expect(request.question.startsWith("Shrň tento výběr ze stránky:")).toBe(true);
    expect(request.question.endsWith("Vybraný odstavec")).toBe(true);
    // A new conversation: the earlier attachment does not travel with the summary.
    expect(request.question).not.toContain("Výběr ze stránky:");
    // P11: a target other than the selection is named in the prompt.
    await selectionToAssistant(context, "explain", "Text sekce", "Sekce „Úvod“");
    await flushPromises();
    expect(api.startConversation.mock.calls.at(-1)![0].question.startsWith("Vysvětli tento obsah (sekce „Úvod“):")).toBe(true);
  });
});

describe("S nástroji (D14)", () => {
  const ask = async (panel: VueWrapper, text: string) => { await type(panel, text); await field(panel).trigger("keydown", { key: "Enter" }); await flushPromises(); };
  const toggle = (panel: VueWrapper) => panel.get("[data-assistant-tools] [role=switch]");
  const withAgent = () => { state.caps.value = { assistant: true, answer: true, agent: true }; };

  it("is off by default (owner, 2026-10-28) and asks for chat.answer with the effort", async () => {
    withAgent();
    const { panel } = await mountPanel();
    expect(toggle(panel).attributes("aria-checked")).toBe("false");
    expect(panel.get("[data-ai-effort-pick]").attributes("disabled")).toBeUndefined();
    await ask(panel, "Jak se připojím k VPN?");
    expect(api.startConversation).toHaveBeenCalledWith(expect.objectContaining({ task: "chat.answer" }), expect.any(AbortSignal));
    expect(api.completeMessage).toHaveBeenCalled();
    expect(agent.complete).not.toHaveBeenCalled();
  });

  it("leads with wiki tasks for a writer once tools are on, a task fills the field with its instruction and its scope; off or a reader gets none", async () => {
    withAgent();
    const assistant = useAssistant();
    assistant.openFor({ ...context, canWrite: true });
    wrapper = mount(AssistantPanel, { attachTo: document.body });
    await flushPromises();
    expect(wrapper.find("[data-assistant-task]").exists()).toBe(false);
    await toggle(wrapper).trigger("click");
    expect(wrapper.findAll("[data-assistant-task]").map((t) => t.text())).not.toContain("Poskládat levé menu prostoru");
    state.caps.value = { ...state.caps.value, arrangePages: true };
    await flushPromises();
    const tasks = wrapper.findAll("[data-assistant-task]");
    expect(tasks.map((t) => t.text())).toEqual(["Převést kontaktní údaje na kontakty", "Doplnit ikonu a tagy stránky", "Doplnit ikony stránkám prostoru", "Otagovat stránky prostoru", "Poskládat levé menu prostoru"]);
    await tasks[2]!.trigger("click");
    expect(field(wrapper).element.value).toContain("Doplň ikony stránkám v prostoru IT");
    expect(assistant.scopeChoice.value).toBe("space");
    await tasks[0]!.trigger("click");
    expect(assistant.scopeChoice.value).toBe("page");
    expect(api.startConversation).not.toHaveBeenCalled();
    await toggle(wrapper).trigger("click");
    expect(wrapper.find("[data-assistant-task]").exists()).toBe(false);
    wrapper.unmount();
    useAssistant().reset();
    const { panel } = await mountPanel();
    await toggle(panel).trigger("click");
    expect(panel.find("[data-assistant-task]").exists()).toBe(false);
  });

  it("turned on by the user asks for the agent, reads the stream and stays on across a new conversation", async () => {
    withAgent();
    const { assistant, panel } = await mountPanel();
    await toggle(panel).trigger("click");
    expect(assistant.tools.value).toBe(true);
    expect(panel.get("[data-ai-effort-pick]").attributes("disabled")).toBeDefined();
    expect(panel.get("[data-assistant-effort]").attributes("title")).toBe("S nástroji si asistent čte, co potřebuje");
    await ask(panel, "Které stránky nemají tag?");
    expect(api.startConversation).toHaveBeenLastCalledWith(expect.objectContaining({ task: "assistant.agent" }), expect.any(AbortSignal));
    expect(agent.complete).toHaveBeenCalledWith("a1", expect.any(AbortSignal), expect.any(Object));
    expect(api.completeMessage).not.toHaveBeenCalled();
    assistant.newConversation();
    await flushPromises();
    expect(toggle(panel).attributes("aria-checked")).toBe("true");
    await ask(panel, "Druhá");
    expect(api.startConversation).toHaveBeenLastCalledWith(expect.objectContaining({ task: "assistant.agent" }), expect.any(AbortSignal));
  });

  it("goes back to off at sign-out", async () => {
    withAgent();
    const { assistant } = await mountPanel();
    assistant.tools.value = true;
    assistant.reset();
    expect(assistant.tools.value).toBe(false);
  });

  // D14a holds without the index too (UX cycle 5, AD5-3): the agent never runs on its own, the switch is there, off, and says why.
  it("stays off without answers from the wiki (no index): nothing runs until the user turns it on", async () => {
    state.caps.value = { assistant: false, answer: false, agent: true };
    const { assistant, panel } = await mountPanel();
    expect(toggle(panel).attributes("aria-checked")).toBe("false");
    await ask(panel, "Které stránky nemají tag?");
    expect(api.startConversation).not.toHaveBeenCalled();
    expect(agent.complete).not.toHaveBeenCalled();
    expect(assistant.notice.value).toBe(toolsOnlyNotice());
    await toggle(panel).trigger("click");
    await ask(panel, "Které stránky nemají tag?");
    expect(api.startConversation).toHaveBeenCalledWith(expect.objectContaining({ task: "assistant.agent" }), expect.any(AbortSignal));
    expect(agent.complete).toHaveBeenCalled();
  });

  // AI is extra (owner, 2026-10-28): what cannot answer is not offered; the suggested questions come back with the switch.
  it("offers no suggested questions without answers from the wiki until the user turns the tools on", async () => {
    state.caps.value = { assistant: false, answer: false, agent: true };
    const { panel } = await mountPanel();
    expect(panel.find("[data-assistant-suggestion]").exists()).toBe(false);
    expect(panel.find(".assistant-suggestions").exists()).toBe(false);
    await toggle(panel).trigger("click");
    expect(panel.find("[data-assistant-suggestion]").exists()).toBe(true);
  });

  it("is not drawn without the agent capability and the request stays as it was", async () => {
    const { panel } = await mountPanel();
    expect(panel.find("[data-assistant-tools]").exists()).toBe(false);
    expect(panel.get("[data-ai-effort-pick]").attributes("disabled")).toBeUndefined();
    await ask(panel, "Bez nástrojů");
    expect(api.startConversation.mock.calls[0]![0].task).toBeUndefined();
    expect(api.completeMessage).toHaveBeenCalled();
  });

  it("shows steps while they run and folds them into Použité kroky after the message", async () => {
    withAgent();
    let release!: () => void;
    agent.complete.mockImplementation(async (id: string, _signal: AbortSignal, callbacks: { onStep: (e: unknown) => void }) => {
      callbacks.onStep({ id: "s1", phase: "start", tool: "atlas_get_space_tree", target: "IT" });
      callbacks.onStep({ id: "s1", phase: "end", tool: "atlas_get_space_tree", target: "IT", ok: true });
      callbacks.onStep({ id: "s2", phase: "start", tool: "atlas_get_page", target: "Instalace VPN" });
      await new Promise<void>((resolve) => { release = resolve; });
      callbacks.onStep({ id: "s2", phase: "end", tool: "atlas_get_page", target: "Instalace VPN", ok: true });
      return answered(id, "otázka");
    });
    const { assistant, panel } = await mountPanel();
    assistant.tools.value = true;
    await ask(panel, "Co je v IT?");
    const live = panel.get("[data-ai-status]");
    expect(live.findAll(".ai-status-step").map((step) => step.text())).toEqual(["Procházím strom prostoru IT", "Čtu stránku Instalace VPN", "Píšu odpověď"]);
    expect(live.get("[data-step=now]").text()).toContain("Čtu stránku Instalace VPN");
    release();
    await flushPromises();
    expect(panel.find("[data-ai-status]").exists()).toBe(false);
    const fold = panel.get("[data-ai-steps-fold]");
    expect(fold.get("button").text()).toBe("Použité kroky ▸ 2");
    expect(fold.get("button").attributes("aria-expanded")).toBe("false");
    await fold.get("button").trigger("click");
    await flushPromises();
    expect(fold.findAll("li").map((li) => li.text())).toEqual(["Procházím strom prostoru IT", "Čtu stránku Instalace VPN"]);
  });

  it("ends a stream that broke as the answer error", async () => {
    withAgent();
    agent.complete.mockRejectedValue(new ApiError(502, "Odpověď asistenta se přerušila.", "stream_interrupted"));
    const { assistant, panel } = await mountPanel();
    assistant.tools.value = true;
    await ask(panel, "Přeruš se");
    expect(panel.text()).toContain("Odpověď se nepodařilo připravit.");
  });
});
