import { notifyAiUsageChanged } from "./quotaUsage";
import { ApiError } from "../api";
import t from "../i18n/ai";
import { runAiAction } from "./aiAction";
import type { AiMessageView, AiSourceView, AiTurnPrepared } from "./assistantApi";

/** What the answer card shows during and after one exchange (UX design §5.3). `canceled` has no card: the person stopped it. */
export type AnswerTurnState = "retrieving" | "preparing" | "done" | "no_sources" | "withheld" | "unavailable" | "error" | "limit" | "canceled";

/** The sentence of a reached usage limit, read where it is shown. */
export const aiLimitText = () => t.limit;

export interface AnswerTurnUpdate {
  state: AnswerTurnState;
  /** The sources found for this turn, from `preparing` on. */
  sources: AiSourceView[];
  conversationId?: string;
  messageId?: string;
  /** The finished message, with `done`. */
  message?: AiMessageView;
  /** The neutral sentence of `unavailable`, the limit text of `limit`. */
  text?: string;
  truncated?: boolean;
}

export interface AnswerTurnDeps {
  /** Starts or continues the conversation: retrieval, no model call yet. */
  prepare: (signal: AbortSignal) => Promise<AiTurnPrepared>;
  /** Writes and validates the answer (the one model call). */
  complete: (messageId: string, signal: AbortSignal) => Promise<AiMessageView>;
  signal?: AbortSignal;
  onUpdate?: (update: AnswerTurnUpdate) => void;
}

/**
 * One exchange as a state machine: retrieving, then preparing, then an end state. Every update goes through `onUpdate` and the
 * last one is returned. Nothing is thrown for a failure the person can see: 503 ai_unavailable ends as the neutral sentence
 * (runAiAction), 429 as `limit`, 502 and anything unexpected as `error`. A `sources_changed` failure (a source was published
 * again during the answer) starts the whole turn once more, because the old revision may hold text its author removed; a second
 * one is an error. Aborting ends as `canceled` and writes nothing further.
 */
export async function runAnswerTurn({ prepare, complete, signal, onUpdate }: AnswerTurnDeps): Promise<AnswerTurnUpdate> {
  const controller = new AbortController();
  const stop = () => controller.abort();
  if (signal?.aborted) controller.abort();
  signal?.addEventListener("abort", stop, { once: true });

  let last: AnswerTurnUpdate = { state: "retrieving", sources: [] };
  const emit = (update: AnswerTurnUpdate) => {
    last = update;
    if (!controller.signal.aborted || update.state === "canceled") onUpdate?.(update);
    return update;
  };
  const canceled = () => emit({ state: "canceled", sources: last.sources, conversationId: last.conversationId, messageId: last.messageId });
  const failure = (error: unknown): AnswerTurnUpdate => {
    if (controller.signal.aborted) return canceled();
    if (error instanceof ApiError && (error.code === "ai_limit" || error.status === 429)) return emit({ state: "limit", sources: [], text: t.limit });
    return emit({ state: "error", sources: last.sources, conversationId: last.conversationId, messageId: last.messageId });
  };

  try {
    for (let attempt = 0; attempt < 2; attempt++) {
      emit({ state: "retrieving", sources: [] });
      let prepared: AiTurnPrepared;
      try {
        const result = await runAiAction(() => prepare(controller.signal));
        if (controller.signal.aborted) return canceled();
        if (result.state === "unavailable") return emit({ state: "unavailable", sources: [], text: result.text });
        prepared = result.value;
      } catch (error) { return failure(error); }

      const ids = { conversationId: prepared.conversationId, messageId: prepared.messageId };
      if (prepared.state === "no_sources") return emit({ state: "no_sources", sources: [], ...ids });
      emit({ state: "preparing", sources: prepared.sources, truncated: prepared.truncated ?? false, ...ids });

      let message: AiMessageView;
      try {
        const result = await runAiAction(() => complete(prepared.messageId, controller.signal));
        if (controller.signal.aborted) return canceled();
        if (result.state === "unavailable") return emit({ state: "unavailable", sources: [], text: result.text, ...ids });
        message = result.value;
      } catch (error) { return failure(error); }

      if (message.state === "answered") return emit({ state: "done", sources: message.sources, truncated: message.truncated ?? false, message, ...ids });
      if (message.state === "no_sources") return emit({ state: "no_sources", sources: [], message, ...ids });
      if (message.state === "withheld") return emit({ state: "withheld", sources: [], ...ids });
      if (message.state === "failed" && message.errorCode === "sources_changed" && attempt === 0) continue;
      return emit({ state: "error", sources: prepared.sources, ...ids });
    }
    return emit({ state: "error", sources: last.sources });
  } finally {
    signal?.removeEventListener("abort", stop);
    notifyAiUsageChanged();
  }
}
