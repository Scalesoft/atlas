<script setup lang="ts">
import { computed } from "vue";
import type { PageRevisionSummary } from "../../history";
import { formatDateTime } from "../../formatDate";
import ChannelBadge from "../ChannelBadge.vue";
import SubjectAvatar from "../settings/SubjectAvatar.vue";
import { describeChanges, shares, sizeBar } from "./versionSummary";
import t from "../../i18n/components-history";

// The inside of a history list row (version or step): who, how much, where in the text is in the preview. Static facts,
// so it never moves on hover. Shared by the history panel and its gallery example. `step`: one saved checkpoint of a version.
const props = defineProps<{ item: PageRevisionSummary; step?: boolean }>();
const time = computed(() => (formatDateTime(props.item.activeFrom) || t.panel.unknownTime).split(" ").at(-1));
const changes = computed(() => props.item.changes ?? null);
const lines = computed(() => changes.value && !props.step ? describeChanges(changes.value) : []);
const people = computed(() => shares(changes.value));
</script>

<template>
  <time :datetime="item.activeFrom">{{ time }}</time>
  <span class="hv-who">
    <span class="hv-author">{{ item.authorName }}</span>
    <span v-if="item.isAi" class="hv-ai" data-history-ai :title="t.version.aiTitle">{{ t.version.ai }}</span>
    <ChannelBadge :channel="item.channel" />
  </span>
  <span v-if="changes" class="hv-size" :title="t.version.sizeTitle(changes.added, changes.removed, changes.blocks)"><span class="addition">+{{ changes.added }}</span> <span class="deletion">−{{ changes.removed }}</span> {{ t.version.lines }}</span>
  <span v-if="item.label" class="hv-label" data-history-label>{{ item.label }}</span>
  <span v-if="changes" class="hv-bar" aria-hidden="true"><span :class="{ ai: item.isAi }" :style="{ width: `${sizeBar(changes)}%` }" /></span>
  <span class="hv-detail">v{{ item.version }}<span v-if="changes && !step"> · {{ changes.blocks }} {{ t.version.blocks }}</span><span v-if="!step && item.checkpoints && item.checkpoints > 1"> · {{ t.version.saves(item.checkpoints) }}</span><span v-if="item.isCurrent"> · {{ t.version.current }}</span><span v-if="item.isPublished"> · {{ t.version.published }}</span><span v-else-if="item.lastPublishedAt"> · {{ t.version.wasPublished }}</span></span>
  <span v-if="item.changeSummary" class="hv-note history-version-summary" :title="item.changeSummary">{{ item.changeSummary }}</span>
  <span v-for="line in lines" :key="line" class="hv-line" data-history-change>{{ line }}</span>
  <span v-if="people.length > 1 && !step" class="hv-people"><span v-for="person in people" :key="person.id" class="hv-person" :title="t.version.share(person.name, person.share)"><SubjectAvatar kind="user" :name="person.name" size="sm" />{{ t.version.percent(person.share) }}</span></span>
</template>

<style scoped>
.hv-who { display: flex; align-items: center; gap: 6px; min-width: 0; }
.hv-author { font-size: 12px; font-weight: 550; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
/* The AI mark is a word, not the glyph (aiGlyphBoundary.guard.test.ts): the change is by AI, the human stays the author. */
.hv-ai { flex-shrink: 0; padding: 0 5px; border-radius: 4px; font-size: 10px; font-weight: 700; line-height: 16px; color: var(--ai-ink, var(--accent)); background: var(--ai-wash-strong, var(--hover)); border: 1px solid var(--ai-line, var(--line)); }
.hv-size, time { font-size: 11px; font-variant-numeric: tabular-nums; align-self: center; white-space: nowrap; }
time { color: var(--muted); }
.hv-label { grid-column: 2 / -1; font-size: 13px; font-weight: 600; overflow-wrap: anywhere; }
.hv-bar { grid-column: 2 / -1; height: 3px; border-radius: 2px; background: var(--line); overflow: hidden; }
.hv-bar > span { display: block; height: 100%; background: var(--accent); border-radius: 2px; }
.hv-bar > span.ai { background: var(--ai-ink, var(--accent)); }
.hv-detail { grid-column: 2 / -1; color: var(--muted); font-size: 10px; }
.hv-note { grid-column: 2 / -1; min-width: 0; color: var(--muted); font-size: 12px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.hv-line { grid-column: 2 / -1; font-size: 11px; color: var(--text); overflow-wrap: anywhere; }
.hv-people { grid-column: 2 / -1; display: flex; flex-wrap: wrap; gap: 4px 10px; font-size: 10px; color: var(--muted); }
.hv-person { display: inline-flex; align-items: center; gap: 4px; }
.hv-person :deep(.subject-avatar) { width: 18px; height: 18px; font-size: 8px; }
.addition { color: var(--success, #23794b); }
.deletion { color: var(--error); }
</style>
