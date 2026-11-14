<script setup lang="ts">
import t from "../i18n/components-b";
import { computed } from "vue";
import AtlasIcon from "./AtlasIcon.vue";
import { Tooltip, TooltipContent, TooltipTrigger } from "./ui/tooltip";

/**
 * „Zpět“ in the top-left corner of the page (owner's variant A, „klín“): a quiet wedge under the workspace bar that stays put while
 * the page scrolls. It does exactly what Ctrl+Z does in the page editor and is there only while something can be undone on a page
 * the reader may edit (a writer, not locked). Pressing it keeps the editor's focus and selection (mousedown does not take focus).
 * Also over a board (BoardView), driven by the board session's undo: while a text or a page preview is edited the wedge undoes that editor (boards/editorUndo.ts), otherwise the board
 * (BoardToolRail keeps its explicit Zpět/Znovu pair beside it). `--undo-wedge-left/top` move the button where the corner is busy.
 */
const props = defineProps<{ editable: boolean; canUndo: boolean }>();
const emit = defineEmits<{ undo: [] }>();
const shown = computed(() => props.editable && props.canUndo);
</script>

<template>
  <div class="page-undo" :data-state="shown ? 'faint' : 'hidden'" :inert="!shown || undefined" data-page-undo>
    <div class="page-undo-wedge" aria-hidden="true" />
    <Tooltip>
      <TooltipTrigger as-child>
        <button type="button" class="page-undo-button" :aria-label="t.wedge.undo" aria-keyshortcuts="Control+Z" @mousedown.prevent @click="emit('undo')">
          <AtlasIcon name="undo-2" :size="17" />
        </button>
      </TooltipTrigger>
      <TooltipContent side="bottom" align="start">{{ t.wedge.undoTip }}</TooltipContent>
    </Tooltip>
  </div>
</template>

<style scoped>
/* A zero-height row at the top of the workspace content: sticky under the workspace bar in the page's own (window) scroll, at the
   content's left edge whatever the sidebar and the dock do; below the bar (z-index 5) and its menus. */
.page-undo {
  position: sticky; top: var(--atlas-workspace-bar-height, var(--atlas-topbar-height, 52px)); z-index: 4; height: 0;
  opacity: 0; translate: calc(var(--atlas-rise) * -0.6) calc(var(--atlas-rise) * -0.6);
  transition: opacity var(--atlas-motion-slow) var(--atlas-ease-out), translate var(--atlas-motion-slow) var(--atlas-ease-out);
}
.page-undo[data-state="faint"] { opacity: 1; translate: none; }
/* The wedge: a faint accent tint in the corner fading out along the diagonal before the box's edges (no
   seam). See-through, so the title and the text under it stay readable; the button lies in the header's top padding (48 px). */
.page-undo-wedge {
  position: absolute; inset: 0 auto auto 0; width: 104px; height: 104px; pointer-events: none;
  background: linear-gradient(135deg, color-mix(in srgb, var(--accent) 10%, transparent) 14%, transparent 50%);
}
.page-undo-button {
  position: absolute; left: var(--undo-wedge-left, 14px); top: var(--undo-wedge-top, 12px); display: inline-flex; align-items: center; justify-content: center;
  width: 28px; height: 28px; padding: 0; border: 0; border-radius: var(--atlas-radius-sm, 6px); background: none;
  color: var(--muted); cursor: pointer; opacity: 0.48;
  transition: opacity var(--atlas-motion-base) var(--atlas-ease-out), color var(--atlas-motion-base) var(--atlas-ease-out),
    scale var(--atlas-motion-fast) var(--atlas-ease-out);
}
@media (hover: hover) and (pointer: fine) {
  .page-undo-button:hover { opacity: 1; color: var(--text); }
}
.page-undo-button:focus-visible { opacity: 1; color: var(--text); outline: 2px solid var(--accent); outline-offset: 2px; }
.page-undo-button:active { opacity: 1; color: var(--accent); scale: var(--atlas-press); }
</style>
