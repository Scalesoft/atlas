import { api } from "./api";
import { createDebouncedLookup } from "./lib/debouncedLookup";

/** P2: where the new page would fit (GET /api/pages/placement-suggestion). `path` runs from the top of the space down to the parent itself. */
export interface PlacementSuggestion {
  /** `null` is the root of the space (then `parentTitle` is null and `path` is empty). */
  parentId: string | null;
  parentTitle: string | null;
  path: { id: string; title: string }[];
}

/** P3: a template that suits the title (GET /api/pages/template-suggestion). */
export interface TemplateSuggestion {
  templateId: string;
  title: string;
  matchedBy: "dictionary" | "meaning";
}

export interface CreationSuggestionsApi {
  /** `null` is "nothing to suggest" (204). Needs the `createSuggestions` capability; 503 and errors reject. */
  placement(title: string, spaceId: string, signal?: AbortSignal): Promise<PlacementSuggestion | null>;
  /** `null` is "nothing to suggest" (204). The dictionary part works without AI. */
  template(title: string, spaceId: string, signal?: AbortSignal): Promise<TemplateSuggestion | null>;
}

const query = (title: string, spaceId: string) => new URLSearchParams({ title: title.trim(), spaceId });
const isObject = (value: unknown): value is Record<string, unknown> => !!value && typeof value === "object" && !Array.isArray(value);

/** Strict reading of a placement answer: anything that does not look like one is "nothing to suggest". */
export function parsePlacement(body: unknown): PlacementSuggestion | null {
  if (!isObject(body)) return null;
  const parentId = body.parentId === null || typeof body.parentId === "string" ? (body.parentId as string | null) : undefined;
  if (parentId === undefined) return null;
  const path = Array.isArray(body.path) ? body.path.filter((step): step is { id: string; title: string } => isObject(step) && typeof step.id === "string" && typeof step.title === "string").map(step => ({ id: step.id, title: step.title })) : [];
  const parentTitle = typeof body.parentTitle === "string" ? body.parentTitle : null;
  if (parentId !== null && !parentTitle && !path.length) return null;
  return { parentId, parentTitle, path };
}

export function parseTemplate(body: unknown): TemplateSuggestion | null {
  if (!isObject(body) || typeof body.templateId !== "string" || typeof body.title !== "string") return null;
  return { templateId: body.templateId, title: body.title, matchedBy: body.matchedBy === "meaning" ? "meaning" : "dictionary" };
}

export const creationSuggestionsApi: CreationSuggestionsApi = {
  async placement(title, spaceId, signal) {
    return parsePlacement(await api<unknown>(`/pages/placement-suggestion?${query(title, spaceId)}`, { signal }));
  },
  async template(title, spaceId, signal) {
    return parseTemplate(await api<unknown>(`/pages/template-suggestion?${query(title, spaceId)}`, { signal }));
  },
};

/** Wait this long after the last keystroke. Placement also needs this many characters of the trimmed title. */
export const CREATION_SUGGESTION_DELAY_MS = 400;
export const PLACEMENT_MIN_LENGTH = 8;
/** The server's own floor (3 normalized characters); the dictionary names short words such as "runbook". */
export const TEMPLATE_MIN_LENGTH = 3;

export interface SuggestionWatcherOptions<T> {
  find: (title: string, signal: AbortSignal) => Promise<T | null>;
  /** The suggestion of the latest title, or `null` when there is none (short title, 204, 503, failure). */
  onResult: (value: T | null) => void;
  delayMs?: number;
  minLength?: number;
}

/**
 * The debounced lookup (lib/debouncedLookup.ts) of one suggestion for the title being typed: a late answer for an older
 * title never shows, and a failed lookup is `null`, never an error. Creating the page does not wait for it.
 */
export function createSuggestionWatcher<T>({ find, onResult, delayMs = CREATION_SUGGESTION_DELAY_MS, minLength = PLACEMENT_MIN_LENGTH }: SuggestionWatcherOptions<T>) {
  return createDebouncedLookup<T | null>({ find, onResult, empty: () => null, delayMs, minLength });
}
