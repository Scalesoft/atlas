import type { NewVault, VaultLocationDraft, VaultLocationKind } from "../../../vaults";

/**
 * Input and output of the vault wizard, on the V2 API shape of src/vaults.ts: `vault` is NewVault,
 * `newLocation` a VaultLocationDraft, and the result carries CreatedVault.generatedPassword.
 */
export interface VaultWizardLocation {
  id: string;
  name: string;
  kind: VaultLocationKind;
  /** Shown to the wiki administrator only; null for everyone else (the server hides it). */
  root: string | null;
  vaultCount: number;
}
/** A KeePass file the attach step offers; a registered one is shown disabled, with the vault's name when known. */
export interface VaultWizardFile {
  name: string;
  sizeBytes: number;
  lastWriteAt: string;
  registered: boolean;
  usedBy: string | null;
}
/** Lists the *.kdbx of a location for the attach step, narrowed by a part of the name. */
export type VaultFileLister = (locationId: string, query: string, signal: AbortSignal) => Promise<{ files: VaultWizardFile[]; truncated: boolean }>;
export interface VaultWizardDraft {
  mode: "create" | "attach";
  vault: NewVault;
  /** null asks the server to generate the master password (create only). */
  password: string | null;
  newLocation?: VaultLocationDraft;
}
export interface VaultWizardResult {
  id: string;
  name: string;
  entryCount: number;
  generatedPassword: string | null;
}
