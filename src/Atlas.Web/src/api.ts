import type { LanguageChoice } from "./i18n";
import t from "./i18n/shell";
import type { SpaceCover } from "./spaceCover";
import type { PageDecoration } from './pageDecorations';
import { notifyAiUnavailable } from './ai/unavailableBus';
export interface Appearance {
  layout: "connected" | "islands";
  /** profile.ts themeAccents lists them with names and hues. */
  accent: "blue" | "green" | "red" | "ocean" | "sunset" | "indigo" | "violet" | "amber";
  mode: "system" | "light" | "dark";
  /** "reduce" always reduces motion; "system" (or missing, from an older server) follows the device. */
  motion?: "system" | "reduce";
  /** "Stránky otevírat: jen pro čtení" (3.6): pages and boards open with editing locked; missing from an older server means false. */
  openLocked?: boolean;
  /** "Přesouvat stránky tažením" (3.6): tree rows can be dragged; missing from an older server means true. */
  dragPages?: boolean;
  /** „Jazyk“ (4.1): the language of the tool; "auto" (or missing, from an older server) leaves it to the browser (src/i18n). */
  language?: LanguageChoice;
  /** „Moje barva“ (5.2): "auto" (or missing, from an older server) or an id of the collaborator palette (collaboratorColors.ts). */
  collaboratorColor?: string;
}
export interface User {
  id: string;
  username: string;
  displayName: string;
  isWikiAdmin: boolean;
  appearance: Appearance;
  /** Wiki switch S7b combined with the user's preference U1; missing (an older server) means off. `disabledSuggestionTypes`
   * is omitted by the server when empty (read a missing one as []). */
  features?: { editorAssist?: boolean; mcp?: boolean; disabledSuggestionTypes?: string[] };
  /** The release whose notes („Co je nového“) the account has seen; null for a new account (whatsNew/notice.ts). */
  releaseNotesSeen?: string | null;
}
export interface Space {
  description?: string;
  id: string;
  name: string;
  icon: string;
  color: string;
  role: "Reader" | "Writer" | "Admin";
  /** The account reaches only single pages of the space (name, icon and colour only; role is "Reader"). */
  itemAccessOnly?: boolean;
  /** The space administrator's rule: None (each reader pins it), Default (pinned for everyone, can be unpinned), Required (always pinned). */
  pinPolicy: SpacePinPolicy;
  /** Effective pin: Required, or the account's own choice, or Default when it made none. */
  pinned: boolean;
  /** Pages of the tree the account sees (no trash, no templates). */
  pageCount: number;
  /** The newest revision the account may see on a visible page (working for Writer and above, published for readers). */
  lastChangedAt: string | null;
  /** The image of the space with its source (spaceCover.ts); omitted or null only by an older server, which reads as no image. */
  cover?: SpaceCover | null;
}
export type SpacePinPolicy = "None" | "Default" | "Required";
export interface PageSummary {
  decoration?: PageDecoration | null;
  id: string;
  spaceId: string;
  parentId: string | null;
  title: string;
  version: number;
  isPublished: boolean;
  /** Ověřený obsah (5.2): the state of the page's verification when the space has it on; the link chip's shield. */
  verificationState?: import("./verification/pageVerification").VerificationState;
}
/**
 * What `GET /pages/{id}?bundle=true` answers beside the page (server PageBundle): each part as its own endpoint would, null when that part
 * could not be read (the client then asks for it alone).
 */
export interface PageBundle {
  personalState?: { isFavorite: boolean; visitedAt: string | null } | null;
  tags?: { tags: { id: string; name: string }[] } | null;
  contacts?: { items: Record<string, { state: "ok" | "hidden" | "trashed" | "missing"; contactId?: string; contact?: unknown }> } | null;
  pages?: { items: { id: string; title: string; type?: string; verificationState?: string }[] } | null;
  /** Ověřený obsah (5.2): the page's verification, so the chip draws at once; absent from a server without it. */
  verification?: import("./verification/pageVerification").PageVerification | null;
  /** Distinct readers of the page in the last 90 days (the panel's Čtenost row); missing while verification is off. */
  readers?: number;
  /** The page's live session, for a writer when it opens as is, for a reader of a Živě page (`readOnly`). */
  collaboration?: {
    documentId: string; generation: string; currentVersion: number; readOnly: boolean;
    /** Not the server's: its state's own read, started beside the bundle (readPageBundle). */
    liveState?: Promise<LiveState | undefined>;
  } | null;
}
/** The live Yjs state, gzipped; its ETag `"<generation>.<hash>"` names the generation it belongs to. */
export interface LiveState { tag: string; gzip: ArrayBuffer }
/**
 * The page with its bundle (`?bundle=true`) and, beside it, the live Yjs state of the session the bundle names: started together, so a
 * cold open waits for neither in turn. The state has a read of its own because the browser keeps it and asks again by ETag (304), so a
 * warm open does not download it again (in the bundle, no-store, it came every time). `query`: more of the bundle's query (`&visit=true`).
 */
export function readPageBundle(id: string, query = "", signal?: AbortSignal): Promise<Page> {
  const options = signal ? { signal } : {};
  const liveState = apiResponse(`/pages/${id}/collaboration/state`, options)
    .then(async response => response.status === 204 ? undefined : { tag: response.headers.get("ETag") ?? "", gzip: await response.arrayBuffer() })
    .catch(() => undefined);
  return api<Page>(`/pages/${id}?bundle=true${query}`, options).then(page => {
    if (page.bundle?.collaboration) page.bundle.collaboration.liveState = liveState;
    return page;
  });
}
export interface Page extends PageSummary {
  /** True only for a reader of a live page (Publikace Živě). */
  live?: boolean;
  /** Only with `?bundle=true`. */
  bundle?: PageBundle | null;
  isTemplate?: boolean;
  contentJson: string;
  publishedVersion: number | null;
  /** When the revision in contentJson began: the working revision for writers, the published one otherwise. */
  activeFrom: string;
  canEdit: boolean;
  /** Whether the page is its space's requirement catalog (module Projector); the server always reports it. */
  isCatalog?: boolean;
  /** The page has its own access, not the space's; sent only when true. */
  ownAccess?: boolean;
}
export interface SetupStatus {
  passwordMinimumLength?: number;
  databaseReady: boolean;
  requiresSetup: boolean;
  bootstrapTokenRequired: boolean;
  workspaceName?: string;
  tokenSource?: {
    kind: "configuration" | "file";
    filePath: string;
    instructions: string;
  };
  error?: string;
  /** The running server, "<release>+<commit>". */
  version?: string;
}
export interface SearchWidget {
  kind: 'contact' | 'youtrack'; title: string; detail: string; headingId?: string | null;
  key?: string | null; state?: string | null; url?: string | null;
}
export interface SearchResult {
  widgets?: SearchWidget[];
  isDraft?: boolean;
  changedAt?: string | null;
  id: string;
  spaceId: string;
  spaceName: string;
  title: string;
  snippet: string;
  headingId?: string | null;
  headingTitle?: string | null;
  matchedAlias?: string | null;
  /** Meaning-only hit (hybrid search): `snippet` is the passage, `headingTitle`/`headingId` its anchor. */
  semantic?: boolean;
  /** Ověřený obsah (5.2): the state of the page's verification (the icon beside the title), the tree's string; absent when it is off. */
  verificationState?: import("./verification/pageVerification").VerificationState | null;
  passage?: { text: string; headingTitle?: string | null; headingId?: string | null } | null;
}
export class ApiError extends Error {
  constructor(
    public status: number,
    message: string,
    public code?: string,
    /** The problem body, for callers that need its extension members (e.g. the reauth step of a 428). */
    public problem?: Readonly<Record<string, unknown>>,
    /** What the server's Retry-After asks for (429, 503 ...), in milliseconds; missing when it sent none. */
    public retryAfterMs?: number,
  ) {
    super(message);
  }
}
/** Retry-After is a number of seconds or an HTTP date. */
function retryAfterOf(header: string | null) {
  if (!header) return undefined;
  const wait = /^\d+$/.test(header.trim()) ? Number(header) * 1000 : Date.parse(header) - Date.now();
  return Number.isFinite(wait) && wait >= 0 ? wait : undefined;
}
/**
 * Reads public/early-reads.js started before the app's code arrived, by API path. Creating the map here, as the app starts,
 * tells a late early-reads.js that the app makes its own requests.
 */
const earlyReads: Map<string, Promise<Response>> = ((globalThis as { atlasEarlyReads?: Map<string, Promise<Response>> }).atlasEarlyReads ??= new Map());
/** The early response of a plain GET of `path`, once; undefined when there is none. */
function takeEarlyRead(path: string) {
  const read = earlyReads.get(path);
  earlyReads.delete(path);
  return read;
}
/** The first view has loaded: an early read nobody adopted must not answer a later request, and its unread body is released. */
export function dropEarlyReads() {
  for (const read of earlyReads.values()) read.then((response) => response.body?.cancel()).catch(() => {});
  earlyReads.clear();
}
let csrf: string | null = null;
let sessionEnded: (() => void) | undefined;
/** The shell's one listener for a request refused as signed out (it verifies the session and signs out with the reason). */
export function onSessionEnded(listener: (() => void) | undefined) {
  sessionEnded = listener;
  liveProbedAt = -Infinity;
}
let liveProbe: Promise<void> | undefined;
let liveProbedAt = -Infinity;
/** The least time between two session checks after live failures: a flapping network retries every second (PERF3-3). */
export const liveProbeGapMs = 10_000;
/**
 * The live connection failed (live/liveConnection.ts, UX1-1; the page editor's hub, collaborationSessionCheck): refused as signed out (a
 * 401 on negotiate, or /auth/me answering 401 after the hub dropped a handshake of an ended session), it ends the session as a refused
 * request does; a lost network stays offline. /auth/me is asked at most once per `liveProbeGapMs`, a check in flight is shared.
 */
export function checkSessionAfterLiveFailure(error: unknown): Promise<void> {
  if ((error as { statusCode?: unknown } | null | undefined)?.statusCode === 401) {
    sessionEnded?.();
    return Promise.resolve();
  }
  if (liveProbe) return liveProbe;
  if (Date.now() - liveProbedAt < liveProbeGapMs) return Promise.resolve();
  liveProbedAt = Date.now();
  return (liveProbe = fetch("/api/auth/me", { credentials: "same-origin" })
    .then(response => { if (response.status === 401) sessionEnded?.(); }, () => {})
    .finally(() => { liveProbe = undefined; }));
}
export function clearCsrf() {
  csrf = null;
}
export async function api<T>(
  path: string,
  options: RequestInit = {},
): Promise<T> {
  const response = await apiResponse(path, options);
  return response.status === 204 ? (undefined as T) : response.json();
}
/** `api` without reading the body: for a stream (NDJSON). Same CSRF, credentials and ApiError for a refused request. */
export async function apiResponse(
  path: string,
  options: RequestInit = {},
): Promise<Response> {
  const method = options.method ?? "GET";
  if (!["GET", "HEAD"].includes(method) && !csrf)
    csrf = (await api<{ token: string }>("/auth/csrf")).token;
  const headers = new Headers(options.headers);
  if (options.body && !(options.body instanceof FormData)) headers.set("Content-Type", "application/json");
  if (!["GET", "HEAD"].includes(method)) headers.set("X-CSRF-TOKEN", csrf!);
  const early = method === "GET" && !options.signal && !options.headers ? takeEarlyRead(path) : undefined;
  const response = await (early ?? fetch(`/api${path}`, {
    ...options,
    headers,
    credentials: "same-origin",
  }));
  if (!response.ok) {
    const problem = await response.json().catch(() => null);
    const failure = new ApiError(
      response.status,
      problem?.detail ??
        problem?.title ??
        t.err.requestFailed(response.status),
      typeof problem?.code === "string" ? problem.code : undefined,
      problem && typeof problem === "object" && !Array.isArray(problem) ? problem : undefined,
      retryAfterOf(response.headers.get("Retry-After")),
    );
    if (failure.code === "ai_unavailable") notifyAiUnavailable();
    // A 401 outside the sign-in endpoints: the session may have ended on the server; the shell checks /auth/me (UX SB4-8).
    if (failure.status === 401 && !path.startsWith("/auth/")) sessionEnded?.();
    throw failure;
  }
  return response;
}
export function post<T>(path: string, body?: unknown) {
  return api<T>(path, {
    method: "POST",
    body: body === undefined ? undefined : JSON.stringify(body),
  });
}
export function put<T>(path: string, body: unknown) {
  return api<T>(path, { method: "PUT", body: JSON.stringify(body) });
}
/** How fetch reports a refused or dropped connection: the browser's own English TypeError (Chromium, Firefox, Safari). */
const networkFailure = /failed to fetch|networkerror|load failed|network request failed/i;
export function errorMessage(error: unknown) {
  // Only the shown text changes: callers still tell a network failure from an ApiError by its type.
  if (error instanceof TypeError && networkFailure.test(error.message))
    return t.err.noConnection;
  return error instanceof Error
    ? error.message
    : t.err.operationFailed;
}
