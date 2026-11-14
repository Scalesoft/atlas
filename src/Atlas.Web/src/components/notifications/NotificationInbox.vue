<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, shallowRef, useId, watch } from "vue";
import { errorMessage } from "../../api";
import { formatDateTime } from "../../formatDate";
import { listKeys } from "../../keyboard/navigation";
import { inboxOpened } from "../../notifications";
import t from "../../i18n/components-search";
import { groupByDay, kindOf, notificationApi, notificationLink, preferenceKinds, subjectTitle, type NotificationApi, type NotificationItem, type NotificationPreference } from "./notificationInbox";
import AtlasIcon from "../AtlasIcon.vue";
import LoadingSkeleton from "../LoadingSkeleton.vue";
import EmptyState from "../settings/EmptyState.vue";
import ErrorState from "../settings/ErrorState.vue";
import InlineReveal from "../settings/InlineReveal.vue";
import { Button } from "../ui/button";
import { Switch } from "../ui/switch";
import "../settings/settings.css";

/**
 * Knihovna → Oznámení (inbox spec 2026-10-29): the reader's mentions and shares, newest event first and grouped by day, the
 * unread ones marked. A row opens its subject at its anchor and marks itself read; „Označit vše jako přečtené“ clears the rest.
 * Keyboard-first: focus lands on the first unread row (else the first), ↑/↓/Home/End walk the rows, Enter opens. A new event
 * (`refreshKey`, from the live `user:` channel) reloads the list and the rows that were not there rise in. The preferences (one
 * switch per kind) sit below in „Nastavení oznámení“, beside what they change.
 *
 * Why not PersonalLibraryPanel: its rows are pages with versions and favourites from offset paging; an inbox row is an event with an
 * actor, a count, a read state and a cursor. The look is the same tabbed library list.
 */
const props = withDefaults(defineProps<{ refreshKey?: number; api?: NotificationApi; now?: Date }>(), { refreshKey: 0, api: () => notificationApi, now: undefined });
const emit = defineEmits<{ unread: [count: number] }>();
const headingId = useId();
const items = shallowRef<NotificationItem[]>([]);
const next = ref<string | null>(null);
const unread = ref(0);
const loaded = ref(false), loading = ref(false), error = ref(""), marking = ref(false);
const arrived = ref(new Set<string>());
const settingsOpen = ref(false);
const preferences = ref<NotificationPreference[]>();
const list = ref<HTMLElement>();
const settingsToggle = ref<{ $el: HTMLElement }>();
const groups = computed(() => groupByDay(items.value, props.now));
const landing = computed(() => (items.value.find(item => !item.read) ?? items.value[0])?.id);
let abort: AbortController | undefined, disposed = false;
const closeInbox = inboxOpened();

async function load(append = false) {
  abort?.abort();
  const controller = (abort = new AbortController());
  loading.value = true; error.value = "";
  try {
    const page = await props.api.list(append ? next.value : null, controller.signal);
    if (disposed || controller.signal.aborted) return;
    const known = new Set(items.value.map(item => item.id));
    // Rows that came with a live signal rise in; the first load staggers instead.
    arrived.value = loaded.value && !append ? new Set(page.items.filter(item => !known.has(item.id)).map(item => item.id)) : new Set();
    items.value = append ? [...items.value, ...page.items.filter(item => !known.has(item.id))] : page.items;
    next.value = page.next;
    setUnread(page.unread);
  } catch (reason) {
    if (!controller.signal.aborted) error.value = errorMessage(reason);
  } finally {
    if (!disposed && abort === controller) { loading.value = false; loaded.value = true; }
  }
}
function setUnread(count: number) { unread.value = count; emit("unread", count); }

function open(item: NotificationItem) {
  if (item.read) return;
  items.value = items.value.map(row => (row.id === item.id ? { ...row, read: true } : row));
  setUnread(Math.max(0, unread.value - 1));
  // The link navigates meanwhile; a failure only leaves the item unread for the next load.
  void props.api.markRead(item.id).then(result => setUnread(result.unread), () => {});
}
async function markAll(event: Event) {
  const trigger = event.currentTarget as HTMLButtonElement | null;
  marking.value = true;
  try {
    const result = await props.api.markAllRead();
    items.value = items.value.map(row => ({ ...row, read: true }));
    setUnread(result.unread);
  } catch (reason) {
    error.value = errorMessage(reason);
  } finally { marking.value = false; }
  // The button is disabled now: its focus moves to the first row (or the settings when there is none) instead of falling to the page.
  await nextTick();
  if (trigger?.disabled && (document.activeElement === trigger || document.activeElement === document.body))
    (list.value?.querySelector<HTMLElement>("[data-notification]") ?? settingsToggle.value?.$el)?.focus({ preventScroll: true });
}

async function toggleSettings() {
  settingsOpen.value = !settingsOpen.value;
  if (settingsOpen.value && !preferences.value) {
    try { preferences.value = (await props.api.preferences()).items; } catch (reason) { error.value = errorMessage(reason); }
  }
}
async function setPreference(kind: string, enabled: boolean) {
  const previous = preferences.value;
  preferences.value = previous?.map(item => (item.kind === kind ? { ...item, enabled } : item));
  try { await props.api.setPreference(kind, enabled); } catch (reason) { preferences.value = previous; error.value = errorMessage(reason); }
}

function actorLine(item: NotificationItem) {
  const kind = kindOf(item.kind);
  return item.actorName ? `${item.actorName} ${kind.action}` : kind.title;
}

watch(() => props.refreshKey, () => void load());
onMounted(async () => {
  await load();
  await nextTick();
  // Focus lands at the logical place only when nothing else holds it (navigation's landFocus rule).
  if (!document.activeElement || document.activeElement === document.body) list.value?.querySelector<HTMLElement>("[data-focus-landing]")?.focus({ preventScroll: true });
});
onBeforeUnmount(() => { disposed = true; abort?.abort(); closeInbox(); });
defineExpose({ load });
</script>

<template>
  <section class="notification-inbox settings-ui" :aria-labelledby="headingId" :aria-busy="loading">
    <header class="notification-heading">
      <div>
        <h2 :id="headingId" class="sr-only">{{ t.notifications.heading }}</h2>
        <p class="help" role="status">{{ unread ? t.notifications.unreadStatus(unread > 99 ? "99+" : String(unread)) : t.notifications.concern }}</p>
      </div>
      <Button variant="outline" data-notifications-read-all :disabled="!unread || marking" :aria-busy="marking" @click="markAll"><AtlasIcon name="read-all" :size="16" />{{ t.notifications.markAll }}</Button>
    </header>
    <ErrorState v-if="error && !items.length" :title="t.notifications.loadError" :text="error" :retry-label="t.notifications.reload" :busy="loading" @retry="load()" />
    <LoadingSkeleton v-else-if="loading && !loaded" :rows="5" row-height="60px" :label="t.notifications.loading" />
    <EmptyState v-else-if="loaded && !items.length" icon="notifications" :title="t.notifications.emptyTitle" :text="t.notifications.emptyText" />
    <div v-if="items.length" ref="list" class="notification-groups" @keydown="listKeys">
      <section v-for="group in groups" :key="group.label" class="notification-group" :aria-label="group.label">
        <h3 class="notification-group-label">{{ group.label }}</h3>
        <ul class="notification-items">
          <li v-for="(item, index) in group.items" :key="item.id" :style="{ '--i': arrived.has(item.id) ? 0 : Math.min(index, 8) }" :data-arrived="arrived.has(item.id) || undefined">
            <component :is="notificationLink(item) ? 'a' : 'button'" class="notification-row" :href="notificationLink(item)" :type="notificationLink(item) ? undefined : 'button'"
              :data-unread="!item.read || undefined" :data-notification="item.id" :data-focus-landing="item.id === landing || undefined"
              :aria-label="t.notifications.rowLabel(item.read, actorLine(item), subjectTitle(item), item.count)" @click="open(item)">
              <span class="notification-icon" aria-hidden="true"><AtlasIcon :name="kindOf(item.kind).icon" :size="16" /></span>
              <span class="notification-copy">
                <span class="notification-line"><strong v-if="item.actorName">{{ item.actorName }}</strong> {{ item.actorName ? kindOf(item.kind).action : kindOf(item.kind).title }}</span>
                <span class="notification-subject" :title="subjectTitle(item)" :data-redacted="item.subjectType === null || undefined">{{ subjectTitle(item) }}</span>
                <span v-if="item.excerpt" class="notification-excerpt" :title="item.excerpt" data-notification-excerpt>„{{ item.excerpt }}“</span>
                <span v-if="item.spaceName || item.count > 1" class="notification-meta">{{ item.spaceName }}<template v-if="item.spaceName && item.count > 1"> · </template><template v-if="item.count > 1">{{ item.count }}×</template></span>
              </span>
              <time :datetime="item.updatedAt">{{ formatDateTime(item.updatedAt) }}</time>
              <span class="notification-dot" aria-hidden="true" />
            </component>
          </li>
        </ul>
      </section>
    </div>
    <footer class="notification-footer">
      <p v-if="error && items.length" class="notification-error" role="alert"><AtlasIcon name="triangle-alert" :size="15" />{{ error }}</p>
      <Button v-if="next" variant="outline" :disabled="loading" :aria-busy="loading" @click="load(true)"><AtlasIcon name="refresh" :size="16" />{{ t.notifications.loadOlder }}</Button>
      <Button ref="settingsToggle" variant="ghost" class="notification-settings-toggle" aria-controls="notification-settings" :aria-expanded="settingsOpen" @click="toggleSettings">
        <AtlasIcon name="settings" :size="16" />{{ t.notifications.settings }}
      </Button>
    </footer>
    <InlineReveal id="notification-settings" :open="settingsOpen">
      <ul class="notification-preferences" :aria-label="t.notifications.preferences">
        <li v-for="kind in preferenceKinds" :key="kind">
          <label><Switch :model-value="preferences?.find(item => item.kind === kind)?.enabled ?? true" :disabled="!preferences" :data-preference="kind"
            @update:model-value="setPreference(kind, !!$event)" />{{ kindOf(kind).title }}</label>
        </li>
      </ul>
    </InlineReveal>
  </section>
</template>

<style scoped>
.notification-inbox { width: 100%; min-height: 306px; color: var(--text); }
.notification-heading { display: flex; align-items: center; justify-content: space-between; gap: 20px; margin-bottom: 16px; }
.notification-heading .help { margin: 0; color: var(--muted); font-size: 14px; line-height: 1.45; }
/* minmax(0, 1fr): an auto track grew to the rows' min-content (a nowrap subject), so at 375 px the time and the unread dot ran past the card and the page scrolled sideways (P17). */
.notification-groups { display: grid; grid-template-columns: minmax(0, 1fr); gap: 18px; }
.notification-group-label { margin: 0 0 8px; color: var(--muted); font-size: 12px; font-weight: 600; letter-spacing: .02em; text-transform: uppercase; }
.notification-items { --items-radius: 14px; list-style: none; margin: 0; padding: 0; border: 1px solid var(--line); border-radius: var(--items-radius); background: var(--surface); overflow: hidden; }
.notification-items li { animation: st-rise-in var(--atlas-motion-slow) var(--atlas-ease-out) backwards; animation-delay: calc(var(--i, 0) * var(--st-stagger)); }
.notification-items li + li { border-top: 1px solid color-mix(in srgb, var(--line) 50%, var(--surface)); }
.notification-row { display: flex; align-items: center; gap: 12px; width: 100%; min-height: 60px; padding: 10px 16px; box-sizing: border-box; background: transparent; border: 0;
  color: var(--text); font: inherit; text-align: left; text-decoration: none; cursor: pointer; transition: background-color var(--atlas-motion-fast) var(--atlas-ease-out); }
@media (hover: hover) and (pointer: fine) {
  .notification-row:hover { background: color-mix(in srgb, var(--hover) 45%, transparent); }
}
.notification-row:focus-visible { outline-offset: -2px; }
.notification-items li:first-child .notification-row { border-top-left-radius: calc(var(--items-radius) - 1px); border-top-right-radius: calc(var(--items-radius) - 1px); }
.notification-items li:last-child .notification-row { border-bottom-left-radius: calc(var(--items-radius) - 1px); border-bottom-right-radius: calc(var(--items-radius) - 1px); }
.notification-icon { display: grid; place-items: center; flex: 0 0 30px; height: 30px; border-radius: 9px; color: var(--muted); background: color-mix(in srgb, var(--line) 30%, var(--surface)); }
.notification-row[data-unread] .notification-icon { color: var(--accent); background: var(--accent-soft); }
.notification-copy { display: flex; flex: 1; min-width: 0; flex-direction: column; gap: 2px; }
.notification-line { font-size: 13px; color: var(--muted); }
.notification-line strong { color: var(--text); font-weight: 600; }
.notification-subject { overflow: hidden; font-size: 14px; font-weight: 600; text-overflow: ellipsis; white-space: nowrap; }
.notification-row:not([data-unread]) .notification-subject { font-weight: 500; }
.notification-subject[data-redacted] { color: var(--muted); font-style: italic; }
.notification-meta { font-size: 12.5px; color: var(--muted); }
.notification-excerpt { overflow: hidden; font-size: 13px; color: var(--muted); text-overflow: ellipsis; white-space: nowrap; }
.notification-row time { flex-shrink: 0; color: var(--muted); font-size: 12.5px; font-variant-numeric: tabular-nums; }
/* The unread mark keeps its room when read, so marking a row never moves it. */
.notification-dot { flex: 0 0 8px; height: 8px; border-radius: 50%; background: var(--accent); opacity: 0; transition: opacity var(--atlas-motion-base) var(--atlas-ease-out); }
.notification-row[data-unread] .notification-dot { opacity: 1; }
.notification-footer { display: flex; flex-wrap: wrap; align-items: center; justify-content: space-between; gap: 8px 18px; margin-top: 14px; }
.notification-settings-toggle { margin-left: auto; }
.notification-error { display: flex; flex: 1 1 240px; align-items: center; gap: 8px; margin: 0; color: var(--error); font-size: 13px; }
.notification-preferences { display: grid; gap: 10px; margin: 12px 0 0; padding: 14px 16px; list-style: none; border: 1px solid var(--line); border-radius: 14px; background: var(--surface); }
.notification-preferences label { display: flex; align-items: center; gap: 10px; font-size: 14px; }
@container workspace-content (max-width: 560px) {
  .notification-heading { flex-wrap: wrap; }
  .notification-row { flex-wrap: wrap; padding: 12px; }
  .notification-row time { width: 100%; padding-left: 42px; }
}
</style>
