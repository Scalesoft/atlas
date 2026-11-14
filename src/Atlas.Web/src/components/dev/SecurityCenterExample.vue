<script setup lang="ts">
import { computed, onBeforeUnmount, ref } from "vue";
import AtlasIcon from "../AtlasIcon.vue";
import { Button } from "../ui/button";
import SettingsShell from "../settings/SettingsShell.vue";
import SettingsPage from "../settings/SettingsPage.vue";
import SettingsSection from "../settings/SettingsSection.vue";
import SettingsList from "../settings/SettingsList.vue";
import SettingsListRow from "../settings/SettingsListRow.vue";
import SettingsTabs from "../settings/SettingsTabs.vue";
import SearchField from "../settings/SearchField.vue";
import FilterMenu from "../settings/FilterMenu.vue";
import StatusBadge from "../settings/StatusBadge.vue";
import SubjectAvatar from "../settings/SubjectAvatar.vue";
import ResultBanner from "../settings/ResultBanner.vue";
import EmptyState from "../settings/EmptyState.vue";
import { useSettingsRoute } from "../settings/useSettingsRoute";
import type { ChoiceOption, SettingsNavGroup, SettingsTab } from "../settings/types";
import AccessEntryRow from "../settings/access/AccessEntryRow.vue";
import AccessExceptionsList from "../settings/access/AccessExceptionsList.vue";
import AccessGrantEditor from "../settings/access/AccessGrantEditor.vue";
import LevelLegend from "../settings/access/LevelLegend.vue";
import PrincipalAccessView from "../settings/access/PrincipalAccessView.vue";
import ResourceAccessPanel from "../settings/access/ResourceAccessPanel.vue";
import PrincipalGrantFlow, { type PrincipalGrantDraft } from "../settings/access/PrincipalGrantFlow.vue";
import { levelLabel, registerAccessKind } from "../settings/access/accessKinds";
import type { AccessChangeDraft, AccessEntry, AccessResource, AccessSource, PrincipalAccess, ResourceAccess, ResourceAccessView } from "../settings/access/accessModel";
import type { AccessRow } from "../settings/access/accessView";

// The security centre on sample data in the shape of the /api/access contract (docs/access-model.md §3): no API, nothing
// persisted. The YouTrack plugin contributes its kind the way a plugin would, from its own module.
registerAccessKind({ id: "youtrack", icon: "blocks", one: "YouTrack projekt", many: "YouTrack", plugin: "YouTrack", parent: "space", grantable: false, levels: { read: "Vidí úkoly projektu ve wiki" } });

const router = useSettingsRoute();
const base = "security-center";
const rest = computed(() => router.route.value.segments.slice(1));
const href = (path: string) => router.href(`${base}/${path}`);

// The app's appearance: the owner's blue accent in the current colour scheme.
const media = window.matchMedia("(prefers-color-scheme: dark)");
const mode = ref(media.matches ? "dark" : "light");
const onScheme = () => (mode.value = media.matches ? "dark" : "light");
media.addEventListener("change", onScheme);
onBeforeUnmount(() => media.removeEventListener("change", onScheme));

const day = 86_400_000;
const at = (days: number) => new Date(Date.now() + days * day).toISOString();
const direct = (level: AccessSource["level"], extra: Partial<AccessSource> = {}): AccessSource => ({ via: "direct", label: "", level, grantId: `g-${level}`, version: "v1", startAt: null, expiresAt: null, effective: true, ...extra });
const viaGroup = (label: string, groupId: string, level: AccessSource["level"]): AccessSource => ({ via: "group", label, groupId, level, effective: true });

const groups = computed<SettingsNavGroup[]>(() => [
  { id: "start", items: [{ path: `${base}/overview`, label: "Přehled", icon: "layout-dashboard", count: 3, tone: "warning", countLabel: "k řešení" }] },
  {
    id: "who",
    label: "Kdo",
    items: [
      { path: `${base}/people`, label: "Lidé", icon: "account" },
      { path: `${base}/groups`, label: "Skupiny", icon: "users" },
      { path: `${base}/tokens`, label: "API tokeny", icon: "key-round" },
    ],
  },
  {
    id: "what",
    label: "Co",
    items: [
      { path: `${base}/spaces`, label: "Prostory", icon: "book-open" },
      { path: `${base}/vaults`, label: "Trezory", icon: "lock", count: 5, countLabel: "trezorů" },
      { path: `${base}/youtrack`, label: "YouTrack", icon: "blocks" },
    ],
  },
  {
    id: "rules",
    label: "Pravidla",
    items: [
      { path: `${base}/protection`, label: "Ochrana hesel", icon: "fingerprint" },
      { path: `${base}/networks`, label: "Sítě a proxy", icon: "network" },
    ],
  },
  {
    id: "activity",
    label: "Činnost",
    items: [
      { path: `${base}/requests`, label: "Žádosti o přístup", icon: "user-check", count: 2, tone: "warning", countLabel: "čeká na vyřízení" },
      { path: `${base}/audit`, label: "Audit", icon: "scroll-text" },
    ],
  },
  {
    id: "general",
    label: "Obecné",
    items: [
      { path: `${base}/modules`, label: "Moduly a integrace", icon: "blocks" },
      { path: `${base}/backups`, label: "Zálohy", icon: "download" },
      { path: `${base}/files`, label: "Umístění souborů", icon: "folder" },
    ],
  },
]);
const spaceGroups: SettingsNavGroup[] = [
  {
    id: "space",
    label: "Prostor",
    items: [
      { path: `${base}/space/general`, label: "Obecné", icon: "settings" },
      { path: `${base}/space/sharing`, label: "Sdílení", icon: "link" },
      { path: `${base}/space/access`, label: "Přístupy", icon: "users" },
      { path: `${base}/space/templates`, label: "Šablony stránek", icon: "page" },
    ],
  },
  { id: "vaults", label: "Trezory a hesla", items: [{ path: `${base}/space/vaults`, label: "Trezory", icon: "lock", count: 2, countLabel: "trezory" }] },
];

// Jana's card: every access with all its paths, grouped by space in the view.
const jana = ref<PrincipalAccess>({
  principal: { kind: "user", id: "jana", name: "Jana Nováková", detail: "jana.novakova", isActive: true, isWikiAdmin: false, lastSignInAt: at(0), groups: [{ id: "g-ops", name: "Provoz tým" }, { id: "g-dev", name: "Vývojáři" }] },
  scope: "wiki",
  canManage: true,
  entries: [
    { kind: "space", resourceId: "provoz", title: "Provoz", spaceId: "provoz", spaceName: "Provoz", spaceIcon: "server", spaceColor: "green", level: "write", sources: [{ ...viaGroup("Provoz tým", "g-ops", "write"), groupSource: "hub", verificationLimited: true, expiresAt: new Date(Date.now() + 5 * 60_000).toISOString(), grantExpiresAt: null }], note: "41 stránek přebírá přístup prostoru" },
    { kind: "vault", resourceId: "v-servers", title: "Servery a síť", spaceId: "provoz", spaceName: "Provoz", level: "read", sources: [direct("read", { expiresAt: at(9) })], note: "14 hesel" },
    { kind: "secret", resourceId: "s-router", title: "Hlavní router", parentTitle: "Síťová zařízení", spaceId: "provoz", spaceName: "Provoz", level: "read", sources: [direct("read", { expiresAt: at(80) })] },
    { kind: "youtrack", resourceId: "yt-ops", title: "Projekty OPS a NET", spaceId: "provoz", spaceName: "Provoz", level: "read", sources: [{ via: "derived", label: "Z role v prostoru", level: "read", effective: true }], note: "Povolené projekty prostoru" },
    { kind: "space", resourceId: "eng", title: "Engineering", spaceId: "eng", spaceName: "Engineering", spaceIcon: "code-xml", spaceColor: "violet", level: "read", sources: [viaGroup("Vývojáři", "g-dev", "read")], note: "128 stránek přebírá přístup prostoru" },
    { kind: "page", resourceId: "p-adr", title: "Rozhodnutí 2026", parentTitle: "Architektura", spaceId: "eng", spaceName: "Engineering", level: "write", sources: [direct("write"), viaGroup("Vývojáři", "g-dev", "read")], note: "Vlastní přístup" },
    { kind: "page", resourceId: "p-salary", title: "Platy a smlouvy", spaceId: "eng", spaceName: "Engineering", level: null, sources: [], note: "Jen pro vybrané" },
    { kind: "token", resourceId: "t-backup", title: "Zálohovací skript", level: "read", sources: [direct("read", { expiresAt: at(150) })], note: "Zálohy na NAS" },
  ],
});

// Who can reach the space Provoz, effective view.
const provoz = ref<ResourceAccess>({
  resource: { kind: "space", id: "provoz", title: "Provoz" },
  canManage: true,
  automatic: [{ label: "Správci wiki", detail: "automaticky ve všech prostorech", level: "manage", count: 3 }],
  entries: [
    { subject: { kind: "group", id: "g-ops", name: "Provoz tým", memberCount: 12 }, level: "write", sources: [direct("write")] },
    { subject: { kind: "group", id: "g-dev", name: "Vývojáři", memberCount: 20 }, level: "read", sources: [direct("read")] },
    { subject: { kind: "user", id: "petr", name: "Petr Svoboda", detail: "petr.svoboda" }, level: "manage", sources: [direct("manage")] },
    { subject: { kind: "user", id: "igor", name: "Igor Kolář", detail: "Externí · igor.kolar" }, level: "read", sources: [direct("read", { expiresAt: at(5) })] },
    { subject: { kind: "user", id: "jana", name: "Jana Nováková", detail: "jana.novakova" }, level: "write", sources: [viaGroup("Provoz tým", "g-ops", "write")] },
    { subject: { kind: "user", id: "tomas", name: "Tomáš Král", detail: "tomas.kral" }, level: "write", sources: [viaGroup("Provoz tým", "g-ops", "write")] },
    { subject: { kind: "user", id: "eva", name: "Eva Malá", detail: "eva.mala", isActive: false }, level: "read", sources: [direct("read", { startAt: at(3), effective: false })] },
  ],
  hasMore: false,
  exceptions: [
    { kind: "page", id: "p-oncall", title: "Kontakty", parentTitle: "Pohotovost", mode: "own", grantCount: 5 },
    { kind: "vault", id: "v-servers", title: "Servery a síť", mode: "grants", grantCount: 4 },
    { kind: "secret", id: "s-router", title: "Hlavní router", parentTitle: "Síťová zařízení", mode: "grants", grantCount: 1 },
  ],
});
const provozView = ref<ResourceAccessView>("effective");

const people = [
  { id: "jana", name: "Jana Nováková", detail: "jana.novakova · Místní účet", badges: [{ tone: "warning" as const, icon: "hourglass", label: "Přístup končí za 9 dní" }] },
  { id: "martin", name: "Marek Musil", detail: "marek.musil · Místní účet", badges: [{ tone: "accent" as const, icon: "shield-check", label: "Správce wiki" }] },
  { id: "igor", name: "Igor Kolář", detail: "igor.kolar · Externí přihlášení", badges: [] },
  { id: "tomas", name: "Tomáš Král", detail: "tomas.kral · Místní účet", badges: [{ tone: "warning" as const, icon: "user-check", label: "Žádost čeká" }] },
  { id: "eva", name: "Eva Malá", detail: "eva.mala · Místní účet", badges: [{ tone: "neutral" as const, icon: "ban", label: "Deaktivovaný" }] },
];
const peopleQuery = ref("");
const peopleState = ref("");
const peopleStates: ChoiceOption[] = [
  { value: "", label: "Všechny", icon: "users" },
  { value: "attention", label: "Vyžaduje pozornost", icon: "triangle-alert" },
  { value: "admin", label: "Správci wiki", icon: "shield-check" },
  { value: "deactivated", label: "Deaktivované", icon: "ban" },
];
// FilterMenu `multiple`: several groups at once as checkboxes; with more than eight a search above them (first match preselected, Enter).
const peopleGroups = ref<string[]>([]);
const peopleGroupOptions: ChoiceOption[] = ["Vývoj", "Podpora", "Obchod", "Vedení", "Účetnictví", "Projektoví manažeři", "Externisté", "Správci serverů", "Marketing", "Analytici"]
  .map((label, index) => ({ value: `group-${index}`, label, badge: String(12 - index) }));

const personTabs = computed<SettingsTab[]>(() => [
  { id: "access", label: "Přístupy", icon: "key-round", href: href("people/jana") },
  { id: "account", label: "Účet", icon: "account", href: href("people/jana/account") },
  { id: "groups", label: "Skupiny", icon: "users", count: 2, href: href("people/jana/groups") },
  { id: "tokens", label: "Tokeny", icon: "key-round", count: 1, href: href("people/jana/tokens") },
  { id: "activity", label: "Činnost", icon: "scroll-text", href: href("people/jana/activity") },
]);
const spaceTabs = (prefix: string, center: boolean): SettingsTab[] => [
  { id: "access", label: "Kdo má přístup", icon: "users", href: href(prefix) },
  { id: "exceptions", label: "Výjimky", icon: "layers", count: provoz.value.exceptions?.length, href: href(`${prefix}/exceptions`) },
  ...(center
    ? [
        { id: "links", label: "Odkazy", icon: "link", count: 2, href: href(`${prefix}/links`) },
        { id: "activity", label: "Činnost", icon: "scroll-text", href: href(`${prefix}/activity`) },
      ]
    : []),
];

// Přidat přístup z karty osoby: the "Který" step searches the kind's resources (here a fixed list).
const catalog: AccessResource[] = [
  { kind: "vault", id: "v-servers", title: "Servery a síť", spaceName: "Provoz", note: "14 hesel · Jana už Čte" },
  { kind: "vault", id: "v-net", title: "Síťová zařízení", spaceName: "Provoz", note: "9 hesel" },
  { kind: "vault", id: "v-prod", title: "Produkce", spaceName: "Engineering", note: "31 hesel" },
  { kind: "space", id: "provoz", title: "Provoz", note: "41 stránek" },
  { kind: "space", id: "eng", title: "Engineering", note: "128 stránek" },
  { kind: "page", id: "p-oncall", title: "Kontakty", parentTitle: "Pohotovost", spaceName: "Provoz" },
  { kind: "secret", id: "s-router", title: "Hlavní router", parentTitle: "Síťová zařízení", spaceName: "Provoz" },
];
const targets = ref<AccessResource[]>([]);
function searchTargets(kind: string, query: string) {
  const text = query.toLocaleLowerCase("cs");
  targets.value = catalog.filter((item) => item.kind === kind && (!text || `${item.title} ${item.parentTitle ?? ""}`.toLocaleLowerCase("cs").includes(text)));
}

// Changes stay in the gallery: the row's editor, a revoke, a new grant.
const editing = ref<string | null>(null);
const result = ref<{ key: number; text: string }>();
let resultKey = 0;
const say = (text: string) => (result.value = { key: ++resultKey, text: `${text} (jen v galerii)` });
function toggle(row: AccessRow) {
  editing.value = editing.value === row.key ? null : row.key;
}
function saveEntry(row: AccessRow, _grant: AccessSource, draft: AccessChangeDraft) {
  jana.value = {
    ...jana.value,
    entries: jana.value.entries.map((entry: AccessEntry) =>
      `${entry.kind}:${entry.resourceId}` === row.key ? { ...entry, level: draft.level, sources: entry.sources.map((source) => (source.via === "direct" ? { ...source, level: draft.level, startAt: draft.startAt, expiresAt: draft.expiresAt } : source)) } : entry,
    ),
  };
  editing.value = null;
  say(`${row.title}: ${levelLabel(draft.level)}`);
}
function saveSubject(row: AccessRow, _grant: AccessSource, draft: AccessChangeDraft) {
  provoz.value = {
    ...provoz.value,
    entries: provoz.value.entries.map((entry) =>
      `${entry.subject.kind}:${entry.subject.id}` === row.key ? { ...entry, level: draft.level, sources: entry.sources.map((source) => (source.via === "direct" ? { ...source, level: draft.level, expiresAt: draft.expiresAt, startAt: draft.startAt } : source)) } : entry,
    ),
  };
  editing.value = null;
  say(`${row.title}: ${levelLabel(draft.level)}`);
}
const revoke = (row: AccessRow) => say(`Odebrat ${row.title}? Stránka se zeptá v ConfirmDialog, u trezoru s novým přihlášením`);
const granted = (draft: PrincipalGrantDraft) => say(`Jana Nováková: ${levelLabel(draft.level)} · ${draft.target.title}`);

// Single components on the Komponenty tab.
const legendLevel = ref("");
const sampleRow = computed<AccessRow>(() => ({ key: "sample", kind: "vault", resourceId: "v-servers", title: "Servery a síť", detail: "Trezor · 14 hesel", level: "read", sources: [direct("read", { expiresAt: at(9) })] }));
const sampleEditing = ref(false);
const sampleGrant = direct("read", { expiresAt: at(30) });

const view = computed(() => {
  const [section, id, part] = rest.value;
  if (section === "components") return { shell: "none", page: "components" };
  if (section === "space") return { shell: "space", page: part === "exceptions" ? "exceptions" : "space-access" };
  if (section === "people" && id === "jana") return { shell: "wiki", page: part === "new" ? "flow" : "person", tab: part ?? "access" };
  if (section === "spaces" && id === "provoz") return { shell: "wiki", page: part === "exceptions" ? "exceptions" : "space-access" };
  if (section === "profile") return { shell: "profile", page: "mine" };
  return { shell: "wiki", page: "people" };
});
const inWiki = computed(() => view.value.shell === "wiki");
const spacePrefix = computed(() => (inWiki.value ? "spaces/provoz" : "space/access"));
const exceptionHref = () => href(`${spacePrefix.value}`);
const examples: SettingsTab[] = [
  { id: "person", label: "Karta osoby", icon: "account", href: href("people/jana") },
  { id: "flow", label: "Přidat přístup", icon: "plus", href: href("people/jana/new") },
  { id: "space", label: "Prostor", icon: "book-open", href: href("spaces/provoz") },
  { id: "settings", label: "Nastavení prostoru", icon: "settings", href: href("space/access") },
  { id: "profile", label: "Moje přístupy", icon: "key-round", href: href("profile") },
  { id: "components", label: "Komponenty", icon: "layers", href: href("components") },
];
const example = computed(() => {
  const [section, id, part] = rest.value;
  if (section === "components") return "components";
  if (section === "space") return "settings";
  if (section === "spaces") return "space";
  if (section === "profile") return "profile";
  if (section === "people" && id && part === "new") return "flow";
  return "person";
});
</script>

<template>
  <div class="security-center-example">
    <SettingsTabs :options="examples" :model-value="example" label="Ukázky bezpečnostního centra" />
    <ResultBanner v-if="result" :key="result.key" :message="result.text" @dismiss="result = undefined" />
    <div class="atlas security-center-proto" data-accent="blue" :data-mode="mode" data-security-center-proto>
      <SettingsShell v-if="view.shell === 'wiki'" title="Správa wiki" eyebrow="Celá wiki" subtitle="Lidé, přístupy, pravidla a provoz všech prostorů." icon="settings" :groups="groups" nav-label="Správa wiki">
        <!-- Lidé -->
        <SettingsPage v-if="view.page === 'people'" title="Lidé" icon="account" description="Klikněte na člověka a uvidíte všechno, k čemu má přístup a odkud.">
          <template #actions><Button type="button"><AtlasIcon name="plus" :size="16" />Pozvat člověka</Button></template>
          <div class="proto-filters" role="search" aria-label="Filtr lidí">
            <div class="proto-search"><SearchField v-model="peopleQuery" label="Hledat jméno nebo login" placeholder="Hledat jméno nebo login…" /></div>
            <FilterMenu v-model="peopleState" :options="peopleStates" label="Stav" icon="users" />
            <FilterMenu v-model:values="peopleGroups" multiple :options="peopleGroupOptions" label="Skupina" icon="users" all-label="Všechny" search-label="Hledat skupinu…" />
            <p class="proto-count">63 lidí</p>
          </div>
          <SettingsSection label="Lidé">
            <SettingsList label="Lidé">
              <SettingsListRow v-for="person in people" :key="person.id" :title="person.name" :description="person.detail">
                <template #leading><SubjectAvatar kind="user" :name="person.name" /></template>
                <template v-if="person.badges.length" #meta><StatusBadge v-for="badge in person.badges" :key="badge.label" :tone="badge.tone" :icon="badge.icon" :label="badge.label" size="sm" /></template>
                <template #actions><Button variant="ghost" size="sm" as-child><a :href="href('people/jana')"><AtlasIcon name="arrow-right" :size="15" />Otevřít</a></Button></template>
              </SettingsListRow>
            </SettingsList>
          </SettingsSection>
        </SettingsPage>

        <!-- Karta osoby -->
        <SettingsPage v-else-if="view.page === 'person'" title="Jana Nováková" icon="account" description="jana.novakova · Aktivní · naposledy přihlášena dnes 9:12 · skupiny Provoz tým, Vývojáři" :back="{ href: href('people'), label: 'Všichni lidé' }">
          <template #actions>
            <Button size="sm" as-child><a :href="href('people/jana/new')"><AtlasIcon name="plus" :size="15" />Přidat přístup</a></Button>
            <Button variant="outline" size="sm" type="button" class="proto-danger" @click="say('Odebrat vše: náhled dopadu, pak nové přihlášení')"><AtlasIcon name="trash" :size="15" />Odebrat vše…</Button>
          </template>
          <SettingsTabs :options="personTabs" :model-value="view.tab ?? 'access'" label="Části karty" />
          <PrincipalAccessView v-if="view.tab === 'access'" :access="jana" :editing="editing" :group-href="() => href('people')" @change="toggle" @cancel="editing = null" @save="saveEntry" @revoke="revoke" />
          <EmptyState v-else icon="layers" title="Tato část čte API" text="Účet, Skupiny, Tokeny a Činnost jsou v aplikaci stávající části detailu účtu; galerie je bez API." compact />
        </SettingsPage>

        <!-- Přidat přístup z karty osoby -->
        <SettingsPage v-else-if="view.page === 'flow'" title="Přidat přístup" icon="plus" description="Jana Nováková · přístup se projeví ihned po uložení." :back="{ href: href('people/jana'), label: 'Jana Nováková' }">
          <SettingsSection label="Přidat přístup">
            <PrincipalGrantFlow subject="Jana Nováková" :targets="targets" :initial="{ kind: 'vault', target: catalog[0], level: 'read' }" @search="searchTargets" @save="granted" @cancel="router.navigate(`${base}/people/jana`)" />
          </SettingsSection>
        </SettingsPage>

        <!-- Prostor: kdo má přístup / výjimky -->
        <SettingsPage v-else title="Provoz" icon="book-open" description="Prostor · 41 stránek · 2 trezory" :back="{ href: href('spaces'), label: 'Všechny prostory' }">
          <template #actions><Button variant="outline" size="sm" type="button"><AtlasIcon name="settings" :size="15" />Nastavení prostoru</Button></template>
          <SettingsTabs :options="spaceTabs(spacePrefix, true)" :model-value="view.page === 'exceptions' ? 'exceptions' : 'access'" label="Části prostoru" />
          <ResourceAccessPanel v-if="view.page === 'space-access'" v-model:view="provozView" :access="provoz" :editing="editing" :subject-href="(subject) => (subject.id === 'jana' ? href('people/jana') : undefined)" :group-href="() => href('people')" @add="say('Přidat přístup: GrantFlow na vlastní adrese')" @change="toggle" @cancel="editing = null" @save="saveSubject" @revoke="revoke" />
          <AccessExceptionsList v-else :exceptions="provoz.exceptions ?? []" :href="exceptionHref" />
        </SettingsPage>
      </SettingsShell>

      <!-- Tatáž komponenta v nastavení prostoru -->
      <SettingsShell v-else-if="view.shell === 'space'" title="Provoz" eyebrow="Tento prostor" subtitle="Nastavení a přístupy platí pro tento prostor." icon="book-open" :groups="spaceGroups" nav-label="Nastavení prostoru">
        <SettingsPage title="Přístupy" icon="users" description="Kdo může prostor číst, upravovat nebo spravovat. Výjimky ukazují stránky a trezory s vlastním přístupem.">
          <SettingsTabs :options="spaceTabs(spacePrefix, false)" :model-value="view.page === 'exceptions' ? 'exceptions' : 'access'" label="Části přístupů" />
          <ResourceAccessPanel v-if="view.page === 'space-access'" v-model:view="provozView" :access="provoz" :editing="editing" @add="say('Přidat přístup')" @change="toggle" @cancel="editing = null" @save="saveSubject" @revoke="revoke" />
          <AccessExceptionsList v-else :exceptions="provoz.exceptions ?? []" :href="exceptionHref" />
        </SettingsPage>
      </SettingsShell>

      <!-- Profil: moje přístupy, jen ke čtení -->
      <SettingsShell
        v-else-if="view.shell === 'profile'"
        title="Profil"
        eyebrow="Váš účet"
        subtitle="Osobní údaje, přihlášení a vaše předvolby vzhledu."
        icon="account"
        :current-path="`${base}/profile`"
        :groups="[{ id: 'me', items: [
          { path: `${base}/profile/account`, label: 'Účet', icon: 'account' },
          { path: `${base}/profile`, label: 'Moje přístupy', icon: 'key-round', exact: true },
          { path: `${base}/profile/requests`, label: 'Moje žádosti', icon: 'user-check' },
          { path: `${base}/profile/api`, label: 'Osobní API', icon: 'code-xml' },
          { path: `${base}/profile/appearance`, label: 'Vzhled', icon: 'palette' },
        ] }]"
        nav-label="Profil"
      >
        <SettingsPage title="Moje přístupy" icon="key-round" description="K čemu máte přístup a odkud. Chybí vám něco? Požádejte o přístup přímo u trezoru nebo stránky.">
          <PrincipalAccessView :access="{ ...jana, scope: 'self', canManage: false }" readonly />
        </SettingsPage>
      </SettingsShell>

      <!-- Jednotlivé komponenty -->
      <div v-else class="proto-components settings-ui">
        <SettingsSection title="AccessEntryRow" description="access/AccessEntryRow.vue — jeden přístup jako jedna věta: kdo nebo co · odkud · do kdy · úroveň (vždy poslední, pevná šířka, barva úrovně). Akce Změnit a Odebrat se objeví při najetí nebo fokusu přes konec řádku, na dotykovém zařízení zůstávají pod řádkem. Ctrl+mezerník, Alt+Enter, Ctrl+., Shift+F10 a pravé tlačítko otevřou na řádku tutéž nabídku „Co s tím jde dělat“ jako ve stromu stránek (RowActionsMenu) se Změnit a Odebrat; řádek bez těchto akcí (automatický, přes skupinu, jen ke čtení) nabídku nemá.">
          <SettingsList label="Ukázkové řádky">
            <AccessEntryRow :row="sampleRow" :editing="sampleEditing" @change="sampleEditing = !sampleEditing" @cancel="sampleEditing = false" @save="(_row, _grant, draft) => { sampleEditing = false; say(`Uložit ${levelLabel(draft.level)}`); }" @revoke="revoke" />
            <AccessEntryRow :row="{ key: 'group', kind: 'space', resourceId: 'provoz', title: 'Jana Nováková', detail: 'jana.novakova', subject: { kind: 'user', id: 'jana', name: 'Jana Nováková' }, level: 'write', sources: [viaGroup('Provoz tým', 'g-ops', 'write')] }" :group-href="() => href('people')" />
            <AccessEntryRow :row="{ key: 'auto', kind: 'space', resourceId: 'provoz', title: 'Správci wiki', detail: '3 lidé · automaticky ve všech prostorech', subject: { kind: 'group', id: 'a', name: 'Správci wiki' }, level: 'manage', sources: [{ via: 'automatic', label: 'Automaticky', level: 'manage', effective: true }], locked: true }" readonly />
            <AccessEntryRow :row="{ key: 'none', kind: 'page', resourceId: 'p', title: 'Platy a smlouvy', detail: 'Stránka · jen pro vybrané', level: null, sources: [] }" readonly />
          </SettingsList>
        </SettingsSection>
        <SettingsSection title="LevelLegend" description="access/LevelLegend.vue — barevný klíč úrovní s počty a zároveň filtr úrovně: stisk ukáže jen tu úroveň, druhý stisk vše.">
          <LevelLegend v-model="legendLevel" :counts="{ manage: 2, write: 3, read: 5, discover: 1 }" />
        </SettingsSection>
        <SettingsSection title="AccessGrantEditor" description="access/AccessGrantEditor.vue — změna jednoho přidělení na místě: úroveň na schodišti druhu (AccessLevelPicker) a platnost (GrantValidityFields), explicitní Uložit.">
          <AccessGrantEditor kind="vault" :grant="sampleGrant" name="Jana Nováková" @save="say(`Uložit ${levelLabel($event.level)}`)" @cancel="say('Zrušit')" />
        </SettingsSection>
        <SettingsSection title="AccessExceptionsList" description="access/AccessExceptionsList.vue — výjimky prostoru; každá otevře tentýž panel Kdo má přístup.">
          <AccessExceptionsList :exceptions="provoz.exceptions ?? []" :href="exceptionHref" />
        </SettingsSection>
        <SettingsSection title="Sestavy" description="PrincipalAccessView seskupuje přístupy podle skutečné ikony a barvy prostoru (SettingsSection slot icon); jednu skupinu kreslí jeho část PrincipalAccessGroup (jen uvnitř něj, samostatně se nepoužívá). Výjimky a další zdroje se rozbalují pomocí InlineReveal; při hledání se otevřou automaticky. Provoz ukazuje obnovované ověření Hubu, trezor skutečný konec oprávnění. Proč má tento přístup vypíše všechny cesty, i slabší. ResourceAccessPanel je opačný pohled: lidé u jednoho zdroje." />
      </div>
    </div>
  </div>
</template>

<style scoped>
.security-center-example { display: grid; gap: 14px; min-width: 0; }
.atlas.security-center-proto { display: block; min-height: 0; padding: 24px; border: 1px solid var(--line); border-radius: var(--atlas-radius-lg); background: var(--surface); color: var(--text); }
.proto-filters { display: flex; flex-wrap: wrap; align-items: center; gap: 8px; }
.proto-search { flex: 1 1 240px; max-width: 360px; }
.proto-count { margin: 0 0 0 auto; color: var(--muted); font-size: 12.5px; }
.proto-danger { color: var(--error); }
.proto-components { display: grid; gap: 16px; }
@media (max-width: 680px) { .atlas.security-center-proto { padding: 12px; } }
</style>
