import { api, ApiError, post } from "../api";
import type { VaultAccessLevel } from "../security";

/**
 * Server calls behind password references in the editor (WP4 V6). Metadata only comes from V6 (search, reference);
 * access requests and fresh re-authentication are V5/V4 endpoints; the release itself is V5's `releaseSecret`. Every call is no-store on the server,
 * and a released value is handed to the caller once and never kept here.
 */

/** Registry vocabulary of the server (VaultSecretView): userName, not the plugin's username. */
export type SecretApiField = "title" | "path" | "userName" | "password" | "url" | "notes";

/** GET /api/secrets/search and /api/secrets/{id}/reference: what the caller may Discover, never a value. */
export interface SecretReferenceView {
  id: string;
  title: string;
  path: string;
  vaultId: string;
  vaultName: string;
  spaceId: string;
  fields: SecretApiField[];
  level: VaultAccessLevel;
  /** For a caller who only discovers it: their latest request for the secret or its vault waits for a decision. */
  requestPending?: boolean;
  /** …or was rejected; they may ask again (editor 0.7.0 shows it after a reload). */
  requestRefused?: boolean;
}

/** The re-authentication refusal of a release (403 reauth_required or fresh_auth_unsupported): the effective protection mode
 * wants a fresh sign-in before every release; unsupported blocks it. */
export interface SecretReauthRequirement {
  mode: string;
  modeSource: string;
  supported: boolean;
}

export type SecretOperation = "reveal" | "copy";

/** POST /api/security/reauth (V4): a password step (then …/{id}/password) or a Hub sign-in at redirectUrl. */
export interface ReauthStart {
  id: string;
  method: "password" | "oidc";
  providerName?: string | null;
  expiresAt: string;
  redirectUrl?: string | null;
}

export interface ReauthStatus {
  id: string;
  state: "pending" | "verified" | "consumed" | "failed" | "expired";
  failure?: string | null;
}

export interface SecretsApi {
  search(query: string, spaceId: string | undefined, signal?: AbortSignal): Promise<SecretReferenceView[]>;
  /** Null when the caller may not Discover the secret (404). */
  reference(secretId: string, signal?: AbortSignal): Promise<SecretReferenceView | null>;
  requestAccess(secretId: string, reason: string, signal?: AbortSignal): Promise<void>;
  startReauth(secretId: string, operation: SecretOperation, returnUrl: string): Promise<ReauthStart>;
  verifyPassword(challengeId: string, password: string): Promise<boolean>;
  reauthStatus(challengeId: string): Promise<ReauthStatus>;
}

const noStore: RequestInit = { cache: "no-store" };
const path = (secretId: string) => `/secrets/${encodeURIComponent(secretId)}`;

export const secretsApi: SecretsApi = {
  search: (query, spaceId, signal) =>
    api<SecretReferenceView[]>(`/secrets/search?q=${encodeURIComponent(query)}${spaceId ? `&spaceId=${encodeURIComponent(spaceId)}` : ""}`, { ...noStore, signal }),
  async reference(secretId, signal) {
    try {
      return await api<SecretReferenceView>(`${path(secretId)}/reference`, { ...noStore, signal });
    } catch (error) {
      if (error instanceof ApiError && error.status === 404) return null;
      throw error;
    }
  },
  async requestAccess(secretId, reason, signal) {
    await api<unknown>(`${path(secretId)}/access-requests`, { method: "POST", ...noStore, signal, body: JSON.stringify({ level: "read", reason }) });
  },
  // Both request shapes of the WP4 contract: V4's flat secretId and the planned target object.
  startReauth: (secretId, operation, returnUrl) =>
    post<ReauthStart>("/security/reauth", { operation, secretId, target: { secretId }, returnUrl }),
  async verifyPassword(challengeId, password) {
    try {
      await post<unknown>(`/security/reauth/${encodeURIComponent(challengeId)}/password`, { password });
      return true;
    } catch (error) {
      if (error instanceof ApiError && error.status === 403) return false;
      throw error;
    }
  },
  reauthStatus: challengeId => api<ReauthStatus>(`/security/reauth/${encodeURIComponent(challengeId)}`, noStore),
};
