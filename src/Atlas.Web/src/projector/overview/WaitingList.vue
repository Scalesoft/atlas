<script setup lang="ts">
import { computed, shallowRef, watch } from "vue";
import AtlasIcon from "../../components/AtlasIcon.vue";
import LoadingRegion from "../../components/LoadingRegion.vue";
import EmptyState from "../../components/settings/EmptyState.vue";
import ErrorState from "../../components/settings/ErrorState.vue";
import SubjectAvatar from "../../components/settings/SubjectAvatar.vue";
import t from "../../i18n/projector";
import type { ContactData } from "../../contacts/contactData";
import { directionIcons, stateIcons } from "../requirementLook";
import { directionLabel, requirementStateLabel } from "../requirementSummary";
import { overviewApi } from "./overviewApi";
import { waitedFor, waitingGroups } from "./overviewModel";
import { useGuardedLoad } from "./useGuardedLoad";
import { listKeys } from "../../keyboard/navigation";
import { pragueNow, historyStampText } from "@atlas/markdown/core";
import "../../components/settings/settings.css";

/**
 * Na co čekáme (spec §4 GetWaiting): the space's requirements waiting for input, grouped by the party we wait on, in the
 * server's order (the longest waiting first). A party that names a contact of the directory shows its initials and organisation.
 * Each item links to its block on its page. Groups rise in turn (.atlas-stagger); reduced motion only fades them.
 */
const props = withDefaults(defineProps<{
  spaceId: string;
  /** Off where a heading around already names the list (a board's card, BoardLinks): no second title, no repeated id. */
  heading?: boolean;
  /** Where the answers come from: the server and the contact directory, or sample data in the gallery. */
  api?: { waiting: typeof overviewApi.waiting; contacts: (parties: string[], signal: AbortSignal) => Promise<ReadonlyMap<string, ContactData>> };
}>(), { heading: true, api: () => ({ waiting: overviewApi.waiting, contacts: overviewApi.contacts }) });

const contacts = shallowRef(new Map<string, ContactData>());

// One directory lookup for all the parties (the first 20); an unknown party stays without an avatar.
async function matchContacts(parties: string[], signal: AbortSignal) {
  const named = parties.filter(Boolean).slice(0, 20);
  if (!named.length) return;
  try {
    const found = await props.api.contacts(named, signal);
    if (!signal.aborted) contacts.value = new Map(found);
  } catch { /* an unknown party stays without an avatar */ }
}

const { value: list, loading, denied, error, load } = useGuardedLoad(async signal => {
  const answer = await props.api.waiting(props.spaceId, signal);
  void matchContacts(answer.parties.map(group => group.party), signal);
  return answer;
});
watch(() => props.spaceId, () => { list.value = undefined; contacts.value = new Map(); void load(); }, { immediate: true });

const groups = computed(() => list.value ? waitingGroups(list.value, pragueNow().date) : []);
/** Reads again, the list on screen until the answer (a board's derived list, BoardLinks). */
defineExpose({ refresh: load });
</script>

<template>
  <section class="waiting-list settings-ui" data-waiting-list :aria-labelledby="heading ? 'waiting-list-heading' : undefined">
    <h2 v-if="heading" id="waiting-list-heading" class="st-sr-only">{{ t.waiting.title }}</h2>
    <EmptyState v-if="denied" icon="lock" :title="t.overview.noAccess" :text="t.waiting.noAccessText" />
    <ErrorState v-else-if="error" :title="t.waiting.loadFailed" :text="error" :busy="loading" @retry="load" />
    <LoadingRegion v-else :loading="loading" :ready="!!list" :rows="4" :label="t.waiting.loading">
      <EmptyState v-if="!groups.length" icon="hourglass" :title="t.waiting.nothing"
        :text="t.waiting.nothingText" />
      <div v-else class="waiting-groups atlas-stagger" @keydown="listKeys">
        <section v-for="group in groups" :key="group.party" class="waiting-group" data-party :aria-label="group.label">
          <header class="waiting-group-head">
            <SubjectAvatar :kind="contacts.get(group.party) ? 'user' : 'group'" :name="contacts.get(group.party)?.name" :photo="contacts.get(group.party)?.photo" size="sm" />
            <div class="waiting-group-name">
              <h3 :title="group.label">{{ group.label }}</h3>
              <span v-if="contacts.get(group.party)?.organization" class="waiting-muted">{{ contacts.get(group.party)?.organization }}</span>
            </div>
            <span class="waiting-muted waiting-count">{{ t.waiting.items(group.items.length) }}</span>
          </header>
          <ul class="waiting-items">
            <li v-for="item in group.items" :key="item.blockId">
              <a :href="item.href" class="waiting-item">
                <span class="waiting-item-text">
                  <strong :title="item.title">{{ item.title }}</strong>
                  <span class="waiting-muted" :title="item.pageTitle">
                    <AtlasIcon :name="directionIcons[item.direction]" :size="12" />{{ directionLabel(item.direction) }} · {{ item.pageTitle }}
                  </span>
                </span>
                <!-- The list is of what waits for input; only a block in another state says which. -->
                <span v-if="item.state !== 'waiting_for_input'" class="waiting-state">
                  <AtlasIcon :name="stateIcons[item.state]" :size="13" />{{ requirementStateLabel(item.state) }}
                </span>
                <span class="waiting-days" :data-undated="item.days === null || undefined" :title="item.date ? `${item.days !== null && item.days < 0 ? t.waiting.until : t.waiting.since} ${historyStampText({ date: item.date })}` : undefined">{{ waitedFor(item.days) }}</span>
              </a>
            </li>
          </ul>
        </section>
      </div>
    </LoadingRegion>
  </section>
</template>

<style scoped>
.waiting-list { container: waiting-list / inline-size; display: grid; gap: 14px; width: 100%; min-width: 0; }
.waiting-groups { display: grid; gap: 12px; }
.waiting-group { overflow: hidden; border: 1px solid var(--line); border-radius: var(--st-radius); background: var(--surface); }
.waiting-group-head { display: flex; align-items: center; gap: 10px; padding: 10px 14px; background: var(--st-subtle); }
.waiting-group-name { display: grid; min-width: 0; }
.waiting-group-name h3 { margin: 0; overflow: hidden; font-size: 14px; text-overflow: ellipsis; white-space: nowrap; }
.waiting-count { margin-left: auto; white-space: nowrap; }
.waiting-muted { display: inline-flex; align-items: center; gap: 4px; min-width: 0; overflow: hidden; color: var(--muted); font-size: 12px; text-overflow: ellipsis; white-space: nowrap; }
.waiting-items { margin: 0; padding: 0; list-style: none; }
.waiting-item {
  display: grid; grid-template-columns: minmax(0, 1fr) auto auto; align-items: center; gap: 4px 14px; padding: 9px 14px; border-top: 1px solid var(--st-line-soft);
  color: var(--text); text-decoration: none;
  transition: background-color var(--atlas-motion-fast) var(--atlas-ease-out);
}
.waiting-item-text { display: grid; min-width: 0; }
.waiting-item-text strong { overflow: hidden; font-weight: 600; text-overflow: ellipsis; white-space: nowrap; }
.waiting-state { display: inline-flex; align-items: center; gap: 4px; padding: 2px 8px; border-radius: 999px; background: color-mix(in srgb, var(--warning, #b7791f) 16%, transparent); color: var(--warning, #975a16); font-size: 12px; white-space: nowrap; }
.waiting-days { grid-column: 3; min-width: 5.5em; color: var(--warning, #975a16); font-weight: 650; font-variant-numeric: tabular-nums; text-align: right; white-space: nowrap; }
.waiting-days[data-undated] { color: var(--muted); font-weight: 500; }
@media (hover: hover) and (pointer: fine) { .waiting-item:hover { background: var(--hover); } }
.waiting-item:focus-visible { outline: 2px solid var(--accent); outline-offset: -2px; }
@container waiting-list (max-width: 520px) {
  .waiting-item { grid-template-columns: minmax(0, 1fr) auto; }
  .waiting-state { display: none; }
  .waiting-days { grid-column: 2; }
}
</style>
