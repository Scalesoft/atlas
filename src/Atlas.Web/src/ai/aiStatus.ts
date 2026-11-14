import { sourceCount } from "../components/ai/aiAnswerText";
import t from "../i18n/ai";
import type { AiScope } from "./assistantApi";

/**
 * The words of the one AI status line (components/ai/AiStatus.vue; board „AI znak – velikosti, stavy a průběh“,
 * „Jeden stavový řádek všude, kde AI pracuje“). Every model task of Atlas runs the same two real phases
 * (ai/answerTurn.ts): `retrieving` (the wiki is searched, no model call yet) and `preparing` (the model reads the
 * found sources and writes). The steps name exactly these phases and nothing the server does not report: no fake
 * progress, no invented reading step.
 */
export type AiStepState = "done" | "now" | "todo";
export interface AiStatusStep { label: string; meta?: string | null; state: AiStepState }
export interface AiStatusView {
  /** The state of the AI glyph for the current step (AiGlyph): the search turns its ring, writing pulses. */
  glyph: "searching" | "writing";
  /** The current step as one line („Hledám ve wiki…“). */
  label: string;
  /** What is known of it („3 zdroje“). */
  meta: string | null;
  steps: AiStatusStep[];
}

/** „4 s“, „1 min 5 s“: the time the task has run, for the status line (never announced). */
export function formatElapsed(ms: number) {
  const seconds = Math.max(0, Math.floor(ms / 1000));
  if (seconds < 60) return `${seconds} s`;
  const minutes = Math.floor(seconds / 60);
  const rest = seconds % 60;
  return rest ? `${minutes} min ${rest} s` : `${minutes} min`;
}

export type AiTaskKind = "answer" | "summary";

function searchLabel(kind: AiTaskKind, scope: AiScope | null | undefined, scopeName?: string | null) {
  if (kind === "summary" || scope?.kind === "page") return t.status.readPage;
  if (scope?.kind === "space") return t.status.searchSpace(scopeName);
  return t.status.searchWiki;
}

/**
 * The status of a running answer turn: `retrieving` searches (ring), `preparing` writes (pulse). A finished or failed
 * turn has no status line; the answer or its state takes its place.
 */
export function answerTurnStatus(
  state: string,
  { sources = 0, kind = "answer", scope = null, scopeName = null }: { sources?: number; kind?: AiTaskKind; scope?: AiScope | null; scopeName?: string | null } = {},
): AiStatusView | null {
  const search = searchLabel(kind, scope, scopeName);
  const write = kind === "summary" ? t.status.writeSummary : t.status.writeAnswer;
  if (state === "retrieving") {
    return { glyph: "searching", label: `${search}…`, meta: null, steps: [{ label: `${search}…`, state: "now" }, { label: write, state: "todo" }] };
  }
  if (state === "preparing") {
    const meta = sources > 0 ? sourceCount(sources) : null;
    return { glyph: "writing", label: write, meta, steps: [{ label: search, meta, state: "done" }, { label: write, state: "now" }] };
  }
  return null;
}
