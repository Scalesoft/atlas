<script setup lang="ts">
import SearchField from "../SearchField.vue";
import { computed, inject, nextTick, onBeforeUnmount, onMounted, ref, watch } from "vue";
import AtlasIcon from "../../AtlasIcon.vue";
import { Button } from "../../ui/button";
import { errorMessage } from "../../../api";
import { animateAtlas, motionMs } from "../../../motion/aiMotion";
import { adminApi, adminPageLimit, type DirectoryUser, type Group, type Invitation, type UserDirectorySummary } from "../../../administration";
import { isPlainNavigationClick } from "../../../pageLinks";
import EmptyState from "../EmptyState.vue";
import ErrorState from "../ErrorState.vue";
import LoadingNote from "../LoadingNote.vue";
import FilterMenu from "../FilterMenu.vue";
import SettingsPage from "../SettingsPage.vue";
import SettingsSection from "../SettingsSection.vue";
import StatusBadge from "../StatusBadge.vue";
import SubjectAvatar from "../SubjectAvatar.vue";
import { useSettingsContext } from "../context";
import { useDirtyGuard } from "../dirtyGuard";
import { useSettingsFlash } from "../flash";
import { locale } from "../../../i18n";
import t from "../../../i18n/settings-people";
import type { ChoiceOption } from "../types";
import { useSettingsRoute } from "../useSettingsRoute";
import PersonCard from "./PersonCard.vue";
import PersonPeek from "./PersonPeek.vue";
import { useWorkspacePanel } from "../../../composables/useWorkspacePanel";
import { Table, TableCaption, TableHead, TableHeader, TableRow, TableCell } from "../../ui/table";
import { outOfReach } from "../../../lib/outOfReach";
import { foldText } from "../../../lib/foldText";
import { createLeaveWave, createRowGlide } from "./rowGlide";
import { useAccessRequestFeed } from "../requests/accessRequestFeed";
import AccountStatusBadge from "./AccountStatusBadge.vue";
import { directorySummaryKey } from "./directorySummary";
import { accountSourceLabels, accountStatus, accountStatusLabels, type AccountStatus } from "./accountStatus";
import InviteAccountWizard from "./InviteAccountWizard.vue";
import type { InviteDraft } from "./types";
import "./people.css";

/**
 * Nastavení wiki → Kdo → Lidé (wiki administrator only). `sub` is the third path segment:
 * none = the list with search and status filter in one line (`?hledat=`, `?stav=`), `new` = the invite wizard,
 * anything else = the person's card with that id (`part`: its tab, or `new` for Přidat přístup). Rows carry badges
 * only where something needs attention: an account waiting for activation or deactivated, a pending access request.
 */
const props = defineProps<{ sub?: string; part?: string }>();
const requests = useAccessRequestFeed();
const context = useSettingsContext();
const router = useSettingsRoute();
const flash = useSettingsFlash();
const guard = useDirtyGuard();

// The server answers one page (adminPageLimit rows) of the accounts matching the typed search; `more` = a full page, so another may follow.
const users = ref<DirectoryUser[]>([]);
const more = ref(false);
const loadingMore = ref(false);
// The directory's tallies: Nastavení wiki's own read when it is around (it reads them as Lidé opens), else this page's.
const shared = inject(directorySummaryKey, undefined);
const own = ref<UserDirectorySummary>();
const summary = computed(() => (shared ? (shared.loaded.value ? shared.people.value : undefined) : own.value));
const loading = ref(false);
const loaded = ref(false);
const error = ref("");
let disposed = false;
let generation = 0;
const typed = () => search.value.trim() || undefined;

/** The list for the typed search; an older answer (a search typed over it) is dropped. */
async function loadList() {
  if (!context.isWikiAdmin) return;
  const current = ++generation;
  loading.value = true;
  error.value = "";
  try {
    const result = await adminApi.users({ q: typed() });
    if (disposed || current !== generation) return;
    users.value = result;
    more.value = result.length >= adminPageLimit;
    await findOne();
    loaded.value = true;
  } catch (failure) {
    if (!disposed && current === generation) error.value = errorMessage(failure);
  } finally {
    if (!disposed && current === generation) loading.value = false;
  }
}
async function loadSummary() {
  if (shared) return shared.load();
  try {
    const counted = await adminApi.userSummary();
    if (!disposed) own.value = counted;
  } catch {
    // The tallies are an extra; the filter stays without them.
  }
}
async function load() {
  if (!context.isWikiAdmin) return;
  if (!shared) void loadSummary();
  // The group column is an extra: its many requests start once the list is in, not beside it.
  await loadList();
  if (!membershipsStarted) void loadMemberships();
}
async function loadMore() {
  const current = generation;
  loadingMore.value = true;
  try {
    const result = await adminApi.users({ q: typed(), offset: users.value.length });
    if (disposed || current !== generation) return;
    users.value = [...users.value, ...result];
    more.value = result.length >= adminPageLimit;
    // P7: the button goes with the last page; the keyboard moves on to the first row it brought, never to <body>.
    if (result[0]) { await nextTick(); document.querySelector<HTMLElement>(`[data-open-account="${CSS.escape(result[0].id)}"]`)?.focus(); }
  } catch (failure) {
    if (!disposed) error.value = errorMessage(failure);
  } finally {
    loadingMore.value = false;
  }
}
let searchTimer: ReturnType<typeof setTimeout> | undefined;
onMounted(load);
onBeforeUnmount(() => {
  disposed = true;
  clearTimeout(searchTimer);
});

const mode = computed(() => (!props.sub ? "list" : props.sub === "new" ? "new" : "detail"));
// The card of an account beyond the loaded rows (a link, a typed address) is fetched by its id.
const found = ref<DirectoryUser>();
const finding = ref(false);
async function findOne() {
  const id = props.sub;
  if (mode.value !== "detail" || !id || users.value.some((user) => user.id === id) || found.value?.id === id) return;
  finding.value = true;
  try {
    found.value = (await adminApi.users({ id }))[0];
  } finally {
    finding.value = false;
  }
}
watch(() => props.sub, () => void findOne().catch((failure) => (error.value = errorMessage(failure))));
const account = computed(() => (mode.value === "detail" ? users.value.find((user) => user.id === props.sub) ?? (found.value?.id === props.sub ? found.value : undefined) : undefined));
const listHref = computed(() => router.href("people/accounts"));
const title = computed(() => (mode.value === "new" ? t.accounts.inviteTitle : mode.value === "detail" ? t.accounts.personTitle : t.accounts.peopleTitle));
const description = computed(() =>
  mode.value === "new"
    ? t.accounts.inviteDescription
    : t.accounts.detailDescription,
);
const waiting = (user: DirectoryUser) => requests?.pending.value.filter((request) => request.requesterId === user.id).length ?? 0;

// Filters live in the query, so a filtered list can be linked and survives Back.
type StatusFilter = "" | AccountStatus | "admin";
const search = computed({
  get: () => router.route.value.query.hledat ?? "",
  set: (value: string) => router.setQuery({ ...router.route.value.query, hledat: value || undefined }),
});
const status = computed({
  get: () => (router.route.value.query.stav ?? "") as StatusFilter,
  set: (value: StatusFilter) => router.setQuery({ ...router.route.value.query, stav: value || undefined }),
});
// The typed search is the server's (name or username, sent when the typing pauses); the status filter narrows the rows it returned.
watch(search, () => {
  clearTimeout(searchTimer);
  if (mode.value === "list" && loaded.value) searchTimer = setTimeout(() => void loadList(), 250);
});
const sorted = computed(() => [...users.value].sort((a, b) => a.displayName.localeCompare(b.displayName, locale)));
const matchesStatus = (user: DirectoryUser, filter: StatusFilter) => !filter || (filter === "admin" ? user.isWikiAdmin : accountStatus(user) === filter);
// While the server's answer is on its way the loaded rows narrow at once (P17), by the server's rule: every word in the name or
// the username, case and diacritics aside. Its rows always match, so the filter stays harmless after.
const shown = computed(() => {
  const words = foldText(search.value).split(/\s+/).filter(Boolean);
  return sorted.value.filter((user) => matchesStatus(user, status.value)
    && words.every((word) => foldText(user.displayName).includes(word) || foldText(user.username).includes(word)));
});
const statusOptions = computed<ChoiceOption[]>(() => {
  // The tallies are the server's count of the whole directory, not of the loaded rows.
  const counted = summary.value;
  const tally = (filter: StatusFilter) => counted && String({ "": counted.total, active: counted.total - counted.pending - counted.deactivated, pending: counted.pending, deactivated: counted.deactivated, admin: counted.admins }[filter]);
  return [
    { value: "", label: t.accounts.everyone, icon: "users", badge: tally("") },
    ...(Object.keys(accountStatusLabels) as AccountStatus[]).map((key) => ({ value: key, label: accountStatusLabels[key].label, icon: accountStatusLabels[key].icon, tone: accountStatusLabels[key].tone, badge: tally(key) })),
    { value: "admin", label: t.accounts.admins, icon: "shield-check", badge: tally("admin") },
  ];
});

// Invite wizard.
const takenUsernames = computed(() => users.value.map((user) => user.username));
const submitInvite = (draft: InviteDraft, proof?: string | null) => adminApi.createUser(draft, proof);
async function invited(invitation: Invitation) {
  if (guard) guard.dirty.value = false;
  router.navigate("people/accounts");
  const name = invitation.user?.displayName ?? t.accounts.newAccount;
  flash.notify(t.accounts.invited(name), { icon: "user-check", highlight: invitation.user?.id });
  // The new row rises in highlighted once the directory is read again.
  await load();
}
function cancelInvite() {
  if (guard) guard.dirty.value = false;
  router.navigate("people/accounts");
}
function setDirty(value: boolean) {
  if (guard) guard.dirty.value = value;
}

// Refresh 2.5 (board 09): people as an aligned table with their groups. Memberships come from the groups' member lists,
// read after the table is on screen; a large directory (many groups) leaves the column out instead of flooding the server.
const groups = ref<Group[]>([]);
const memberships = ref<Record<string, Group[]>>({});
const membershipsLoaded = ref(false);
const maxGroupsForColumn = 60;
let membershipsStarted = false;
// shortcut: reads each group's first page only (adminPageLimit members), so people beyond it show no group here and the group
// filter counts cover the loaded rows; the group's own card lists and searches all of them. Upgrade when the column must be exact.
async function loadMemberships() {
  membershipsStarted = true;
  try {
    const list = await adminApi.groups();
    if (disposed || list.length > maxGroupsForColumn) return;
    groups.value = list;
    const map: Record<string, Group[]> = {};
    const queue = [...list];
    const worker = async () => {
      for (let group = queue.shift(); group; group = queue.shift()) {
        const members = await adminApi.members(group.id);
        for (const member of members) (map[member.id] ??= []).push(group);
      }
    };
    await Promise.all(Array.from({ length: Math.min(6, queue.length) }, worker));
    if (disposed) return;
    for (const list of Object.values(map)) list.sort((a, b) => a.name.localeCompare(b.name, locale));
    memberships.value = map;
    membershipsLoaded.value = true;
  } catch {
    // The column is an extra; the people stay listed without it.
  }
}
const groupFilter = computed({
  get: () => router.route.value.query.skupina ?? "",
  set: (value: string) => router.setQuery({ ...router.route.value.query, skupina: value || undefined }),
});
const groupOptions = computed<ChoiceOption[]>(() => [
  { value: "", label: t.accounts.anyGroup, icon: "users" },
  { value: "none", label: t.accounts.noGroup, icon: "account", badge: String(users.value.filter((user) => !memberships.value[user.id]?.length).length) },
  ...[...groups.value].sort((a, b) => a.name.localeCompare(b.name, locale)).map((group) => ({ value: group.id, label: group.name, icon: "users", badge: String(users.value.filter((user) => memberships.value[user.id]?.some((item) => item.id === group.id)).length) })),
]);
const listed = computed(() => {
  const filter = groupFilter.value;
  if (!filter || !membershipsLoaded.value) return shown.value;
  return shown.value.filter((user) => (filter === "none" ? !memberships.value[user.id]?.length : memberships.value[user.id]?.some((group) => group.id === filter)));
});
// One set per line: the directory's waiting accounts over the whole list, a narrowed list's own beside its own count.
const narrowed = computed(() => !!search.value.trim() || !!status.value || !!groupFilter.value);
const pendingCount = computed(() => (narrowed.value ? listed.value.filter((user) => accountStatus(user) === "pending").length : summary.value?.pending ?? 0));
// The person in the shared dock (?osoba=): a click on a name opens the summary there; the name stays a link to the full
// card for a new tab and the keyboard. The address is the state: closing the dock clears it, a deep link opens it.
const panel = useWorkspacePanel();
const peekId = computed(() => router.route.value.query.osoba ?? "");
const peeked = computed(() => (peekId.value ? users.value.find((user) => user.id === peekId.value) : undefined));
const peekShown = () => panel.open.value && panel.content.value === PersonPeek;
watch(peeked, (user) => {
  if (user) panel.show({ label: user.displayName, content: PersonPeek, props: { account: user, currentUserId: context.userId, spaces: context.spaces }, focus: false });
  else if (peekShown()) panel.hide();
}, { immediate: true });
watch(() => panel.open.value, (open) => { if (!open && peekId.value && panel.content.value === PersonPeek) closePeek(); });
onBeforeUnmount(() => { if (peekShown()) panel.hide(); });
function peek(user: DirectoryUser, event: MouseEvent) {
  if (!isPlainNavigationClick(event)) return;
  event.preventDefault();
  router.setQuery({ ...router.route.value.query, osoba: peekId.value === user.id ? undefined : user.id });
}
function closePeek() {
  router.setQuery({ ...router.route.value.query, osoba: undefined });
}

// A filter can bring back or take away dozens of rows at once. Vue's own leave classes force a layout of the table for
// every row (200 ms for 44 rows, 3 s at a laptop's 4x slower CPU), so each row gets one Web Animation and the tokens are
// read once per batch. A row a flow has just created ([data-new]) keeps its own rise and glow (settings.css).
let batch: { rise: number; fade: number; easing: string } | undefined;
function tokens(element: Element) {
  if (!batch) {
    batch = { rise: motionMs(element, "--atlas-motion-slow"), fade: motionMs(element, "--atlas-motion-fast"), easing: getComputedStyle(element).getPropertyValue("--atlas-ease-out").trim() || "ease-out" };
    queueMicrotask(() => (batch = undefined));
  }
  return batch;
}
function whenDone(animation: Animation | null, done: () => void) {
  if (!animation) { done(); return; }
  animation.addEventListener("finish", done);
  animation.addEventListener("cancel", done);
}
function rowEnter(element: Element, done: () => void) {
  if (element.hasAttribute("data-new")) { done(); return; }
  const { rise, easing } = tokens(element);
  whenDone(animateAtlas(element, [{ opacity: 0, transform: "translateY(var(--atlas-rise))" }, { opacity: 1, transform: "none" }], { duration: rise, easing, fill: "backwards" }), done);
}
// The rows that stay glide to their new places (rowGlide.ts): the places are read just before the rows change (a filter brings rows
// in, so the others make room) and again just before the leaving rows are taken out of the table, when their fade has ended, and
// a glide that is still running is continued from where it has got to. The browser would not do it: the leaving rows keep their
// room until the fade ends. A leaving row is out of reach of the keyboard and of assistive technology for its fade.
const glide = createRowGlide();
const rowsGroup = ref<{ $el: HTMLElement }>();
const bodyRows = () => rowsGroup.value?.$el?.children ?? [];
let rowsBefore: ReturnType<typeof glide.tops> | undefined;
watch(listed, () => { rowsBefore = glide.tops(bodyRows()); }, { flush: "pre" });
watch(listed, () => {
  const before = rowsBefore;
  rowsBefore = undefined;
  if (before) glide.glide(bodyRows(), before);
}, { flush: "post" });
const leaveWave = createLeaveWave(glide);
function rowLeave(element: Element, done: () => void) {
  const { fade, easing } = tokens(element);
  const finish = leaveWave(element.parentElement);
  outOfReach(element);
  whenDone(animateAtlas(element, [{ opacity: 1 }, { opacity: 0 }], { duration: fade, easing, fill: "forwards" }), () => finish(done));
}

function replace(saved: DirectoryUser) {
  users.value = users.value.map((user) => (user.id === saved.id ? saved : user));
  if (found.value?.id === saved.id) found.value = saved;
  void loadSummary();
}
</script>

<template>
  <PersonCard v-if="mode === 'detail' && context.isWikiAdmin && loaded && account" :key="account.id" :account="account" :part="part" :current-user-id="context.userId" :reload="load" @saved="replace" />
  <SettingsPage v-else :title="title" :description="mode === 'list' ? t.accounts.listDescription : description" :back="mode === 'list' ? undefined : { href: listHref, label: t.accounts.allPeople }">
    <template v-if="mode === 'list' && context.isWikiAdmin" #actions>
      <Button as-child><a :href="router.href('people/accounts/new')" data-invite><AtlasIcon name="plus" :size="16" />{{ t.accounts.inviteTitle }}</a></Button>
    </template>

    <EmptyState v-if="!context.isWikiAdmin" icon="lock" :title="t.common.wikiAdminsOnly" :text="t.accounts.adminsOnlyText" />
    <ErrorState v-else-if="error && !loaded" :title="t.accounts.loadFailed" :text="error" :busy="loading" @retry="load()" />
    <LoadingNote v-else-if="!loaded || finding" :label="t.accounts.loading" />

    <SettingsSection v-else-if="mode === 'new'" :label="t.accounts.inviteTitle">
      <InviteAccountWizard :submit="submitInvite" :taken-usernames="takenUsernames" @done="invited" @cancel="cancelInvite" @dirty="setDirty" />
    </SettingsSection>

    <EmptyState v-else-if="mode === 'detail'" icon="account" :title="t.accounts.notFound" :text="t.accounts.notFoundText">
      <Button as-child variant="outline"><a :href="listHref"><AtlasIcon name="back" :size="16" />{{ t.accounts.allPeople }}</a></Button>
    </EmptyState>

    <template v-else>
      <ErrorState v-if="error" :title="t.accounts.refreshFailed" :text="error" :busy="loading" @retry="load()" />
      <div class="people-layout">
        <div class="people-main">
          <div class="people-filters-line" role="search" :aria-label="t.accounts.filter">
            <div class="people-search"><SearchField v-model="search" :label="t.accounts.searchLabel" :placeholder="t.accounts.searchPlaceholder" data-account-search /></div>
            <FilterMenu v-model="status" :options="statusOptions" :label="t.accounts.status" icon="users" data-account-filter />
            <FilterMenu v-if="membershipsLoaded && groups.length" v-model="groupFilter" :options="groupOptions" :label="t.accounts.group" icon="users" data-account-group-filter />
          </div>
          <p class="people-count" aria-live="polite">{{ t.accounts.count(listed.length, !!more) }}<template v-if="pendingCount"> · {{ t.accounts.awaiting(pendingCount) }}</template></p>
          <div v-if="listed.length" class="people-table-frame">
            <Table class="people-table">
              <TableCaption class="sr-only">{{ t.accounts.tableLabel }}</TableCaption>
              <TableHeader>
                <TableRow>
                  <TableHead scope="col">{{ t.accounts.colPerson }}</TableHead>
                  <TableHead v-if="membershipsLoaded" scope="col" class="people-col-groups">{{ t.accounts.colGroups }}</TableHead>
                  <TableHead scope="col" class="people-col-state">{{ t.accounts.colState }}</TableHead>
                  <TableHead scope="col" class="people-col-source">{{ t.accounts.colSource }}</TableHead>
                </TableRow>
              </TableHeader>
              <TransitionGroup ref="rowsGroup" tag="tbody" name="people-rows" class="[&_tr:last-child]:border-0" :css="false" @enter="rowEnter" @leave="rowLeave">
                <TableRow v-for="user in listed" :key="user.id" :data-account="user.id" :data-new="flash.highlight.value === user.id || undefined" :data-selected="peeked?.id === user.id || undefined">
                  <TableHead scope="row" class="people-person-cell">
                    <span class="people-person">
                    <SubjectAvatar kind="user" :name="user.displayName" :user-id="user.id" size="sm" />
                    <span class="people-name">
                      <span class="people-name-line">
                        <a class="people-link" :href="router.href(`people/accounts/${user.id}`)" :data-open-account="user.id" :aria-label="t.accounts.openFor(user.displayName)" @click="peek(user, $event)">{{ user.displayName }}</a>
                        <StatusBadge v-if="user.isWikiAdmin" tone="accent" icon="shield-check" :label="t.common.wikiAdmin" size="sm" />
                        <StatusBadge v-if="user.id === context.userId" tone="info" icon="account" :label="t.accounts.you" size="sm" />
                      </span>
                      <span class="people-login">{{ user.username }}</span>
                    </span>
                    </span>
                  </TableHead>
                  <TableCell v-if="membershipsLoaded" class="people-col-groups">
                    <span class="people-groups">
                      <a v-for="group in (memberships[user.id] ?? []).slice(0, 2)" :key="group.id" class="people-group" :href="router.href(`people/groups/${group.id}`)">{{ group.name }}</a>
                      <span v-if="(memberships[user.id]?.length ?? 0) > 2" class="people-more">+{{ memberships[user.id]!.length - 2 }}</span>
                      <span v-if="!memberships[user.id]?.length" class="people-more" :aria-label="t.accounts.noGroupLabel">—</span>
                    </span>
                  </TableCell>
                  <TableCell class="people-col-state">
                    <span class="people-state">
                      <AccountStatusBadge v-if="accountStatus(user) !== 'active'" :account="user" />
                      <StatusBadge v-else tone="success" dot :label="t.accounts.active" size="sm" />
                      <StatusBadge v-if="waiting(user)" tone="warning" icon="user-check" :label="t.accounts.requestWaiting(waiting(user))" size="sm" data-account-requests />
                    </span>
                  </TableCell>
                  <TableCell class="people-col-source">{{ accountSourceLabels[user.source].label }}</TableCell>
                </TableRow>
              </TransitionGroup>
            </Table>
          </div>
          <EmptyState v-else-if="users.length || search.trim()" compact icon="search" :title="users.length ? t.accounts.noneMatch(users.length) : t.accounts.nobodyMatches" :text="t.accounts.tryOther" />
          <EmptyState v-else compact icon="account" :title="t.accounts.emptyTitle" :text="t.accounts.emptyText" />
          <div v-if="more" class="st-actions">
            <Button variant="outline" type="button" :disabled="loadingMore" data-more-accounts @click="loadMore"><AtlasIcon :name="loadingMore ? 'loader-circle' : 'plus'" :size="16" :class="{ 'atlas-spinner': loadingMore }" />{{ t.common.loadMore }}</Button>
          </div>
        </div>
      </div>
    </template>
  </SettingsPage>
</template>

<style scoped>
.people-main { display: grid; gap: 12px; min-width: 0; }
.people-filters-line { display: flex; flex-wrap: wrap; align-items: center; gap: 8px; }
.people-search { flex: 1 1 260px; min-width: min(220px, 100%); max-width: 340px; }
.people-count { margin: 0; color: var(--muted); font-size: 13px; font-variant-numeric: tabular-nums; }
.people-table-frame { overflow: hidden; border: 1px solid var(--line); border-radius: var(--st-radius); background: var(--surface); }
.people-table thead th { height: 40px; padding: 0 16px; border-bottom: 1px solid var(--st-line-soft); color: var(--muted); font-size: 12.5px; font-weight: 600; text-align: left; white-space: nowrap; background: var(--st-subtle); }
.people-table tbody tr { transition: background-color var(--atlas-motion-fast) var(--atlas-ease-out); }
/* The table rises in once when it arrives (its data comes after the page); a row that a filter brings back rises, one that
   it takes away fades out and the rest glide to their places (rowEnter, rowLeave, rowGlide.ts; there is no CSS move class: Vue's
   own move would measure the rows a second time and fight the glide). The glow stays for a row a flow has
   just created ([data-new]), not for every row a filter shows again. */
.people-table-frame { animation: st-rise-in var(--atlas-motion-slow) var(--atlas-ease-out) backwards; }
.people-table tbody tr + tr > * { border-top: 1px solid var(--st-line-soft); }
.people-table tbody tr[data-selected] { background: color-mix(in srgb, var(--accent-soft) 32%, var(--surface)); }
@media (hover: hover) and (pointer: fine) {
  .people-table tbody tr:hover { background: color-mix(in srgb, var(--accent-soft) 32%, var(--surface)); }
}
.people-table tbody :is(th, td) { height: 52px; padding: 8px 16px; vertical-align: middle; text-align: left; font-weight: 400; }
.people-table .people-person-cell { white-space: normal; }
.people-person { display: flex; align-items: center; gap: 12px; min-width: 220px; }
.people-person :deep(.subject-avatar) { width: 32px; height: 32px; color: var(--accent); background: var(--accent-soft); }
.people-name { display: grid; min-width: 0; }
.people-name-line { display: flex; flex-wrap: wrap; align-items: center; gap: 4px 8px; min-width: 0; }
.people-link { color: var(--text); font-weight: 600; text-decoration: none; overflow-wrap: anywhere; }
@media (hover: hover) and (pointer: fine) {
  .people-link:hover { color: var(--accent); text-decoration: underline; text-underline-offset: 3px; }
}
.people-link:focus-visible { outline: 2px solid var(--accent); outline-offset: 2px; border-radius: 3px; }
.people-login { color: var(--muted); font-size: 12.5px; overflow-wrap: anywhere; }
.people-groups { display: flex; flex-wrap: wrap; align-items: center; gap: 4px; }
.people-group { display: inline-flex; align-items: center; min-height: 24px; padding: 0 7px; border-radius: 6px; background: color-mix(in srgb, var(--line) 38%, var(--surface)); color: var(--text); font-size: 12.5px; text-decoration: none; white-space: nowrap; }
@media (hover: hover) and (pointer: fine) {
  .people-group:hover { color: var(--accent); }
}
.people-group:focus-visible { outline: 2px solid var(--accent); outline-offset: 2px; }
.people-more { color: var(--muted); font-size: 12px; }
.people-state { display: flex; flex-wrap: wrap; align-items: center; gap: 6px; }
.people-col-source { color: var(--muted); font-size: 13px; white-space: nowrap; }
@container settings-shell (max-width: 720px) {
  .people-col-source, .people-col-groups { display: none; }
  .people-person { min-width: 0; }
  .people-table tbody :is(th, td) { padding-inline: 12px; }
}
</style>
