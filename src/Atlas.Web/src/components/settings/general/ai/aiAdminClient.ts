import type { InjectionKey } from "vue";
import type { AiModelCatalog, ProfileKind, modelCatalogRequest } from "./aiAdmin";
import { ApiError, api } from "../../../../api";
import t from "../../../../i18n/settings-ai";
import type { GenerationMode, GenerationVerification, GenerationView, IndexStatus, SaveAiProfileRequest } from "./aiAdmin";

/**
 * The index endpoints of Administrace → AI (V2, served). The index flow
 * takes a client so the gallery and tests can drive it with `aiAdminMock.ts`; production uses the
 * HTTP one, which calls exactly the contract of plan §10 / §24:
 *   GET  /api/admin/ai/index
 *   POST /api/admin/ai/index/generations { mode, profile? }
 *   POST /api/admin/ai/index/generations/{id}/verify | /activate { expectedVersion } | /cancel
 */
export interface SavedIndexProfileSelection { savedProfileId: string; expectedSavedVersion: string; expectedProfileVersion: string | null; }

export interface AiIndexClient {
  status(): Promise<IndexStatus>;
  start(mode: GenerationMode, profile?: SaveAiProfileRequest, saved?: SavedIndexProfileSelection): Promise<GenerationView>;
  verify(id: string): Promise<GenerationVerification>;
  activate(id: string, expectedVersion: string): Promise<void>;
  cancel(id: string): Promise<void>;
}

type Request = <T>(method: string, path: string, body?: unknown) => Promise<T>;

export function indexClientOver(request: Request): AiIndexClient {
  const base = "/admin/ai/index";
  return {
    status: () => request("GET", base),
    start: (mode, profile, saved) => request("POST", `${base}/generations`, saved ? { mode, ...saved } : { mode, profile }),
    verify: (id) => request("POST", `${base}/generations/${id}/verify`),
    activate: async (id, expectedVersion) => {
      await request("POST", `${base}/generations/${id}/activate`, { expectedVersion });
    },
    cancel: async (id) => {
      await request("POST", `${base}/generations/${id}/cancel`);
    },
  };
}

export const httpIndexClient: AiIndexClient = indexClientOver((method, path, body) =>
  api(path, { method, body: body === undefined ? undefined : JSON.stringify(body) }),
);

/** A client over the in-memory mock: same failures as the HTTP one (ApiError with status and code). */
export function mockIndexClient(handle: (method: string, path: string, body?: unknown) => { status: number; body?: unknown }): AiIndexClient {
  return indexClientOver(async (method, path, body) => {
    const result = handle(method, path, body);
    if (result.status >= 400) {
      const problem = (result.body ?? {}) as { detail?: string; code?: string };
      throw new ApiError(result.status, problem.detail ?? t.index.requestFailed(result.status), problem.code);
    }
    return result.body as never;
  });
}

export type AiModelDiscovery = (kind: ProfileKind, request: ReturnType<typeof modelCatalogRequest>, signal: AbortSignal) => Promise<AiModelCatalog>;
export const aiModelDiscoveryKey: InjectionKey<AiModelDiscovery> = Symbol("ai-model-discovery");
