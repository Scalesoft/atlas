import type { ChoiceOption, StatusTone } from "../../types";
import { plural } from "../../../../i18n";
import t from "../../../../i18n/settings-ai";
import { formatDateTime } from "../../../../formatDate";

/*
 * Administrace → AI: contracts, drafts and wording shared by the module's sections.
 *
 * REAL contracts (served): A1 AiSwitchView, AiProfileView, SaveAiProfileRequest, AiFeaturesView;
 * A2 AiTestResult (POST …/profiles/{kind}/test, 6 a minute); A3 AiOverview (GET /api/admin/ai,
 * profiles of all three kinds, `index` null while no index generation is live); V2 the index
 * (GET /api/admin/ai/index, POST …/generations, …/{id}/verify, …/activate, …/cancel; activate and
 * cancel answer the new IndexStatus) and the overview's index tile.
 * A server without these packages answers 404; the sections treat that as "not in this server version"
 * (see `isMissingEndpoint`), and `aiAdminMock.ts` stands in for them in tests, the gallery and the
 * Playwright spec.
 */

export type ProfileKind = "embeddings" | "quick" | "chat" | "rerank";
export type ProviderId = "lmstudio" | "gateway" | "openai" | "anthropic";

// ---- A1 (real) -------------------------------------------------------------------------------

export interface AiSwitchView { enabled: boolean; version: string | null }

export interface AiProfileView {
  kind: ProfileKind;
  configured: boolean;
  enabled: boolean;
  provider: "openai-compatible" | "gateway" | "anthropic" | null;
  location: "company" | "cloud" | null;
  baseUrl: string | null;
  model: string | null;
  modelVersion: string | null;
  dimensions: number | null;
  queryPrefix: string;
  documentPrefix: string;
  timeoutSeconds: number;
  maxConcurrency: number;
  reasoningEffort: string | null;
  temperature: number | null;
  nativeJsonSchema: boolean;
  requestDimensions: boolean;
  useChatProfile: boolean;
  inputPricePerMillion: number | null;
  outputPricePerMillion: number | null;
  hasApiKey: boolean;
  modelLockedByIndex: boolean;
  version: string | null;
}

export interface SaveAiProfileRequest {
  enabled: boolean;
  provider: string;
  location: string;
  baseUrl: string | null;
  model: string;
  modelVersion: string | null;
  dimensions: number | null;
  queryPrefix: string | null;
  documentPrefix: string | null;
  timeoutSeconds: number;
  maxConcurrency: number;
  reasoningEffort: string | null;
  temperature: number | null;
  nativeJsonSchema: boolean;
  requestDimensions: boolean;
  useChatProfile: boolean;
  inputPricePerMillion: number | null;
  outputPricePerMillion: number | null;
  apiKey: string | null;
  clearApiKey: boolean;
  expectedVersion: string | null;
}

/** Q3: `limitable` tasks (the answer tasks) carry the admin's daily limit per user, `null` = no limit; an older server omits both. */
export interface AiFeatureItem { task: string; group: string; enabled: boolean; profileKind: ProfileKind; cloudWorkingText: boolean | null; limitable?: boolean; dailyLimit?: number | null }
export interface AiFeaturesView { items: AiFeatureItem[]; version: string | null }

// ---- A3 (real: GET /api/admin/ai) -------------------------------------------------------------

export type AiProfileState = "NotConfigured" | "Disabled" | "Unknown" | "Available" | "Unavailable";
export interface AiProfileStatus {
  kind: ProfileKind;
  useChatProfile?: boolean;
  state: AiProfileState;
  since: string | null;
  lastErrorCode: string | null;
  lastSuccessAt: string | null;
  model: string | null;
  location: string | null;
  /** The profile works but needs attention, as the backups' `warning` (Gateway serving another model than configured). */
  warning?: string | null;
}
export interface AiUsageSummary { calls: number; errors: number; inputTokens: number | null; outputTokens: number | null; estimatedCost: number | null }
/** Served (V2 fills A3's slot; the server's `AiIndexSummary`, null without a live generation). */
export interface AiIndexSummary {
  state: "None" | "Building" | "Active";
  model: string | null;
  eligiblePages: number;
  indexedPages: number;
  queue: { pending: number; failing: number; oldestPendingAt: string | null };
  rebuildRecommended: boolean;
}
export interface AiOverview { enabled: boolean; switchVersion: string | null; profiles: AiProfileStatus[]; index: AiIndexSummary | null; usage7Days: AiUsageSummary }

// ---- A2 (real: POST /api/admin/ai/profiles/{kind}/test) ---------------------------------------

/** No text from the upstream, only a code from the outcome table. */
export interface AiTestResult { ok: boolean; code: string | null; latencyMs: number; dimensions: number | null; model: string | null; warning?: string | null }

// ---- V2 (served: /api/admin/ai/index…) ---------------------------------------------------------

export type GenerationState = "Building" | "Active" | "Retired" | "Failed";
export interface GenerationView {
  id: string;
  state: GenerationState;
  model: string;
  modelVersion: string | null;
  chunkingVersion: string;
  eligiblePages: number;
  indexedPages: number;
  chunks: number;
  pendingTasks: number;
  failingTasks: number;
  missing: { pageId: string; title: string }[];
  createdAt: string;
  activatedAt: string | null;
  version: string;
}
/** Complete enough to request verification; activation still requires its passing result. */
export function isIndexBuildComplete(generation: GenerationView | null | undefined): boolean {
  return !!generation && generation.indexedPages === generation.eligiblePages && generation.pendingTasks === 0
    && generation.failingTasks === 0 && generation.missing.length === 0;
}
export interface IndexStatus {
  active: GenerationView | null;
  building: GenerationView | null;
  retired: GenerationView[];
  queue: { pending: number; failing: number; oldestPendingAt: string | null };
}
export type GenerationMode = "build" | "rebuild" | "replace";
export interface GenerationVerification {
  complete: boolean;
  missingPages: number;
  stalePages: number;
  dimensionsOk: boolean;
  sanity: { checked: number; hitsAt5: number };
  canActivate: boolean;
}

// ---- Wording ---------------------------------------------------------------------------------

// Getters: the texts are read where they are shown, after the language's file has arrived.
export const kindLabels: Record<ProfileKind, string> = {
  get embeddings() { return t.kinds.embeddings; },
  get quick() { return t.kinds.quick; },
  get chat() { return t.kinds.chat; },
  get rerank() { return t.kinds.rerank; },
};
export const kindIcons: Record<ProfileKind, string> = { embeddings: "database", quick: "activity", chat: "quote", rerank: "list-ordered" };

/** One sentence per outcome code of the server (A2). The upstream's own text never reaches the client. */
export function errorCodeText(code: string | null | undefined): string {
  if (!code) return "";
  return Object.hasOwn(t.errorTexts, code) ? t.errorTexts[code as keyof typeof t.errorTexts] : t.unknownError;
}

export interface ProfileStateView { tone: StatusTone; icon: string; label: string; since?: string; error?: { code: string; text: string }; lastSuccess?: string }

/** Příloha B "Správa: stav profilu": Nenastaveno · Vypnuto · Dostupné · Nedostupné od {čas} · Poslední chyba: {kód}. */
export function profileStateView(status: AiProfileStatus | undefined): ProfileStateView {
  const lastSuccess = status?.lastSuccessAt ? formatDateTime(status.lastSuccessAt) : undefined;
  switch (status?.state) {
    case "Available":
      // Working with a warning reads as the backups' "Vyžaduje pozornost"; AiHealthStatus says what is wrong.
      if (status.warning) return { tone: "warning", icon: "triangle-alert", label: t.states.attention, since: status.since ? formatDateTime(status.since) : undefined, lastSuccess };
      return { tone: "success", icon: "circle-check", label: t.states.available, since: status.since ? formatDateTime(status.since) : undefined, lastSuccess };
    case "Unavailable":
      return {
        tone: "danger",
        icon: "circle-x",
        label: status.since ? t.states.unavailableSince(formatDateTime(status.since)) : t.states.unavailable,
        since: status.since ? formatDateTime(status.since) : undefined,
        error: status.lastErrorCode ? { code: status.lastErrorCode, text: errorCodeText(status.lastErrorCode) } : undefined,
        lastSuccess,
      };
    case "Disabled":
      return { tone: "neutral", icon: "ban", label: t.states.off };
    case "Unknown":
      return { tone: "info", icon: "hourglass", label: t.states.unverified, lastSuccess };
    default:
      return { tone: "neutral", icon: "clock", label: t.states.notSet };
  }
}

/** The one-word state of the module card, from the overview. */
export function overviewState(overview: AiOverview): { tone: StatusTone; icon: string; label: string } {
  if (!overview.enabled) return { tone: "neutral", icon: "ban", label: t.states.off };
  const embeddings = overview.profiles.find((item) => item.kind === "embeddings");
  if (!embeddings || embeddings.state === "NotConfigured") return { tone: "neutral", icon: "clock", label: t.states.notSet };
  if (embeddings.state === "Unavailable") return { tone: "danger", icon: "circle-x", label: t.states.unavailable };
  if (embeddings.state === "Available") return { tone: "success", icon: "circle-check", label: t.states.available };
  if (embeddings.state === "Disabled") return { tone: "neutral", icon: "ban", label: t.states.off };
  return { tone: "info", icon: "hourglass", label: t.states.unverified };
}

/** Without the A3 overview the card can only say what A1 knows. */
export function fallbackState(sw: AiSwitchView, embeddings: AiProfileView): { tone: StatusTone; icon: string; label: string } {
  if (!sw.enabled) return { tone: "neutral", icon: "ban", label: t.states.off };
  if (!embeddings.configured) return { tone: "neutral", icon: "clock", label: t.states.notSet };
  return embeddings.enabled ? { tone: "info", icon: "circle-check", label: t.states.on } : { tone: "neutral", icon: "ban", label: t.states.off };
}

export const pagesText = (value: number) => plural(value, t.pages);

// ---- Profile draft ---------------------------------------------------------------------------

export interface ProviderPreset {
  id: ProviderId;
  label: string;
  description: string;
  icon: string;
  provider: "openai-compatible" | "gateway" | "anthropic";
  location: "company" | "cloud";
  defaultUrl: string;
  urlRequired: boolean;
  keyRequired: boolean;
  urlHint: string;
}

export const providerPresets: Record<ProviderId, ProviderPreset> = {
  lmstudio: { id: "lmstudio", get label() { return t.providers.lmstudio.label; }, get description() { return t.providers.lmstudio.description; }, icon: "server", provider: "openai-compatible", location: "company", defaultUrl: "http://127.0.0.1:1234/v1", urlRequired: true, keyRequired: false, get urlHint() { return t.providers.lmstudio.urlHint; } },
  gateway: { id: "gateway", get label() { return t.providers.gateway.label; }, get description() { return t.providers.gateway.description; }, icon: "server", provider: "gateway", location: "company", defaultUrl: "http://127.0.0.1:8080/v1", urlRequired: true, keyRequired: false, get urlHint() { return t.providers.gateway.urlHint; } },
  openai: { id: "openai", get label() { return t.providers.openai.label; }, get description() { return t.providers.openai.description; }, icon: "globe", provider: "openai-compatible", location: "cloud", defaultUrl: "https://api.openai.com/v1", urlRequired: false, keyRequired: true, get urlHint() { return t.providers.openai.urlHint; } },
  anthropic: { id: "anthropic", get label() { return t.providers.anthropic.label; }, get description() { return t.providers.anthropic.description; }, icon: "globe", provider: "anthropic", location: "cloud", defaultUrl: "https://api.anthropic.com", urlRequired: false, keyRequired: true, get urlHint() { return t.providers.anthropic.urlHint; } },
};

export function providerOptions(kind: ProfileKind): ChoiceOption[] {
  const ids: ProviderId[] = kind === "embeddings" || kind === "rerank" ? ["lmstudio", "openai"] : ["lmstudio", "gateway", "openai", "anthropic"];
  return ids.map((id) => ({ value: id, label: providerPresets[id].label, description: providerPresets[id].description, icon: providerPresets[id].icon }));
}

export interface ProfileDraft {
  providerId: ProviderId | "";
  enabled: boolean;
  baseUrl: string;
  model: string;
  modelVersion: string;
  apiKey: string;
  clearApiKey: boolean;
  queryPrefix: string;
  documentPrefix: string;
  timeoutSeconds: number;
  maxConcurrency: number;
  requestDimensions: boolean;
  reasoningEffort: string;
  temperature: string;
  nativeJsonSchema: boolean;
  inputPrice: string;
  outputPrice: string;
}

export function presetOf(view: Pick<AiProfileView, "provider" | "location">): ProviderId | "" {
  if (view.provider === "gateway") return "gateway";
  if (view.provider === "anthropic") return "anthropic";
  if (view.provider === "openai-compatible") return view.location === "cloud" ? "openai" : "lmstudio";
  return "";
}

export function draftFromView(view: AiProfileView): ProfileDraft {
  return {
    providerId: presetOf(view),
    enabled: view.configured ? view.enabled : false,
    baseUrl: view.baseUrl ?? "",
    model: view.model ?? "",
    modelVersion: view.modelVersion ?? "",
    apiKey: "",
    clearApiKey: false,
    queryPrefix: view.queryPrefix,
    documentPrefix: view.documentPrefix,
    timeoutSeconds: view.timeoutSeconds,
    maxConcurrency: view.maxConcurrency,
    requestDimensions: view.requestDimensions,
    reasoningEffort: view.reasoningEffort ?? "none",
    temperature: view.temperature === null ? "" : String(view.temperature),
    nativeJsonSchema: view.nativeJsonSchema,
    inputPrice: view.inputPricePerMillion === null ? "" : String(view.inputPricePerMillion),
    outputPrice: view.outputPricePerMillion === null ? "" : String(view.outputPricePerMillion),
  };
}

export const draftDirty = (draft: ProfileDraft, view: AiProfileView) => JSON.stringify(draft) !== JSON.stringify(draftFromView(view));

export function originOf(url: string): string {
  try {
    return new URL(url.trim()).origin;
  } catch {
    return "";
  }
}

/** The stored key is bound to its provider and address origin; the server drops it when either changes. */
export function keyWillBeDropped(draft: ProfileDraft, view: Pick<AiProfileView, "hasApiKey" | "provider" | "location" | "baseUrl">): boolean {
  if (!view.hasApiKey || draft.clearApiKey || draft.apiKey.trim()) return false;
  const was = originOf(view.baseUrl?.trim() || (view.provider === "anthropic" ? providerPresets.anthropic.defaultUrl : view.provider === "openai-compatible" && view.location === "cloud" ? providerPresets.openai.defaultUrl : ""));
  const now = originOf(draft.baseUrl.trim() || (draft.providerId === "openai" || draft.providerId === "anthropic" ? providerPresets[draft.providerId].defaultUrl : ""));
  const provider = draft.providerId ? providerPresets[draft.providerId].provider : null;
  return view.provider !== provider || was !== now;
}

/**
 * A provider change keeps what still fits (model, version, timeouts, prices) and drops what does not:
 * an address that belonged to the previous preset (or that is not HTTPS for a cloud provider) goes back
 * to the new preset's default, a typed key is cleared, Anthropic-only rules reset. Returns the new draft
 * and the names of the fields it reset, for the note shown next to the choice.
 */
export function changeProvider(draft: ProfileDraft, next: ProviderId, kind: ProfileKind): { draft: ProfileDraft; dropped: string[] } {
  const preset = providerPresets[next];
  const previous = draft.providerId ? providerPresets[draft.providerId] : undefined;
  const result: ProfileDraft = { ...draft, providerId: next, apiKey: "" };
  const dropped: string[] = [];
  const url = draft.baseUrl.trim();
  if (previous?.id === next) return { draft, dropped };
  const protocolChanged = previous && (previous.provider === "anthropic") !== (preset.provider === "anthropic");
  const incompatible = protocolChanged || !url || url === previous?.defaultUrl || (preset.location === "cloud" && !/^https:\/\//i.test(url)) || (preset.location === "company" && url === providerPresets.openai.defaultUrl);
  if (incompatible) {
    if (url && url !== preset.defaultUrl) dropped.push(t.summary.url);
    result.baseUrl = preset.id === "anthropic" ? "" : preset.defaultUrl;
  }
  if (draft.apiKey) dropped.push(t.summary.key);
  if (kind === "embeddings") result.requestDimensions = next === "openai";
  return { draft: result, dropped };
}

export interface ProfileErrors { provider?: string; baseUrl?: string; model?: string; apiKey?: string; timeout?: string; concurrency?: string; temperature?: string; prices?: string; prefix?: string }

/** Client-side mirror of the server's validation; the server stays the authority. The URL is required only for an enabled profile. */
export function validateDraft(draft: ProfileDraft, view: Pick<AiProfileView, "hasApiKey" | "provider" | "location" | "baseUrl">): ProfileErrors {
  const errors: ProfileErrors = {};
  const preset = draft.providerId ? providerPresets[draft.providerId] : undefined;
  const url = draft.baseUrl.trim();
  if (draft.enabled && !preset) errors.provider = t.errors.provider;
  if (url) {
    const parsed = (() => {
      try {
        return new URL(url);
      } catch {
        return undefined;
      }
    })();
    if (!parsed || !/^https?:$/.test(parsed.protocol) || parsed.username || parsed.password || parsed.search || parsed.hash) errors.baseUrl = t.errors.urlInvalid;
    else if (preset?.location === "cloud" && parsed.protocol !== "https:") errors.baseUrl = t.errors.urlHttps;
  } else if (draft.enabled && preset?.urlRequired) errors.baseUrl = t.errors.urlRequired;
  if (draft.enabled && !draft.model.trim()) errors.model = t.errors.modelRequired;
  if (draft.enabled && preset?.keyRequired && !draft.apiKey.trim() && !(view.hasApiKey && !draft.clearApiKey && !keyWillBeDropped(draft, view))) errors.apiKey = t.errors.keyRequired;
  if (!(draft.timeoutSeconds >= 1 && draft.timeoutSeconds <= 300)) errors.timeout = t.errors.timeout;
  if (!(draft.maxConcurrency >= 1 && draft.maxConcurrency <= 16)) errors.concurrency = t.errors.concurrency;
  if (draft.temperature.trim() !== "" && !(Number(draft.temperature) >= 0 && Number(draft.temperature) <= 2)) errors.temperature = t.errors.temperature;
  for (const price of [draft.inputPrice, draft.outputPrice]) if (price.trim() !== "" && !(Number(price) >= 0 && Number(price) < 100_000_000)) errors.prices = t.errors.prices;
  if (draft.queryPrefix.length > 200 || draft.documentPrefix.length > 200) errors.prefix = t.errors.prefix;
  return errors;
}

const number = (text: string) => (text.trim() === "" ? null : Number(text));

/** `useChatProfile` (Rychlé úlohy only): the quick tasks borrow the Chat profile and need no provider of their own. */
export function draftToRequest(draft: ProfileDraft, view: AiProfileView, kind: ProfileKind, useChatProfile = false): SaveAiProfileRequest {
  const preset = providerPresets[draft.providerId || "lmstudio"];
  const embeddings = kind === "embeddings";
  const rerank = kind === "rerank";
  const locked = view.modelLockedByIndex && embeddings;
  return {
    enabled: draft.enabled,
    provider: preset.provider,
    location: preset.location,
    baseUrl: draft.baseUrl.trim() || null,
    model: locked ? (view.model ?? draft.model.trim()) : draft.model.trim(),
    modelVersion: (locked ? view.modelVersion : draft.modelVersion.trim()) || null,
    dimensions: embeddings ? 1024 : null,
    queryPrefix: embeddings ? (locked ? view.queryPrefix : draft.queryPrefix) : null,
    documentPrefix: embeddings ? (locked ? view.documentPrefix : draft.documentPrefix) : null,
    timeoutSeconds: draft.timeoutSeconds,
    maxConcurrency: draft.maxConcurrency,
    reasoningEffort: embeddings || rerank ? null : draft.reasoningEffort || "none",
    temperature: embeddings || rerank ? null : number(draft.temperature),
    nativeJsonSchema: rerank ? true : draft.nativeJsonSchema,
    requestDimensions: embeddings ? draft.requestDimensions : false,
    useChatProfile: kind === "quick" && useChatProfile,
    inputPricePerMillion: number(draft.inputPrice),
    outputPricePerMillion: embeddings || rerank ? null : number(draft.outputPrice),
    apiKey: draft.apiKey.trim() || null,
    clearApiKey: draft.clearApiKey,
    expectedVersion: view.version,
  };
}

/** The live summary under the form: what the profile will do once saved. */
export function profileSummary(draft: ProfileDraft, view: AiProfileView, kind: ProfileKind): { key: string; icon: string; label: string; value: string; code?: boolean }[] {
  const preset = draft.providerId ? providerPresets[draft.providerId] : undefined;
  const key = draft.clearApiKey
    ? t.summary.keyRemoved
    : draft.apiKey
      ? t.summary.keyNew
      : view.hasApiKey
        ? keyWillBeDropped(draft, view)
          ? t.summary.keyDropped
          : t.summary.keyStored
        : preset?.keyRequired
          ? t.summary.keyMissing
          : t.summary.keyNone;
  const items = [
    { key: "provider", icon: "server", label: t.summary.provider, value: preset ? `${preset.label} (${preset.location === "cloud" ? t.summary.cloud : t.summary.company})` : t.summary.notChosen },
    { key: "url", icon: "link", label: t.summary.url, value: draft.baseUrl.trim() || (preset?.location === "cloud" ? preset.defaultUrl : t.summary.urlMissing), code: !!draft.baseUrl.trim() },
    { key: "model", icon: "database", label: t.summary.model, value: draft.model.trim() ? `${draft.model.trim()}${draft.modelVersion.trim() ? ` · ${draft.modelVersion.trim()}` : ""}` : t.summary.modelMissing },
    { key: "key", icon: "key-round", label: t.summary.key, value: key },
  ];
  if (kind === "embeddings") items.push({ key: "dimensions", icon: "hash", label: t.summary.dimensions, value: t.summary.dimensionsValue });
  else if (kind !== "rerank") items.push({ key: "thinking", icon: "activity", label: t.summary.thinking, value: draft.reasoningEffort || "none" });
  items.push({ key: "state", icon: draft.enabled ? "circle-check" : "ban", label: t.summary.stateAfterSave, value: draft.enabled ? t.states.on : t.states.off });
  return items;
}

export const maxDailyLimit = 10_000;
/** The daily limit field: empty = no limit, otherwise a whole number 1 to 10 000 (server rule, Q3). Returns the number, null for empty, or an error text. */
export function parseDailyLimit(text: string): { value: number | null; error?: string } {
  const trimmed = text.trim();
  if (trimmed === "") return { value: null };
  const value = Number(trimmed);
  if (!/^\d+$/.test(trimmed) || value < 1 || value > maxDailyLimit) return { value: null, error: t.errors.dailyLimit };
  return { value };
}

export function isMissingEndpoint(failure: unknown): boolean {
  const status = typeof failure === "object" && failure !== null && "status" in failure ? (failure as { status: number }).status : 0;
  return status === 404 || status === 405;
}

export const featureGroups: { id: string; label: string; icon: string }[] = [
  { id: "search", get label() { return t.features.groups.search; }, icon: "search" },
  { id: "read", get label() { return t.features.groups.read; }, icon: "book-open" },
  { id: "write", get label() { return t.features.groups.write; }, icon: "link" },
  { id: "create", get label() { return t.features.groups.create; }, icon: "file-plus" },
];

/** Title and description are read from the language's messages where they are shown; only the icon lives here. */
const featureText = (task: keyof typeof t.featureTexts, icon: string) => ({
  get title() { return t.featureTexts[task].title; },
  get description() { return t.featureTexts[task].description; },
  icon,
});

export const featureTexts: Record<string, { title: string; description: string; icon: string }> = {
  "page.summary": featureText("page.summary", "book-open"),
  "editor.intent": featureText("editor.intent", "blocks"),
  "editor.transform": featureText("editor.transform", "pencil"),
  "editor.outline": featureText("editor.outline", "list"),
  "publish.check": featureText("publish.check", "pencil"),
  "publish.change-summary": featureText("publish.change-summary", "pencil"),
  "page.tag-suggest": featureText("page.tag-suggest", "hash"),
  "search.embed-query": featureText("search.embed-query", "search"),
  "search.rerank": featureText("search.rerank", "list-ordered"),
  "page.related": featureText("page.related", "link"),
  "editor.link": featureText("editor.link", "link"),
  "search.answer": featureText("search.answer", "search"),
  "chat.answer": featureText("chat.answer", "book-open"),
  "assistant.agent": featureText("assistant.agent", "book-open"),
  "assistant.working-text": featureText("assistant.working-text", "pencil"),
  "assistant.arrange-pages": featureText("assistant.arrange-pages", "list-ordered"),
  "page.create-suggest": featureText("page.create-suggest", "file-plus"),
};

export interface AiModelCatalog { ok: boolean; code: string | null; models: { id: string; name: string }[] }
export function modelCatalogRequest(draft: ProfileDraft) {
  const preset = providerPresets[draft.providerId || "lmstudio"];
  return { provider: preset.provider, location: preset.location, baseUrl: draft.baseUrl.trim() || null, apiKey: draft.apiKey.trim() || null, clearApiKey: draft.clearApiKey };
}
