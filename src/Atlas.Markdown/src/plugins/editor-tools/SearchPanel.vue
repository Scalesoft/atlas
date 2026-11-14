<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import type { Editor } from '@tiptap/core'
import { editorSearchState, findInEditor, replaceInEditor } from './index'
import { isMarkdownComposition, trackMarkdownComposition } from '../../core'
import { editorToolsStrings } from './runtimeStrings'

const props = defineProps<{ editor: Editor; close: () => void }>()
const state = editorSearchState(props.editor)
const term = ref(state.searchTerm); const replacement = ref(state.replaceTerm)
const caseSensitive = ref(state.caseSensitive); const wholeWord = ref(state.wholeWord); const useRegex = ref(state.useRegex)
const invalid = ref(false); const revision = ref(0)
/** What „Nahradit vše“ did, in the status line until the next search. */
const replaced = ref('')
const form = ref<HTMLFormElement | null>(null)
let composition: ReturnType<typeof trackMarkdownComposition> | undefined
onMounted(() => { if (form.value) composition = trackMarkdownComposition(form.value) })
onBeforeUnmount(() => composition?.destroy())
const refresh = () => { revision.value++; if (!props.editor.isEditable) replacement.value = '' }
props.editor.on('transaction', refresh)
props.editor.on('update', refresh)
const Observer = props.editor.view.dom.ownerDocument.defaultView?.MutationObserver
const observer = Observer ? new Observer(refresh) : undefined
observer?.observe(props.editor.view.dom, { attributes: true, attributeFilter: ['contenteditable'] })
onBeforeUnmount(() => { props.editor.off('transaction', refresh); props.editor.off('update', refresh); observer?.disconnect() })
const editable = computed(() => { void revision.value; return props.editor.isEditable })
const results = computed(() => { void revision.value; return editorSearchState(props.editor) })
function search() { replaced.value = ''; invalid.value = !findInEditor(props.editor, term.value, { caseSensitive: caseSensitive.value, wholeWord: wholeWord.value, useRegex: useRegex.value }) }
function replace(all = false) {
  const count = results.value.results.length
  if (!replaceInEditor(props.editor, replacement.value, all)) return
  if (all) replaced.value = editorToolsStrings.replacedStatus(count)
  // With no match left the pressed button disables and focus would fall to the page: the text takes it, so Ctrl+Z undoes the
  // replacement at once (the panel stays open, the editor is one of its roots).
  if (!editorSearchState(props.editor).results.length) props.editor.view.focus()
}
function navigate(previous = false) { if (results.value.results.length) previous ? props.editor.commands.goToPreviousResult() : props.editor.commands.goToNextResult() }
function enter(event: KeyboardEvent) {
  if (isMarkdownComposition(event, props.editor) || composition?.active()) return
  event.preventDefault(); navigate(event.shiftKey)
}
/** P3: the first Esc in a search clears the term and stays; the panel closes on the next one (contextToolbar leaves a marked field its Esc). */
function escape(event: KeyboardEvent) {
  if (!term.value || event.isComposing || isMarkdownComposition(event, props.editor) || composition?.active()) return
  event.preventDefault(); event.stopPropagation()
  term.value = ''; search()
}
function submit(event: Event) { event.preventDefault(); if (!composition?.active()) navigate() }
</script>

<template>
  <form ref="form" @submit="submit">
    <div class="md-context-fields">
      <label class="md-context-field md-context-field-wide">{{ editorToolsStrings.searchFieldLabel }}
        <input v-model="term" type="search" :data-md-own-escape="term ? '' : undefined" @input="search" @keydown.enter="enter" @keydown.esc="escape">
      </label>
      <label class="md-search-option"><input v-model="caseSensitive" type="checkbox" @change="search"> {{ editorToolsStrings.caseSensitiveLabel }}</label>
      <label class="md-search-option"><input v-model="wholeWord" type="checkbox" :disabled="useRegex" @change="search"> {{ editorToolsStrings.wholeWordLabel }}</label>
      <label class="md-search-option md-context-field-wide"><input v-model="useRegex" type="checkbox" @change="search"> {{ editorToolsStrings.regexLabel }}</label>
      <p v-if="useRegex" class="md-context-field-wide">{{ editorToolsStrings.regexInfo }}</p>
      <p v-if="invalid" class="md-context-field-wide" role="alert">{{ editorToolsStrings.regexInvalid }}</p>
      <p class="md-context-field-wide" role="status" aria-live="polite">{{ replaced || (results.results.length ? editorToolsStrings.resultsStatus((results.currentIndex ?? 0) + 1, results.results.length) : editorToolsStrings.noResults) }}</p>
      <div class="md-module-actions md-context-field-wide">
        <button type="button" :disabled="!results.results.length" @click="navigate(true)">{{ editorToolsStrings.previous }}</button>
        <button type="button" :disabled="!results.results.length" @click="navigate()">{{ editorToolsStrings.next }}</button>
      </div>
      <label v-if="editable" class="md-context-field md-context-field-wide">{{ editorToolsStrings.replaceWithLabel }}
        <input v-model="replacement" type="text">
      </label>
      <div v-if="editable" class="md-module-actions md-context-field-wide">
        <button type="button" :disabled="!results.results.length" @click="replace()">{{ editorToolsStrings.replace }}</button>
        <button type="button" :disabled="!results.results.length" @click="replace(true)">{{ editorToolsStrings.replaceAll }}</button>
      </div>
    </div>
  </form>
</template>

<style scoped>
.md-search-option { display: flex; align-items: center; gap: 6px; font-size: 12px; }
.md-search-option input { width: auto; }
p { margin: 0; font-size: 12px; color: var(--text-dim); }
</style>
