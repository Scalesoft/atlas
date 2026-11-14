import { api, ApiError, post } from "./api";
import t from "./i18n/shell";
import { freshAuthUnsupportedCode, reauthRequiredCode, securityChange } from "./protection";
import type { VaultAccessLevel } from "./security";

/**
 * Access requests and audited release of secret values (WP4 V5). Typed clients only: the UI lives elsewhere.
 * A released value exists only in the response of its release; keep it in memory for at most expiresInSeconds, never in
 * storage, the document, logs or analytics, and drop it at once on a secretAccessRevoked event.
 */

/** The registry vocabulary of releasable fields. */
export type SecretField = "userName" | "password" | "url" | "notes";
export type ReleaseOperation = "reveal" | "copy";

export interface ReleaseRequest {
  fields: SecretField[];
  operation: ReleaseOperation;
  /** The fresh re-authentication proof (WP4 V4) after a reauth answer; one request consumes at most one proof. */
  proofId?: string | null;
  /** The page the reference is shown on; the server keeps it in the audit once it confirms the page references the secret
   * (Čtenost stránky, for managers of the vault). */
  pageId?: string | null;
}

/** mode: the effective protection mode; modeSource: where it is set; supported false: the sign-in cannot prove a fresh login
 * ("nepodporováno") and the value is never released. */
export interface ReauthChallenge {
  mode: "never" | "outsideTrustedNetworks" | "always";
  modeSource: "instance" | "vault" | "secret";
  supported: boolean;
}

/** A requested field that is empty in KeePass is left out of values. */
export type ReleaseResult =
  | { kind: "released"; values: Partial<Record<SecretField, string>>; expiresInSeconds: number }
  | { kind: "reauth"; reauth: ReauthChallenge };

/**
 * POST /api/secrets/{id}/release. The re-authentication refusal every endpoint shares (403 reauth_required, or
 * fresh_auth_unsupported, with the mode in `reauth`) is an answer, not an error: it asks for a fresh sign-in before this release
 * (the re-authentication window of security changes never covers one) or says it is unsupported. Other failures throw ApiError:
 * 403 (no Čte, a request is possible), 404 (missing, or code secret_field_empty), 409 with code vault_locked, vault_unavailable,
 * vault_error or secret_stale, 429 release_rate_limited.
 */
export async function releaseSecret(secretId: string, request: ReleaseRequest, signal?: AbortSignal): Promise<ReleaseResult> {
  const { token } = await api<{ token: string }>("/auth/csrf", { signal });
  const response = await fetch(`/api/secrets/${encodeURIComponent(secretId)}/release`, {
    method: "POST",
    headers: { "Content-Type": "application/json", "X-CSRF-TOKEN": token },
    body: JSON.stringify(request),
    credentials: "same-origin",
    cache: "no-store",
    signal,
  });
  const body = await response.json().catch(() => null);
  if (response.ok) {
    return { kind: "released", values: body.values, expiresInSeconds: body.expiresInSeconds };
  }
  if (response.status === 403 && (body?.code === reauthRequiredCode || body?.code === freshAuthUnsupportedCode) && body.reauth) {
    return { kind: "reauth", reauth: body.reauth as ReauthChallenge };
  }
  throw new ApiError(
    response.status,
    body?.detail ?? body?.title ?? t.err.requestFailed(response.status),
    typeof body?.code === "string" ? body.code : undefined,
  );
}

/** POST /api/secrets/{id}/release/report: the client showed (reveal) or copied (copy) released values; audited as a client
 * report, not as proof of reading. */
export function reportRelease(secretId: string, fields: SecretField[], operation: ReleaseOperation) {
  return post<void>(`/secrets/${secretId}/release/report`, { fields, operation });
}

export type AccessRequestState = "pending" | "approved" | "rejected" | "cancelled";

/** secretId and secretTitle are null for a request on the whole vault. decidedBy: approver, rejecter or (cancelled) the
 * requester. grantId/grantExpiresAt: the grant an approval created or that already covered it. canManage: the caller may decide. */
export interface AccessRequest {
  id: string;
  spaceId: string;
  vaultId: string;
  vaultName: string;
  secretId: string | null;
  secretTitle: string | null;
  level: Extract<VaultAccessLevel, "read">;
  reason: string;
  state: AccessRequestState;
  requesterId: string;
  requesterName: string;
  createdAt: string;
  decidedBy: string | null;
  decidedByName: string | null;
  decidedAt: string | null;
  note: string | null;
  grantId: string | null;
  grantExpiresAt: string | null;
  canManage: boolean;
}
export interface AccessRequestList {
  items: AccessRequest[];
  hasMore: boolean;
}

export const accessRequestApi = {
  /** Ask for Čte with a short reason (at most 500 characters); a pending request for the same target is returned as is.
   * 409 access_already_granted when the caller can read already. */
  requestSecret: (secretId: string, reason: string) =>
    post<AccessRequest>(`/secrets/${secretId}/access-requests`, { level: "read", reason }),
  requestVault: (vaultId: string, reason: string) =>
    post<AccessRequest>(`/vaults/${vaultId}/access-requests`, { level: "read", reason }),
  /** Requests the caller can approve or reject now; pending unless state is all. */
  list: (state: "pending" | "all" = "pending", vaultId?: string) =>
    api<AccessRequestList>(`/access-requests?${new URLSearchParams({ state, ...(vaultId ? { vaultId } : {}) })}`),
  mine: () => api<AccessRequestList>("/access-requests/mine"),
  /** expiresAt defaults to 30 days and never outlasts the approver's own authority (403 for a later explicit end). The approval
   * writes a grant, a security change: proof is a verified re-authentication (operation security) for the request's secret, or
   * its vault for a vault-wide request; null inside the session's re-authentication window. */
  approve: (requestId: string, expiresAt?: string | null, proof?: string | null) =>
    securityChange<AccessRequest>(`/access-requests/${requestId}/approve`, "POST", { expiresAt: expiresAt ?? null }, proof),
  /** A rejection changes no access and needs no fresh sign-in (by design); proof only if a server ever asks (reauth_required). */
  reject: (requestId: string, note?: string | null, proof?: string | null) =>
    securityChange<AccessRequest>(`/access-requests/${requestId}/reject`, "POST", { note: note ?? null }, proof),
  cancel: (requestId: string) => post<AccessRequest>(`/access-requests/${requestId}/cancel`),
};
