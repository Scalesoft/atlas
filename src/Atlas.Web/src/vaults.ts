import { api, post, put } from "./api";
import shellMore from "./i18n/shell-more";
import type { VaultAccessLevel } from "./security";

/** unlocked · needsPassword (the stored master password stopped working) · unavailable (share or file unreachable) · error (damaged or unsupported file). */
export type VaultStatus = "unlocked" | "needsPassword" | "unavailable" | "error";
/** managed: a folder Atlas keeps in its own data directory · local: a folder the administrator enters · smb: a network share. */
export type VaultLocationKind = "managed" | "local" | "smb";
export type VaultField = "userName" | "password" | "url" | "notes";

/** Root and credentials are returned only to the wiki administrator; the password never is. */
export interface VaultLocation {
  id: string;
  name: string;
  kind: VaultLocationKind;
  root: string | null;
  username: string | null;
  domain: string | null;
  hasPassword: boolean;
  vaultCount: number;
}
export interface VaultLocationDraft {
  name: string;
  kind: VaultLocationKind;
  root: string;
  username: string;
  domain: string;
  /** Empty keeps the stored password. */
  password: string;
}
/** How each location kind is named and drawn, shared by the location wizards, lists and pickers. */
export const vaultLocationKinds: Record<VaultLocationKind, { label: string; description: string; badge: string; icon: string }> = {
  managed: { get label() { return shellMore.vault.managed.label; }, get description() { return shellMore.vault.managed.description; }, get badge() { return shellMore.vault.managed.badge; }, icon: "database" },
  local: { get label() { return shellMore.vault.local.label; }, get description() { return shellMore.vault.local.description; }, get badge() { return shellMore.vault.local.badge; }, icon: "folder" },
  smb: { get label() { return shellMore.vault.smb.label; }, get description() { return shellMore.vault.smb.description; }, badge: "SMB", icon: "server" },
};

/** The draft switched to another kind: the name stays, the previous kind's folder and credentials do not. */
export function withVaultLocationKind(draft: VaultLocationDraft, kind: VaultLocationKind): VaultLocationDraft {
  return kind === draft.kind ? draft : { ...draft, kind, root: kind === "smb" ? "smb://" : "", username: "", domain: "", password: "" };
}

/** A managed location has no folder to enter; SMB needs smb://…, a local folder any other path (the server checks it). */
export function vaultLocationRootReady(draft: VaultLocationDraft): boolean {
  const root = draft.root.trim();
  if (draft.kind === "managed") return true;
  return draft.kind === "smb" ? /^smb:\/\/.+/i.test(root) : root.length > 0 && !/^smb:/i.test(root);
}

export interface Vault {
  id: string;
  spaceId: string;
  spaceName: string;
  name: string;
  locationId: string;
  locationName: string;
  fileName: string;
  status: VaultStatus;
  lastFailure: string | null;
  lastRefreshAt: string | null;
  snapshotSha256: string | null;
  entryCount: number;
  createdAt: string;
  /** The caller's vault-wide level, null for none (always sent since WP4 V3). */
  level?: VaultAccessLevel | null;
}
/** Registry metadata of one KeePass entry. Values are never part of it. */
export interface VaultSecret {
  secretId: string;
  entryUuid: string;
  title: string;
  groupPath: string;
  availableFields: VaultField[];
  firstSeenAt: string;
  removedAt: string | null;
  /** The caller's level on this entry, null for none (always sent since WP4 V3). */
  level?: VaultAccessLevel | null;
}
/** A KeePass file in a location's folder; usedBy names the vault using it when the caller may see that vault. */
export interface VaultLocationFile {
  name: string;
  sizeBytes: number;
  lastWriteAt: string;
  registered: boolean;
  usedBy: string | null;
}
export interface VaultLocationFiles {
  files: VaultLocationFile[];
  /** More files matched than the server lists (500); narrow the search. */
  truncated: boolean;
}
export interface VaultVerification {
  vault: Vault;
  entryCount: number | null;
  snapshotSha256: string | null;
  missing: VaultSecret[];
}
export interface CreatedVault {
  vault: Vault;
  /** Returned once, only when the server generated the master password. */
  generatedPassword: string | null;
}
export interface NewVault {
  spaceId: string;
  locationId: string;
  name: string;
  fileName: string;
}

export const vaultStatusLabels: Record<VaultStatus, string> = {
  get unlocked() { return shellMore.vault.status.unlocked; },
  get needsPassword() { return shellMore.vault.status.needsPassword; },
  get unavailable() { return shellMore.vault.status.unavailable; },
  get error() { return shellMore.vault.status.error; },
};
export const vaultFieldLabels: Record<VaultField, string> = {
  get userName() { return shellMore.vault.field.userName; },
  get password() { return shellMore.vault.field.password; },
  get url() { return shellMore.vault.field.url; },
  get notes() { return shellMore.vault.field.notes; },
};

export const vaultApi = {
  locations: () => api<VaultLocation[]>("/vault-locations"),
  saveLocation: (id: string | null, draft: VaultLocationDraft) =>
    id ? put<VaultLocation>(`/vault-locations/${id}`, draft) : post<VaultLocation>("/vault-locations", draft),
  archiveLocation: (id: string) => api<void>(`/vault-locations/${id}`, { method: "DELETE" }),
  /** *.kdbx in the location's folder; spaceId: the space a vault is attached to (lets its administrator list). */
  locationFiles: (id: string, spaceId: string | null, q = "", signal?: AbortSignal) => {
    const query = new URLSearchParams();
    if (spaceId) query.set("spaceId", spaceId);
    if (q) query.set("q", q);
    const search = query.toString();
    return api<VaultLocationFiles>(`/vault-locations/${id}/files${search ? `?${search}` : ""}`, { signal });
  },
  testLocation: (draft: VaultLocationDraft, locationId: string | null) =>
    post<{ message: string }>("/vault-locations/test", { ...draft, locationId }),
  list: () => api<Vault[]>("/vaults"),
  create: (vault: NewVault, password: string | null) =>
    post<CreatedVault>("/vaults/new", { ...vault, generatePassword: password === null, password }),
  attach: (vault: NewVault, password: string) => post<Vault>("/vaults/attach", { ...vault, password }),
  replacePassword: (id: string, password: string) => put<Vault>(`/vaults/${id}/password`, { password }),
  archive: (id: string) => post<void>(`/vaults/${id}/archive`),
  verify: (id: string) => post<VaultVerification>(`/vaults/${id}/verify`),
  secrets: (id: string) => api<VaultSecret[]>(`/vaults/${id}/secrets`),
};
