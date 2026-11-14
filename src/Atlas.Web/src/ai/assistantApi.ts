import { api } from "../api";
import type { AnswerEffort } from "./answerEffort";

// Contracts of the answer and assistant endpoints (plan phase 3 §7, §8, §20). Types only describe what the server sends; the
// components are presentational and never call these functions, the answer turn (answerTurn.ts) and later the assistant do.

export interface AiScope {
  kind: "wiki" | "space" | "page";
  spaceId?: string | null;
  pageId?: string | null;
}

/** One source the model was given. The UI draws citations from this list only, never from numbers found in a sentence. */
export interface AiSourceView {
  number: number;
  type: string;
  pageId: string;
  spaceId: string;
  spaceName: string;
  title: string;
  headingTitle?: string | null;
  headingId?: string | null;
  /** At most 320 characters of the chunk, only in a live answer or while the chunk still exists. */
  excerpt?: string | null;
  cited: boolean;
  /** The page was published again after the answer was written. */
  changed: boolean;
}

export interface AiSentenceView {
  text: string;
  /** Numbers of the sources the sentence rests on; a number that is not in `sources` is ignored. */
  sources: number[];
}

export interface AiProvenanceView {
  location: "company" | "cloud";
  provider: string;
  model: string;
}

export type AiMessageState = "preparing" | "answered" | "no_sources" | "withheld" | "failed" | "unavailable" | "canceled";

export interface AiMessageView {
  id: string;
  conversationId: string;
  role: "user" | "assistant";
  state?: AiMessageState | null;
  question?: string | null;
  scope?: AiScope | null;
  sentences: AiSentenceView[];
  sources: AiSourceView[];
  provenance?: AiProvenanceView | null;
  createdAt: string;
  rating?: "up" | "down" | null;
  errorCode?: string | null;
  /** Some page sections exceeded the summary budget. */
  truncated?: boolean;
  /** The tool steps of an agent turn (optional; the stream carries them live, see assistant/agentStream.ts). */
  steps?: { tool: string; target?: string | null; ok?: boolean }[];
  /** WP-20: the write proposal of an answered agent turn; `GET /api/ai/proposals/{id}` reopens its card. */
  proposalId?: string | null;
}

/** The task of a turn: the agent with tools (`assistant.agent`) or the answer from the wiki (`chat.answer`, the default). */
export type AssistantTask = "assistant.agent" | "chat.answer";

export interface AiTurnPrepared {
  truncated?: boolean;
  conversationId: string;
  messageId: string;
  state: "preparing" | "no_sources";
  sources: AiSourceView[];
}

export type AiFeedbackReason = "inaccurate" | "missing_info" | "wrong_sources" | "unsuitable" | "other";
export interface AiFeedbackBody {
  rating: "up" | "down";
  reason?: AiFeedbackReason | null;
  note?: string | null;
}

const json = (body: unknown, method = "POST", signal?: AbortSignal): RequestInit => ({ method, body: JSON.stringify(body), signal });


export function startConversation(request: { origin: "search" | "assistant"; question: string; scope: AiScope; effort?: AnswerEffort; task?: AssistantTask }, signal?: AbortSignal) {
  return api<AiTurnPrepared>("/ai/conversations", json(request, "POST", signal));
}

export function askFollowUp(conversationId: string, request: { question: string; scope: AiScope; effort?: AnswerEffort; task?: AssistantTask }, signal?: AbortSignal) {
  return api<AiTurnPrepared>(`/ai/conversations/${encodeURIComponent(conversationId)}/messages`, json(request, "POST", signal));
}

/** Writes the answer; stopping it is aborting this request. */
export function completeMessage(messageId: string, signal?: AbortSignal) {
  return api<AiMessageView>(`/ai/messages/${encodeURIComponent(messageId)}/complete`, { method: "POST", signal });
}

export function sendFeedback(messageId: string, feedback: AiFeedbackBody, signal?: AbortSignal) {
  return api<void>(`/ai/messages/${encodeURIComponent(messageId)}/feedback`, json(feedback, "PUT", signal));
}


// --- Conversations (plan phase 3 §8, §20; Q4) -------------------------------------------------------------------------
// Everything the assistant panel reads or deletes. These work without AI (they are the user's own data), so a call here is
// never gated by a capability; whether the panel is shown at all is.

export interface AiConversationSummary {
  id: string;
  /** `null` = the conversation is no longer available (a source was withdrawn); never guess a title for it. */
  title: string | null;
  available: boolean;
  lastActivityAt: string;
  provenance?: AiProvenanceView | null;
}

export interface AiConversationList {
  items: AiConversationSummary[];
  /** Cursor for `before` of the next page, `null` at the end. */
  next?: string | null;
}

export interface AiConversationView {
  id: string;
  available: boolean;
  /** False when the conversation cannot be continued: unavailable sources or an incompatible chat profile (outdated). */
  canContinue: boolean;
  /** The chat profile changed to another provider, location or model since the conversation began. Read-only. */
  outdated: boolean;
  provenance?: AiProvenanceView | null;
  messages: AiMessageView[];
}

export function listConversations(before?: string | null, signal?: AbortSignal) {
  return api<AiConversationList>(`/ai/conversations${before ? `?before=${encodeURIComponent(before)}` : ""}`, { signal });
}

/** „Pokračovat v asistentovi“ (S6): lists a search conversation and returns it. No model call. Idempotent. */
export function continueConversation(id: string, signal?: AbortSignal) {
  return api<AiConversationView>(`/ai/conversations/${encodeURIComponent(id)}/continue`, { method: "POST", signal });
}

export function deleteConversation(id: string) {
  return api<void>(`/ai/conversations/${encodeURIComponent(id)}`, { method: "DELETE" });
}

export function deleteAllConversations() {
  return api<void>("/ai/conversations", { method: "DELETE" });
}

/** The user's default scope of the assistant (`GET /api/me/ai-preferences`, `assistantScope`); anything unknown is `context`. */
export async function loadAssistantScopePreference(signal?: AbortSignal): Promise<"context" | "wiki"> {
  const body = await api<{ assistantScope?: unknown }>("/me/ai-preferences", { signal });
  return body?.assistantScope === "wiki" ? "wiki" : "context";
}

/** Automatic answers are the default; explicit on-demand or disabled preferences remain in effect. */
export async function loadSearchAnswerPreference(signal?: AbortSignal): Promise<"ask" | "auto" | "never"> {
  const body = await api<{ searchAnswer?: unknown }>("/me/ai-preferences", { signal });
  return body?.searchAnswer === "ask" || body?.searchAnswer === "never" ? body.searchAnswer : "auto";
}

export function preparePageSummary(pageId: string, signal?: AbortSignal) {
  return api<AiTurnPrepared>("/assistant/summary", json({ pageId }, "POST", signal));
}
