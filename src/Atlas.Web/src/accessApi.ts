import { api, post } from "./api";
import { securityChange } from "./protection";
import type {
  AccessCandidate,
  AccessGrantBody,
  AccessKindView,
  AccessResource,
  AdminApiToken,
  PrincipalAccess,
  ResourceAccess,
  ResourceAccessView,
  RevokeAllImpact,
} from "./components/settings/access/accessModel";

/**
 * Typed client of the security centre's access layer (docs/access-model.md §3): the access kinds, a principal's card, who
 * reaches one resource, the grant targets and candidates, grant changes and "Odebrat vše", and the wiki's personal API
 * tokens. UI components live elsewhere; this module only shapes requests and responses.
 *
 * Grant changes go through `securityChange`: a change that needs a fresh sign-in (manage on a space or page, every vault
 * and secret grant, revoke-all) is refused with 403 `reauth_required` without a proof; the caller confirms with
 * ConfirmWithReauth and repeats it with the proof (X-Atlas-Reauth), or sends it without one inside the session's window.
 */
const segment = (value: string) => encodeURIComponent(value);

function query(values: Record<string, string | null | undefined>) {
  const params = new URLSearchParams();
  for (const [key, value] of Object.entries(values)) if (value) params.set(key, value);
  const text = params.toString();
  return text ? `?${text}` : "";
}

export const accessApi = {
  /** The enabled kinds (YouTrack only when its module is configured). */
  kinds: () => api<AccessKindView[]>("/access/kinds"),
  /** The caller's own access, scope self (Profil › Moje přístupy). */
  me: () => api<PrincipalAccess>("/access/me"),
  /** A person's or group's card: the whole wiki for a wiki administrator, one space (`spaceId`) for its administrator. */
  principal: (kind: "user" | "group", id: string, spaceId?: string | null) =>
    api<PrincipalAccess>(`/access/principals/${kind}/${segment(id)}${query({ spaceId })}`),
  /** Who reaches one resource; `effective` lists at most 200 entries, then `hasMore`. `q` (a space) narrows by name or username before that cap. */
  resource: (kind: string, id: string, view: ResourceAccessView = "effective", q = "") =>
    api<ResourceAccess>(`/access/resources/${segment(kind)}/${segment(id)}${query({ view, q: q.trim() })}`),
  /** Up to 20 resources of a kind the caller may grant, for the "K čemu" step. */
  resources: (kind: string, q = "") => api<AccessResource[]>(`/access/resources${query({ kind, q: q.trim() })}`),
  /** Up to 20 active users or groups matching `q` (one character minimum): never the whole directory. `exceptMe` leaves the caller
   * out, for a share with others (a personal note's owner). */
  candidates: (kind: "user" | "group", q: string, exceptMe = false) =>
    api<AccessCandidate[]>(`/access/candidates${query({ kind, q: q.trim(), exceptMe: exceptMe ? "true" : undefined })}`),
  /** Sets or changes a grant; `proof` is a verified fresh sign-in, or null inside the session's window. */
  grant: (kind: string, id: string, body: AccessGrantBody, proof?: string | null) =>
    securityChange<void>(`/access/resources/${segment(kind)}/${segment(id)}/grants`, "PUT", body, proof),
  /** Closes a grant; `version` is its CAS version. */
  revoke: (kind: string, id: string, subjectKind: "user" | "group", subjectId: string, version: string | null | undefined, proof?: string | null) =>
    securityChange<void>(`/access/resources/${segment(kind)}/${segment(id)}/grants/${subjectKind}/${segment(subjectId)}${query({ version })}`, "DELETE", undefined, proof),
  /** What "Odebrat vše" would close. */
  revokeAllPreview: (userId: string) => api<RevokeAllImpact>(`/access/principals/user/${segment(userId)}/revoke-all`),
  /** Closes every direct grant and group membership and revokes the personal API tokens; the account stays active. */
  revokeAll: (userId: string, proof?: string | null) => securityChange<void>(`/access/principals/user/${segment(userId)}/revoke-all`, "POST", undefined, proof),
};

export const apiTokenAdminApi = {
  list: () => api<AdminApiToken[]>("/admin/api-tokens"),
  revoke: (id: string) => api<void>(`/admin/api-tokens/${segment(id)}`, { method: "DELETE" }),
  /** Whether the levels the token-rights upgrade set still wait for a wiki administrator's review. */
  summary: () => api<{ levelsReview: boolean }>("/admin/api-tokens/summary"),
  /** Confirms the review (idempotent); the notice and the overview item disappear. */
  confirmLevelsReview: () => post<{ levelsReview: boolean }>("/admin/api-tokens/levels-review", {}),
};
