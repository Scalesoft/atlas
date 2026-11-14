<script setup lang="ts">
import { computed } from "vue";
import AppDialog from "./AppDialog.vue";
import t from "../i18n/shell-more";
import { helpSections } from "../keyboard/shortcuts";
import { editorKeymap as exportedEditorKeymap, type EditorKeymapEntry } from "../keyboard/editorKeymap";
import { boardKeymap as exportedBoardKeymap } from "../boards/boardKeymap";
import { useModules } from "../composables/useModules";
import { requirementKeymap as exportedRequirementKeymap } from "../projector/requirementKeymap";

// „Klávesové zkratky“ (? or Ctrl+/): every shortcut of the application by context, from the one registry (keyboard/shortcuts.ts),
// the editor's own keymap under „Editor“, with the projector module the requirement blocks' keys under „Požadavky“ and with the
// module Boards the board's under „Tabule“. It only lists; the keys themselves belong to App.vue's listener and the widgets.
const props = withDefaults(defineProps<{ open: boolean; editorKeymap?: readonly EditorKeymapEntry[]; requirementKeymap?: readonly EditorKeymapEntry[]; boardKeymap?: readonly EditorKeymapEntry[];
  /** An assistant can answer; without one the help leaves out Ctrl J and the AI wording. */
  ai?: boolean }>(),
  { editorKeymap: () => exportedEditorKeymap, requirementKeymap: undefined, boardKeymap: undefined, ai: true });
defineEmits<{ close: [] }>();
const modules = useModules();
const sections = computed(() => helpSections(props.editorKeymap, props.requirementKeymap ?? (modules.enabled("projector") ? exportedRequirementKeymap : []),
  props.boardKeymap ?? (modules.enabled("boards") ? exportedBoardKeymap : []), props.ai));
</script>

<template>
  <AppDialog class="keyboard-help-dialog" :title="t.keyboardHelp.title" size="wide" :open="open" @close="$emit('close')">
    <div class="keyboard-help">
      <section v-for="section in sections" :key="section.title" class="keyboard-help-section" :aria-labelledby="`keyboard-help-${section.title}`">
        <h3 :id="`keyboard-help-${section.title}`">{{ section.title }}</h3>
        <dl>
          <div v-for="entry in section.entries" :key="`${entry.keys}:${entry.label}`" class="keyboard-help-row">
            <dt><template v-for="(part, index) in entry.keys.split(/, (?:pak|then) /)" :key="index"><span v-if="index" class="keyboard-help-then">{{ t.keyboardHelp.then }}</span><kbd>{{ part }}</kbd></template></dt>
            <dd>{{ entry.label }}</dd>
          </div>
        </dl>
      </section>
      <p class="keyboard-help-note">{{ t.keyboardHelp.noteBefore }}<kbd>Ctrl K</kbd>{{ t.keyboardHelp.noteAnd }}<kbd>&gt;</kbd>{{ t.keyboardHelp.noteEnd }}</p>
    </div>
  </AppDialog>
</template>

<style scoped>
/* AppDialog already animates itself (opacity and transform); motion.css's `atlas-reveal` keyframes on every dialog[open] ran on top of that. The search dialog is excluded there, as this one is here. */
:global(.atlas dialog.keyboard-help-dialog[open]) { animation: none; }
/* The scroll area is focusable (keyboard scrolling): the shared ring, inside because the dialog clips it. */
.keyboard-help:focus-visible { outline: var(--focus-ring); outline-offset: -2px; }
.keyboard-help { display: grid; grid-template-columns: repeat(auto-fit, minmax(min(100%, 250px), 1fr)); align-items: start; gap: 20px 32px; max-height: min(72dvh, 680px); padding: 4px 20px 20px; overflow: auto; }
.keyboard-help-section h3 { margin: 0 0 8px; color: var(--muted); font-size: 12px; font-weight: 600; letter-spacing: .02em; text-transform: uppercase; }
.keyboard-help-section dl { display: grid; gap: 6px; margin: 0; }
.keyboard-help-row { display: grid; grid-template-columns: minmax(96px, auto) 1fr; gap: 12px; align-items: baseline; }
.keyboard-help-row dt { display: flex; flex-wrap: wrap; align-items: baseline; gap: 4px; }
.keyboard-help-then { color: var(--muted); font-size: 12px; }
.keyboard-help-row dd { margin: 0; color: var(--text); font-size: 13px; }
.keyboard-help kbd { padding: 1px 6px; border: 1px solid var(--line); border-radius: var(--atlas-radius-sm); color: var(--text); background: var(--surface-muted, var(--hover)); font: 500 12px/1.6 inherit; white-space: nowrap; }
.keyboard-help-note { grid-column: 1 / -1; margin: 0; color: var(--muted); font-size: 12px; }
</style>
