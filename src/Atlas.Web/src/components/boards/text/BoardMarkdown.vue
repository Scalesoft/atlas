<script setup lang="ts">
import t from "../../../i18n/boards";
import { computed } from "vue";
import { MarkdownRenderer } from "@atlas/markdown/vue";
import "@atlas/markdown/styles/base.css";
import "@atlas/markdown/themes/ocean.css";
import "@atlas/markdown/themes/forest.css";
import "@atlas/markdown/themes/midnight.css";
import { boardBlockPlugins, boardTextDocument, inertBlockHost } from "../../../boards/editorBlocks";
import { useEditorTheme } from "../../../editor/editorSetup";
import { boardLinkPolicy, escapeInlineMarkdown, isPlainBoardText } from "./boardMarkdownText";
import "./boardTextSurface.css";

/**
 * A board text shown at rest (spec §3.4): the Markdown of a note, a text, a shape, a frame title or a connector label, read as a page
 * reads Markdown (`boardTextDocument`, the editor's dialect included) and rendered by the editor's static renderer with the page's
 * plugins, exactly as `BoardTextEditor` edits it, so starting to edit does not move a line. Safe as a page: the renderer takes no
 * HTML from the input, and links other than http(s), mailto and Atlas become text. `inline` (labels, titles) renders one line of
 * inline Markdown: a start that would open a block stays text.
 * The text takes the font size of its container (the canvas sets it: auto-fit notes, s–xl); headings scale with it.
 * A plain text (isPlainBoardText) is drawn as the renderer's own DOM for one paragraph, without an editor behind it (BD2-1).
 * Why not PageCard's excerpt or the page renderer: those show plain text or a rich document, a board text is Markdown.
 */
const props = withDefaults(defineProps<{ text: string; inline?: boolean; ariaLabel?: string }>(), { inline: false, ariaLabel: t.markdown.label });

const theme = useEditorTheme();
const value = computed(() => boardTextDocument(props.inline ? escapeInlineMarkdown(props.text) : props.text));
const plain = computed(() => isPlainBoardText(props.text));
const plugins = [...boardBlockPlugins(inertBlockHost), boardLinkPolicy];
</script>

<template>
  <div v-if="plain" :class="['md-editor readonly md-renderer paper', theme, 'atlas-document-editor', 'board-text', 'is-plain', { 'is-inline': inline }]" role="document" :aria-label="ariaLabel" data-board-markdown>
    <div><div><div class="paper-inner"><div class="editor-body"><div class="ProseMirror tiptap" contenteditable="false"><p>{{ text }}</p></div></div></div></div></div>
  </div>
  <MarkdownRenderer v-else :value="value" profile="rich" :ariaLabel="ariaLabel" :plugins="plugins" variant="document"
    :class="[theme, 'atlas-document-editor', 'board-text', { 'is-inline': inline }]" data-board-markdown />
</template>
