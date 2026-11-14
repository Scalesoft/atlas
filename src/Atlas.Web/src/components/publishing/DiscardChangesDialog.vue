<script setup lang="ts">
import { ref, shallowRef, watch } from "vue";
import { api, errorMessage, type Appearance, type Page } from "../../api";
import { historyApi, type PageHistory, type PageRevision } from "../../history";
import t from "../../i18n/components-publishing";
import HistoryChanges from "../HistoryChanges.vue";
import LoadingSkeleton from "../LoadingSkeleton.vue";
import ConfirmDialog from "../settings/ConfirmDialog.vue";

/**
 * The confirmation of "Zahodit změny…" (PublishControl's `discard`, the palette): the history's own comparison of the draft against
 * the published revision, so the writer sees what goes, and on confirming the history's restore of the published revision (the server
 * then has nothing left to publish; the discarded state stays in the history). A private composition of ConfirmDialog and
 * HistoryChanges for the page shell, loaded ahead of its first opening; the shell keeps only the flag.
 *
 * The question and its buttons are there at once and wait for nothing: the comparison fills in below them (the published side is
 * asked for immediately, the draft's once its last keystrokes are saved), and confirming before it arrives discards the same thing.
 */
const props = defineProps<{
  open: boolean;
  /** The shell's, so its loading placeholder hands over to this dialog without a second entrance. */
  title: string;
  pageId: string;
  publishedRevisionId: string;
  /** The page's version as the shell knows it. */
  version: number;
  dark: boolean;
  accent: Appearance["accent"];
  /** Saves the last keystrokes: they belong to what is shown and discarded. */
  prepare: () => Promise<unknown>;
  /** The shell's guard of a history restore; `false` = do not restore. */
  beforeRestore: () => Promise<boolean>;
}>();
const emit = defineEmits<{ close: []; restored: [page: Page, message: string] }>();

const busy = ref(false);
const error = ref("");
const compare = shallowRef<{ draft: PageRevision; published: PageRevision; version: number }>();
let run = 0;
watch(() => props.open, async open => {
  if (!open) return;
  const current = ++run, pageId = props.pageId, publishedId = props.publishedRevisionId;
  compare.value = undefined;
  error.value = "";
  try {
    const signal = new AbortController().signal;
    const published = historyApi.revision(pageId, publishedId, signal);
    published.catch(() => undefined);
    await props.prepare();
    // The newest version alone: its revision is the draft, its number what the restore expects.
    const history = await api<PageHistory>(`/pages/${encodeURIComponent(pageId)}/history?limit=1`, { signal });
    const draft = await historyApi.revision(pageId, history.items[0]!.id, signal);
    if (current === run && props.open) compare.value = { draft, published: await published, version: history.currentVersion };
  } catch (failure) { if (current === run) error.value = errorMessage(failure); }
}, { immediate: true });

async function discard() {
  if (busy.value) return;
  busy.value = true;
  error.value = "";
  try {
    if (!await props.beforeRestore()) return;
    emit("restored", await historyApi.restore(props.pageId, props.publishedRevisionId, Math.max(compare.value?.version ?? 0, props.version)), t.discard.done);
  } catch (failure) { error.value = errorMessage(failure); }
  finally { busy.value = false; }
}
</script>

<template>
  <ConfirmDialog :open="open" size="wide" :title="title" :confirm-label="t.discard.action" confirm-icon="history" :busy="busy" :error="error"
    @confirm="discard" @cancel="emit('close')" @closed="compare = undefined">
    <p>{{ t.discard.text }}</p>
    <div class="discard-changes">
      <HistoryChanges v-if="compare" :revision="compare.draft" :previous="compare.published" :label="t.discard.label" :dark="dark" :accent="accent" />
      <LoadingSkeleton v-else-if="!error" :rows="3" instant />
    </div>
  </ConfirmDialog>
</template>

<style scoped>
/* The comparison keeps its place while it loads and scrolls inside the dialog. */
.discard-changes { min-height: 132px; max-height: min(50vh, 520px); overflow: auto; }
</style>
