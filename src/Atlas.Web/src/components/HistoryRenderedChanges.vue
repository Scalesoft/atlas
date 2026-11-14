<script setup lang="ts">
import type { Appearance } from "../api";
import { computed, nextTick, onBeforeUnmount, onErrorCaptured, onMounted, ref, shallowRef, watch } from "vue";
import type { MarkdownRichValue } from "@atlas/markdown/core";
import { MarkdownRenderer } from "@atlas/markdown/vue";
import "@atlas/markdown/styles/base.css";
import "@atlas/markdown/themes/ocean.css";
import "@atlas/markdown/themes/forest.css";
import "@atlas/markdown/themes/midnight.css";
import AtlasIcon from "./AtlasIcon.vue";
import t from "../i18n/components-history";
import { atlasContentPlugins } from "../documentWidgets";
import { alignChangedBlocks, changedTextRanges, formattingOnly, isBlankBlock as blank, onlyMovedAway, type HistoryBlock, type HistoryBlockDiff } from "../historyBlocks";

/**
 * The change view of a revision as the reader sees the page: changed blocks rendered with the
 * document renderer (tables, callouts, diagrams, not their Markdown), marked Přidáno / Odstraněno /
 * Upraveno, one unchanged block around each change for orientation and longer unchanged runs folded.
 * Inside an edited block the added (and, when any, the removed) words are highlighted with the CSS
 * Custom Highlight API, which marks text without touching the renderer's DOM; without it the block
 * pair is still shown. HistoryChanges keeps the Markdown comparison as the exact fallback.
 */
const props = defineProps<{ diff: HistoryBlockDiff; first: boolean; dark: boolean; accent: Appearance["accent"] }>();
const emit = defineEmits<{ failed: [] }>();

type Item =
  | { kind: "added" | "removed" | "moved"; key: string; blocks: HistoryBlock[]; value: MarkdownRichValue }
  | { kind: "modified" | "formatted"; key: string; before: MarkdownRichValue; after: MarkdownRichValue }
  | { kind: "blank"; key: string };
type Row =
  | { kind: "context"; key: string; value: MarkdownRichValue }
  | { kind: "skip"; key: string; segment: number; blocks: number }
  | { kind: "change"; key: string; items: Item[] };

// A page is read change by change; beyond this the Markdown view lists the rest exactly.
const MAX_ITEMS = 80;
const plugins = atlasContentPlugins({ kind: "history" });
const theme = computed(() => (props.dark ? "md-theme-midnight" : props.accent === "green" ? "md-theme-forest" : ""));
const expanded = ref(new Set<number>());
watch(() => props.diff, () => { expanded.value = new Set(); });

function doc(blocks: HistoryBlock[]): MarkdownRichValue {
  return { format: "atlas-rich", version: 1, ...(props.diff.plugins ? { plugins: props.diff.plugins } : {}), document: { type: "doc", content: blocks as never } };
}
const layout = computed(() => {
  const rows: Row[] = [];
  let items = 0, truncated = false;
  const segments = props.diff.segments;
  segments.forEach((segment, index) => {
    if (truncated) return;
    if (segment.kind === "same") {
      const blocks = segment.blocks;
      const content = blocks.flatMap((block, at) => (blank(block) ? [] : [at]));
      // One unchanged block on each side of a change keeps the reader oriented; the rest folds.
      const shown = [...new Set([index > 0 ? content[0] : undefined, index < segments.length - 1 ? content.at(-1) : undefined].filter((at): at is number => at !== undefined))];
      if (expanded.value.has(index) || content.length <= shown.length + 1) {
        if (content.length) rows.push({ kind: "context", key: `c${index}`, value: doc(blocks) });
        return;
      }
      const head = index > 0 ? content[0]! : -1, tail = index < segments.length - 1 ? content.at(-1)! : -1;
      if (head >= 0) rows.push({ kind: "context", key: `h${index}`, value: doc([blocks[head]!]) });
      rows.push({ kind: "skip", key: `s${index}`, segment: index, blocks: content.length - shown.length });
      if (tail >= 0 && tail !== head) rows.push({ kind: "context", key: `t${index}`, value: doc([blocks[tail]!]) });
      return;
    }
    // A block that moved shows once, where it went (Přesunuto); its old place shows nothing.
    if (onlyMovedAway(segment, props.diff.moved)) return;
    const moved = props.diff.moved;
    const removed = segment.removed.filter((block) => !blank(block) && !moved.has(block)), added = segment.added.filter((block) => !blank(block));
    const change: Item[] = [];
    // Consecutive added (or removed, moved) blocks read as one passage in one box.
    const run = (kind: "added" | "removed" | "moved", block: HistoryBlock, key: string) => {
      const last = change.at(-1);
      if (last?.kind === kind) last.blocks.push(block);
      else change.push({ kind, key, blocks: [block], value: doc([]) });
    };
    for (const [k, entry] of alignChangedBlocks(removed, added).entries()) {
      if (items >= MAX_ITEMS) { truncated = true; break; }
      // Only the marks changed (bold, a link): said so, the words alone would show no change.
      if (entry.kind === "modified") change.push({ kind: formattingOnly(entry.before, entry.after) ? "formatted" : "modified", key: `m${index}-${k}`, before: doc([entry.before]), after: doc([entry.after]) });
      else run(entry.kind === "added" && moved.has(entry.block) ? "moved" : entry.kind, entry.block, `${entry.kind[0]}${index}-${k}`);
      items++;
    }
    for (const item of change) if (item.kind === "added" || item.kind === "removed" || item.kind === "moved") item.value = doc(item.blocks);
    rows.push({ kind: "change", key: `x${index}`, items: change.length ? change : [{ kind: "blank", key: `b${index}` }] });
  });
  return { rows, truncated };
});
function expand(segment: number) { expanded.value = new Set([...expanded.value, segment]); }

// The rows fill in from the top, a few a frame: the first changes are on screen at once and a long comparison never holds the
// main thread for the whole page (each row is a document renderer). What is shown stays where it is.
const SLICE = 5;
const shown = ref(SLICE);
let filling = 0;
function fill() {
  filling = 0;
  if (shown.value >= layout.value.rows.length) return;
  shown.value += SLICE;
  filling = requestAnimationFrame(fill);
}
watch(() => props.diff, () => { shown.value = SLICE; });
watch(layout, () => { if (!filling) filling = requestAnimationFrame(fill); }, { immediate: true });

const root = ref<HTMLElement>();
const failure = ref(false);
onErrorCaptured(() => { failure.value = true; emit("failed"); return false; });

// Word marks inside edited blocks. The registry is document-wide, so each instance removes only its ranges.
const highlightSupported = typeof CSS !== "undefined" && "highlights" in CSS && typeof Highlight === "function";
const withRemovals = shallowRef(new Set<string>());
let owned: { name: string; range: Range }[] = [];
function registry(name: string) {
  let highlight = CSS.highlights.get(name);
  if (!highlight) { highlight = new Highlight(); CSS.highlights.set(name, highlight); }
  return highlight;
}
function clearMarks() {
  for (const { name, range } of owned) CSS.highlights.get(name)?.delete(range);
  owned = [];
}
function rangesFor(element: Element, spans: [number, number][]) {
  const walker = document.createTreeWalker(element, NodeFilter.SHOW_TEXT);
  const nodes: { node: Text; start: number; end: number }[] = [];
  let offset = 0;
  for (let node = walker.nextNode() as Text | null; node; node = walker.nextNode() as Text | null) {
    nodes.push({ node, start: offset, end: offset + node.data.length });
    offset += node.data.length;
  }
  const ranges: Range[] = [];
  for (const [from, to] of spans)
    for (const entry of nodes) {
      if (entry.end <= from || entry.start >= to) continue;
      const range = document.createRange();
      range.setStart(entry.node, Math.max(from, entry.start) - entry.start);
      range.setEnd(entry.node, Math.min(to, entry.end) - entry.start);
      ranges.push(range);
    }
  return ranges;
}
function mark(name: string, ranges: Range[]) {
  const highlight = registry(name);
  for (const range of ranges) { highlight.add(range); owned.push({ name, range }); }
}
let frame = 0;
function paint() {
  frame = 0;
  if (!highlightSupported || !root.value) return;
  clearMarks();
  const removals = new Set<string>();
  for (const pair of root.value.querySelectorAll<HTMLElement>("[data-modified]")) {
    const before = pair.querySelector("[data-side=before]"), after = pair.querySelector("[data-side=after]");
    if (!before || !after) continue;
    const { removed, added } = changedTextRanges(before.textContent ?? "", after.textContent ?? "");
    if (removed.length) { removals.add(pair.dataset.modified!); mark("atlas-history-removed", rangesFor(before, removed)); }
    mark("atlas-history-added", rangesFor(after, added));
  }
  withRemovals.value = removals;
}
function schedule() { if (!frame) frame = requestAnimationFrame(paint); }
// Renderer plugins (diagrams, attachments) fill in after mount; repaint when the rendered text changes.
let observer: MutationObserver | undefined;
onMounted(() => {
  if (!highlightSupported || !root.value) return;
  observer = new MutationObserver(schedule);
  observer.observe(root.value, { subtree: true, childList: true, characterData: true });
  void nextTick(schedule);
});
watch(layout, () => { void nextTick(schedule); });
onBeforeUnmount(() => { observer?.disconnect(); if (filling) cancelAnimationFrame(filling); if (frame) cancelAnimationFrame(frame); if (highlightSupported) clearMarks(); });

const tags = t.rendered.tags;
</script>

<template>
  <p v-if="failure" role="alert" class="notice error">{{ t.rendered.failed }}</p>
  <div v-else ref="root" class="rendered-changes" :class="theme">
    <template v-if="first">
      <p class="rendered-note"><AtlasIcon name="file-plus" :size="14" />{{ t.rendered.firstNote }}</p>
      <!-- @vue-generic {'rich'} -->
      <MarkdownRenderer :value="doc(diff.segments.flatMap((segment) => (segment.kind === 'same' ? segment.blocks : segment.added)))" profile="rich" :ariaLabel="t.rendered.firstContent" :plugins="plugins" :class="theme" />
    </template>
    <template v-else v-for="row in layout.rows.slice(0, shown)" :key="row.key">
      <div v-if="row.kind === 'context'" class="rendered-context" aria-hidden="true">
        <!-- @vue-generic {'rich'} -->
        <MarkdownRenderer :value="row.value" profile="rich" :ariaLabel="t.rendered.unchangedContent" :plugins="plugins" :class="theme" />
      </div>
      <button v-else-if="row.kind === 'skip'" type="button" class="rendered-skip" @click="expand(row.segment)">
        <AtlasIcon name="chevron-down" :size="14" />{{ t.rendered.blocks(row.blocks) }} · {{ t.rendered.show }}
      </button>
      <section v-else class="rendered-change" data-change tabindex="-1" :aria-label="t.rendered.change">
        <div v-for="item in row.items" :key="item.key" class="rendered-item" :data-kind="item.kind" :data-modified="'before' in item ? item.key : undefined">
          <span class="rendered-tag">{{ tags[item.kind] }}</span>
          <template v-if="'before' in item">
            <!-- The earlier wording is shown only when words were removed; additions alone read in the new block. -->
            <!-- data-side wraps only the rendered block: its text is what the word comparison reads. -->
            <div v-show="!highlightSupported || withRemovals.has(item.key)" class="rendered-block rendered-before">
              <small>{{ t.rendered.before }}</small>
              <div data-side="before">
                <!-- @vue-generic {'rich'} -->
                <MarkdownRenderer :value="item.before" profile="rich" :ariaLabel="t.rendered.previousWording" :plugins="plugins" :class="theme" />
              </div>
            </div>
            <div class="rendered-block rendered-after">
              <small v-if="!highlightSupported || withRemovals.has(item.key)">{{ t.rendered.now }}</small>
              <div data-side="after">
                <!-- @vue-generic {'rich'} -->
                <MarkdownRenderer :value="item.after" profile="rich" :ariaLabel="t.rendered.newWording" :plugins="plugins" :class="theme" />
              </div>
            </div>
          </template>
          <p v-else-if="item.kind === 'blank'" class="rendered-blank">{{ t.rendered.blank }}</p>
          <div v-else class="rendered-block">
            <!-- @vue-generic {'rich'} -->
            <MarkdownRenderer :value="item.value" profile="rich" :ariaLabel="{ added: t.rendered.addedContent, removed: t.rendered.removedContent, moved: t.rendered.movedContent }[item.kind]" :plugins="plugins" :class="theme" />
          </div>
        </div>
      </section>
    </template>
    <p v-if="!first && shown < layout.rows.length" class="rendered-note" role="status" data-filling><AtlasIcon name="loader-circle" :size="14" class="atlas-spinner" />{{ t.rendered.loadingMore }}</p>
    <p v-else-if="layout.truncated" class="rendered-note" role="status"><AtlasIcon name="list" :size="14" />{{ t.rendered.more }}</p>
  </div>
</template>

<style scoped>
.rendered-changes { display: grid; gap: 6px; padding: 16px 0 36px; min-width: 0; }
/* See HistoryRevisionPreview: the renderer root takes this component's scope attribute. */
.md-renderer { --font-sans: "DM Sans", system-ui, sans-serif; min-width: 0; }
/* The change boxes tint the background; the themes' own renderer surface (midnight) would cover it. */
.rendered-changes .md-editor.md-renderer { background: transparent; }
.rendered-note { display: flex; align-items: center; gap: 8px; margin: 0 0 6px; color: var(--muted); font-size: 12.5px; }
.rendered-context { opacity: .5; max-height: 12rem; overflow: hidden; mask-image: linear-gradient(to bottom, #000 70%, transparent); }
.rendered-skip {
  display: flex; align-items: center; justify-content: center; gap: 8px; width: 100%; min-height: 32px; padding: 4px 10px;
  border: 1px dashed var(--line); border-radius: var(--atlas-radius-md, 10px); background: transparent; color: var(--muted);
  font: inherit; font-size: 12.5px; cursor: pointer;
  transition: background-color var(--atlas-motion-fast), color var(--atlas-motion-fast);
}
@media (hover: hover) and (pointer: fine) { .rendered-skip:hover { background: var(--hover); color: var(--text); } }
.rendered-skip:focus-visible, .rendered-change:focus-visible { outline: 2px solid var(--accent); outline-offset: 3px; }
.rendered-change { display: grid; gap: 8px; margin: 4px 0; border-radius: var(--atlas-radius-md, 10px); animation: st-fade-in var(--atlas-motion-base) var(--atlas-ease-out) both; }
.rendered-item { --change: var(--accent); position: relative; display: grid; gap: 6px; padding: 8px 12px 8px 14px; border-left: 3px solid var(--change); border-radius: 0 var(--atlas-radius-md, 10px) var(--atlas-radius-md, 10px) 0; background: color-mix(in srgb, var(--change) 6%, transparent); }
.rendered-item[data-kind="added"] { --change: var(--success, #23794b); }
.rendered-item[data-kind="removed"] { --change: var(--error); }
.rendered-item[data-kind="removed"] .rendered-block { opacity: .72; text-decoration: line-through; text-decoration-color: color-mix(in srgb, var(--error) 55%, transparent); }
.rendered-tag { justify-self: start; padding: 1px 8px; border-radius: 999px; color: var(--change); background: color-mix(in srgb, var(--change) 12%, transparent); font-size: 11px; font-weight: 650; letter-spacing: .02em; }
.rendered-item[data-kind="blank"] { --change: var(--muted); }
.rendered-blank { margin: 0; color: var(--muted); font-size: 12.5px; }
.rendered-block { min-width: 0; overflow-x: auto; }
/* Block margins belong between blocks of a page, not inside a change box. */
.rendered-block :deep(.ProseMirror > :first-child), .rendered-context :deep(.ProseMirror > :first-child) { margin-top: 0; }
.rendered-block :deep(.ProseMirror > :last-child), .rendered-context :deep(.ProseMirror > :last-child) { margin-bottom: 0; }
.rendered-block > small { display: block; color: var(--muted); font-size: 11px; font-weight: 600; }
.rendered-before { padding-bottom: 6px; border-bottom: 1px dashed color-mix(in srgb, var(--change) 30%, transparent); opacity: .8; }
</style>

<style>
/* Word marks of HistoryRenderedChanges (CSS Custom Highlight API; custom properties do not reach
   highlight pseudo-elements everywhere, so the colours are literal). */
::highlight(atlas-history-added) { background-color: rgb(35 121 75 / .24); }
::highlight(atlas-history-removed) { background-color: rgb(180 58 73 / .2); text-decoration: line-through; text-decoration-color: rgb(180 58 73 / .8); }
:root[data-mode="dark"] ::highlight(atlas-history-added) { background-color: rgb(74 222 128 / .26); }
:root[data-mode="dark"] ::highlight(atlas-history-removed) { background-color: rgb(248 113 113 / .24); text-decoration-color: rgb(248 113 113 / .85); }
</style>
