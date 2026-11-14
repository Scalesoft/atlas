import { api } from "../api";

export { notifyAiUnavailable, onAiUnavailable } from "./unavailableBus";

/** What the server says the signed-in user may use (GET /api/ai/capabilities). Booleans only: no providers, models or reasons. */
export interface AiCapabilities {
  semanticSearch: boolean;
  related: boolean;
  editorLinks: boolean;
  createSuggestions: boolean;
  /** Answer from the wiki in omnisearch (phase 3). */
  answer: boolean;
  /** The Assistant panel (phase 3). */
  assistant: boolean;
  editorIntent: boolean; editorTransform: boolean; editorOutline: boolean;
  publishCheck: boolean; changeSummary: boolean; pageSummary: boolean; tagSuggestions: boolean; agent: boolean;
  /** The agent may propose the left menu of a space as one batch of moves (assistant.arrange-pages). */
  arrangePages: boolean;
}

export const AI_CAPABILITY_KEYS = ["semanticSearch", "related", "editorLinks", "createSuggestions", "answer", "assistant", "editorIntent", "editorTransform", "editorOutline", "publishCheck", "changeSummary", "pageSummary", "tagSuggestions", "agent", "arrangePages"] as const;

/** Nothing available: the default, and the answer to any failure. A later phase's unknown field is ignored; a missing one is false. */
export function noAiCapabilities(): AiCapabilities {
  return { semanticSearch: false, related: false, editorLinks: false, createSuggestions: false, answer: false, assistant: false, editorIntent: false, editorTransform: false, editorOutline: false, publishCheck: false, changeSummary: false, pageSummary: false, tagSuggestions: false, agent: false, arrangePages: false };
}

/** Strict reading of a response body: only a literal `true` counts. */
export function parseAiCapabilities(body: unknown): AiCapabilities {
  const result = noAiCapabilities();
  if (body && typeof body === "object" && !Array.isArray(body))
    for (const key of AI_CAPABILITY_KEYS) result[key] = (body as Record<string, unknown>)[key] === true;
  return result;
}

export async function fetchAiCapabilities(signal?: AbortSignal): Promise<AiCapabilities> {
  return parseAiCapabilities(await api<unknown>("/ai/capabilities", { signal }));
}
