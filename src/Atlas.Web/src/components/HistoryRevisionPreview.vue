<script setup lang="ts">
import type { Appearance } from "../api";
import { computed, onErrorCaptured, ref, watch } from "vue";
import { MarkdownRenderer } from "@atlas/markdown/vue";
import "@atlas/markdown/styles/base.css";
import "@atlas/markdown/themes/ocean.css";
import "@atlas/markdown/themes/forest.css";
import "@atlas/markdown/themes/midnight.css";
import { readDocument } from "../document";
import { atlasContentPlugins } from "../documentWidgets";
import { createOverviewReading } from "../overview/overviewReading";
import { useHistoryFormat } from "../historyFormat";
import HistoryFormatSwitch from "./HistoryFormatSwitch.vue";
import t from "../i18n/components-history";

// With `markdown` the version can also be read as its stored Markdown (the shared Náhled | Markdown choice).
// `pageId`: the page's level of its overviews, as its reader sees them.
const props = defineProps<{ contentJson: string; dark: boolean; accent: Appearance["accent"]; markdown?: string; pageId?: string }>();
const { format } = useHistoryFormat();
const showSource = computed(() => props.markdown !== undefined && format.value === "source");
const failure = ref("");
const value = computed(() => {
  try { return readDocument(props.contentJson); }
  catch { return null; }
});
// Only the plugin's static schema/renderer is used here. There is no editor,
// collaboration session or insertion command in a historical document.
const plugins = [...atlasContentPlugins({ kind: "history" }), createOverviewReading(() => props.pageId)];
watch(() => props.contentJson, () => { failure.value = ""; });
onErrorCaptured(() => { failure.value = t.preview.failedRender; return false; });
</script>
<template>
  <div v-if="markdown !== undefined" class="history-version-toolbar"><HistoryFormatSwitch /></div>
  <pre v-if="showSource" class="history-source" :aria-label="t.preview.sourceLabel">{{ markdown }}</pre>
  <p v-else-if="!value || failure" role="alert" class="notice error">{{ failure || t.preview.unsupported }}</p>
  <!-- @vue-generic {'rich'} -->
  <MarkdownRenderer v-else :value="value" profile="rich" :ariaLabel="t.preview.previewLabel" :plugins="plugins"
    :class="dark ? 'md-theme-midnight' : accent === 'green' ? 'md-theme-forest' : ''" />
</template>

<style scoped>
/* The renderer is a .md-editor surface (ocean.css's default --font-sans falls back to
   Inter). DocumentEditor.vue overrides this token on its own editor instance; this
   read-only preview needs the same override so history/diff content renders in the
   host's DM Sans instead of the editor package's bundled default.
   No :deep() here: <MarkdownRenderer> is used directly in this template, so Vue
   stamps this component's own scope attribute onto its root .md-renderer element
   (the same "style a single-root child's outer element from the parent" fallthrough
   DocumentEditor.vue relies on) — a `:deep(.md-renderer)` selector demands a
   *separate* scoped ancestor and silently never matches. */
.md-renderer {
  --font-sans: "DM Sans", system-ui, sans-serif;
}
.history-version-toolbar { position: sticky; top: 0; z-index: 1; display: flex; align-items: center; gap: 12px; min-height: 42px; margin-bottom: 12px; border-bottom: 1px solid var(--line); background: var(--surface); color: var(--muted); font-size: 12px; }
.history-source { margin: 0; padding: 4px 0 36px; white-space: pre-wrap; overflow-wrap: anywhere; font: 13px/1.7 ui-monospace, "Cascadia Code", Consolas, monospace; color: var(--text); animation: st-fade-in var(--atlas-motion-base) var(--atlas-ease-out) both; }
</style>

