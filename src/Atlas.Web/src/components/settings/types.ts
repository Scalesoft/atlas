/** Shared vocabulary of the settings design system. */
export type StatusTone = "success" | "warning" | "danger" | "info" | "neutral" | "accent";

export interface SettingsNavItem {
  /** Path below the settings base, e.g. `global/security/networks`. */
  path: string;
  label: string;
  icon: string;
  /** A small number beside the label (items, open issues). */
  count?: number;
  /** Tone of the count; `warning`/`danger` also add an icon so the meaning is not colour-only. */
  tone?: StatusTone;
  /** Spoken with the count, e.g. "1 vyžaduje pozornost". */
  countLabel?: string;
  /** Matches only this exact path (for overview pages that are ancestors of others). */
  exact?: boolean;
  /** Temporarily unavailable (a save in progress): the link stays visible but does not navigate. */
  disabled?: boolean;
  /** A quiet word beside the label, e.g. a module's state "vypnuto" (refresh 2.5 rail). */
  note?: string;
}

export interface SettingsNavGroup {
  id: string;
  label?: string;
  items: SettingsNavItem[];
}

export interface ChoiceOption {
  value: string;
  label: string;
  description?: string;
  icon?: string;
  tone?: StatusTone;
  /** Short tag in the card corner, e.g. "Doporučeno" or "Nyní". */
  badge?: string;
  disabled?: boolean;
  /** Why an option is unavailable; shown in place of the description. */
  disabledReason?: string;
}

export interface OriginStep {
  /** Level name: Instance, Trezor, Heslo. */
  level: string;
  icon?: string;
  /** Human value when set on this level. */
  value?: string;
  /** True when this level sets its own value; false when it inherits. */
  explicit: boolean;
  href?: string;
}

export interface ImpactItem {
  icon: string;
  label: string;
  detail?: string;
  tone?: StatusTone;
}

export interface AccessPath {
  /** How the right reaches the subject: directly, through a group, as a space administrator or a role. */
  via: "direct" | "group" | "spaceAdmin" | "role";
  /** The group, space or role name. */
  label: string;
  /** Where the grant is set: "trezor Provoz – servery", "prostor Provoz". */
  scope: string;
  /** Value of the one level the path gives, from the level options (a higher level includes the lower ones). */
  level: string;
  /** Inherited automatically and not editable here (space administrators). */
  locked?: boolean;
  until?: string;
  note?: string;
}

export interface FlowStep {
  id: string;
  /** Short name in the progress list. */
  label: string;
  /** The question asked on the stage. */
  title: string;
  hint?: string;
  icon?: string;
  /** What was chosen, shown on the chip once the step is done. */
  summary?: string;
  /** False disables Další (nothing chosen yet). */
  ready?: boolean;
  /** Runs on Další; false keeps the step (and lets it show its errors). */
  validate?: () => boolean;
  /** Další emits `next` without moving on; the parent navigates (async submit). */
  manual?: boolean;
  /** A manual operation has completed for the current draft; progressive flows may reveal its result. */
  complete?: boolean;
  /** Lock this section while an external operation needs recovery. */
  disabled?: boolean;
  nextLabel?: string;
  nextIcon?: string;
  /** The step confirms itself (e.g. ConfirmWithReauth): no Další. */
  hideNext?: boolean;
  /** A result step: no Zrušit, Zpět or Další, and done steps can no longer be reopened. */
  final?: boolean;
}

export interface GrantSubject {
  id: string;
  name: string;
  kind: "user" | "group";
  detail?: string;
}

export interface GrantDraft {
  kind: "user" | "group";
  subjectId: string;
  /** The chosen role or access level. */
  role: string;
  startAt: string;
  expiresAt: string;
}

export interface SummaryItem {
  key: string;
  icon: string;
  label: string;
  value?: string;
  /** Monospace value (paths, ranges). */
  code?: boolean;
}

/** One link of a decision chain: address → how determined → rule → decision. */
export interface DecisionStep {
  icon: string;
  label: string;
  value: string;
  detail?: string;
  tone: StatusTone;
}

export interface SettingsTab {
  id: string;
  label: string;
  icon: string;
  href: string;
  count?: number;
}
