<script lang="ts">
import t from "../../../i18n/boards";
import { shallowReactive } from "vue";
import type { YouTrackIssueSnapshot as Issue } from "../../../boards/boardBlockNodes";
/** The issues last added from this picker, per space, newest first: the rows it opens with (P17).
 * shortcut: kept for the browser tab only; persist them if people miss them after a reload. */
const recent = shallowReactive(new Map<string, Issue[]>());
</script>

<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch, watchEffect } from "vue";
import type { BoardBlockNode } from "../../../boards/boardApi";
import { youtrackNode, type YouTrackIssueSnapshot } from "../../../boards/boardBlockNodes";
import { useYouTrackSearch, type YouTrackIssueFetcher } from "../../../composables/useYouTrackSearch";
import ErrorState from "../../settings/ErrorState.vue";
import LoadingNote from "../../settings/LoadingNote.vue";
import type { ChoiceOption } from "../../settings/types";
import BoardPickList from "./BoardPickList.vue";
import BoardPickerPanel from "./BoardPickerPanel.vue";
import { maxPicked, useMultiPick } from "./useMultiPick";

/**
 * Which YouTrack issues new blocks show: the issue search of the integration (`useYouTrackSearch`, the one of the omnisearch), over
 * the projects allowed for the board's space, in a multi-pick list, at most 100 at once. It emits one `youtrackIssue` node of the
 * editor per issue, with the snapshot the search returned (id, key, title, url, state), exactly as the editor's YouTrack plugin
 * stores an issue in a page; the board has no task of its own. Without YouTrack (module off, no connection, no project for the
 * space) the search fails as one: the panel says so and offers a retry instead of an empty list. `fetchIssues` replaces the call
 * to the server (the gallery). Before anything is typed it lists the issues last added in the board's space (at most 8). Private to the board.
 */
const props = defineProps<{ open: boolean; spaceId: string; spaceName?: string; fetchIssues?: YouTrackIssueFetcher; anchor?: HTMLElement | null }>();
const emit = defineEmits<{ pick: [blocks: BoardBlockNode[]]; cancel: [] }>();

const minLength = 2;
const query = ref("");
const { issues, pending, failed, attempted, search, stop } = useYouTrackSearch(props.fetchIssues);
const basket = useMultiPick(maxPicked);
/** Every issue the search showed while the picker is open, by its key: a pick outlives the next search. */
const known = new Map<string, YouTrackIssueSnapshot>();
let timer: ReturnType<typeof setTimeout> | undefined;
/** The typed text waits for its debounce: the shown issues are still the previous text's. */
const waiting = ref(false);

const searchSpaces = () => [{ id: props.spaceId, name: props.spaceName ?? "" }];
function run() {
  const text = query.value.trim();
  clearTimeout(timer);
  waiting.value = text.length >= minLength;
  if (!waiting.value) { stop(); return; }
  timer = setTimeout(() => { waiting.value = false; search(text, searchSpaces()); }, 250);
}
watch(query, run);
watch(() => props.open, open => {
  if (open) return;
  clearTimeout(timer);
  waiting.value = false;
  stop();
  query.value = "";
  basket.clear();
  known.clear();
});
onBeforeUnmount(() => clearTimeout(timer));

watchEffect(() => { for (const issue of issues.value) known.set(issue.key, issue); });
const recentHere = () => recent.get(props.spaceId) ?? [];
const options = computed<ChoiceOption[]>(() => (query.value.trim() ? issues.value : recentHere()).map(issue => ({
  value: issue.key, label: issue.title, description: [issue.key, issue.state].filter(Boolean).join(" · "), icon: "ticket",
})));
const unavailable = computed(() => failed.value > 0 && !pending.value && !issues.value.length);
const emptyText = computed(() => (attempted.value ? t.issuePicker.emptyTried : t.issuePicker.emptyStart));
const confirmLabel = computed(() => (basket.picked.value.length ? t.issuePicker.addCount(basket.picked.value.length) : t.issuePicker.add));
const retry = () => { stop(); run(); };
function confirm() {
  const picked = basket.picked.value.flatMap(option => known.get(option.value) ?? recentHere().find(issue => issue.key === option.value) ?? []);
  recent.set(props.spaceId, [...picked, ...recentHere().filter(issue => !picked.some(one => one.key === issue.key))].slice(0, 8));
  emit("pick", picked.flatMap(issue => youtrackNode(issue) ?? []));
}
</script>

<template>
  <BoardPickerPanel :open="open" :anchor="anchor" :title="t.issuePicker.title" @close="emit('cancel')">
    <div class="board-issue-picker" data-board-issue-picker>
      <ErrorState v-if="unavailable" :title="t.issuePicker.unavailable" :text="t.issuePicker.unavailableText" :busy="pending > 0" @retry="retry" />
      <template v-else>
        <BoardPickList v-model:query="query" :options="options" :picked="basket.picked.value" :loading="waiting || pending > 0" :label="t.issuePicker.label" :placeholder="t.issuePicker.placeholder" :empty-text="emptyText" @toggle="basket.toggle" @confirm="confirm" />
        <LoadingNote :active="pending > 0" :label="t.issuePicker.searching" />
      </template>
    </div>
    <template #footer>
      <button type="button" class="md-btn" @click="emit('cancel')">{{ t.issuePicker.cancel }}</button>
      <button type="button" class="md-btn-primary" :disabled="!basket.picked.value.length" data-board-issue-confirm @click="confirm">{{ confirmLabel }}</button>
    </template>
  </BoardPickerPanel>
</template>

<style scoped>
.board-issue-picker { display: grid; gap: 10px; }
</style>
