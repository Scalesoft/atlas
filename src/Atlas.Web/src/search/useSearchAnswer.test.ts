import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { flushPromises } from "@vue/test-utils";
import { effectScope, nextTick, ref } from "vue";

const api = vi.hoisted(() => ({ startConversation: vi.fn(), completeMessage: vi.fn(), loadSearchAnswerPreference: vi.fn(), sendFeedback: vi.fn() }));
vi.mock("../ai/assistantApi", () => api);
import { useSearchAnswer } from "./useSearchAnswer";

const scopes: ReturnType<typeof effectScope>[] = [];
function setup() {
  const input = { question: ref("Jak připojit VPN?"), spaceId: ref(""), open: ref(true), enabled: ref(true), askMode: ref(false), autoAllowed: ref(true) };
  const scope = effectScope(); scopes.push(scope);
  const answer = scope.run(() => useSearchAnswer(input))!;
  return { input, answer };
}
const done = { id: "a1", conversationId: "c1", role: "assistant", state: "answered", sentences: [{ text: "Přes klienta.", sources: [] }], sources: [], createdAt: "2026-10-05T10:00:00Z" };
beforeEach(() => {
  vi.clearAllMocks(); api.loadSearchAnswerPreference.mockResolvedValue("ask");
  api.startConversation.mockResolvedValue({ conversationId: "c1", messageId: "a1", state: "preparing", sources: [] });
  api.completeMessage.mockResolvedValue(done);
});
afterEach(() => { scopes.splice(0).forEach(scope => scope.stop()); vi.useRealTimers(); });

describe("useSearchAnswer", () => {
  it("offers on a question, runs only on request and aborts on a query or space change", async () => {
    const { input, answer } = setup(); await flushPromises();
    expect(answer.state.value).toBe("offer"); expect(api.startConversation).not.toHaveBeenCalled();
    await answer.ask(); expect(answer.state.value).toBe("done"); expect(answer.message.value).toEqual(done);
    input.spaceId.value = "s1"; await nextTick(); expect(answer.state.value).toBe("offer"); expect(answer.message.value).toBeNull();
    let resolve!: (value: typeof done) => void;
    api.completeMessage.mockReturnValue(new Promise(r => { resolve = r; }));
    const pending = answer.ask(); await flushPromises();
    const signal = api.completeMessage.mock.calls.at(-1)![1] as AbortSignal;
    input.question.value = "Kde je dokumentace?"; await nextTick();
    expect(signal.aborted).toBe(true); resolve(done); await pending;
    expect(answer.state.value).toBe("offer"); expect(answer.message.value).toBeNull();
  });

  it("debounces a plain search answer in the selected space and keeps never quiet outside ask mode", async () => {
    vi.useFakeTimers(); api.loadSearchAnswerPreference.mockResolvedValue("auto");
    const { input, answer } = setup(); input.question.value = "VPN klient"; await flushPromises();
    await vi.advanceTimersByTimeAsync(200);
    input.spaceId.value = "engineering"; await nextTick();
    await vi.advanceTimersByTimeAsync(699); expect(api.startConversation).not.toHaveBeenCalled();
    await vi.advanceTimersByTimeAsync(1);
    expect(answer.state.value).toBe("done");
    api.loadSearchAnswerPreference.mockResolvedValue("never");
    const other = setup(); await flushPromises(); await vi.advanceTimersByTimeAsync(700);
    expect(other.answer.visible.value).toBe(false);
    other.input.askMode.value = true; await nextTick();
    expect(other.answer.visible.value).toBe(true);
    await vi.advanceTimersByTimeAsync(700); expect(other.answer.state.value).toBe("done");
  });

  it("keeps an explicit mode switch quiet with auto enabled and resumes after a real question edit", async () => {
    vi.useFakeTimers(); api.loadSearchAnswerPreference.mockResolvedValue("auto");
    const { input } = setup(); input.question.value = "VPN klient"; input.autoAllowed.value = false; input.askMode.value = true;
    await flushPromises(); await vi.advanceTimersByTimeAsync(1000);
    expect(api.startConversation).not.toHaveBeenCalled();
    input.question.value = "Jak se připojit?"; input.autoAllowed.value = true;
    await nextTick(); await vi.advanceTimersByTimeAsync(700);
    expect(api.startConversation).toHaveBeenCalledOnce();
  });

  it("keeps the automatic answer when the same question switches to ask mode (Ctrl+Enter), one conversation", async () => {
    vi.useFakeTimers(); api.loadSearchAnswerPreference.mockResolvedValue("auto");
    let resolve!: (value: typeof done) => void;
    api.completeMessage.mockReturnValue(new Promise(r => { resolve = r; }));
    const { input, answer } = setup(); input.question.value = "VPN klient"; await flushPromises();
    await vi.advanceTimersByTimeAsync(700);
    expect(answer.pending.value).toBe(true);
    input.askMode.value = true; await nextTick();
    expect(await answer.ask()).toBe(false);
    resolve(done); await flushPromises();
    expect(answer.state.value).toBe("done");
    input.askMode.value = false; await nextTick();
    expect(answer.state.value).toBe("done");
    expect(api.startConversation).toHaveBeenCalledOnce();
  });

  it("sends nothing without the capability or with a detected password and erases sources during an outage", async () => {
    const { input, answer } = setup(); input.enabled.value = false; await nextTick();
    await answer.ask(); expect(api.startConversation).not.toHaveBeenCalled();
    input.enabled.value = true; input.question.value = "Nouzový účet admin, heslo Srv#2026!rack"; await nextTick();
    await answer.ask(); expect(api.startConversation).not.toHaveBeenCalled(); expect(answer.notice.value).toContain("heslo");
    input.question.value = "Jak připojit VPN?"; await nextTick();
    let resolve!: (value: typeof done) => void; api.completeMessage.mockReturnValue(new Promise(r => { resolve = r; }));
    const pending = answer.ask(); await flushPromises(); input.enabled.value = false; await nextTick();
    expect(answer.state.value).toBe("unavailable"); expect(answer.sources.value).toEqual([]);
    resolve(done); await pending; expect(answer.state.value).toBe("unavailable"); expect(answer.message.value).toBeNull();
  });
});

it("forgets preferences on sign-out and ignores the previous account's late response", async () => {
  let resolve!: (value: string) => void;
  api.loadSearchAnswerPreference.mockReturnValueOnce(new Promise(r => { resolve = r; }));
  const { answer } = setup();
  answer.resetSession();
  resolve("auto"); await flushPromises();
  expect(answer.preference.value).toBe("ask");
  api.loadSearchAnswerPreference.mockResolvedValue("never");
  await answer.refreshPreference();
  expect(answer.preference.value).toBe("never");
});
