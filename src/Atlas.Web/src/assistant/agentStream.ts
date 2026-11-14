import { ApiError, apiResponse } from "../api";
import type { AiMessageView } from "../ai/assistantApi";
import t from "../i18n/assistant";

/**
 * The agent turn stream (spec S6): `POST /api/ai/messages/{id}/complete` with `Accept: application/x-ndjson` answers with one
 * JSON object per line (UTF-8, `\n`), `Content-Type: application/x-ndjson`. The server (WP-14) emits exactly this:
 *
 *   {"type":"step","id":"s1","phase":"start","tool":"atlas_get_page","target":"Instalace VPN"}
 *   {"type":"step","id":"s1","phase":"end","tool":"atlas_get_page","target":"Instalace VPN","ok":true}
 *   {"type":"proposal","proposal":{...}}                      (F2, WP-20; the panel only passes it on)
 *   {"type":"message","message":<AiMessageView>}              last line of a finished turn; state answered | no_sources | withheld | failed
 *   {"type":"error","code":"ai_unavailable","status":503}     last line of a turn that broke; `status` optional (default 502)
 *
 * - `step`: `id` is unique within the turn and joins `start` and `end`; `tool` is the MCP tool name (`atlas_*`, never shown);
 *   `target` is an optional display name the model's call concerned (page title, space name, search text), at most 80
 *   characters, never page content; `ok` (only on `end`) is false when the tool returned an error. Steps may overlap.
 * - Exactly one `message` or `error` line ends the stream. A stream that ends without one (connection cut, server
 *   restarted) is a broken turn: the reader throws `ApiError(502, …, "stream_interrupted")` and the panel shows the answer error.
 * - Unknown line types and unknown fields are ignored (forward compatible); a line that is not JSON is a broken stream.
 * - `AiMessageView.steps` (optional) repeats the finished steps for a reopened conversation: `{ tool, target?, ok }[]`.
 * - Without `Accept: application/x-ndjson` the endpoint keeps answering one `AiMessageView` as JSON (task `chat.answer`).
 */

export interface AgentStepEvent { id: string; phase: "start" | "end"; tool: string; target?: string | null; ok?: boolean }
export interface AgentStepView { id: string; label: string; state: "now" | "done" | "failed" }
export interface AgentCallbacks { onStep?: (event: AgentStepEvent) => void; onProposal?: (proposal: unknown) => void }


/** „Čtu stránku Instalace VPN“: the verb of the tool and, when the server named one, what it concerns. */
export function stepLabel(tool: string, target?: string | null) {
  const verb = t.agent.verbs[tool] ?? t.agent.fallbackVerb;
  return target?.trim() ? `${verb} ${target.trim()}` : verb;
}

const interrupted = () => new ApiError(502, t.agent.interrupted, "stream_interrupted");

/** Reads the NDJSON body to its end; resolves with the final message. Aborting rejects with the fetch's own AbortError. */
export async function readAgentStream(body: ReadableStream<Uint8Array> | null, callbacks: AgentCallbacks = {}): Promise<AiMessageView> {
  if (!body) throw interrupted();
  const reader = body.getReader();
  const decoder = new TextDecoder();
  let buffer = "";
  let message: AiMessageView | null = null;
  const handle = (line: string) => {
    if (!line.trim()) return;
    let event: Record<string, unknown>;
    try { event = JSON.parse(line); } catch { throw interrupted(); }
    if (!event || typeof event !== "object") throw interrupted();
    if (event.type === "step" && typeof event.id === "string" && typeof event.tool === "string" && (event.phase === "start" || event.phase === "end"))
      callbacks.onStep?.({ id: event.id, phase: event.phase, tool: event.tool, target: typeof event.target === "string" ? event.target : null, ok: event.ok !== false });
    else if (event.type === "proposal") callbacks.onProposal?.(event.proposal);
    else if (event.type === "message" && event.message && typeof event.message === "object") message = event.message as AiMessageView;
    else if (event.type === "error")
      throw new ApiError(typeof event.status === "number" ? event.status : 502, t.agent.failed, typeof event.code === "string" ? event.code : undefined);
  };
  try {
    for (;;) {
      const { done, value } = await reader.read();
      buffer += decoder.decode(value, { stream: !done });
      let newline: number;
      while ((newline = buffer.indexOf("\n")) >= 0) { handle(buffer.slice(0, newline)); buffer = buffer.slice(newline + 1); }
      if (done) break;
    }
    handle(buffer); // a last line the server did not end with a newline
  } finally { reader.releaseLock(); }
  if (!message) throw interrupted();
  return message;
}

/** Folds step events into the list the status line shows: a `start` adds a running step, its `end` closes it. */
export function applyStep(steps: readonly AgentStepView[], event: AgentStepEvent): AgentStepView[] {
  const state = event.phase === "start" ? "now" : event.ok === false ? "failed" : "done";
  const label = stepLabel(event.tool, event.target);
  return steps.some((step) => step.id === event.id)
    ? steps.map((step) => (step.id === event.id ? { id: step.id, label, state } : step))
    : [...steps, { id: event.id, label, state }];
}

/** `completeMessage` for the agent task: the same endpoint asked for NDJSON; steps and proposals go to the callbacks. */
export async function completeAgentMessage(messageId: string, signal?: AbortSignal, callbacks: AgentCallbacks = {}) {
  const response = await apiResponse(`/ai/messages/${encodeURIComponent(messageId)}/complete`, { method: "POST", headers: { Accept: "application/x-ndjson" }, signal });
  return readAgentStream(response.body, callbacks);
}
