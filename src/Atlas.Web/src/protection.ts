import { readonly, ref } from "vue";
import shellMore from "./i18n/shell-more";
import { api, ApiError, post } from "./api";

/**
 * Typed clients of the security centre API (WP4 V4): protection modes, trusted networks and proxies, connection diagnostics and
 * fresh re-authentication. UI components live elsewhere; this module only shapes requests and responses.
 */

/** never (Nevyžadovat) · outsideTrustedNetworks (Mimo důvěryhodné sítě) · always (Vždy). */
export type ProtectionMode = "never" | "outsideTrustedNetworks" | "always";
export type ProtectionLevel = "instance" | "vault" | "secret";

export const protectionModeLabels: Record<ProtectionMode, string> = {
  get never() { return shellMore.protection.never; },
  get outsideTrustedNetworks() { return shellMore.protection.outside; },
  get always() { return shellMore.protection.always; },
};

/** supported · unknown (a provider not verified yet) · unsupported ("nepodporováno": nothing requiring it is released or changed). */
export type FreshAuthState = "supported" | "unknown" | "unsupported";

/** One level of the inheritance chain; mode null inherits from the level above. */
export interface ProtectionLevelView {
  level: ProtectionLevel;
  targetId: string | null;
  name: string | null;
  mode: ProtectionMode | null;
}
/** How many vaults and current secrets take their effective mode from this level. */
export interface ProtectionImpact {
  vaults: number;
  secrets: number;
}
/** What a release by the caller would need right now. blocked: re-authentication is required but cannot be proven. */
export interface ReleasePreview {
  reauthRequired: boolean;
  networkException: boolean;
  freshAuth: FreshAuthState;
  blocked: boolean;
}
export interface ProtectionState {
  level: ProtectionLevel;
  targetId: string | null;
  /** The level's own mode; null while it inherits. */
  explicit: ProtectionMode | null;
  effective: ProtectionMode;
  /** The level the effective mode comes from. */
  source: ProtectionLevel;
  chain: ProtectionLevelView[];
  canManage: boolean;
  /** CAS token of the level's own setting; null while it inherits. */
  version: string | null;
  /** Present for managers only. */
  impact: ProtectionImpact | null;
  preview: ReleasePreview;
  configurationVersion: string;
}

/** A vault or secret with its own mode (GET /api/security/protection/overrides, WikiAdmin); secretId null: the vault's own. */
export interface ProtectionOverride {
  spaceId: string;
  spaceName: string;
  vaultId: string;
  vaultName: string;
  secretId: string | null;
  secretTitle: string | null;
  mode: ProtectionMode;
}

export type NetworkKind = "lan" | "vpn" | "other";
export interface TrustedAddress {
  id: string;
  name: string;
  /** null for a proxy. */
  kind: NetworkKind | null;
  /** Normalized CIDR; a single address is /32 or /128. */
  range: string;
  isActive: boolean;
  warnings: string[];
}
export interface TrustedNetworkList {
  /** Security configuration version: the expectedVersion of every network, proxy and hop change. */
  version: string;
  items: TrustedAddress[];
}
export interface ProxyEnvironment {
  /** Http:TrustedProxies of the server environment, imported once at importedAt and never applied again. */
  addresses: string[];
  differs: boolean;
  /** Http:RecoverTrustedProxies is on: proxies are read-only in the GUI. */
  recovery: boolean;
  importedAt: string | null;
}
export interface TrustedProxyList {
  version: string;
  hopLimit: number;
  maxHopLimit: number;
  items: TrustedAddress[];
  environment: ProxyEnvironment;
}
export interface TrustedAddressDraft {
  name: string;
  /** Networks only. */
  kind?: NetworkKind | null;
  range: string;
  isActive: boolean;
  expectedVersion: string;
}
export interface TrustedAddressSaved {
  id: string;
  warnings: string[];
  version: string;
}
export interface TrustedMatch {
  id: string;
  name: string;
  kind: NetworkKind | null;
  range: string;
}
export interface AddressTestResult {
  address: string | null;
  addressError: string | null;
  isProxy: boolean;
  network: TrustedMatch | null;
  inactiveMatches: TrustedMatch[];
  draft: {
    range: string | null;
    normalized: boolean;
    error: string | null;
    containsAddress: boolean;
    warnings: string[];
  } | null;
}

/** direct · forwarded (through trusted proxies) · unclear (no network exception). */
export type ClientAddressSource = "direct" | "forwarded" | "unclear";
export type ClientAddressReason =
  | "direct"
  | "forwarded"
  | "noPeer"
  | "untrustedForwarding"
  | "proxyWithoutForwardedFor"
  | "invalidForwardedFor"
  | "hopLimitExceeded";
/** "Vaše připojení": diagnostics for the wiki administrator, never an authorization proof. */
export interface ConnectionView {
  address: string | null;
  source: ClientAddressSource;
  reason: ClientAddressReason;
  peer: string | null;
  peerIsProxy: boolean;
  hops: number;
  forwardedFor: string[];
  network: TrustedMatch | null;
  networkException: boolean;
  instanceMode: ProtectionMode;
  reauthRequired: boolean;
  version: string;
}

/** security: a security change (no target = global security); reveal and copy: a value release of one secret. */
export type ReauthOperation = "security" | "reveal" | "copy";
export interface ReauthTarget {
  vaultId?: string;
  secretId?: string;
}
/** password: send it to …/password; oidc: open redirectUrl, the provider returns to returnUrl, then poll status(). */
export interface ReauthStart {
  id: string;
  method: "password" | "oidc";
  providerId: string | null;
  providerName: string | null;
  expiresAt: string;
  redirectUrl: string | null;
}
export type ReauthState = "pending" | "verified" | "consumed" | "failed" | "expired";
export type ReauthFailure = "password" | "identity" | "session" | "auth_time_missing" | "auth_time_stale";
export interface ReauthStatus {
  id: string;
  operation: ReauthOperation | "selftest";
  targetKind: "instance" | "vault" | "secret" | "none";
  targetId: string | null;
  method: "password" | "oidc";
  state: ReauthState;
  failure: ReauthFailure | null;
  expiresAt: string;
  /** Until when a verified proof can be used (two minutes, once). */
  proofExpiresAt: string | null;
  /** Once verified: until when further security changes of this session need no new sign-in (ten minutes). Never a release. */
  windowExpiresAt?: string | null;
}
/** Per configured provider; whether its login form was really shown is not knowable on the server. */
export interface FreshAuthProvider {
  providerId: string;
  displayName: string;
  verdict: FreshAuthState;
  authTimePresent: boolean | null;
  authTimeFresh: boolean | null;
  checkedAt: string | null;
  selfTest: boolean | null;
}

/** Error codes of a refused change, release or start, all with status 403: reauth_required (sign in again, then retry with the
 * proof), fresh_auth_unsupported (show "nepodporováno"), reauth_password (wrong password, the session stays), proxy_lockout (the
 * change would cut the admin's own connection). */
export const reauthRequiredCode = "reauth_required";
export const freshAuthUnsupportedCode = "fresh_auth_unsupported";

/** The header that carries a verified proof with the change it authorizes. */
export const reauthHeader = "X-Atlas-Reauth";

/** Held back from the server's end of the window, so a change sent just before it closes is not refused on the way. */
const windowMargin = 15_000;
const windowUntil = ref<number | null>(null);

/**
 * The re-authentication window of this session as the server last reported it (GET /api/security/me, a verified challenge): for ten
 * minutes after a fresh sign-in, security changes go out without a proof and the confirmation asks for no password. The server stays
 * the judge; a reauth_required answer closes the window here, so the next confirmation signs in again. Value releases never use it.
 */
export const reauthWindow = {
  /** Epoch milliseconds the window is treated as open until; null when closed. */
  until: readonly(windowUntil),
  /** Takes the server's end of the window (ISO), or closes it for none. */
  sync(expiresAt: string | null | undefined) {
    const end = expiresAt ? Date.parse(expiresAt) - windowMargin : Number.NaN;
    windowUntil.value = Number.isFinite(end) && end > Date.now() ? end : null;
  },
  close() {
    windowUntil.value = null;
  },
  isOpen(now = Date.now()) {
    return windowUntil.value !== null && now < windowUntil.value;
  },
};

/** Every security change goes through here: the proof header when there is one, and a reauth_required refusal closes the window. */
export async function securityChange<T>(path: string, method: string, body: unknown, proof: string | null | undefined): Promise<T> {
  try {
    return await api<T>(path, {
      method,
      body: body === undefined ? undefined : JSON.stringify(body),
      headers: proof ? { [reauthHeader]: proof } : undefined,
    });
  } catch (error) {
    if (error instanceof ApiError && error.code === reauthRequiredCode) reauthWindow.close();
    throw error;
  }
}

function protectionPath(level: ProtectionLevel, id?: string) {
  return level === "instance" ? "/security/protection" : `/${level === "vault" ? "vaults" : "secrets"}/${id}/protection`;
}

/** proof: a verified re-authentication, or null inside the re-authentication window. */
function addresses(path: "networks" | "proxies") {
  return {
    create: (draft: TrustedAddressDraft, proof: string | null) => securityChange<TrustedAddressSaved>(`/security/${path}`, "POST", draft, proof),
    update: (id: string, draft: TrustedAddressDraft, proof: string | null) =>
      securityChange<TrustedAddressSaved>(`/security/${path}/${id}`, "PUT", draft, proof),
    remove: (id: string, expectedVersion: string, proof: string | null) =>
      securityChange<{ version: string }>(`/security/${path}/${id}?${new URLSearchParams({ expectedVersion })}`, "DELETE", undefined, proof),
  };
}

export const protectionApi = {
  get: (level: ProtectionLevel, id?: string) => api<ProtectionState>(protectionPath(level, id)),
  /** mode null inherits (not for the instance); expectedVersion is the current state's version. */
  set: (level: ProtectionLevel, id: string | undefined, mode: ProtectionMode | null, expectedVersion: string | null, proof: string | null) =>
    securityChange<ProtectionState>(protectionPath(level, id), "PUT", { mode, expectedVersion }, proof),
  /** Every vault and secret that does not follow the instance default. */
  overrides: () => api<ProtectionOverride[]>("/security/protection/overrides"),
};

export const networksApi = {
  list: () => api<TrustedNetworkList>("/security/networks"),
  test: (request: { address?: string; range?: string; excludeId?: string }) => post<AddressTestResult>("/security/networks/test", request),
  ...addresses("networks"),
};

export const proxiesApi = {
  list: () => api<TrustedProxyList>("/security/proxies"),
  setHopLimit: (hopLimit: number, expectedVersion: string, proof: string | null) =>
    securityChange<{ version: string }>("/security/proxies/hops", "PUT", { hopLimit, expectedVersion }, proof),
  ...addresses("proxies"),
};

export const connectionApi = {
  get: () => api<ConnectionView>("/security/connection"),
};

export const reauthApi = {
  start: (operation: ReauthOperation, target: ReauthTarget = {}, returnUrl?: string) =>
    post<ReauthStart>("/security/reauth", { operation, ...target, returnUrl }),
  password: (id: string, password: string) => post<ReauthStatus>(`/security/reauth/${id}/password`, { password }),
  status: (id: string) => api<ReauthStatus>(`/security/reauth/${id}`),
  /** Wiki administrators: checks that the provider proves a fresh login (releases nothing). */
  startSelfTest: (providerId?: string, returnUrl?: string) => post<ReauthStart>("/security/reauth/self-test", { providerId, returnUrl }),
  selfTests: () => api<FreshAuthProvider[]>("/security/reauth/self-test"),
};
