<script setup lang="ts">
import { nextTick, ref } from "vue";
import { landFocus } from "../keyboard/navigation";
import { releaseNotes, unseenReleases, type ReleaseNote } from "../whatsNew/releaseNotes";
import ReleaseChangelog from "./release/ReleaseChangelog.vue";
import ReleaseSplash from "./release/ReleaseSplash.vue";

// „Co je nového“ (`#/whats-new`). Opened after an update (`since`: the last release this account had seen), it first welcomes with
// ReleaseSplash when a named release is among the unseen ones; otherwise, and from the splash's „Celý changelog“, it is the full
// ReleaseChangelog, where a named release's codename or its tile in the release gallery replays its splash; closing a replayed splash
// goes back to the changelog. An update by patches only of the named release the account knows welcomes as a small update of it. It
// owns no state beyond that choice. `current`: the release this server runs; `close`: back to Atlas.
const props = withDefaults(defineProps<{ current?: string; since?: string; dark?: boolean; accent?: string; notes?: readonly ReleaseNote[] }>(),
  { current: undefined, since: undefined, accent: undefined, notes: () => releaseNotes });
const emit = defineEmits<{ close: [] }>();
const { unseen, featured, patched, others } = unseenReleases(props.notes, props.since, props.current);
/** The splash shown: the welcome after an update, or a release replayed from the changelog. */
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
      @close="splash.since ? emit('close') : show(undefined)" @changelog="show(undefined)" />
    <ReleaseChangelog v-else :notes="notes" :current="current" :dark="dark" :accent="accent" @splash="show({ note: $event, others: [] })" />
  </div>
</template>

<style scoped>
.whats-new.is-splash { max-width: 1040px; margin: 0 auto; padding: 20px var(--atlas-page-gutter, 16px) 40px; }
</style>
