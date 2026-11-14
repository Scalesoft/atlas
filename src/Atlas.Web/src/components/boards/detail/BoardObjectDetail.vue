<script setup lang="ts">
import t from "../../../i18n/boards";
import { computed } from "vue";
import { relationsOf, type BoardDetailTab } from "../../../boards/boardDetail";
import { itemLabel, pageTargetOf } from "../../../boards/boardFlow";
import type { BoardViewItem } from "../../../boards/boardSession";
import AtlasIcon from "../../AtlasIcon.vue";
import EmptyState from "../../settings/EmptyState.vue";
import { Button } from "../../ui/button";
import type { BoardBlockHost } from "../../../boards/editorBlocks";
import type { CommentApi } from "../../../comments/commentApi";
import { discussionOf } from "../../../comments/boardComments";
import type { ViewSessions } from "../../../live/viewSession";
import UsagePanel from "../../objects/UsagePanel.vue";
import CommentThread from "../../comments/CommentThread.vue";
import PagePreview from "../preview/PagePreview.vue";
import BoardMarkdown from "../text/BoardMarkdown.vue";
import { flattenInlineMarkdown } from "../text/boardMarkdownText";

/**
 * The object detail of a board's card or of the board itself (objects spec §6), the content of the page workspace dock
 * (WorkspaceSidePanel, the dock of pages) whose tabs are Přehled · Popis · Vazby · Použito na · Historie. It shows what the
 * board knows today: a page card's page (its preview as Popis), its connectors as Vazby, its cards on this board as Kde je
 * použito and its Diskuse (CommentThread: an object card's object's comments, else the board's, anchored at the item); the rest
 * says what will fill it. Typed relations, placements across boards and history come with the object core.
 */

const props = defineProps<{
  /** The card; null for the board itself. */
  item: BoardViewItem | null;
  items: readonly BoardViewItem[];
  boardTitle: string;
  tab: BoardDetailTab;
  /** PagePreview's data sources (the gallery passes local ones); the app's by default. */
  preview?: Pick<InstanceType<typeof PagePreview>["$props"], "load" | "live" | "view">;
  /** The board, for its Diskuse; without it the tab says comments are not available here. */
  boardId?: string;
  /** The live comments of what the detail shows, for Přehled (the board's quiet badge). */
  commentCount?: number;
  /** The comment thread's data sources (the gallery passes local ones) and its editor services. */
  comments?: { api?: CommentApi; views?: Pick<ViewSessions, "open"> };
  host?: BoardBlockHost;
}>();
const emit = defineEmits<{ openObject: [id: string, typeId: string]; openPage: [pageId: string]; focus: [itemId: string]; comments: [objectId: string, count: number] }>();

const target = computed(() => (props.item ? pageTargetOf(props.item) : undefined));
const title = computed(() => (props.item ? target.value?.title ?? itemLabel(props.item) : props.boardTitle));
const relations = computed(() => (props.item ? relationsOf(props.items, props.item.id) : []));
// Použito na: every place that uses the card's page or object, or the board itself (UsagePanel, GET /api/usage/{id}).
const usedId = computed(() => (props.item ? props.item.targetResourceId : props.boardId));
const counts = computed(() => ({
  items: props.items.filter(item => item.kind !== "connector" && !item.deleted).length,
  connectors: props.items.filter(item => item.kind === "connector" && !item.deleted).length,
}));
const stateLabels = { waiting: t.detailView.waiting, done: t.detailView.done, blocked: t.detailView.blocked } as const;
const discussion = computed(() => (props.boardId ? discussionOf(props.item, props.boardId) : undefined));
// A board comment's place is an item of this board; a page comment's a heading of the page, which opens it.
function showAnchor(anchor: string) {
  if (discussion.value?.ofBoard) emit("focus", anchor);
  else if (discussion.value) emit("openPage", discussion.value.objectId);
}
</script>

<template>
  <div class="board-detail" :data-board-object-detail="tab">
    <template v-if="tab === 'overview'">
      <header class="board-detail-head">
        <AtlasIcon :name="item ? 'page' : 'layout-dashboard'" :size="18" />
        <h3 class="board-detail-title" :title="title">{{ title }}</h3>
      </header>
      <dl class="board-detail-facts">
        <dt>{{ t.detailView.kind }}</dt><dd>{{ item ? t.detailView.pageOnBoard : t.detailView.board }}</dd>
        <template v-if="target?.spaceTitle"><dt>{{ t.detailView.space }}</dt><dd>{{ target.spaceTitle }}</dd></template>
        <template v-if="item && item.target?.available === false"><dt>{{ t.detailView.status }}</dt><dd>{{ t.detailView.pageUnavailable }}</dd></template>
        <template v-if="item"><dt>{{ t.detailView.relations }}</dt><dd>{{ relations.length }}</dd></template>
        <template v-else><dt>{{ t.detailView.items }}</dt><dd>{{ counts.items }} · {{ t.detailView.connectors(counts.connectors) }}</dd></template>
        <template v-if="commentCount"><dt>{{ t.detailView.comments }}</dt><dd data-board-detail-comments>{{ commentCount }}</dd></template>
      </dl>
      <p v-if="target?.excerpt" class="board-detail-excerpt">{{ target.excerpt }}</p>
      <div v-if="item" class="board-detail-actions">
        <Button v-if="item.targetResourceId && item.target?.available !== false" size="sm" type="button" data-board-detail-open @click="emit('openPage', item.targetResourceId)">
          <AtlasIcon name="external" :size="15" />{{ t.detailView.openPage }}</Button>
        <Button variant="outline" size="sm" type="button" data-board-detail-focus @click="emit('focus', item.id)"><AtlasIcon name="search" :size="15" />{{ t.detailView.show }}</Button>
      </div>
    </template>

    <template v-else-if="tab === 'description'">
      <PagePreview v-if="item?.targetResourceId" class="board-detail-preview" :page-id="item.targetResourceId" :anchor="item.props?.display === 'service' ? null : item.props?.anchor ?? null" :visible="true" v-bind="preview" />
      <EmptyState v-else icon="file-text" :title="t.detailView.noDescription" :text="t.detailView.noDescriptionText" />
    </template>

    <template v-else-if="tab === 'relations'">
      <ul v-if="relations.length" class="board-detail-list atlas-stagger" :aria-label="t.detailView.relationsOnBoard">
        <li v-for="relation in relations" :key="relation.id">
          <button type="button" class="board-detail-row" data-board-detail-relation @click="emit('focus', relation.other.id)">
            <AtlasIcon :name="relation.direction === 'out' ? 'arrow-right' : 'arrow-left'" :size="15" />
            <span class="board-detail-row-body">
              <!-- A text of the board is Markdown: the other end's text is shown rendered, on one line. -->
              <BoardMarkdown v-if="relation.other.kind !== 'page' && relation.other.text" class="board-detail-row-title" :text="flattenInlineMarkdown(relation.other.text)" inline
                :aria-label="itemLabel(relation.other)" />
              <span v-else class="board-detail-row-title">{{ itemLabel(relation.other) }}</span>
              <BoardMarkdown v-if="relation.label" class="board-detail-row-label" :text="relation.label" inline :aria-label="t.detailView.relationLabel" />
            </span>
            <span v-if="relation.state" class="board-detail-state" :data-state="relation.state">{{ stateLabels[relation.state] }}</span>
          </button>
        </li>
      </ul>
      <EmptyState v-else icon="network" :title="t.detailView.noRelations" :text="item ? t.detailView.noCardRelations : t.detailView.noBoardRelations" />
    </template>

    <template v-else-if="tab === 'usage'">
      <UsagePanel v-if="usedId" :key="usedId" :object-id="usedId" @open="(id, typeId) => (typeId === 'atlas.page' ? emit('openPage', id) : emit('openObject', id, typeId))" />
      <EmptyState v-else icon="layers" :title="t.detailView.noUsage" :text="t.detailView.noUsageText" />
    </template>

    <template v-else-if="tab === 'comments'">
      <CommentThread v-if="discussion" :key="`${discussion.objectId}:${discussion.anchor}`" :object-id="discussion.objectId" :anchor="discussion.anchor"
        :view-key="`board:${boardId}`" :host="host" :lazy="false" v-bind="comments" @count="emit('comments', discussion.objectId, $event)" @anchor="showAnchor" />
      <EmptyState v-else icon="quote" :title="t.detailView.noDiscussion" :text="t.detailView.noDiscussionText" />
    </template>

    <EmptyState v-else icon="history" :title="t.detailView.noHistory" :text="t.detailView.noHistoryText" />
  </div>
</template>

<style scoped>
.board-detail { display: flex; flex-direction: column; gap: 14px; min-width: 0; padding: 16px; animation: st-fade-in var(--atlas-motion-base) var(--atlas-ease-out); }
.board-detail-head { display: flex; align-items: center; gap: 8px; min-width: 0; color: var(--muted); }
.board-detail-title { min-width: 0; margin: 0; overflow: hidden; color: var(--text); font-size: 16px; font-weight: 600; text-overflow: ellipsis; white-space: nowrap; }
.board-detail-facts { display: grid; grid-template-columns: auto minmax(0, 1fr); gap: 6px 12px; margin: 0; padding: 12px; border: 1px solid color-mix(in srgb, var(--line) 80%, transparent);
  border-radius: 10px; background: var(--surface); font-size: 13px; }
.board-detail-facts dt { color: var(--muted); }
.board-detail-facts dd { margin: 0; min-width: 0; overflow-wrap: anywhere; }
.board-detail-excerpt { margin: 0; color: var(--muted); font-size: 13px; line-height: 1.5; }
.board-detail-actions { display: flex; flex-wrap: wrap; gap: 8px; }
.board-detail-preview { min-height: 240px; border: 1px solid var(--line); border-radius: 10px; overflow: hidden; }
.board-detail-list { display: flex; flex-direction: column; gap: 6px; margin: 0; padding: 0; list-style: none; }
.board-detail-row { display: flex; align-items: center; gap: 10px; width: 100%; padding: 9px 12px; border: 1px solid color-mix(in srgb, var(--line) 80%, transparent); border-radius: 10px;
  background: var(--surface); color: var(--text); font: inherit; text-align: left; cursor: pointer;
  transition: border-color var(--atlas-motion-fast) var(--atlas-ease-out), background-color var(--atlas-motion-fast) var(--atlas-ease-out); }
@media (hover: hover) and (pointer: fine) { .board-detail-row:hover { border-color: var(--line); background: color-mix(in srgb, var(--hover) 50%, var(--surface)); } }
.board-detail-row:focus-visible { outline: 2px solid var(--accent); outline-offset: 2px; }
.board-detail-row > svg { flex: none; color: var(--muted); }
.board-detail-row-body { display: flex; flex: 1; flex-direction: column; min-width: 0; }
.board-detail-row-title, .board-detail-row-label { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.board-detail-row-title { font-size: 13px; }
.board-detail-row-label { color: var(--muted); font-size: 12px; }
.board-detail-state { flex: none; padding: 0 6px; border-radius: 6px; font-size: 11.5px; font-weight: 600; }
.board-detail-state[data-state="waiting"] { background: var(--warning-bg); color: var(--warning); }
.board-detail-state[data-state="done"] { background: var(--success-bg); color: var(--success); }
.board-detail-state[data-state="blocked"] { background: var(--error-bg); color: var(--error); }
.board-detail-note { margin: 0; color: var(--muted); font-size: 12.5px; line-height: 1.5; }
</style>
