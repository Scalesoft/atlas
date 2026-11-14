import t from "../../../i18n/settings-security";
import type { ProtectionLevel, ProtectionLevelView, ProtectionState } from "../../../protection";
import type { ChoiceOption, OriginStep } from "../types";
import type { ProtectionMode, ProtectionSetting } from "./types";

/** The three fresh sign-in modes (spec 5.1) in the words the owner sees. */
// The texts are getters, read when used, so the language of the page applies.
export const modeInfo: Record<ProtectionMode, { label: string; icon: string; description: string; trusted: boolean; elsewhere: boolean }> = {
  never: {
    get label() { return t.modes.never.label; },
    icon: "lock-open",
    get description() { return t.modes.never.description; },
    trusted: false,
    elsewhere: false,
  },
  outsideTrustedNetworks: {
    get label() { return t.modes.outsideTrustedNetworks.label; },
    icon: "network",
    get description() { return t.modes.outsideTrustedNetworks.description; },
    trusted: false,
    elsewhere: true,
  },
  always: {
    get label() { return t.modes.always.label; },
    icon: "lock",
    get description() { return t.modes.always.description; },
    trusted: true,
    elsewhere: true,
  },
};

export const modeOrder: ProtectionMode[] = ["never", "outsideTrustedNetworks", "always"];

export function modeOptions(current?: ProtectionMode): ChoiceOption[] {
  return modeOrder.map((mode) => ({
    value: mode,
    label: modeInfo[mode].label,
    description: modeInfo[mode].description,
    icon: modeInfo[mode].icon,
    tone: mode === "never" ? "warning" : mode === "always" ? "success" : undefined,
    badge: mode === current ? t.modes.now : mode === "always" ? t.modes.default : undefined,
  }));
}

/** The four choices on a vault or secret: inherit, or one of the modes. */
export function settingOptions(current: ProtectionSetting, inherited: { mode: ProtectionMode; from: string }): ChoiceOption[] {
  return [
    {
      value: "inherit",
      label: t.modes.inherit(modeInfo[inherited.mode].label),
      description: t.modes.inheritDescription(inherited.from),
      icon: "arrow-right-left",
      badge: current === "inherit" ? t.modes.now : undefined,
    },
    ...modeOrder.map((mode) => ({
      value: mode,
      label: modeInfo[mode].label,
      description: modeInfo[mode].description,
      icon: modeInfo[mode].icon,
      tone: (mode === "never" ? "warning" : mode === "always" ? "success" : undefined) as ChoiceOption["tone"],
      badge: current === mode ? t.modes.now : undefined,
    })),
  ];
}

/** The mode a level would take if it inherited: the nearest explicit level above it. */
export function inheritedMode(chain: ProtectionLevelView[]): { mode: ProtectionMode; level: ProtectionLevel } {
  for (let index = chain.length - 2; index >= 0; index--) {
    const step = chain[index]!;
    if (step.mode) return { mode: step.mode, level: step.level };
  }
  return { mode: "always", level: "instance" };
}

/** "instance", "trezoru Servery", "instance přes trezor" — where an inherited mode comes from, for "Převzít (nyní: …)". */
export function inheritedFromText(chain: ProtectionLevelView[]) {
  const from = inheritedMode(chain);
  if (from.level === "instance") return chain.length > 2 ? t.modes.fromInstanceViaVault : t.modes.fromInstance;
  const vault = chain.find((step) => step.level === "vault");
  return t.modes.fromVault(vault?.name ?? "");
}

/** The setting of a level as the wizard offers it: its own mode, or inherit. */
export function currentSetting(state: ProtectionState): ProtectionSetting {
  return state.level === "instance" ? state.effective : (state.explicit ?? "inherit");
}

/** EffectiveValue's chain from the API chain: Instance → Trezor → Heslo, each with its own mode or "přebírá". */
export function originChain(chain: ProtectionLevelView[], hrefs: Partial<Record<ProtectionLevel, string>> = {}): OriginStep[] {
  const names: Record<ProtectionLevel, string> = { instance: t.modes.levelInstance, vault: t.modes.levelVault, secret: t.modes.levelSecret };
  const icons: Record<ProtectionLevel, string> = { instance: "shield-check", vault: "lock", secret: "key-round" };
  return chain.map((step) => ({
    level: names[step.level],
    icon: icons[step.level],
    value: step.mode ? modeInfo[step.mode].label : undefined,
    explicit: !!step.mode,
    href: hrefs[step.level],
  }));
}
