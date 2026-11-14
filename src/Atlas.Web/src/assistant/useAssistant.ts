import { computed, effectScope, ref, shallowRef, watch } from "vue";
import { ApiError } from "../api";
import { detectSecrets } from "../assist/rules/secretDetector";
import { aiTarget, QUESTION_MAX_LENGTH } from "../assist/aiIntents";
import ai from "../i18n/ai";
import t from "../i18n/assistant";
import { runAnswerTurn, type AnswerTurnState, type AnswerTurnUpdate } from "../ai/answerTurn";
import {
  askFollowUp, completeMessage, continueConversation, deleteAllConversations, deleteConversation, listConversations,
  loadAssistantScopePreference, sendFeedback, startConversation,
  type AiConversationSummary, type AiConversationView, type AiMessageView, type AiProvenanceView, type AiScope, type AiSentenceView, type AssistantTask, type AiSourceView,
} from "../ai/assistantApi";
import type { AnswerEffort } from "../ai/answerEffort";
import type { Proposal } from "./proposals";
import { applyStep, completeAgentMessage, stepLabel, type AgentStepView } from "./agentStream";
import { useAiCapabilities } from "../composables/useAiCapabilities";
import { buildShellHashEncoded } from "../shellRoute";

/**
 * The state of the Assistant (plan phase 3 §15), one for the whole app: whether the panel is open, the scope, the current
 * conversation with its messages, the exchange in flight and the draft of the next question. The panel and the entries
 * (sidebar, commands, „Zeptat se na stránku“) only read and call this; nothing in here draws.
 *
 * - The conversation lives in the database (Q4). Here is only what the screen shows; the draft is memory only and survives
 *   closing the panel, not the session.
 * - One exchange is `runAnswerTurn` (ai/answerTurn.ts): prepare, then complete; stopping aborts the complete request.
 * - Losing the `assistant` capability closes the panel unless an exchange is in flight or has just ended as unavailable. Then
 *   the panel stays, the answer place holds the neutral sentence and the composer is gone; closing it is final (AI-25, O8).
 */

export type ScopeChoice = "page" | "space" | "wiki";

/** What the screen behind the panel is about. `pageId` is only set while a published page is being read (O12). */
export interface AssistantContext {
  pageId: string | null;
  pageTitle: string | null;
  spaceId: string | null;
  spaceName: string | null;
  /** The person may write in the space: only then are wiki tasks (proposals they could confirm) offered. */
  canWrite?: boolean;
}

// The notices are read where they are shown.
export const secretQueryNotice = () => t.notice.secretQuery;
/** Without answers from the wiki (no index) only the agent can answer, and it runs only when the user turns it on (D14a, AD5-3). */
export const toolsOnlyNotice = () => t.notice.toolsOnly;
export { QUESTION_MAX_LENGTH };
/** How a selected passage of the page travels with the next question („Zeptat se“ in the selection toolbar). */
export const SELECTION_PREFIX = "\n\nVýběr ze stránky:\n";
/** The prefix names what was pointed at („Prvek tabule:“, „Sekce „Úvod“:“); a plain selection keeps „Výběr ze stránky:“ (P11). */
export const selectionPrefix = (target?: string) => (!target || target === "Výběr" ? SELECTION_PREFIX : `\n\n${aiTarget(target)}:\n`);

export type AnswerItemState = Exclude<AnswerTurnState, "canceled">;

export type ThreadItem =
  | { kind: "question"; key: string; text: string }
  | { kind: "scope"; key: string; text: string }
  | {
    kind: "answer"; key: string; messageId: string | null; question: string; state: AnswerItemState;
    sentences: AiSentenceView[]; sources: AiSourceView[]; provenance: AiProvenanceView | null; createdAt: string | null;
    rating: "up" | "down" | null; scope: AiScope | null; truncated?: boolean; text?: string; steps?: AgentStepView[];
    proposalId?: string | null; proposal?: Proposal | null;
  };

/** `outdated`: another provider, location or model since it began; `unavailable`: a source was withdrawn. Both read-only. */
export type ReadOnlyReason = "outdated" | "unavailable" | null;

export const NO_CONTEXT: AssistantContext = { pageId: null, pageTitle: null, spaceId: null, spaceName: null };

const open = ref(false);
const view = ref<"thread" | "list">("thread");
const context = ref<AssistantContext>(NO_CONTEXT);
const scopeChoice = ref<ScopeChoice>("wiki");
const scopeTouched = ref(false);
/** The effort of the next question; memory only, kept across conversations until sign-out. */
const effort = ref<AnswerEffort>("quick");
/** „S nástroji“ (D14): the next questions use the agent. Off by default (owner, 2026-10-28: tools only when the user turns them on); memory only like the effort, reset at sign-out. Needs the `agent` capability. */
const tools = ref(false);
/** The panel works with answers from the wiki or with the agent alone (the agent needs no index). */
const assistantReachable = () => { const caps = useAiCapabilities().capabilities.value; return caps.assistant || caps.agent; };
/** A question can be answered now: from the wiki (the index), or by the agent once the user turned „S nástroji“ on (D14a). Explicit
 * asks (selection, suggestions) show only then; AI is extra, so what cannot answer is not offered (owner, 2026-10-28). */
const canAnswer = computed(() => { const caps = useAiCapabilities().capabilities.value; return caps.assistant || (caps.agent && tools.value); });
const preference = ref<"context" | "wiki">("context");
const conversationId = ref<string | null>(null);
const items = ref<ThreadItem[]>([]);
const readOnly = ref<ReadOnlyReason>(null);
const draft = ref("");
/** Text selected on the page that goes with the next question (refresh 2.5, „Zeptat se“ on a selection); memory only. */
const selection = ref<string | null>(null);
/** What the attached text is („Výběr“, „Sekce „Úvod““, „Prvek tabule“, „Tabule“): the chip names it and the question's prefix too. */
const selectionTarget = ref("Výběr");
const attachedPrefix = computed(() => selectionPrefix(selectionTarget.value));
const notice = ref("");
const loadError = ref("");
const loading = ref(false);
const outage = ref(false);
const list = ref<{ items: AiConversationSummary[]; next: string | null; loading: boolean; error: string }>({ items: [], next: null, loading: false, error: "" });
const lastQuestions = ref<string[]>([]);
const navigator = shallowRef<((source: AiSourceView, newTab: boolean) => void) | null>(null);

let controller: AbortController | undefined;
let loadController: AbortController | undefined;
let lastScopeKey: string | null = null;
let preferenceLoaded = false;
let preferenceGeneration = 0;
let counter = 0;
let scope: ReturnType<typeof effectScope> | undefined;
const nextKey = () => `m${++counter}`;

const pending = computed(() => items.value.some((item) => item.kind === "answer" && (item.state === "retrieving" || item.state === "preparing")));
const scopeOptions = computed<ScopeChoice[]>(() => {
  const options: ScopeChoice[] = [];
  if (context.value.pageId) options.push("page");
  if (context.value.spaceId) options.push("space");
  options.push("wiki");
  return options;
});
// The open page alone rarely holds the answer: the context default is its space, the page stays a choice (owner, 2026-10-20).
const defaultScope = (): ScopeChoice => preference.value === "wiki" ? "wiki" : scopeOptions.value.includes("space") ? "space" : "wiki";
/** The scope the next question uses: the chosen one while the screen still offers it, else the nearest wider one. */
const activeScope = computed<ScopeChoice>(() => {
  for (const candidate of [scopeChoice.value, "space", "wiki"] as ScopeChoice[]) if (scopeOptions.value.includes(candidate)) return candidate;
  return "wiki";
});
const canCompose = computed(() => !loading.value && !outage.value && !readOnly.value);
/** Room for the typed question: the attached selection travels in the same question and counts against the limit. */
const questionLimit = computed(() => QUESTION_MAX_LENGTH - (selection.value ? attachedPrefix.value.length + selection.value.length : 0));

export function scopeLabel(choice: ScopeChoice, ctx: AssistantContext = context.value) {
  if (choice === "page") return t.scope.page;
  if (choice === "space") return t.scope.space(ctx.spaceName);
  return t.scope.wiki;
}

function resolveScope(choice: ScopeChoice = activeScope.value, ctx: AssistantContext = context.value): AiScope {
  if (choice === "page" && ctx.pageId) return { kind: "page", pageId: ctx.pageId, spaceId: ctx.spaceId };
  if (choice === "space" && ctx.spaceId) return { kind: "space", spaceId: ctx.spaceId };
  return { kind: "wiki" };
}
const scopeKeyOf = (value: AiScope) => `${value.kind}:${value.pageId ?? value.spaceId ?? ""}`;

function scopeDividerText(choice: ScopeChoice) {
  const label = choice === "page" && context.value.pageTitle ? t.scope.pageTitled(context.value.pageTitle) : scopeLabel(choice);
  return t.scope.changed(label);
}

// --- Items of the thread -------------------------------------------------------------------------------------------------
/** The card reports its current state (loaded, decided, expired) so the thread item keeps it. */
function setProposal(key: string, proposal: Proposal) {
  const item = answerItem(key);
  if (item) item.proposal = proposal;
}
function answerItem(key: string) {
  const found = items.value.find((item) => item.key === key);
  return found?.kind === "answer" ? found : undefined;
}

function fromMessages(messages: readonly AiMessageView[]): ThreadItem[] {
  const result: ThreadItem[] = [];
  for (const message of messages) {
    if (message.role === "user") {
      if (message.question) result.push({ kind: "question", key: nextKey(), text: message.question });
      continue;
    }
    const state = message.state;
    if (state === "canceled" || state === "preparing" || !state) continue;
    const mapped: AnswerItemState = state === "answered" ? "done" : state === "failed" ? "error" : state;
    result.push({
      kind: "answer", key: nextKey(), messageId: message.id, question: message.question ?? "", state: mapped,
      sentences: mapped === "done" ? message.sentences : [], sources: mapped === "done" ? message.sources : [],
      provenance: message.provenance ?? null, createdAt: message.createdAt, rating: message.rating ?? null, scope: message.scope ?? null, truncated: message.truncated ?? false,
      proposalId: message.proposalId ?? null,
      steps: mapped === "done" ? message.steps?.map((step, index) => ({ id: `s${index}`, label: stepLabel(step.tool, step.target), state: step.ok === false ? "failed" as const : "done" as const })) : undefined,
    });
  }
  return result;
}

function apply(key: string, update: AnswerTurnUpdate) {
  if (update.state === "canceled") {
    items.value = items.value.filter((item) => item.key !== key);
    return;
  }
  const item = answerItem(key);
  if (!item) return;
  if (update.conversationId) conversationId.value = update.conversationId;
  if (update.messageId) item.messageId = update.messageId;
  if (update.state === "retrieving") item.steps = [];
  item.state = update.state;
  item.sources = update.sources; item.truncated = update.truncated ?? false;
  item.text = update.text;
  if (update.state === "done" && update.message) {
    item.sentences = update.message.sentences;
    item.sources = update.message.sources;
    item.provenance = update.message.provenance ?? null;
    item.createdAt = update.message.createdAt;
    item.rating = update.message.rating ?? null;
    item.scope = update.message.scope ?? item.scope;
    item.proposalId = update.message.proposalId ?? item.proposalId ?? null;
  }
}

// --- Asking -------------------------------------------------------------------------------------------------------------
export interface AskOptions { /** Re-asking a failed exchange: the scope divider and the history of questions stay as they are. */ retry?: boolean }

async function ask(raw: string, options: AskOptions = {}): Promise<boolean> {
  const typed = raw.trim();
  if (!typed || controller || !canCompose.value || !assistantReachable()) return false;
  // A selection attached from the page goes with this question, in its text: the server sees exactly what the thread shows.
  const attached = options.retry ? null : selection.value;
  const question = attached ? `${typed}${attachedPrefix.value}${attached}` : typed;
  if (question.length > QUESTION_MAX_LENGTH) return false;
  if (detectSecrets(question).length > 0) { notice.value = t.notice.secretQuery; return false; }
  const caps = useAiCapabilities().capabilities.value;
  if (!caps.assistant && !tools.value) { notice.value = t.notice.toolsOnly; return false; }
  notice.value = "";
  const scopeValue = resolveScope();
  const key = scopeKeyOf(scopeValue);
  if (!options.retry) {
    if (lastScopeKey !== null && lastScopeKey !== key) items.value.push({ kind: "scope", key: nextKey(), text: scopeDividerText(activeScope.value) });
    lastQuestions.value = [...lastQuestions.value.filter((q) => q !== typed), typed].slice(-20);
  }
  lastScopeKey = key;
  if (draft.value.trim() === typed) draft.value = "";
  if (attached) selection.value = null;
  const id = nextKey();
  // A new message supersedes every open proposal (the server does the same); the card turns read-only at once.
  for (const item of items.value) if (item.kind === "answer" && item.proposal?.state === "pending") item.proposal = { ...item.proposal, state: "superseded" };
  items.value.push({ kind: "question", key: nextKey(), text: question });
  items.value.push({ kind: "answer", key: id, messageId: null, question, state: "retrieving", sentences: [], sources: [], provenance: null, createdAt: null, rating: null, scope: scopeValue });

  const mine = new AbortController();
  controller = mine;
  // Without the `agent` capability the request stays as it always was (no task); with it the switch decides, also without the index.
  const task: AssistantTask | undefined = caps.agent ? (tools.value ? "assistant.agent" : "chat.answer") : undefined;
  const complete = task === "assistant.agent"
    ? (messageId: string, signal: AbortSignal) => completeAgentMessage(messageId, signal, {
      onStep: (event) => { const item = answerItem(id); if (item) item.steps = applyStep(item.steps ?? [], event); },
      onProposal: (proposal) => { const item = answerItem(id); if (item && proposal) item.proposal = proposal as Proposal; },
    })
    : completeMessage;
  // A model that times out or is busy answers 503 too: while the capability survives the refresh, it is a failure to retry, not an outage.
  const completeOrFail = async (messageId: string, signal: AbortSignal) => {
    try { return await complete(messageId, signal); } catch (failure) {
      if (failure instanceof ApiError && failure.code === "ai_unavailable") {
        await useAiCapabilities().refresh();
        if (assistantReachable()) throw new Error("model_unavailable");
      }
      throw failure;
    }
  };
  let blocked: ReadOnlyReason = null;
  const prepare = async (signal: AbortSignal) => {
    try {
      return conversationId.value
        ? await askFollowUp(conversationId.value, { question, scope: scopeValue, effort: effort.value, task }, signal)
        : await startConversation({ origin: "assistant", question, scope: scopeValue, effort: effort.value, task }, signal);
    } catch (failure) {
      if (failure instanceof ApiError && failure.status === 409) {
        if (failure.code === "conversation_outdated") blocked = "outdated";
        else if (failure.code === "conversation_unavailable") blocked = "unavailable";
      }
      throw failure;
    }
  };
  const last = await runAnswerTurn({ prepare, complete: completeOrFail, signal: mine.signal, onUpdate: (update) => apply(id, update) });
  if (controller !== mine) return false;
  controller = undefined;
  if (last.state === "unavailable") outage.value = true;
  if (blocked) {
    // The conversation cannot take another question: take the exchange back, keep the question for a new conversation.
    removeExchange(id);
    readOnly.value = blocked;
    draft.value = question;
    return false;
  }
  if (outage.value) {
    const item = answerItem(id);
    if (item) { item.state = "unavailable"; item.text = ai.unavailable; item.sentences = []; item.sources = []; }
  }
  return last.state === "done";
}

function removeExchange(answerKey: string) {
  const index = items.value.findIndex((item) => item.key === answerKey);
  if (index < 0) return;
  const drop = items.value[index - 1]?.kind === "question" ? [index - 1, index] : [index];
  items.value = items.value.filter((_, i) => !drop.includes(i));
}

/** The first Escape: ends the running exchange; the thread keeps the question. */
function stop() {
  controller?.abort();
}

/** Asks the same question again after an error: the failed exchange is replaced. */
async function retry(key: string) {
  const index = items.value.findIndex((item) => item.key === key);
  const item = items.value[index];
  if (!item || item.kind !== "answer" || controller) return false;
  removeExchange(key);
  return ask(item.question, { retry: true });
}

// --- Conversation ---------------------------------------------------------------------------------------------------------
function clearConversation() {
  controller?.abort(); controller = undefined; selection.value = null;
  loadController?.abort(); loadController = undefined; loading.value = false;
  conversationId.value = null; items.value = []; readOnly.value = null; notice.value = ""; loadError.value = "";
  lastScopeKey = null; outage.value = false;
}

function newConversation() {
  clearConversation();
  view.value = "thread";
  scopeTouched.value = false;
  scopeChoice.value = defaultScope();
}

/** Opens a conversation of „Moje konverzace“ or one handed over from omnisearch (S6); lists it, never asks the model again. */
async function continueFrom(id: string) {
  if (!assistantReachable() && !open.value) return false;
  clearConversation();
  open.value = true; view.value = "thread"; loading.value = true;
  const mine = new AbortController();
  loadController = mine;
  try {
    const result: AiConversationView = await continueConversation(id, mine.signal);
    if (loadController !== mine) return false;
    conversationId.value = result.id;
    items.value = fromMessages(result.messages);
    readOnly.value = result.outdated ? "outdated" : !result.available || !result.canContinue ? "unavailable" : null;
    const lastAnswered = [...result.messages].reverse().find((m) => m.scope);
    lastScopeKey = lastAnswered?.scope ? scopeKeyOf(lastAnswered.scope) : null;
    return true;
  } catch {
    if (loadController !== mine) return false;
    conversationId.value = null;
    loadError.value = t.notice.loadFailed;
    return false;
  } finally {
    if (loadController === mine) { loadController = undefined; loading.value = false; }
  }
}

async function deleteCurrent() {
  const id = conversationId.value;
  if (!id) { newConversation(); return true; }
  try { await deleteConversation(id); } catch { notice.value = t.notice.deleteFailed; return false; }
  newConversation();
  return true;
}

async function deleteAll() {
  try { await deleteAllConversations(); } catch { list.value.error = t.notice.deleteAllFailed; return false; }
  clearHistory();
  return true;
}

/** Forget deleted conversations, including an active load and cached history, without resetting account preferences. */
function clearHistory() {
  newConversation();
  lastQuestions.value = [];
  list.value = { items: [], next: null, loading: false, error: "" };
}

async function loadList(more = false) {
  if (list.value.loading) return;
  const mine = list.value;
  mine.loading = true; mine.error = "";
  try {
    const page = await listConversations(more ? mine.next : null);
    mine.items = more ? [...mine.items, ...page.items] : page.items;
    mine.next = page.next ?? null;
  } catch { mine.error = t.notice.listFailed; }
  finally { mine.loading = false; }
}

function showList() {
  view.value = "list";
  void loadList();
}
function showThread() { view.value = "thread"; }

// --- Opening --------------------------------------------------------------------------------------------------------------
async function ensurePreference() {
  if (preferenceLoaded) return;
  return refreshPreference();
}

/** Reload a saved default without changing the scope of an existing conversation or explicit choice. */
async function refreshPreference() {
  preferenceLoaded = true;
  const generation = ++preferenceGeneration;
  try {
    const result = await loadAssistantScopePreference();
    if (generation !== preferenceGeneration) return;
    preference.value = result;
  } catch { if (generation === preferenceGeneration) preferenceLoaded = false; return; }
  if (!scopeTouched.value && !items.value.length) scopeChoice.value = defaultScope();
}

function setContext(next: AssistantContext | null) {
  context.value = next ?? NO_CONTEXT;
}

/** Opens the panel for what is on screen. An existing conversation stays; a new one starts with the default scope. */
function openFor(next: AssistantContext | null = context.value) {
  if (!assistantReachable()) return false;
  setContext(next);
  if (!open.value && !items.value.length) { scopeTouched.value = false; scopeChoice.value = defaultScope(); }
  view.value = "thread";
  open.value = true;
  void ensurePreference();
  return true;
}

/**
 * Attaches text pointed at to the next question (or removes it with `null`), named by its `target` („Prvek tabule“, „Sekce „Úvod““);
 * the scope becomes the page.
 */
function attachSelection(text: string | null, target?: string) {
  const value = text?.trim() || null, prefix = selectionPrefix(target);
  if (value && value.length + prefix.length >= QUESTION_MAX_LENGTH) return false;
  selection.value = value;
  selectionTarget.value = target ? aiTarget(target) : "Výběr";
  if (value) chooseScope("page");
  return true;
}

function chooseScope(choice: ScopeChoice) {
  if (scopeOptions.value.includes(choice)) { scopeChoice.value = choice; scopeTouched.value = true; }
}

function close() {
  if (!open.value) return;
  open.value = false;
  // Closing after an outage is final: nothing of the conversation stays on screen.
  if (outage.value && !pending.value) { clearConversation(); view.value = "thread"; }
}

/** Sign-out and tests: forgets everything of the account. */
function reset() {
  preferenceGeneration++;
  clearConversation();
  open.value = false; view.value = "thread"; context.value = NO_CONTEXT; scopeChoice.value = "wiki"; scopeTouched.value = false; effort.value = "quick"; tools.value = false;
  preference.value = "context"; preferenceLoaded = false; draft.value = ""; lastQuestions.value = [];
  list.value = { items: [], next: null, loading: false, error: "" }; navigator.value = null; editor.value = null;
}

// --- Citations and feedback -----------------------------------------------------------------------------------------------
export function sourceHref(source: Pick<AiSourceView, "pageId" | "headingId">) {
  return buildShellHashEncoded({ view: "page", pageId: source.pageId, heading: source.headingId ?? undefined });
}

/** A citation opens the page in the main area and leaves the panel open; Ctrl+click opens another tab. */
function openSource(source: AiSourceView, newTab: boolean) {
  if (newTab) { window.open(`${location.pathname}${location.search}${sourceHref(source)}`, "_blank", "noopener"); return; }
  if (navigator.value) navigator.value(source, false);
  else location.hash = sourceHref(source);
}
function setNavigator(next: ((source: AiSourceView, newTab: boolean) => void) | null) { navigator.value = next; }
/** „Otevřít v editoru“ of an applied draft proposal (spec §7.2): the shell opens the page with its live editor attached; without one, the page. */
const editor = shallowRef<((pageId: string) => void) | null>(null);
function setEditor(next: ((pageId: string) => void) | null) { editor.value = next; }
function openEditor(pageId: string) {
  if (editor.value) editor.value(pageId);
  else location.hash = sourceHref({ pageId, headingId: null });
}

async function rate(key: string, rating: "up" | "down") {
  const item = answerItem(key);
  if (!item?.messageId) return;
  const before = item.rating;
  item.rating = rating;
  if (rating === "down") return; // the reason follows (`sendReason`); the rating alone is saved with it
  try { await sendFeedback(item.messageId, { rating }); } catch { item.rating = before; }
}
async function sendReason(key: string, feedback: { rating: "down"; reason: string | null; note: string | null }) {
  const item = answerItem(key);
  if (!item?.messageId) return;
  try { await sendFeedback(item.messageId, { rating: "down", reason: feedback.reason as never, note: feedback.note }); } catch { item.rating = null; }
}

// --- Outage ---------------------------------------------------------------------------------------------------------------
function watchCapability() {
  if (scope) return;
  scope = effectScope(true);
  scope.run(() => {
    const { capabilities } = useAiCapabilities();
    watch(() => capabilities.value.assistant || capabilities.value.agent, (on) => {
      if (on) { if (!pending.value) outage.value = false; return; }
      if (pending.value || outage.value) { outage.value = true; return; }
      if (open.value) close();
    });
  });
}

export function useAssistant() {
  watchCapability();
  return {
    open, view, context, scopeChoice: activeScope, scopeOptions, items, readOnly, draft, selection, selectionTarget, questionLimit, notice, loadError, loading, outage, list, lastQuestions, pending,
    conversationId, canCompose, canAnswer, effort, tools, setProposal,
    scopeLabel, openFor, setContext, chooseScope, attachSelection, close, ask, stop, retry, newConversation, continueFrom, deleteCurrent, deleteAll,
    refreshPreference, clearHistory, showList, showThread, loadList, openSource, setNavigator, openEditor, setEditor, rate, sendReason, reset,
  };
}
