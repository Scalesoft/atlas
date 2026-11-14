<script setup lang="ts">
import { locale } from "../../../i18n";
import t from "../../../i18n/settings-people";
import { computed, onBeforeUnmount, ref, watch } from "vue";
import AtlasIcon from "../../AtlasIcon.vue";
import { Button } from "../../ui/button";
import { disclosureHooks } from "../disclosure";
import { accessApi } from "../../../accessApi";
import { errorMessage } from "../../../api";
import type { DirectoryUser } from "../../../administration";
import { formatDateTime } from "../../../formatDate";
import AccessLevelBadge from "../AccessLevelBadge.vue";
import LoadingNote from "../LoadingNote.vue";
import StatusBadge from "../StatusBadge.vue";
import SubjectAvatar from "../SubjectAvatar.vue";
import { allLevelOptions } from "../access/accessKinds";
import type { PrincipalAccess } from "../access/accessModel";
import { useOptionalSettingsRoute } from "../useSettingsRoute";
import AccountStatusBadge from "./AccountStatusBadge.vue";
import { accountSourceLabels } from "./accountStatus";

/**
 * The person beside the list of people (refresh 2.5, board 09): who it is, the state, the groups and what spaces the
 * person reaches and how, with the way into the full card (Přístupy, Účet, Skupiny, Tokeny, Činnost) and to Přidat
 * přístup. Read only: every change happens on the card, so there is one place for each action. Private to AccountsPage;
 * the gallery passes `preview` (local data) instead of letting it read the person's access.
 */
const props = defineProps<{ account: DirectoryUser; currentUserId: string; spaces?: { id: string; color: string }[]; preview?: PrincipalAccess }>();
const emit = defineEmits<{ close: [] }>();
const router = useOptionalSettingsRoute();
const href = (path: string) => (router ? router.href(path) : `#/settings/${path}`);
const access = ref<PrincipalAccess>();
const error = ref("");
let request = 0;
async function load() {
  const current = ++request;
  access.value = props.preview;
  error.value = "";
  if (props.preview) return;
  try {
    const next = await accessApi.principal("user", props.account.id);
    if (current === request) access.value = next;
  } catch (failure) {
    if (current === request) error.value = errorMessage(failure);
  }
}
watch(() => props.account.id, load, { immediate: true });
onBeforeUnmount(() => request++);

const path = computed(() => `people/accounts/${props.account.id}`);
const groups = computed(() => [...(access.value?.principal.groups ?? [])].sort((a, b) => a.name.localeCompare(b.name, locale)));
const spaceEntries = computed(() => (access.value?.entries ?? []).filter((entry) => entry.kind === "space").sort((a, b) => a.title.localeCompare(b.title, locale)));
const otherEntries = computed(() => (access.value?.entries.length ?? 0) - spaceEntries.value.length);
const spaceColor = (id?: string | null) => props.spaces?.find((space) => space.id === id)?.color ?? "blue";
const spaceOptions = allLevelOptions();
// The loading note keeps `min-height: 20px`, which would hold its box open for the whole fold and drop the card by its height at the end.
const loadingHooks = {
  ...disclosureHooks,
  onLeave(element: Element, done: () => void) {
    (element as HTMLElement).style.minHeight = "0";
    disclosureHooks.onLeave(element, done);
  },
};
</script>

<template>
  <aside class="person-peek" :aria-label="t.peek.aria(account.displayName)" data-person-peek>
    <header class="person-peek-head">
      <SubjectAvatar kind="user" :name="account.displayName" />
      <div class="person-peek-who">
        <h3>{{ account.displayName }}</h3>
        <p>{{ account.username }} · {{ accountSourceLabels[account.source].label.toLocaleLowerCase(locale) }}</p>
      </div>
      <button type="button" class="person-peek-close" :aria-label="t.peek.close" data-person-peek-close @click="emit('close')"><AtlasIcon name="close" :size="17" /></button>
    </header>
    <div class="person-peek-badges">
      <AccountStatusBadge :account="account" size="sm" />
      <StatusBadge v-if="account.isWikiAdmin" tone="accent" icon="shield-check" :label="t.common.wikiAdmin" size="sm" />
      <StatusBadge v-if="account.id === currentUserId" tone="info" icon="account" :label="t.peek.you" size="sm" />
    </div>
    <div class="person-peek-actions">
      <Button size="sm" as-child><a :href="href(path)" data-person-peek-open>{{ t.peek.openCard }}</a></Button>
      <Button size="sm" variant="outline" as-child><a :href="href(`${path}/new`)"><AtlasIcon name="plus" :size="15" />{{ t.common.addAccess }}</a></Button>
      <Button size="sm" variant="ghost" as-child><a :href="href(`${path}/account`)">{{ t.peek.accountAndPassword }}</a></Button>
    </div>

    <!-- What loads last unfolds under the header (height and opacity) instead of making the card grow in one frame. -->
    <Transition :css="false" v-bind="loadingHooks"><LoadingNote v-if="!access && !error" :label="t.common.loadingAccess" /></Transition>
    <p v-if="error" class="st-note" role="alert"><AtlasIcon name="triangle-alert" :size="15" />{{ error }}</p>
    <Transition :css="false" v-bind="disclosureHooks">
      <div v-if="access" class="person-peek-body">
        <p v-if="access.principal.lastSignInAt" class="person-peek-meta">{{ t.peek.lastSignIn(formatDateTime(access.principal.lastSignInAt)) }}</p>
        <section class="person-peek-group" aria-labelledby="person-peek-groups">
          <h4 id="person-peek-groups" class="st-group-label">{{ t.common.groups }}</h4>
          <ul v-if="groups.length" class="person-peek-chips">
            <li v-for="group in groups" :key="group.id"><a :href="href(`people/groups/${group.id}`)">{{ group.name }}</a></li>
          </ul>
          <p v-else class="person-peek-empty">{{ t.peek.noGroups }}</p>
        </section>
        <section class="person-peek-group" aria-labelledby="person-peek-spaces">
          <h4 id="person-peek-spaces" class="st-group-label">{{ t.peek.spaceAccess }}</h4>
          <ul v-if="spaceEntries.length" class="person-peek-spaces">
            <li v-for="entry in spaceEntries" :key="entry.resourceId">
              <span class="person-peek-dot" :data-color="spaceColor(entry.resourceId)" aria-hidden="true" />
              <span class="person-peek-space" :title="entry.title">{{ entry.title }}</span>
              <span class="person-peek-via" :title="entry.sources[0]?.via === 'direct' ? undefined : entry.sources[0]?.label">{{ entry.sources[0]?.via === "direct" ? t.peek.directly : entry.sources[0]?.label }}</span>
              <AccessLevelBadge v-if="entry.level" :options="spaceOptions" :value="entry.level" tone="level" />
              <span v-else class="person-peek-via">{{ t.peek.noAccess }}</span>
            </li>
          </ul>
          <p v-else class="person-peek-empty">{{ t.peek.noSpaces }}</p>
          <a v-if="otherEntries > 0" class="st-link person-peek-more" :href="href(path)">{{ t.peek.more(otherEntries) }}<AtlasIcon name="arrow-right" :size="14" /></a>
        </section>
      </div>
    </Transition>
  </aside>
</template>

<style scoped>
.person-peek > * { flex-shrink: 0; min-width: 0; }
/* A column and not a grid: a note that unfolds under the header (disclosureHooks) takes its gap with it, a grid track cannot. */
.person-peek { display: flex; flex-direction: column; gap: 14px; min-width: 0; padding: 18px; border: 1px solid var(--line); border-radius: var(--st-radius); background: var(--surface); animation: st-rise-in var(--atlas-motion-slow) var(--atlas-ease-out); }
/* The rule above would win over the Transition's own st-fade-leave-active: the card stayed at full opacity until it was removed. */
.person-peek.st-fade-leave-active { animation: st-fade-out var(--atlas-motion-fast) var(--atlas-ease-in) both; pointer-events: none; }
.person-peek-body { display: flex; flex-direction: column; gap: 14px; }
.person-peek-head { display: flex; align-items: center; gap: 12px; }
.person-peek-head :deep(.subject-avatar) { width: 44px; height: 44px; font-size: 14px; color: var(--accent); background: var(--accent-soft); }
.person-peek-who { flex: 1; min-width: 0; }
.person-peek-who h3 { margin: 0; font-size: 17px; font-weight: 650; overflow-wrap: anywhere; }
.person-peek-who p { margin: 1px 0 0; color: var(--muted); font-size: 13px; }
.person-peek-close { display: grid; place-items: center; width: 32px; height: 32px; border: 0; border-radius: 8px; background: transparent; color: var(--muted); cursor: pointer; }
@media (hover: hover) and (pointer: fine) {
  .person-peek-close:hover { background: var(--hover); color: var(--text); }
}
.person-peek-close:focus-visible { outline: 2px solid var(--accent); outline-offset: 2px; }
.person-peek-badges, .person-peek-actions { display: flex; flex-wrap: wrap; gap: 6px; }
.person-peek-meta { margin: 0; color: var(--muted); font-size: 12.5px; }
.person-peek-group { display: grid; grid-template-columns: minmax(0, 1fr); gap: 8px; padding-top: 12px; border-top: 1px solid var(--st-line-soft); }
.person-peek-chips { display: flex; flex-wrap: wrap; gap: 6px; margin: 0; padding: 0; list-style: none; }
.person-peek-chips a { display: inline-flex; align-items: center; min-height: 28px; padding: 0 9px; border: 1px solid var(--line); border-radius: 8px; color: var(--text); font-size: 13px; text-decoration: none; transition: border-color var(--atlas-motion-fast) var(--atlas-ease-out); }
@media (hover: hover) and (pointer: fine) {
  .person-peek-chips a:hover { border-color: var(--accent); color: var(--accent); }
}
.person-peek-chips a:focus-visible { outline: 2px solid var(--accent); outline-offset: 2px; }
.person-peek-spaces { display: grid; grid-template-columns: minmax(0, 1fr); margin: 0; padding: 0; list-style: none; }
.person-peek-spaces > li { display: flex; align-items: center; gap: 8px; min-height: 40px; border-top: 1px solid var(--st-line-soft); }
.person-peek-spaces > li:first-child { border-top: 0; }
.person-peek-dot { flex-shrink: 0; width: 10px; height: 10px; border-radius: 3px; background: var(--appearance-fg, var(--accent)); }
.person-peek-space { flex: 1; min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; font-size: 13.5px; }
.person-peek-via { color: var(--muted); font-size: 12px; white-space: nowrap; max-width: 110px; overflow: hidden; text-overflow: ellipsis; }
.person-peek-empty { margin: 0; color: var(--muted); font-size: 13px; }
.person-peek-more { font-size: 13px; }
</style>
