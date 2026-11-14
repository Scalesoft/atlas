<script setup lang="ts">
import { computed, ref, shallowRef, watch } from "vue";
import { lazyComponent } from "../lazyComponent";
import type { Appearance } from "../api";
import type { PageRevision } from "../history";
import { compareHistoryTextAsync, openApiChanges, type HistoryTextDiff } from "../historyDiff";
import { compareHistoryBlocks } from "../historyBlocks";
import { useHistoryFormat } from "../historyFormat";
import HistoryFormatSwitch from "./HistoryFormatSwitch.vue";
import { scrollBehavior } from "../motion/aiMotion";
import { Button } from "./ui/button";
import LoadingSkeleton from "./LoadingSkeleton.vue";
import t from "../i18n/components-history";

// The renderer loads with the editor package, like the full-version preview.
const HistoryRenderedChanges = lazyComponent(() => import("./HistoryRenderedChanges.vue"));
// `label` replaces the version line for a change that is not a stored revision yet (the assistant's draft proposal).
const props = withDefaults(defineProps<{ revision: PageRevision; previous: PageRevision | null; dark?: boolean; accent?: Appearance["accent"]; label?: string; diff?: HistoryTextDiff }>(), { dark: false, accent: "blue", label: undefined, diff: undefined });
// The history panel passes the diff it already computed in a worker; otherwise it is computed there now and the word counts (and the
// Markdown view) arrive with it. The rendered changes never wait for it.
const ownDiff = shallowRef<HistoryTextDiff>();
watch(() => [props.diff, props.previous?.markdown, props.revision.markdown] as const, async ([given, before, after]) => {
  ownDiff.value = undefined;
  if (given) return;
  const diff = await compareHistoryTextAsync(before ?? "", after);
  if (!props.diff && (props.previous?.markdown ?? "") === (before ?? "") && props.revision.markdown === after) ownDiff.value = diff;
}, { immediate: true });
const difference = computed(() => props.diff ?? ownDiff.value);
const blocks = computed(() => compareHistoryBlocks(props.previous?.contentJson ?? null, props.revision.contentJson));
// Rendered by default: readers compare pages, not Markdown. The Markdown view stays the exact word-level record.
const { format } = useHistoryFormat();
const renderFailed = ref(false);
const view = computed(() => (format.value === "rendered" && blocks.value.readable && !renderFailed.value ? "rendered" : "source"));
const titleChanged = computed(() => props.previous && props.previous.title !== props.revision.title);
const richOnly = computed(() => props.previous && props.previous.markdown === props.revision.markdown && props.previous.contentJson !== props.revision.contentJson);
// The Markdown projection carries only a summary line per OpenAPI block; list source changes explicitly.
const apiChanges = computed(() => props.previous ? openApiChanges(props.previous.contentJson, props.revision.contentJson) : []);
const root = ref<HTMLElement>();
const activeChange = ref(0);
const changeCount = computed(() => (view.value === "rendered" ? blocks.value.changes : difference.value?.parts.filter(p => p.kind !== "same").length ?? 0) + (titleChanged.value ? 1 : 0) + apiChanges.value.length);
watch(() => props.revision.id, () => { activeChange.value = 0; renderFailed.value = false; });
watch(view, () => { activeChange.value = 0; });
function jump(direction: number) {
  const changes = root.value?.querySelectorAll<HTMLElement>("[data-change]");
  if (!changes?.length) return;
  activeChange.value = (activeChange.value + direction + changes.length) % changes.length;
  changes[activeChange.value]?.scrollIntoView({ behavior: scrollBehavior(root.value), block: "center" });
  changes[activeChange.value]?.focus({ preventScroll: true });
}
/** ↑ and ↓ on a change or on the two jump buttons step through the changes, like the buttons. */
function arrows(event: KeyboardEvent) {
  if ((event.key !== "ArrowDown" && event.key !== "ArrowUp") || event.altKey || event.ctrlKey || event.metaKey || event.shiftKey) return;
  if (!(event.target instanceof Element) || !event.target.matches("[data-change], .changes-jump button")) return;
  event.preventDefault();
  jump(event.key === "ArrowDown" ? 1 : -1);
}
/** Scrolls the diff to the heading of a changed section (the rendered view shows it as a heading, the Markdown view as `## text`). */
function scrollToHeading(text: string) {
  const element = root.value;
  if (!element) return false;
  const walker = document.createTreeWalker(element, NodeFilter.SHOW_TEXT);
  const wanted = text.trim();
  for (let node = walker.nextNode(); node; node = walker.nextNode()) {
    const host = node.parentElement;
    if (!host || !node.textContent?.includes(wanted) || host.closest(".changes-toolbar, .title-change")) continue;
    if (!host.closest("h1,h2,h3,h4,h5,h6") && !/^\s*#{1,6}\s/.test(node.textContent.slice(Math.max(0, node.textContent.indexOf(wanted) - 8)))) continue;
    host.scrollIntoView({ behavior: scrollBehavior(element), block: "start" });
    return true;
  }
  return false;
}
defineExpose({ scrollToHeading });
</script>

<template>
  <div ref="root" class="history-changes" @keydown="arrows">
    <div class="changes-toolbar">
      <span>{{ label ?? (previous ? `v${previous.version} → v${revision.version}` : t.changes.firstVersion) }}</span>
      <!-- Words, counted here from the two texts; the version list counts lines and blocks: the unit is named first, so they read apart. -->
      <!-- The number of changed places is at hand at once; the words arrive with the text comparison. -->
      <strong v-if="changeCount" class="changes-places">{{ t.changes.places(changeCount) }}</strong>
      <span v-if="difference" class="changes-legend" :title="t.changes.legendTitle">{{ t.changes.words }} <span class="addition">+{{ difference.added }}</span> <span class="deletion">−{{ difference.removed }}</span></span>
      <HistoryFormatSwitch v-if="blocks.readable" />
      <div v-if="changeCount" class="changes-jump">
        <span>{{ activeChange + 1 }}/{{ changeCount }}</span>
        <Button variant="ghost" size="icon-sm" :aria-label="t.changes.previous" @click="jump(-1)">↑</Button>
        <Button variant="ghost" size="icon-sm" :aria-label="t.changes.next" @click="jump(1)">↓</Button>
      </div>
    </div>
    <p v-if="view === 'source' && difference?.coarse" class="help">{{ t.changes.coarse }}</p>
    <div v-if="titleChanged" class="title-change" data-change tabindex="-1"><small>{{ t.changes.titleChange }}</small><del>{{ previous!.title }}</del><ins>{{ revision.title }}</ins></div>
    <div v-if="apiChanges.length" class="api-changes" :aria-label="t.changes.apiChanges">
      <small>{{ t.changes.apiDefinitions }}</small>
      <ul><li v-for="(change, index) in apiChanges" :key="index" data-change tabindex="-1" :data-kind="change.kind"><strong>{{ change.title }}</strong><span v-if="change.title !== change.filename" class="api-file"> ({{ change.filename }})</span> — {{ change.label }}</li></ul>
    </div>
    <p v-if="view === 'source' && richOnly" class="help" role="status">{{ apiChanges.length ? t.changes.richOnlyApi : t.changes.richOnly }}</p>
    <p v-else-if="previous && !changeCount && (view === 'rendered' || difference)" class="help">{{ t.changes.unchanged }}</p>
    <HistoryRenderedChanges v-if="view === 'rendered'" :diff="blocks" :first="!previous" :dark="dark" :accent="accent" @failed="renderFailed = true" />
    <LoadingSkeleton v-else-if="!difference" :rows="3" instant />
    <div v-else-if="difference.parts.length" class="text-diff" :aria-label="t.changes.textChanges"><template v-for="(part, index) in difference.parts" :key="index"><del v-if="part.kind === 'removed'" data-change tabindex="-1" :aria-label="t.changes.deleted">{{ part.text }}</del><ins v-else-if="part.kind === 'added'" data-change tabindex="-1" :aria-label="t.changes.added">{{ part.text }}</ins><span v-else>{{ part.text }}</span></template></div>
    <p v-else class="help">{{ t.changes.noText }}</p>
  </div>
</template>

<style scoped>
.history-changes { min-width: 0; }
.changes-toolbar { position: sticky; top: 0; z-index: 1; display: flex; flex-wrap: wrap; align-items: center; gap: 12px; min-height: 42px; background: var(--surface); border-bottom: 1px solid var(--line); color: var(--muted); font-size: 12px; }
.changes-places { font-weight: 600; color: var(--text); }
.changes-jump { margin-left: auto; display: flex; align-items: center; gap: 4px; font-variant-numeric: tabular-nums; }
.text-diff { white-space: pre-wrap; overflow-wrap: anywhere; line-height: 1.85; padding: 20px 0 36px; font-size: 14px; }
.addition { color: var(--success, #23794b); }
.deletion { color: var(--error); }
del { background: color-mix(in srgb, var(--error) 13%, transparent); text-decoration-color: var(--error); }
ins { background: color-mix(in srgb, var(--success, #23794b) 13%, transparent); text-decoration: underline; text-decoration-color: var(--success, #23794b); }
[data-change]:focus { outline: 2px solid var(--accent); outline-offset: 3px; }
.title-change { display: flex; flex-wrap: wrap; gap: 8px; padding: 16px 0; border-bottom: 1px solid var(--line); }
.title-change small { width: 100%; color: var(--muted); }
.api-changes { padding: 16px 0; border-bottom: 1px solid var(--line); }
.api-changes small { color: var(--muted); }
.api-changes ul { margin: 6px 0 0; padding-left: 20px; }
.api-changes li { overflow-wrap: anywhere; }
.api-file { color: var(--muted); }
@media (prefers-reduced-motion: reduce) { [data-change] { scroll-behavior: auto; } }
</style>
