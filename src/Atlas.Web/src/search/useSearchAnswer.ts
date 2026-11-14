import { computed, onScopeDispose, ref, shallowRef, watch, type Ref } from "vue";
import { detectSecrets } from "../assist/rules/secretDetector";
import { aiUnavailableText } from "../ai/aiAction";
import { runAnswerTurn, type AnswerTurnState } from "../ai/answerTurn";
import { completeMessage, loadSearchAnswerPreference, sendFeedback, startConversation, type AiFeedbackBody, type AiMessageView, type AiSourceView } from "../ai/assistantApi";
import { secretQueryNotice } from "../assistant/useAssistant";

interface SearchAnswerInput {
  question: Ref<string>; spaceId: Ref<string>; open: Ref<boolean>; enabled: Ref<boolean>;
  askMode: Ref<boolean>; contextSpaceId?: Ref<string>; autoAllowed?: Ref<boolean>;
}
/** A search answer belongs to one query and space. Changing either invalidates the whole turn, including late responses. */
export function useSearchAnswer(input: SearchAnswerInput) {
  const state = ref<"offer" | AnswerTurnState>("offer");
  const sources = ref<AiSourceView[]>([]);
  const message = shallowRef<AiMessageView | null>(null);
  const conversationId = ref<string | null>(null), messageId = ref<string | null>(null);
  const notice = ref(""), rating = ref<"up" | "down" | null>(null);
  const preference = ref<"ask" | "auto" | "never">("ask");
  let preferenceLoaded = false, preferenceEpoch = 0, version = 0, controller: AbortController | undefined, timer: ReturnType<typeof setTimeout> | undefined;
  const pending = computed(() => state.value === "retrieving" || state.value === "preparing");
  const eligible = computed(() => input.open.value && input.enabled.value
    && (input.askMode.value ? !!input.question.value.trim() : preference.value !== "never" && input.question.value.trim().length >= 2));
  const visible = computed(() => input.open.value && input.enabled.value && (eligible.value || state.value !== "offer") && state.value !== "canceled");
  function reset() {
    version++; controller?.abort(); controller = undefined; clearTimeout(timer);
    state.value = "offer"; sources.value = []; message.value = null; conversationId.value = null; messageId.value = null; notice.value = ""; rating.value = null;
  }
  watch([input.question, input.spaceId, input.open], reset, { flush: "sync" });
  // The mode alone does not change the question: an answer on its way or done stays (Ctrl+Enter during the automatic answer
  // started a second conversation, 2026-10-28).
  watch(input.askMode, () => { if (!controller && state.value !== "done") reset(); }, { flush: "sync" });
  watch(input.enabled, (enabled) => {
    if (enabled) return;
    if (pending.value) {
      version++; controller?.abort(); controller = undefined; clearTimeout(timer);
      state.value = "unavailable"; sources.value = []; message.value = null; notice.value = aiUnavailableText();
    } else reset();
  }, { flush: "sync" });
  async function ensurePreference() {
    if (!input.open.value || !input.enabled.value || preferenceLoaded) return;
    preferenceLoaded = true;
    const mine = preferenceEpoch;
    try { const result = await loadSearchAnswerPreference(); if (mine === preferenceEpoch) preference.value = result; }
    catch { if (mine === preferenceEpoch) preferenceLoaded = false; }
  }
  async function refreshPreference() { preferenceEpoch++; preferenceLoaded = false; preference.value = "ask"; clearTimeout(timer); await ensurePreference(); }
  function resetSession() { reset(); preferenceEpoch++; preferenceLoaded = false; preference.value = "ask"; }
  watch([input.open, input.enabled], () => { void ensurePreference(); }, { immediate: true });

  async function ask() {
    const question = input.question.value.trim();
    if (!input.open.value || !input.enabled.value || !question || controller) return false;
    if (detectSecrets(question).length) { notice.value = secretQueryNotice(); return false; }
    clearTimeout(timer); notice.value = ""; message.value = null; sources.value = []; rating.value = null;
    const mine = version, request = new AbortController(); controller = request;
    const scope = input.spaceId.value ? { kind: "space" as const, spaceId: input.spaceId.value } : { kind: "wiki" as const };
    const update = await runAnswerTurn({
      prepare: signal => startConversation({ origin: "search", question, scope }, signal), complete: completeMessage,
      signal: request.signal, onUpdate: update => {
        if (mine !== version) return;
        state.value = update.state; sources.value = update.sources;
        if (update.conversationId) conversationId.value = update.conversationId;
        if (update.messageId) messageId.value = update.messageId;
        if (update.message) message.value = update.message;
      },
    });
    if (controller === request) controller = undefined;
    return mine === version && update.state === "done";
  }
  watch([eligible, input.question, input.spaceId, input.askMode, preference, state, () => input.autoAllowed?.value ?? true], () => {
    clearTimeout(timer);
    if ((input.autoAllowed?.value ?? true) && eligible.value && (input.askMode.value || preference.value === "auto") && state.value === "offer") timer = setTimeout(() => { void ask(); }, 700);
  }, { immediate: true });
  function stop() { controller?.abort(); }
  async function feedback(body: AiFeedbackBody) {
    if (!messageId.value) return;
    const before = rating.value; rating.value = body.rating;
    if (body.rating === "down" && !body.reason) return;
    try { await sendFeedback(messageId.value, body); } catch { rating.value = before; }
  }
  onScopeDispose(reset);
  return { state, sources, message, conversationId, messageId, notice, rating, preference, pending, visible, ask, stop, reset, resetSession, refreshPreference, feedback };
}
