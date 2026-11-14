// MOCK of the AI administration API for tests, the gallery and tests/browser/settings-ai.spec.ts.
// No imports on purpose: Playwright loads this file directly.
//
// It serves the A1 endpoints the way the merged server does (CAS versions, write-only key, 409
// ai_generation_required), the A2 test and A3 overview in the server's shapes (the server lists all
// three profile kinds and answers `index: null` until V2), and, clearly marked MOCKED, V2:
//   A2  POST /api/admin/ai/profiles/{kind}/test
//   A3  GET  /api/admin/ai
//   V2  GET  /api/admin/ai/index, POST …/generations, …/{id}/verify, …/activate, …/cancel (MOCKED)
// When those packages land, the component code needs no change: delete the MOCKED branches (or just
// stop using this file in the places that reach the real server) and fix any shape that differs.

export interface MockResponse { status: number; body?: unknown }
type Json = Record<string, any>;

export interface AiAdminMockOptions {
  enabled?: boolean;
  /** Embeddings and quick profile start configured and enabled. */
  configured?: boolean;
  /** An active index generation exists, so the embedding model is locked. */
  activeIndex?: boolean;
  /** A generation is already Building. */
  building?: boolean;
  /** Embeddings are Unavailable since a recent failure (state + code). */
  embeddingsDown?: boolean;
  /** Every building generation advances by this many pages per status read. */
  step?: number;
  /** Answer the A3/A2/V2 endpoints with 404, as a server without those packages does. */
  withoutLaterPackages?: boolean;
}

const now = () => new Date().toISOString();
const iso = (minutesAgo: number) => new Date(Date.now() - minutesAgo * 60_000).toISOString();

function emptyProfile(kind: string): Json {
  const rerank = kind === "rerank";
  return {
    kind, configured: false, enabled: false, provider: null, location: null, baseUrl: null, model: null, modelVersion: null, dimensions: kind === "embeddings" ? 1024 : null,
    queryPrefix: "", documentPrefix: "", timeoutSeconds: rerank ? 10 : 30, maxConcurrency: 4, reasoningEffort: null, temperature: null, nativeJsonSchema: true, requestDimensions: false,
    useChatProfile: false, inputPricePerMillion: null, outputPricePerMillion: null, hasApiKey: false, modelLockedByIndex: false, version: null,
  };
}

export function createAiAdminMock(options: AiAdminMockOptions = {}) {
  const config = { enabled: false, configured: false, activeIndex: false, building: false, embeddingsDown: false, step: 400, withoutLaterPackages: false, ...options };
  let serial = 1;
  const version = () => `v${++serial}`;
  const sw = { enabled: config.enabled, version: config.enabled || config.configured ? "s1" : (null as string | null) };
  const groups = [{ id: "g-ops", name: "Provoz", version: "g1", source: "local" }, { id: "g-dev", name: "Vývoj", version: "g2", source: "local" }];
  const access: Json = { version: "a1", groups: [{ groupId: "g-ops", groupName: "Provoz", searchAnswerDailyLimit: null, chatAnswerDailyLimit: null, pageSummaryDailyLimit: null, agentDailyLimit: null, toolsEnabled: true }] };
  const profiles: Record<string, Json> = { embeddings: emptyProfile("embeddings"), quick: emptyProfile("quick"), chat: emptyProfile("chat"), rerank: emptyProfile("rerank") };
  if (config.configured) {
    profiles.embeddings = { ...profiles.embeddings, configured: true, enabled: true, provider: "openai-compatible", location: "company", baseUrl: "http://lmstudio.firma.example.test:1234/v1", model: "text-embedding-bge-m3", modelVersion: "bge-m3@lmstudio", hasApiKey: false, version: "e1", modelLockedByIndex: config.activeIndex };
    profiles.quick = { ...profiles.quick, configured: true, enabled: true, provider: "openai-compatible", location: "company", baseUrl: "http://lmstudio.firma.example.test:1234/v1", model: "google/gemma-4-26b-a4b", reasoningEffort: "none", hasApiKey: false, version: "q1" };
    profiles.chat = { ...profiles.chat, configured: true, enabled: true, provider: "openai-compatible", location: "company", baseUrl: "http://lmstudio.firma.example.test:1234/v1", model: "google/gemma-4-26b-a4b", reasoningEffort: "none", hasApiKey: false, version: "c1" };
  }
  const features: Json = {
    version: "f1",
    items: [
      { task: "search.embed-query", group: "search", enabled: true, profileKind: "embeddings", cloudWorkingText: null },
      { task: "search.rerank", group: "search", enabled: true, profileKind: "rerank", cloudWorkingText: null },
      { task: "search.answer", group: "search", enabled: true, profileKind: "chat", cloudWorkingText: null, limitable: true, dailyLimit: null },
      { task: "page.related", group: "read", enabled: true, profileKind: "embeddings", cloudWorkingText: null },
      { task: "chat.answer", group: "read", enabled: true, profileKind: "chat", cloudWorkingText: null, limitable: true, dailyLimit: null },
      { task: "assistant.agent", group: "read", enabled: true, profileKind: "chat", cloudWorkingText: null, limitable: true, dailyLimit: null },
      { task: "assistant.working-text", group: "write", enabled: true, profileKind: "chat", cloudWorkingText: false },
      { task: "assistant.arrange-pages", group: "write", enabled: true, profileKind: "chat", cloudWorkingText: null },
      { task: "editor.link", group: "write", enabled: true, profileKind: "embeddings", cloudWorkingText: false },
      { task: "page.create-suggest", group: "create", enabled: true, profileKind: "embeddings", cloudWorkingText: false },
    ],
  };
  // MOCKED (V2) generation state.
  let nextGeneration = 1;
  const generation = (state: string, model: string, indexed: number): Json => ({
    id: `gen-${nextGeneration++}`, state, model, modelVersion: null, chunkingVersion: "headings-1800@1", eligiblePages: 1480, indexedPages: indexed, chunks: indexed * 5,
    pendingTasks: 1480 - indexed, failingTasks: 0, missing: [], createdAt: iso(60), activatedAt: state === "Active" ? iso(600) : null, version: version(),
  });
  let active: Json | null = config.activeIndex ? generation("Active", "text-embedding-bge-m3", 1480) : null;
  let building: Json | null = config.building ? generation("Building", "text-embedding-bge-m3", 420) : null;
  let candidate: Json | null = null;
  let verified = false;
  const calls: { method: string; path: string; body: unknown }[] = [];

  const problem = (status: number, detail: string, code?: string): MockResponse => ({ status, body: { status, detail, ...(code ? { code } : {}) } });
  const stale = (expected: unknown, current: unknown) => (expected ?? null) !== (current ?? null);

  const profileError = (kind: string, body: Json): MockResponse | undefined => {
    if (body.provider === "gateway" && (kind === "embeddings" || body.location !== "company")) return problem(400, "Gateway podporuje jen firemní rychlé úlohy a chat.");
    if (body.provider === "anthropic" && kind === "embeddings") return problem(400, "Anthropic neposkytuje embeddingy; použijte LM Studio nebo OpenAI.");
    if (kind === "rerank" && body.provider !== "openai-compatible") return problem(400, "Reranker podporuje jen poskytovatele kompatibilního s OpenAI.");
    if (body.useChatProfile && kind !== "quick") return problem(400, "Použít profil Chat lze jen u rychlých úloh.");
    if (body.enabled && !body.useChatProfile && !String(body.model ?? "").trim()) return problem(400, "Pro zapnutí profilu vyplňte model.");
    if (body.enabled && !body.useChatProfile && ((body.provider === "openai-compatible" && body.location === "company") || body.provider === "gateway") && !body.baseUrl) return problem(400, "Pro zapnutí profilu vyplňte adresu API.");
    if (body.apiKey && body.clearApiKey) return problem(400, "Klíč API nelze současně změnit a odstranit.");
    return undefined;
  };
  const origin = (url: string | null) => { try { return new URL(url ?? "").origin; } catch { return ""; } };
  const profileOrigin = (profile: Json) => origin(profile.baseUrl || (profile.provider === "anthropic" ? "https://api.anthropic.com" : profile.provider === "openai-compatible" && profile.location === "cloud" ? "https://api.openai.com/v1" : null));
  const saveProfile = (kind: string, body: Json): MockResponse => {
    const current = profiles[kind]!;
    if (stale(body.expectedVersion, current.version)) return problem(409, "Nastavení mezitím změnil jiný správce.");
    const invalid = profileError(kind, body);
    if (invalid) return invalid;
    const changesModel = kind === "embeddings" && current.modelLockedByIndex && (body.model !== current.model || (body.modelVersion ?? null) !== current.modelVersion || body.queryPrefix !== current.queryPrefix || body.documentPrefix !== current.documentPrefix);
    if (changesModel) return problem(409, "Změna modelu vyžaduje novou generaci indexu. Použijte Index → Vyměnit model.", "ai_generation_required");
    const keepKey = current.hasApiKey && !body.clearApiKey && current.provider === body.provider && profileOrigin(current) === profileOrigin(body);
    profiles[kind] = {
      ...current, kind, configured: true, enabled: body.enabled, provider: body.provider, location: body.location, baseUrl: body.baseUrl, model: body.model, modelVersion: body.modelVersion ?? null,
      dimensions: kind === "embeddings" ? 1024 : null, queryPrefix: body.queryPrefix ?? "", documentPrefix: body.documentPrefix ?? "", timeoutSeconds: body.timeoutSeconds, maxConcurrency: body.maxConcurrency,
      reasoningEffort: body.reasoningEffort ?? null, temperature: body.temperature ?? null, nativeJsonSchema: body.nativeJsonSchema, requestDimensions: !!body.requestDimensions,
      useChatProfile: !!body.useChatProfile, inputPricePerMillion: body.inputPricePerMillion ?? null, outputPricePerMillion: body.outputPricePerMillion ?? null, hasApiKey: body.apiKey ? true : keepKey, version: version(),
    };
    return { status: 200, body: profiles[kind] };
  };

  // A3 shape (served by the server; the index tile stays MOCKED until V2)
  const overview = (): Json => {
    const status = (kind: string): Json => {
      const profile = profiles[kind]!;
      if (kind === "quick" && profile.configured && profile.useChatProfile) {
        const shared = status("chat");
        return { ...shared, kind, useChatProfile: true, ...(!profile.enabled || !sw.enabled ? { state: "Disabled", since: null, lastErrorCode: null, lastSuccessAt: null } : {}) };
      }
      if (!profile.configured) return { kind, state: "NotConfigured", since: null, lastErrorCode: null, lastSuccessAt: null, model: null, location: null };
      if (!profile.enabled || !sw.enabled) return { kind, state: "Disabled", since: null, lastErrorCode: null, lastSuccessAt: null, model: profile.model, location: profile.location };
      if (kind === "embeddings" && config.embeddingsDown) return { kind, state: "Unavailable", since: iso(23), lastErrorCode: "timeout", lastSuccessAt: iso(95), model: profile.model, location: profile.location };
      return { kind, state: "Available", since: iso(60 * 5), lastErrorCode: null, lastSuccessAt: iso(1), model: profile.model, location: profile.location };
    };
    const g = building ?? active;
    return {
      enabled: sw.enabled, switchVersion: sw.version, profiles: [status("embeddings"), status("quick"), status("chat"), status("rerank")],
      index: g ? { state: building ? "Building" : "Active", model: g.model, eligiblePages: g.eligiblePages, indexedPages: g.indexedPages, queue: queue(), rebuildRecommended: !!active && active.chunkingVersion !== "headings-1800@2" } : { state: "None", model: null, eligiblePages: 0, indexedPages: 0, queue: queue(), rebuildRecommended: false },
      usage7Days: { calls: 1840, errors: 12, inputTokens: 912_340, outputTokens: null, estimatedCost: 0.18 },
    };
  };
  const queue = () => ({ pending: building ? building.pendingTasks : 0, failing: building ? building.failingTasks : 0, oldestPendingAt: building && building.pendingTasks ? iso(14) : null });
  const indexStatus = (): Json => ({ active, building, retired: [], queue: queue() });
  const advance = () => {
    if (!building) return;
    const indexed = Math.min(building.eligiblePages, building.indexedPages + config.step);
    building = { ...building, indexedPages: indexed, chunks: indexed * 5, pendingTasks: building.eligiblePages - indexed, version: version() };
  };

  const usageReport = () => {
    const until = now();
    const firstDay = new Date(until);
    firstDay.setUTCHours(0, 0, 0, 0);
    firstDay.setUTCDate(firstDay.getUTCDate() - 6);
    const profile = Object.values(profiles).find(candidate => candidate.configured && candidate.model);
    const task = profile?.kind === "embeddings" ? "index.embed" : profile?.kind === "quick" ? "editor.transform" : "chat.answer";
    const operation = profile?.kind === "embeddings" ? "embedding" : "chat";
    const lastCallAt = iso(4);
    const provider = profile ? [{
      provider: profile.provider, location: profile.location, model: profile.model, calls: 12, errors: 1, canceled: 1,
      inputTokens: 1000, outputTokens: null, inputKnownCalls: 9, outputKnownCalls: 0, costKnownCalls: 0,
      estimatedCost: null, lastCallAt,
    }] : [];
    const items = profile ? [{
      task, kind: profile.kind, operation, calls: 12, errors: 1, inputTokens: 1000, outputTokens: null,
      inputKnownCalls: 9, outputKnownCalls: 0, costKnownCalls: 0, p50Ms: 800, p95Ms: 1500, estimatedCost: null,
    }] : [];
    const days = Array.from({ length: 7 }, (_, offset) => {
      const date = new Date(firstDay);
      date.setUTCDate(date.getUTCDate() + offset);
      const calls = profile && offset === 6 ? 12 : 0;
      return { day: date.toISOString(), calls, errors: calls ? 1 : 0, canceled: calls ? 1 : 0,
        inputTokens: calls ? 1000 : null, outputTokens: null, inputKnownCalls: calls ? 9 : 0,
        outputKnownCalls: 0, costKnownCalls: 0, estimatedCost: null };
    });
    return { since: firstDay.toISOString(), until, items, providers: provider, days };
  };

  function handle(method: string, rawPath: string, body?: unknown): MockResponse {
    const path = rawPath.split("?")[0]!.replace(/^\/api/, "");
    calls.push({ method, path, body });
    const b = (body ?? {}) as Json;
    let match: RegExpMatchArray | null;

    if (path === "/admin/groups" && method === "GET") return { status: 200, body: groups };
    if (path === "/admin/ai/access") {
      if (method === "GET") return { status: 200, body: access };
      if (stale(b.expectedVersion, access.version)) return problem(409, "Přístup mezitím změnil jiný správce.");
      const incoming = b.groups as Json[];
      if (!Array.isArray(incoming) || new Set(incoming.map(group => group.groupId)).size !== incoming.length) return problem(400, "Skupiny se nesmí opakovat.");
      if (incoming.some(group => !groups.some(candidate => candidate.id === group.groupId))) return problem(404, "Skupina neexistuje.");
      access.groups = incoming.map(group => ({ ...group, groupName: groups.find(candidate => candidate.id === group.groupId)!.name }));
      access.version = version();
      return { status: 200, body: access };
    }
    if (/^\/admin\/ai\/profiles\/(embeddings|quick|chat|rerank)\/models$/.test(path) && method === "POST") {
      return { status: 200, body: { ok: true, code: null, models: [{ id: "sample-model", name: "Ukázkový model" }, { id: "sample-second", name: "Další ukázkový model" }] } };
    }
    if (path === "/admin/ai/switch") {
      if (method === "GET") return { status: 200, body: sw };
      if (stale(b.expectedVersion, sw.version)) return problem(409, "Nastavení mezitím změnil jiný správce.");
      sw.enabled = !!b.enabled;
      sw.version = version();
      return { status: 200, body: sw };
    }
    if ((match = path.match(/^\/admin\/ai\/profiles\/([a-z]+)$/))) {
      const kind = match[1]!;
      if (!profiles[kind]) return problem(404, "Takový profil AI neexistuje.");
      if (method === "GET") return { status: 200, body: { ...profiles[kind], modelLockedByIndex: kind === "embeddings" && !!active } };
      return saveProfile(kind, b);
    }
    if (path === "/admin/ai/features") {
      if (method === "GET") return { status: 200, body: features };
      if (stale(b.expectedVersion, features.version)) return problem(409, "Nastavení mezitím změnil jiný správce.");
      for (const item of (b.items ?? []) as Json[]) {
        const current = (features.items as Json[]).find((x) => x.task === item.task);
        if (!current) return problem(400, "Neznámá úloha AI.");
        if (item.cloudWorkingText !== null && item.cloudWorkingText !== undefined && current.cloudWorkingText === null) return problem(400, "Úloha neposílá pracovní text, povolení cloudu se jí netýká.");
        if (item.dailyLimit !== null && item.dailyLimit !== undefined) {
          if (!current.limitable) return problem(400, "Úloha nemá denní limit.");
          if (!Number.isInteger(item.dailyLimit) || item.dailyLimit < 1 || item.dailyLimit > 10_000) return problem(400, "Denní limit musí být celé číslo od 1 do 10 000.");
        }
        current.enabled = !!item.enabled;
        // Q3: a limitable task's limit is not kept when left out; the client sends it (or null) every time.
        if (current.limitable) current.dailyLimit = item.dailyLimit ?? null;
        if (current.cloudWorkingText !== null && item.cloudWorkingText !== null && item.cloudWorkingText !== undefined) current.cloudWorkingText = !!item.cloudWorkingText;
      }
      features.version = version();
      return { status: 200, body: features };
    }

    // ---- everything below is MOCKED: A3 overview, A2 test, V2 index ---------------------------
    if (config.withoutLaterPackages) return problem(404, "Not Found");
    if (path === "/admin/ai" && method === "GET") return { status: 200, body: overview() };
    if (path === "/admin/ai/usage" && method === "GET") return { status: 200, body: usageReport() };
    if ((match = path.match(/^\/admin\/ai\/profiles\/([a-z]+)\/test$/)) && method === "POST") {
      const profile = profiles[match[1]!];
      if (!profile?.configured) return problem(409, "Profil ještě není uložený.");
      if (match[1] === "embeddings" && config.embeddingsDown) return { status: 200, body: { ok: false, code: "timeout", latencyMs: 30_000, dimensions: null, model: profile.model } };
      return { status: 200, body: { ok: true, code: null, latencyMs: match[1] === "embeddings" ? 142 : match[1] === "rerank" ? 95 : 1180, dimensions: match[1] === "embeddings" ? 1024 : null, model: profile.model } };
    }
    if (path === "/admin/ai/index" && method === "GET") {
      advance();
      return { status: 200, body: indexStatus() };
    }
    if (path === "/admin/ai/index/generations" && method === "POST") {
      if (building) return problem(409, "Stavba indexu už běží.");
      if (b.mode === "replace") {
        const model = String(b.profile?.model ?? "").trim();
        if (!model) return problem(400, "Pro zapnutí profilu vyplňte model.");
        candidate = { ...profiles.embeddings, model, modelVersion: b.profile?.modelVersion ?? null };
        building = generation("Building", model, 0);
      } else building = generation("Building", String(profiles.embeddings!.model ?? "model"), 0);
      verified = false;
      return { status: 200, body: building };
    }
    if ((match = path.match(/^\/admin\/ai\/index\/generations\/([^/]+)\/(verify|activate|cancel)$/)) && method === "POST") {
      if (!building || building.id !== match[1]) return problem(404, "Generace neexistuje.");
      if (match[2] === "cancel") {
        building = null;
        candidate = null;
        return { status: 200, body: indexStatus() };
      }
      if (match[2] === "verify") {
        const complete = building.pendingTasks === 0;
        verified = complete;
        return { status: 200, body: { complete, missingPages: building.pendingTasks, stalePages: 0, dimensionsOk: true, sanity: { checked: 50, hitsAt5: complete ? 46 : 0 }, canActivate: complete } };
      }
      if (stale(b.expectedVersion, building.version)) return problem(409, "Generaci mezitím změnil jiný správce. Ověřte ji znovu.");
      if (!verified) return problem(409, "Generace není úplná.");
      if (active) active = { ...active, state: "Retired" };
      active = { ...building, state: "Active", activatedAt: now(), version: version() };
      building = null;
      if (candidate) profiles.embeddings = { ...candidate, version: version(), modelLockedByIndex: true };
      else profiles.embeddings!.modelLockedByIndex = true;
      return { status: 200, body: indexStatus() };
    }
    return problem(404, `unexpected ${method} ${path}`);
  }

  return {
    handle,
    calls,
    /** Test and demo hooks. */
    state: { profiles, features, sw, get building() { return building; }, get active() { return active; } },
    bumpGenerationVersion() {
      if (building) building = { ...building, version: version() };
    },
    /** Pages a building generation gains per status read from now on; 0 holds it running. */
    pace(step: number) {
      config.step = step;
    },
    complete() {
      if (building) building = { ...building, indexedPages: building.eligiblePages, chunks: building.eligiblePages * 5, pendingTasks: 0, version: version() };
    },
  };
}

export type AiAdminMock = ReturnType<typeof createAiAdminMock>;
