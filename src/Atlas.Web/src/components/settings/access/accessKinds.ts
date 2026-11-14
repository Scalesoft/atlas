import { computed, shallowRef, type Component } from "vue";
import { accessApi } from "../../../accessApi";
import t from "../../../i18n/settings-access";
import type { ChoiceOption } from "../types";
import type { AccessKindView, AccessLevel } from "./accessModel";

export type { AccessLevel } from "./accessModel";

/**
 * The access kinds registry (docs/access-model.md §4): one vocabulary for every kind of resource. One staircase
 * Vidí → Čte → Upravuje → Spravuje; each kind takes the steps that mean something for it and says in its own words what
 * a step allows. The registry is filled from GET /api/access/kinds (the enabled kinds, plugins included); until then, and
 * in the component gallery, the core kinds below stand in. A plugin may add UI extras (tabs on its resources) from its own
 * lazily loaded module with `registerAccessKind({ id, tabs })`; the security centre, the person's card, the grant flow
 * and the audit then show the kind without further code.
 */
export const accessLevels: AccessLevel[] = ["discover", "read", "write", "manage"];

const levelNames: Record<AccessLevel, { icon: string; rank: number }> = {
  discover: { icon: "eye", rank: 10 },
  read: { icon: "book-open", rank: 20 },
  write: { icon: "pencil", rank: 30 },
  manage: { icon: "shield-check", rank: 40 },
};

/** A tab a plugin adds to its resources (e.g. the projects of a YouTrack kind); the component is loaded lazily. */
export interface AccessKindTab {
  id: string;
  label: string;
  icon: string;
  component: Component;
}

export interface AccessKind {
  id: string;
  icon: string;
  one: string;
  many: string;
  /** The steps of the staircase this kind uses, lowest first, with what each allows here. */
  levels: Partial<Record<AccessLevel, string>>;
  /** Contributed by a plugin or another service, not by the core. */
  plugin?: string | null;
  /** Kinds that live inside another (a page or a vault inside a space). */
  parent?: string | null;
  /** The caller may grant something of this kind (from the server; the core kinds assume yes until it answers). */
  grantable?: boolean;
  tabs?: AccessKindTab[];
}
export type AccessKindRegistration = { id: string } & Partial<Omit<AccessKind, "id">>;

// The core kinds, in the server's words. They stand in until GET /api/access/kinds answers.
// Read where shown (a function), so the language of the page applies.
const coreKinds = (): AccessKind[] => {
  const k = t.kinds;
  return [
    { id: "wiki", icon: "shield-check", one: k.wiki.one, many: k.wiki.many, grantable: false, levels: { manage: k.wiki.manage } },
    { id: "space", icon: "book-open", one: k.space.one, many: k.space.many, grantable: true, levels: { read: k.space.read, write: k.space.write, manage: k.space.manage } },
    { id: "page", icon: "file-text", one: k.page.one, many: k.page.many, parent: "space", grantable: true, levels: { read: k.page.read, write: k.page.write, manage: k.page.manage } },
    { id: "vault", icon: "lock", one: k.vault.one, many: k.vault.many, parent: "space", grantable: true, levels: { discover: k.vault.discover, read: k.vault.read, manage: k.vault.manage } },
    { id: "secret", icon: "key-round", one: k.secret.one, many: k.secret.many, parent: "vault", grantable: true, levels: { discover: k.secret.discover, read: k.secret.read, manage: k.secret.manage } },
    { id: "token", icon: "key-round", one: k.token.one, many: k.token.many, grantable: false, levels: { read: k.token.read, write: k.token.write } },
    { id: "item", icon: "lock", one: k.item.one, many: k.item.many, grantable: true, levels: { read: k.item.read, write: k.item.write } },
  ];
};

const serverKinds = shallowRef<AccessKind[] | null>(null);
const registrations = shallowRef<ReadonlyMap<string, AccessKindRegistration>>(new Map());

function complete(kind: AccessKindRegistration): kind is AccessKind {
  return !!kind.icon && !!kind.one && !!kind.many && !!kind.levels;
}

/** Every known kind: the server's list once loaded (with the registered UI extras), the core and registered ones before. */
const known = computed<AccessKind[]>(() => {
  const extras = registrations.value;
  if (serverKinds.value) return serverKinds.value.map((kind) => ({ ...kind, tabs: extras.get(kind.id)?.tabs ?? kind.tabs }));
  const core = coreKinds();
  const merged = core.map((kind) => ({ ...kind, ...(extras.get(kind.id) ?? {}) }) as AccessKind);
  for (const extra of extras.values()) if (!core.some((kind) => kind.id === extra.id) && complete(extra)) merged.push(extra);
  return merged;
});

/** A plugin's own module adds or completes a kind (UI extras such as tabs); later calls merge into earlier ones. */
export function registerAccessKind(kind: AccessKindRegistration) {
  const next = new Map(registrations.value);
  next.set(kind.id, { ...(next.get(kind.id) ?? {}), ...kind });
  registrations.value = next;
}

function fromView(view: AccessKindView): AccessKind {
  const levels: Partial<Record<AccessLevel, string>> = {};
  for (const item of [...view.levels].sort((a, b) => levelRank(a.level) - levelRank(b.level))) levels[item.level] = item.description;
  return { id: view.id, icon: view.icon, one: view.one, many: view.many, parent: view.parent, plugin: view.plugin, grantable: view.grantable, levels };
}

/** Replaces the stand-ins with the server's kinds. */
export function applyAccessKinds(views: AccessKindView[]) {
  serverKinds.value = views.map(fromView);
}

let loading: Promise<void> | undefined;
/** Loads the kinds once per page (a failure keeps the core kinds and lets the next call try again). */
export function loadAccessKinds(load: () => Promise<AccessKindView[]> = accessApi.kinds): Promise<void> {
  loading ??= load().then(applyAccessKinds, (failure) => {
    loading = undefined;
    throw failure;
  });
  return loading;
}
/** For tests and the gallery: back to the core kinds and no registrations. */
export function resetAccessKinds() {
  serverKinds.value = null;
  registrations.value = new Map();
  loading = undefined;
}
/** Starts loading the server's kinds (errors keep the core ones) and returns the reactive list. */
export function useAccessKinds() {
  void loadAccessKinds().catch(() => undefined);
  return { kinds: known, loaded: computed(() => serverKinds.value !== null) };
}

/** A kind by id; an unknown one (a plugin the UI has no module for) still renders with a neutral icon and its id. */
export function accessKind(id: string): AccessKind {
  return known.value.find((kind) => kind.id === id) ?? { id, icon: "blocks", one: id, many: id, levels: {}, grantable: false };
}
export function accessKinds(): AccessKind[] {
  return known.value;
}
/** Kinds a person can be given access to in PrincipalGrantFlow (not the wiki role and not API tokens). */
export function grantableKinds(): AccessKind[] {
  return known.value.filter((kind) => kind.grantable !== false && kind.id !== "wiki" && kind.id !== "token");
}
/** Plugin kinds for the Co menu of Nastavení wiki. */
export function pluginKinds(): AccessKind[] {
  return known.value.filter((kind) => !!kind.plugin);
}

export function levelRank(level: AccessLevel | null | undefined) {
  return level ? levelNames[level].rank : 0;
}
export function levelLabel(level: AccessLevel | null | undefined) {
  return level ? t.levels[level] : t.levels.none;
}
/** The staircase of one kind for AccessLevelPicker, lowest first, each step in the kind's own words. */
export function levelOptions(kindId: string): ChoiceOption[] {
  const kind = accessKind(kindId);
  const used = accessLevels.filter((level) => kind.levels[level] !== undefined);
  return (used.length ? used : accessLevels).map((level) => ({ value: level, label: t.levels[level], icon: levelNames[level].icon, description: kind.levels[level] }));
}
/** The whole staircase, for badges: the same level fills the same bars in every kind. */
export function allLevelOptions(): ChoiceOption[] {
  return accessLevels.map((level) => ({ value: level, label: t.levels[level], icon: levelNames[level].icon }));
}
/** "Čte → Upravuje → Spravuje" of one kind. */
export function levelHierarchy(kindId: string) {
  return levelOptions(kindId)
    .map((option) => option.label)
    .join(" → ");
}
