<script setup lang="ts">
import t from "../../../i18n/boards";
import { watch } from "vue";
import { PopoverAnchor, PopoverContent, PopoverPortal, PopoverRoot } from "reka-ui";
import { useEditorTheme } from "../../../editor/editorSetup";
import AtlasIcon from "../../AtlasIcon.vue";
import { rememberFocus } from "../../../lib/focusReturn";
import "../canvas/boardPopover.css";

/**
 * The frame of the board's pickers (page, YouTrack issues, contacts): the page editor's light popover (its link and `/Kontakt`
 * pickers), dropping from the middle of the canvas's top edge, without a backdrop, so the board the cards land on stays in sight.
 * Esc, the close button and a click outside cancel; the focus goes to the first field and back where it was.
 * Why not AppDialog: a modal over the whole page dims the board and asks a question; a picker only adds to what is in front of you.
 */
const props = defineProps<{ open: boolean; title: string; anchor?: HTMLElement | null }>();
const emit = defineEmits<{ close: [] }>();
const theme = useEditorTheme();
// The middle of the anchor's top edge (the window's without one): the new cards land in the middle of the canvas, below the panel.
const reference = {
  getBoundingClientRect() {
    const box = props.anchor?.getBoundingClientRect() ?? new DOMRect(0, 0, window.innerWidth, 0);
    return new DOMRect(box.left + box.width / 2, box.top + 12, 0, 0);
  },
};
function focusField(event: Event) {
  event.preventDefault();
  (event.target as HTMLElement).querySelector<HTMLElement>("input:not([type=checkbox])")?.focus({ preventScroll: true });
}
// Back to where the picker was opened from (the canvas with the new cards selected, or the rail's button): it has no trigger of its own.
let restoreFocus: (() => boolean) | null = null;
watch(() => props.open, open => { if (open) restoreFocus = rememberFocus(); }, { immediate: true, flush: "sync" });
function focusOpener(event: Event) {
  event.preventDefault();
  restoreFocus?.();
  restoreFocus = null;
}
</script>

<template>
  <div class="board-picker-frame">
  <!-- One root, where the panel stands in its owner; the portal takes the panel itself to the body. -->
  <PopoverRoot :open="open" @update:open="value => { if (!value) emit('close'); }">
    <PopoverAnchor :reference="reference" />
    <!-- forceMount: the portal is there in the first render, so a picker's first rows are too (P17); the content still follows `open`. -->
    <PopoverPortal force-mount>
      <PopoverContent class="md-editor atlas-document-editor md-context-popover board-picker nodrag nopan nowheel" :class="theme" side="bottom" align="center" :side-offset="0"
        :collision-padding="12" :aria-label="title" data-positioned data-board-picker @open-auto-focus="focusField" @close-auto-focus="focusOpener">
        <header class="board-picker-head">
          <span class="board-picker-title">{{ title }}</span>
          <button type="button" class="md-picker-close" :aria-label="t.pickerPanel.close" @click="emit('close')"><AtlasIcon name="close" :size="16" /></button>
        </header>
        <div class="board-picker-body"><slot /></div>
        <footer v-if="$slots.footer" class="board-picker-actions"><slot name="footer" /></footer>
      </PopoverContent>
    </PopoverPortal>
  </PopoverRoot>
  </div>
</template>

<style scoped>
/* Out of the owner's flow: as `display: contents` its empty anchor was a flex item, and each picker cost the board a 12 px gap. */
.board-picker-frame { position: absolute; width: 0; height: 0; }
.board-picker-head { display: flex; align-items: center; justify-content: space-between; gap: 8px; padding: 6px 6px 0 12px; }
.board-picker-title { color: var(--text-dim, var(--muted)); font-size: 12px; font-weight: 600; }
.board-picker-head .md-picker-close { display: grid; place-items: center; min-height: 0; border-radius: var(--radius-sm, 6px); }
.board-picker-body { display: grid; gap: 8px; padding: 4px 10px 10px; }
.board-picker-actions { display: flex; flex-wrap: wrap; justify-content: flex-end; gap: 6px; padding: 8px 10px 10px; border-top: 1px solid var(--line); }
</style>
