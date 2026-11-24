<script setup lang="ts">
import { computed, nextTick, ref } from "vue";
import { landFocus } from "../keyboard/navigation";
import { releaseNotes, unseenReleases, type ReleaseNote } from "../whatsNew/releaseNotes";
import { releaseLabel } from "../whatsNew/notice";
import AppDialog from "./AppDialog.vue";
import ReleaseChangelog from "./release/ReleaseChangelog.vue";
import ReleaseSplash from "./release/ReleaseSplash.vue";

// „Co je nového“ (`#/whats-new`). Opened after an update (`since`: the last release this account had seen), it first welcomes with
// ReleaseSplash when a named release is among the unseen ones; otherwise, and from the splash's „Celý changelog“, it is the full
// ReleaseChangelog, where „Přehrát úvod“ (or the codename) of a named release opens its splash in a dialog over the changelog, played
// from the start as the first time; closing it leaves the changelog where it was. An update by patches only of the named release the
// account knows welcomes as a small update of it. It owns no state beyond that choice. `current`: the release this server runs;
// `close`: back to Atlas.
const props = withDefaults(defineProps<{ current?: string; since?: string; dark?: boolean; accent?: string; notes?: readonly ReleaseNote[] }>(),
  { current: undefined, since: undefined, accent: undefined, notes: () => releaseNotes });
const emit = defineEmits<{ close: [] }>();
const { unseen, featured, patched, others } = unseenReleases(props.notes, props.since, props.current);
/** A named release replayed from the changelog, in the dialog. */
const replay = ref<ReleaseNote>();
const replayTitle = computed(() => replay.value ? `Atlas ${releaseLabel(replay.value.version)} ${replay.value.codename ?? ""}`.trim() : "");
/** The splash shown in the page: the welcome after an update. */
const splash = ref<{ note: ReleaseNote; since?: string; others: readonly ReleaseNote[]; patches?: readonly ReleaseNote[] }>();
if (featured?.mark && props.since) splash.value = { note: featured, since: props.since, others };
else if (patched?.mark && props.since) splash.value = { note: patched, since: props.since, others: [], patches: unseen };
const root = ref<HTMLElement>();
async function show(next: typeof splash.value) {
  splash.value = next;
  await nextTick();
  root.value?.scrollIntoView?.({ block: "start" });
  landFocus(root.value);
}
</script>

<template>
  <div ref="root" class="whats-new" :class="{ 'is-splash': splash }" data-whats-new>
    <ReleaseSplash v-if="splash" :key="splash.note.version" :note="splash.note" :since="splash.since" :others="splash.others" :patches="splash.patches"
      @close="emit('close')" @changelog="show(undefined)" />
    <ReleaseChangelog v-else :notes="notes" :current="current" :dark="dark" :accent="accent" @splash="replay = $event" />
    <AppDialog class="whats-new-replay" size="wide" :title="replayTitle" :open="!!replay" @close="replay = undefined">
      <ReleaseSplash v-if="replay" :key="replay.version" :note="replay" :others="[]" @close="replay = undefined" @changelog="replay = undefined" />
    </AppDialog>
  </div>
</template>

<style scoped>
.whats-new.is-splash { max-width: 1040px; margin: 0 auto; padding: 20px var(--atlas-page-gutter, 16px) 40px; }
/* The splash keeps its own page width in the dialog; the dialog's heading would repeat its title. */
:global(.atlas .app-dialog.whats-new-replay[data-size="wide"]) { width: min(1040px, calc(100vw - 32px)); }
:global(.whats-new-replay > .app-dialog-header h2) { position: absolute; width: 1px; height: 1px; overflow: hidden; clip-path: inset(50%); }
</style>
