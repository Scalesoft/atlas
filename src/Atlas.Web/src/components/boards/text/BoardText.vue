<script lang="ts">
import { computed, inject, onBeforeUnmount, ref, watch } from "vue";

/** The one text whose editor stays after its editing (the last entered): a return to it starts at once, and a long session does not
 * keep an editor alive for every text it ever edited. */
const kept = ref<string>();
</script>

<script setup lang="ts">
import { boardCanvasKey } from "../canvas/boardCanvasContext";
import { BoardMarkdown, BoardTextEditor, loadTextEditor, textEditorReady } from "./textEditorChunk";
import type { TextBinding } from "./textBinding";

/**
 * The text of a board item or connector: drawn at rest by `BoardMarkdown`, the static
 * render of the same DOM, and by the page editor only once a writer enters it (owner, 4.0: no live editor per card until then; a
 * board holds hundreds of texts and an editor costs a few hundred ms at a slow CPU). The editing starts from a click into the selected
 * card's text (the caret at the click, `ctx.editPoint`; the editor is built while that click's button is down, `ctx.entering`), a
 * double-click, Enter or F2; nothing moves when the editor takes over, and it
 * stays the text's element after the editing ends (read-only, following the binding) until another text is entered (`kept`). A
 * reader's selected text is selectable and copyable (contract J2.5). The default slot is the empty-text fallback.
 */
const props = defineProps<{ id: string; editing: boolean; binding: TextBinding; text?: string; inline?: boolean; ariaLabel?: string; placeholder?: string; editorClass?: string; textClass?: string }>();
const ctx = inject(boardCanvasKey)!;

const live = computed(() => kept.value === props.id || props.editing);
// Once entered, the editor stays (no rebuilding it at the next editing of the same text).
watch(live, now => { if (now) { kept.value = props.id; void loadTextEditor().catch(() => undefined); } }, { immediate: true });
// A writer pressing into the selected card's text: its click is about to edit it, the editor is built during the press (BoardCanvas).
watch(() => ctx.entering?.value === props.id, entered => { if (entered) kept.value = props.id; });
// A text that went (its item, the board closed) keeps nothing: reopened, it starts at rest again.
onBeforeUnmount(() => { if (kept.value === props.id) kept.value = undefined; });
const readable = computed(() => !ctx.canEdit.value && ctx.single.value === props.id);
</script>

<template>
  <BoardTextEditor v-if="live && (text || editing)" :class="[editing ? editorClass : textClass, { 'is-readable nopan': !ctx.canEdit.value }]" :active="editing" :at="editing ? ctx.editPoint.value : undefined" data-board-text :binding="binding" :draft-key="`board-text:${id}`" :host="ctx.blockHost"
    :inline="inline" :aria-label="ariaLabel" :placeholder="placeholder" @cancel="ctx.endEdit(id)" @commit="ctx.endEdit(id)" @unsaved="ctx.unsaved(id, $event)" />
  <BoardMarkdown v-if="text && (!live || !textEditorReady)" :class="[textClass, { 'is-readable nopan': readable }]" :text="text" :inline="inline" :aria-label="ariaLabel" data-board-text />
  <slot v-else-if="!text && !editing" />
</template>
