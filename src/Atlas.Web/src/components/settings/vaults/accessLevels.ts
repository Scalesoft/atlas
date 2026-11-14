import t from "../../../i18n/settings-vaults";
import type { ChoiceOption } from "../types";
import type { VaultAccessLevel } from "../../../security";

/** The access staircase, lowest first (src/security.ts). */
export const vaultAccessLevels: VaultAccessLevel[] = ["discover", "read", "manage"];

/**
 * The access staircase in the owner's words. Each level includes every level below it; the
 * write level between Čte and Spravuje is planned and deliberately not offered yet.
 */
export const levelInfo: Record<VaultAccessLevel, { label: string; short: string; icon: string; description: string; includes: string }> = {
  discover: { get label() { return t.levels.discover.label; }, get short() { return t.levels.discover.label; }, icon: "eye", get description() { return t.levels.discover.description; }, get includes() { return t.levels.discover.includes; } },
  read: { get label() { return t.levels.read.label; }, get short() { return t.levels.read.label; }, icon: "key-round", get description() { return t.levels.read.description; }, get includes() { return t.levels.read.includes; } },
  manage: { get label() { return t.levels.manage.label; }, get short() { return t.levels.manage.label; }, icon: "shield-check", get description() { return t.levels.manage.description; }, get includes() { return t.levels.manage.includes; } },
};

/** Read when shown (not at module load), so the language of the page applies. */
export const levelOptions = (): ChoiceOption[] => vaultAccessLevels.map((level) => ({
  value: level,
  label: levelInfo[level].label,
  description: levelInfo[level].description,
  icon: levelInfo[level].icon,
}));
