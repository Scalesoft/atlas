import { api } from "./api";
import t from "./i18n/shell";
import shellMore from "./i18n/shell-more";
import type { FreshAuthState } from "./protection";

/**
 * One access level over a vault or secret, a staircase like page roles (WP4 V3): every level includes the lower ones.
 * discover (Vidí): title, path and field names. read (Čte): also reveal and copy the value. manage (Spravuje): also grant
 * access, change protection and read the audit. Writing values is not part of 1.1 (managed in KeePass).
 */
export type VaultAccessLevel = "discover" | "read" | "manage";

export const vaultAccessLevelLabels: Record<VaultAccessLevel, string> = {
  get discover() { return shellMore.role.discover; },
  get read() { return t.role.reader; },
  get manage() { return t.role.admin; },
};

const levelOrder: VaultAccessLevel[] = ["discover", "read", "manage"];

/** Whether a held level (null: none) includes the required one. */
export function includesVaultLevel(held: VaultAccessLevel | null | undefined, required: VaultAccessLevel) {
  return held != null && levelOrder.indexOf(held) >= levelOrder.indexOf(required);
}

/** One vault the caller has a level on; secretGrants: grants on single secrets reach further into the vault. */
export interface VaultAccessSummary {
  vaultId: string;
  spaceId: string;
  level: VaultAccessLevel;
  secretGrants: boolean;
}
/** What the UI may offer; the server still authorizes every call. */
export interface SecurityCapabilities {
  /** Networks, proxies and the default protection mode: every wiki administrator. */
  globalSecurityAdmin: boolean;
  /** A wiki administrator manages every vault and secret; vaultManagerSpaces is then empty instead of listing every space. */
  managesAllVaults: boolean;
  /** Spaces whose vaults the caller manages as a space administrator. */
  vaultManagerSpaces: string[];
  vaults: VaultAccessSummary[];
  /** Whether the caller can prove a fresh sign-in; unsupported blocks everything that requires one. */
  freshAuth: FreshAuthState;
  /** password or oidc; null without any method. */
  freshAuthMethod: "password" | "oidc" | null;
  /** Until when security changes of this session need no new sign-in (ten minutes after the last one); null when none is open. */
  reauthWindowExpiresAt?: string | null;
}

export const securityApi = {
  me: () => api<SecurityCapabilities>("/security/me"),
};
