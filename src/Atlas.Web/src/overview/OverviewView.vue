<script setup lang="ts">
import { NodeViewWrapper, nodeViewProps } from "@tiptap/vue-3";
import { computed, toRaw } from "vue";
import { kindOfNode, overviewKind, type OverviewHost } from "./overviewKinds";
import { selectOverview } from "./overviewNode";
import t from "../i18n/overview";

/**
 * The overview block in the editor: its kind's card with the kind's bulk actions for its foot (one that changes the page only while the
 * editor is editable), a quiet placeholder for a kind not known here.
 */
const props = defineProps(nodeViewProps);
// The raw editor: node view props are deeply reactive, and a transaction built from a proxied state is refused.
const editor = toRaw(props.editor);
const kind = computed(() => overviewKind(kindOfNode(props.node.type.name, props.node.attrs) ?? ""));
const host = computed(() => (props.extension.options as { host?: OverviewHost }).host);
const actions = computed(() => (kind.value?.actions ?? []).filter(action => !action.pageOnly || editor.isEditable || !!action.readable?.(editor))
  .map(action => ({ id: action.id, label: action.label, icon: action.icon, run: () => action.run({ editor, items: kind.value!.collect(editor.state.doc) }) })));

// A click on the card's frame, header or empty room selects it as a table or an image is selected; its controls keep their own clicks.
// A reader's card is never selected.
const CONTROLS = "button, a, input, select, textarea, label, [role=slider], [role=button], [tabindex]";
function select(event: MouseEvent) {
  const pos = props.getPos(), control = (event.target as Element).closest?.(CONTROLS);
  if (!editor.isEditable || typeof pos !== "number" || (control && (event.currentTarget as Element).contains(control))) return;
  selectOverview(editor, pos);
}
</script>

<template>
  <NodeViewWrapper class="atlas-overview-node" :data-overview-kind="kind?.kind" @click="select">
    <component :is="kind.card" v-if="kind" :editor="editor" :host="host" :actions="actions" />
    <p v-else class="atlas-overview" contenteditable="false">{{ t.unavailable }}</p>
  </NodeViewWrapper>
</template>

<style>
/* Selected by a click, the arrows or the grip (OverviewArrows): the selected component's outline (P12), as a table's or a chart's. */
.atlas-overview-node.ProseMirror-selectednode { outline: var(--state-selected-outline, 2px solid var(--accent)); outline-offset: var(--focus-offset, 2px); }
/* The card is drawn from the first frame: the editor's `content-visibility: auto` stood it in as a 40px block until that frame (content.css),
   and the page below it then shifted down by the card's height (a layout shift of .03 on every page with a summary). */
.md-editor.paper .editor-body .ProseMirror > .atlas-overview-node[data-overview-kind] { content-visibility: visible; }
/* Inside a range selected across it: the text selection's tint over the card, which takes no text selection of its own. */
.ProseMirror .atlas-overview-node[data-in-selection] > * { --overview-selection: color-mix(in srgb, var(--accent) 28%, transparent);
  background-image: linear-gradient(var(--overview-selection), var(--overview-selection)); }
</style>
