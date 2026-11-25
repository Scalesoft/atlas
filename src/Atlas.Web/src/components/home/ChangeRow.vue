<script setup lang="ts">
import { computed } from "vue";
import AtlasIcon from "../AtlasIcon.vue";
import ChannelBadge from "../ChannelBadge.vue";
import SubjectAvatar from "../settings/SubjectAvatar.vue";
import { formatAgo, formatDateTime } from "../../formatDate";
import { isPlainNavigationClick, pageLink } from "../../pageLinks";
import type { RecentChange } from "./homeApi";
import t from "../../i18n/components-home";

/**
 * One page in Co se změnilo: the page and its space, who changed it and when, and how — a person (nothing more), or with AI: the word
 * AI (as in the page history; never the AI glyph) and the channel with its token where the server names it (ChannelBadge). The row opens
 * the page; „Historie“ opens this change in the page history, for those who may edit the page. Use it for changes of pages; a list of
 * pages without an author (Nedávné, Oblíbené) is PersonalLibraryPanel's row.
 */
const props = defineProps<{ change: RecentChange; now?: number }>();
const emit = defineEmits<{ open: [change: RecentChange]; history: [change: RecentChange] }>();
const href = computed(() => pageLink(props.change.pageId));
// A channel with AI has its own mark; a person's web change has none, the name says it all.
const agent = computed(() => props.change.channel === "mcp" || props.change.channel === "api" || props.change.channel === "assistant");
function open(event: MouseEvent) {
  if (!isPlainNavigationClick(event)) return;
  event.preventDefault();
  emit("open", props.change);
}
</script>

<template>
  <div class="change-row" data-change-row :data-ai="change.isAi || undefined">
    <a class="change-open" :href="href" :aria-label="t.changes.openPage(change.title)" @click="open">
      <SubjectAvatar kind="user" :name="change.authorName" :user-id="change.authorId" size="sm" />
      <span class="change-copy">
        <strong class="change-title" :title="change.title">{{ change.title }}</strong>
        <span class="change-meta">
          <span v-if="change.spaceName" class="change-space" :title="change.spaceName">{{ change.spaceName }}</span>
          <span class="change-author">{{ change.authorName }}</span>
          <span v-if="change.isAi" class="change-ai" data-change-ai :title="t.changes.aiMarkTitle">{{ t.changes.aiMark }}</span>
          <ChannelBadge v-if="agent" :channel="change.channel" :token="change.apiToken" detailed />
          <time :datetime="change.at" :title="formatDateTime(change.at)">{{ formatAgo(change.at, now) }}</time>
          <span v-if="change.furtherChanges > 0" class="change-further change-further-inline">{{ t.changes.further(change.furtherChanges) }}</span>
        </span>
      </span>
      <span v-if="change.furtherChanges > 0" class="change-further change-further-side">{{ t.changes.further(change.furtherChanges) }}</span>
    </a>
    <button v-if="change.canOpenHistory" type="button" class="change-history" :aria-label="t.changes.openHistory(change.title)" :title="t.changes.history"
      data-change-history @click="emit('history', change)"><AtlasIcon name="history" :size="16" /><span class="change-history-label">{{ t.changes.history }}</span></button>
  </div>
</template>

<style scoped>
.change-row { container: change-row / inline-size; display: flex; align-items: center; gap: 4px; min-height: 60px; padding: 0 10px 0 0; box-sizing: border-box; }
.change-open { display: flex; flex: 1; align-items: center; gap: 12px; min-width: 0; align-self: stretch; padding: 10px 6px 10px var(--st-pad-x, 18px); color: var(--text); text-decoration: none; border-radius: 8px; }
.change-copy { display: grid; flex: 1; gap: 4px; min-width: 0; }
.change-title { font-size: 14px; font-weight: 600; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.change-meta { display: flex; flex-wrap: wrap; align-items: center; gap: 2px 8px; min-width: 0; font-size: 12px; color: var(--muted); }
.change-space { max-width: 22ch; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.change-space::after { content: "·"; margin-left: 8px; }
.change-author { font-weight: 550; color: var(--text); }
.change-meta time { white-space: nowrap; }
/* The AI mark of the page history (HistoryVersionBody): a word in the AI wash, never the glyph (aiGlyphBoundary.guard.test.ts). */
.change-ai { padding: 0 5px; border-radius: 4px; font-size: 10px; font-weight: 700; line-height: 16px; color: var(--ai-ink, var(--accent)); background: var(--ai-wash-strong, var(--hover)); border: 1px solid var(--ai-line, var(--line)); }
.change-further { flex: 0 0 auto; font-size: 12px; color: var(--muted); white-space: nowrap; }
/* Beside the row where there is room, in its meta line on a narrow one: only one of them is ever displayed. */
.change-further-inline { display: none; }
.change-history { display: inline-flex; flex: 0 0 auto; align-items: center; gap: 6px; height: 32px; padding: 0 10px; border: 1px solid transparent; border-radius: 8px; background: transparent; color: var(--muted); font: inherit; font-size: 13px; cursor: pointer; }
@media (hover: hover) and (pointer: fine) {
  .change-open:hover { background: var(--hover); }
  .change-history:hover { background: var(--hover); color: var(--text); }
}
.change-history:active { scale: var(--atlas-press); }
.change-open:focus-visible, .change-history:focus-visible { outline: 2px solid var(--accent); outline-offset: -2px; }
@container change-row (max-width: 520px) {
  .change-history-label, .change-further-side { display: none; }
  .change-further-inline { display: inline; }
  .change-history { padding: 0 8px; }
}
</style>
