<script setup lang="ts">
import t from "../../../i18n/boards";
import AtlasIcon from "../../AtlasIcon.vue";
import { Button } from "../../ui/button";
import type { BoardTool } from "./boardCanvasContext";
import BoardToolGlyph from "./BoardToolGlyph.vue";
import type { BoardToolIcon } from "./boardToolIcons";

/**
 * The vertical tool rail on the left (spec §3.1): the tools with their keys, the page picker and the editor's components (K: the
 * canvas opens them as a search beside the rail, the command panel's list; I and C go straight to a YouTrack issue and a contact),
 * then undo and redo. Read-only keeps Výběr and Ruka.
 */
// `orientation`: a column on the left, or a row along the bottom of a narrow board (the canvas decides, U2-10).
// `multiSelect`: on a touch screen, whether a tap adds to the selection („Vybírat více“, the Shift a finger lacks); absent elsewhere.
withDefaults(defineProps<{ tool: BoardTool; canEdit: boolean; canUndo: boolean; canRedo: boolean; canPickPage: boolean; orientation?: "vertical" | "horizontal"; multiSelect?: boolean }>(), { orientation: "vertical", multiSelect: undefined });
const emit = defineEmits<{ tool: [tool: BoardTool]; pickPage: []; components: []; undo: []; redo: []; multiSelect: [] }>();

const tools: { id: BoardTool; label: string; key: string; glyph?: BoardToolIcon; icon?: string; edit: boolean }[] = [
  { id: "select", label: t.rail.select, key: "V", glyph: "select", edit: false },
  { id: "hand", label: t.rail.hand, key: "H", glyph: "hand", edit: false },
  { id: "note", label: t.rail.note, key: "N", glyph: "note", edit: true },
  { id: "text", label: t.rail.text, key: "T", icon: "type", edit: true },
  { id: "shape", label: t.rail.shape, key: "S", glyph: "shape", edit: true },
  { id: "frame", label: t.rail.frame, key: "F", glyph: "frame", edit: true },
  { id: "connector", label: t.rail.connector, key: "L", glyph: "connector", edit: true },
];
</script>

<template>
  <div class="board-rail" role="toolbar" :aria-label="t.rail.label" :aria-orientation="orientation" data-board-rail>
    <template v-for="item in tools" :key="item.id">
      <Button v-if="canEdit || !item.edit" variant="ghost" size="icon-sm" type="button" :aria-label="item.label" :title="`${item.label} (${item.key})`"
        :aria-pressed="tool === item.id" :data-board-tool="item.id" @click="emit('tool', item.id)">
        <BoardToolGlyph v-if="item.glyph" :name="item.glyph" :size="18" /><AtlasIcon v-else :name="item.icon!" :size="18" />
      </Button>
    </template>
    <Button v-if="multiSelect !== undefined" variant="ghost" size="icon-sm" type="button" :aria-label="t.rail.multi" :title="t.rail.multiTitle"
      :aria-pressed="multiSelect" data-board-multi-select @click="emit('multiSelect')"><AtlasIcon name="list-checks" :size="18" /></Button>
    <template v-if="canEdit">
      <Button v-if="canPickPage" variant="ghost" size="icon-sm" type="button" :aria-label="t.rail.page" :title="t.rail.pageTitle" data-board-tool="page" @click="emit('pickPage')"><AtlasIcon name="page" :size="18" /></Button>
      <Button variant="ghost" size="icon-sm" type="button" :aria-label="t.rail.component" :title="t.rail.componentTitle" aria-haspopup="listbox" data-board-tool="component"
        @click="emit('components')"><BoardToolGlyph name="blocks" :size="18" /></Button>
      <span class="board-rail-gap" />
      <Button variant="ghost" size="icon-sm" type="button" :aria-label="t.rail.undo" :title="t.rail.undoTitle" :disabled="!canUndo" data-board-undo @click="emit('undo')"><AtlasIcon name="undo-2" :size="16" /></Button>
      <Button variant="ghost" size="icon-sm" type="button" :aria-label="t.rail.redo" :title="t.rail.redoTitle" :disabled="!canRedo" data-board-redo @click="emit('redo')"><AtlasIcon name="redo-2" :size="16" /></Button>
    </template>
  </div>
</template>

<style scoped>
.board-rail { display: flex; flex-direction: column; align-items: center; gap: 2px; padding: 4px; border: 1px solid var(--line); border-radius: var(--atlas-radius-md); background: var(--surface); box-shadow: var(--refresh-card-shadow); }
.board-rail :deep(button[aria-pressed="true"]) { color: var(--accent); background: var(--accent-soft); }
.board-rail :deep(button:active:not(:disabled)) { scale: var(--atlas-press); }
.board-rail-gap { flex: none; width: 20px; height: 1px; margin: 4px 0; background: var(--line); }
.board-rail[aria-orientation="horizontal"] { flex-direction: row; overflow-x: auto; scrollbar-width: none; }
.board-rail[aria-orientation="horizontal"] > * { flex: none; }
.board-rail[aria-orientation="horizontal"] .board-rail-gap { width: 1px; height: 20px; margin: 0 4px; }
</style>
