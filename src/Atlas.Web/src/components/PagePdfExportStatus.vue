<script setup lang="ts">
import t from "../i18n/components-b";
import { computed, onBeforeUnmount, ref, watch } from "vue";
import { api, errorMessage, post } from "../api";
import { formatTime } from "../formatDate";
import { pageTopic, useLiveFeed } from "../live/liveFeed";
import { Button } from "./ui/button";
import { Tooltip, TooltipContent, TooltipTrigger } from "./ui/tooltip";
import StatusBadge from "./settings/StatusBadge.vue";

/**
 * Whether the PDF of the page's saved version is done: the writer finishes the text and goes to the export folder, so the state of the
 * automatic export (PagePdfExportDialog) sits beside the page's own save state. Nothing is shown for a page without an enabled export.
 * The server says "read again" over /hubs/live when a PDF was stored or failed; a new saved version (`version`) is read again after a
 * short pause, because the export becomes pending with the save, not with a message. Unsaved typing is "waiting until you finish".
 */
const props = defineProps<{ pageId: string; version: number; dirty: boolean; settingsOpen: boolean }>();
const emit = defineEmits<{ download: [] }>();
interface ExportState { settings: { enabled: boolean }; status: "paused" | "pending" | "current" | "error"; lastExportedAt: string | null; lastFileName: string | null }
const state = ref<ExportState>();
const retrying = ref(false), retryError = ref("");
let disposed = false, generation = 0, lastRead = 0, timer: ReturnType<typeof setTimeout> | undefined;
async function load() {
  const current = ++generation;
  lastRead = Date.now();
  try {
    const result = await api<ExportState>("/pages/" + encodeURIComponent(props.pageId) + "/pdf-export");
    if (!disposed && current === generation) state.value = result;
  } catch { if (!disposed && current === generation) state.value = undefined; }
}
// The feed reads the state as its subscription takes effect (at once on the page's channel, already joined): no read of its own on
// mounting, which asked twice per page open. Without the feed (refused, offline) it is read once then.
const { refresh, unavailable } = useLiveFeed(pageTopic(props.pageId, "PdfExportChanged"), load);
watch(unavailable, off => { if (off) refresh(); });
onBeforeUnmount(() => { disposed = true; clearTimeout(timer); });
// A new version is read after 1.5 s, unless the server's push has already made the read (a publication bumps the version and pushes at once): one set, not both.
watch(() => props.version, () => { clearTimeout(timer); timer = setTimeout(() => { if (Date.now() - lastRead > 1200) refresh(); }, 1500); });
watch(() => props.settingsOpen, open => { if (!open) refresh(); });
async function retry() {
  retrying.value = true; retryError.value = "";
  try { await post("/pages/" + encodeURIComponent(props.pageId) + "/pdf-export/run"); await load(); }
  catch (failure) { retryError.value = errorMessage(failure); }
  finally { retrying.value = false; }
}
const shown = computed(() => state.value?.settings.enabled && state.value.status !== "paused");
const badge = computed(() => {
  const status = props.dirty ? "pending" : state.value?.status;
  if (status === "current") return { tone: "success" as const, label: t.pdf.status.ready(formatTime(state.value?.lastExportedAt)) };
  if (status === "error") return { tone: "danger" as const, label: t.pdf.status.failed };
  return { tone: "info" as const, label: props.dirty ? t.pdf.status.waitingToFinish : t.pdf.status.preparing };
});
const fileName = computed(() => !props.dirty && state.value?.status === "current" ? state.value.lastFileName : null);
</script>

<template>
  <div v-if="shown" class="pdf-status" data-pdf-status :data-state="props.dirty ? 'pending' : state?.status" role="status" aria-live="polite">
    <StatusBadge :key="badge.label" :tone="badge.tone" :label="badge.label" size="sm" />
    <span v-if="fileName" class="pdf-status-file" :title="fileName">{{ t.pdf.status.file(fileName) }}</span>
    <Button v-if="state?.status === 'error' && !props.dirty" variant="link" size="sm" :aria-busy="retrying" :disabled="retrying" data-pdf-status-retry @click="retry">{{ t.pdf.status.retry }}</Button>
    <span v-if="retryError" class="pdf-status-error" role="alert">{{ retryError }}</span>
    <Tooltip>
      <TooltipTrigger as-child><Button variant="ghost" size="sm" :aria-description="t.pdf.status.downloadHint" data-pdf-status-download @click="emit('download')">{{ t.pdf.status.download }}</Button></TooltipTrigger>
      <TooltipContent>{{ t.pdf.status.downloadHint }}</TooltipContent>
    </Tooltip>
  </div>
</template>

<style scoped>
.pdf-status { display: inline-flex; align-items: center; gap: 6px; min-width: 0; }
.pdf-status-file { color: var(--muted-foreground); font-size: 12px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; max-width: 28ch; }
.pdf-status-error { color: var(--error); font-size: 12px; }
@media (max-width: 640px) { .pdf-status-file { display: none; } }
</style>
