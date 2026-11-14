<script setup lang="ts">
import t from "../../i18n/components-personal";
import { nextTick, ref } from "vue";
import { maxNoteText, maxNoteTitle, type NoteData } from "../../personal/personalApi";
import BoardMarkdown from "../boards/text/BoardMarkdown.vue";
import BoardTextEditor from "../boards/text/BoardTextEditor.vue";
import type { TextBinding } from "../boards/text/textBinding";
import AtlasIcon from "../AtlasIcon.vue";

/**
 * A private note (spec 2026-10-29 §1) as a card: its title as one line of inline Markdown and its text as Markdown, each edited in
 * place with the page editor when `editable` (BoardTextEditor; Enter in the title, Esc or a click outside ends it), shown with the
 * page renderer otherwise (BoardMarkdown). An edit is emitted as the change of that one field. Why not ContactCard: a contact's
 * fields are a person's; a note has a title and free text. Why not a page: a note has no tree, history or publication.
 */
const props = withDefaults(defineProps<{ note: NoteData; editable?: boolean; startEditing?: boolean }>(), { editable: false, startEditing: false });
const emit = defineEmits<{ change: [fields: Partial<NoteData>] }>();

type Field = keyof NoteData;
const labels: Record<Field, string> = { title: t.note.titleLabel, text: t.note.textLabel };
const editing = ref<Field | null>(props.startEditing && props.editable ? "text" : null);
const root = ref<HTMLElement>();

function binding(field: Field): TextBinding {
  return { read: () => props.note[field], commit: markdown => save(field, markdown) };
}
function start(field: Field, event?: MouseEvent) {
  if (!props.editable || editing.value || (event?.target as HTMLElement | null)?.closest("a")) return;
  editing.value = field;
}
function stop() {
  const field = editing.value;
  editing.value = null;
  // Leaving with the keyboard keeps the keyboard on the field just edited.
  if (field) void nextTick(() => root.value?.querySelector<HTMLElement>(`[data-note-field="${field}"]`)?.focus({ preventScroll: true }));
}
function save(field: Field, markdown: string) {
  if (editing.value !== field) return;
  stop();
  const value = field === "title" ? markdown.trim() : markdown.trimEnd();
  // An empty title goes back to the saved one: a note always has a name.
  if (value !== props.note[field] && (field === "text" || value)) emit("change", { [field]: value });
}
</script>

<template>
  <article ref="root" class="note-card" :class="{ 'is-editable': editable }" data-note-card>
    <div class="note-field note-title" :class="{ 'is-editing': editing === 'title' }" :tabindex="editable && editing !== 'title' ? 0 : undefined"
      :role="editable && editing !== 'title' ? 'button' : undefined" :aria-label="editable && editing !== 'title' ? t.note.edit(labels.title) : undefined"
      data-note-field="title" @click="start('title', $event)" @keydown.enter.self="start('title')" @keydown.space.self.prevent="start('title')">
      <BoardTextEditor v-if="editing === 'title'" :binding="binding('title')" inline :max="maxNoteTitle" :aria-label="labels.title" :placeholder="t.note.titlePlaceholder" @cancel="stop" />
      <BoardMarkdown v-else :key="note.title" class="note-value" :text="note.title" inline :aria-label="labels.title" />
    </div>
    <div class="note-field note-text" :class="{ 'is-editing': editing === 'text', 'is-empty': !note.text }" :tabindex="editable && editing !== 'text' ? 0 : undefined"
      :role="editable && editing !== 'text' ? 'button' : undefined" :aria-label="editable && editing !== 'text' ? t.note.edit(labels.text) : undefined"
      data-note-field="text" @click="start('text', $event)" @keydown.enter.self="start('text')" @keydown.space.self.prevent="start('text')">
      <BoardTextEditor v-if="editing === 'text'" :binding="binding('text')" :max="maxNoteText" :aria-label="labels.text" :placeholder="t.note.textPlaceholder" @cancel="stop" />
      <BoardMarkdown v-else-if="note.text" :key="note.text" class="note-value" :text="note.text" :aria-label="labels.text" />
      <span v-else class="note-placeholder"><AtlasIcon name="plus" :size="13" />{{ editable ? t.note.writeText : t.note.noText }}</span>
    </div>
  </article>
</template>

<style scoped>
.note-card { display: grid; gap: 4px; min-width: 0; color: var(--text); container-type: inline-size; }
.note-field { min-width: 0; border-radius: 6px; overflow-wrap: anywhere; }
.note-title { font-size: 15px; font-weight: 600; }
.note-text { font-size: 14px; }
/* A changed value fades in where the old one was; nothing moves. */
.note-value { display: block; animation: st-fade-in var(--atlas-motion-base) var(--atlas-ease-out); }
.note-field :deep(.board-text-editor) { animation: st-fade-in var(--atlas-motion-fast) var(--atlas-ease-out); }
.is-editable .note-field:not(.is-editing) { cursor: text; transition: background-color var(--atlas-motion-fast) var(--atlas-ease-out); }
@media (hover: hover) and (pointer: fine) { .is-editable .note-field:not(.is-editing):hover { background: var(--hover); } }
.note-field:focus-visible { outline: 2px solid var(--accent); outline-offset: 2px; }
.note-placeholder { display: inline-flex; align-items: center; gap: 4px; color: var(--muted); font-size: 12.5px; }
</style>
