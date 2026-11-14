<script setup lang="ts">
import t from "../../../i18n/boards";
import { createSuggestionList } from "@atlas/markdown/core";
import "@atlas/markdown/styles/base.css";
import "@atlas/markdown/themes/forest.css";
import "@atlas/markdown/themes/midnight.css";
import "@atlas/markdown/themes/ocean.css";
import { computed, onBeforeUnmount, onMounted, ref, useId, watch } from "vue";
import { findCommands, type BoardCommand } from "../../../boards/boardCommands";
import { useEditorTheme } from "../../../editor/editorSetup";
import { listKeys, useSearchKeys } from "../../../lib/searchKeys";
import "./boardPopover.css";

/**
 * The board's command panel (objects spec §5.2): one searchable list of what the board can do, opened at the pointer by `/` or a
 * tap of Space, over a selection by Ctrl/⌘+K. Its list is the page editor's own suggestion list (the slash menu's look, keys and
 * motion: arrows, Home, End, Enter, Esc); only the search field above it is the board's, since on a board nothing is typed into a
 * text. The keys are every search's (P3, P8): the first Esc empties the search and the next one closes, Enter or Tab runs the first
 * match once something is typed (the untouched empty search runs nothing until the arrows move to a command). Placed inside the
 * canvas at `at`, kept in its room, above the point when there is no room below.
 *
 * Why its own component: the slash menu lives inside the editor and filters what is typed into the document; the board has no
 * document under the pointer. The entries come from boardCommands.ts, which reads the editor's slash registry.
 */
// `heading` names what is listed: the board's commands, or only its components (the rail's Komponenta, K).
const props = withDefaults(defineProps<{ commands: readonly BoardCommand[]; /** The board's items by name (itemCommands), listed after the commands for a typed search. */ finds?: readonly BoardCommand[]; at: { x: number; y: number }; room: { width: number; height: number }; heading?: string; /** Typed before the panel was in: its search starts with it. */ typed?: string }>(),
  { heading: () => t.panel.commands });
const emit = defineEmits<{ run: [id: string]; close: [] }>();

const WIDTH = 300, HEIGHT = 360, GAP = 8;
// The editor's theme tokens (they live on `.md-editor`, and the editor copies them to its own popups): the list's colours.
const theme = useEditorTheme();
const root = ref<HTMLElement>();
const host = ref<HTMLElement>();
const input = ref<HTMLInputElement>();
const query = ref(props.typed ?? "");
const active = ref("");
const listId = `board-commands-${useId()}`;
const list = createSuggestionList({
  document, label: t.panel.label(props.heading), heading: props.heading, emptyText: t.panel.empty, testId: "board-command-list",
  onSelect: id => emit("run", id), onActiveChange: id => { active.value = id; },
});
list.element.id = listId;

const MAX_FOUND = 8;
const shown = computed(() => [...findCommands(query.value, props.commands), ...(props.finds && query.value.trim().length >= 2 ? findCommands(query.value, props.finds).slice(0, MAX_FOUND) : [])]);
watch(shown, commands => list.update(commands.map(entry => ({ id: entry.id, title: entry.title, icon: `svg:${entry.icon}`, ...(entry.description && { description: entry.description }) }))),
  { immediate: true });

// Below the point, else above it; where neither fits (a short canvas), from the canvas's top and no taller than the canvas, so
// the search is never cut off.
const place = computed(() => {
  const { at, room } = props;
  const x = Math.max(GAP, Math.min(at.x, room.width - WIDTH - GAP));
  if (at.y + GAP + HEIGHT <= room.height || at.y < room.height / 2) return { left: `${x}px`, top: `${at.y + GAP}px` };
  if (at.y - GAP - HEIGHT >= GAP) return { left: `${x}px`, bottom: `${room.height - at.y + GAP}px` };
  return { left: `${x}px`, top: `${GAP}px`, maxHeight: `${Math.max(0, room.height - 2 * GAP)}px` };
});

const runActive = () => list.onKeyDown(new KeyboardEvent("keydown", { key: "Enter" }));
// The first Esc empties the search, the next one closes the panel; Enter or Tab runs the highlighted command once something is typed or the
// arrows (or the pointer) moved to one. The untouched list highlights none (the list selects its first row all along), so Enter runs nothing.
const keys = useSearchKeys({ text: () => query.value, clear: () => { query.value = ""; }, take: runActive, keepFocus: true, empty: () => true });
const lit = keys.lit;
function onKeydown(event: KeyboardEvent) {
  if (keys.onKeydown(event)) return;
  if (event.key === "Escape" || event.key === "Tab" || (event.key === "Backspace" && !query.value)) {
    event.preventDefault();
    emit("close");
  } else listKeys(event, keys, list);
}
function onPointerDown(event: PointerEvent) {
  if (!root.value?.contains(event.target as Node)) emit("close");
}
onMounted(() => {
  host.value?.append(list.element);
  input.value?.focus({ preventScroll: true });
  document.addEventListener("pointerdown", onPointerDown, true);
});
onBeforeUnmount(() => document.removeEventListener("pointerdown", onPointerDown, true));
</script>

<template>
  <div ref="root" class="md-suggestion-popup board-command-panel nodrag nopan nowheel" :class="['md-editor', theme, 'atlas-document-editor']" :style="place" :data-untouched="!lit || undefined" data-board-command-panel>
    <input ref="input" v-model="query" class="board-command-query" type="text" role="combobox" :aria-label="t.panel.search(heading)" aria-autocomplete="list"
      aria-expanded="true" :aria-controls="listId" :aria-activedescendant="(lit && active) || undefined" :placeholder="t.panel.placeholder(heading)" autocomplete="off" spellcheck="false"
      data-board-command-query @keydown="onKeydown" />
    <div ref="host" class="board-command-host" @mouseover="keys.move()" />
  </div>
</template>

<style scoped>
/* The slash menu's box (the editor's tokens and its entering motion) around the search and the editor's own list. */
.board-command-panel { position: absolute; z-index: 8; display: flex; flex-direction: column; width: min(300px, calc(100cqi - 16px)); max-height: 360px; overflow: hidden;
  border: 1px solid var(--line); border-radius: var(--radius-md, 8px); background: var(--paper, var(--surface)); box-shadow: var(--shadow-menu);
  animation: md-popover-enter var(--md-motion-menu) var(--md-motion-ease) both; }
.board-command-query { margin: 4px 4px 0; padding: 6px 10px; border: 0; border-bottom: 1px solid var(--line); border-radius: var(--radius-sm, 6px) var(--radius-sm, 6px) 0 0;
  background: transparent; color: var(--text); font-size: 13px; outline: none; }
.board-command-query:focus-visible { outline: 2px solid var(--accent); outline-offset: -2px; }
/* The list shrinks with a short canvas and scrolls inside the panel. */
.board-command-host { display: flex; flex: 1 1 auto; flex-direction: column; min-height: 0; }
/* Nothing typed or moved to yet: the list's selected first row is not drawn, as Enter takes none. */
.board-command-panel[data-untouched] :deep(.md-suggestion-list .row.on) { background: transparent; color: var(--text); }
.board-command-panel[data-untouched] :deep(.md-suggestion-list .row.on .ic) { color: var(--muted); }
.board-command-panel :deep(.md-suggestion-list) { flex: 1 1 auto; min-height: 0; width: auto; max-width: none; max-height: 312px; border: 0; box-shadow: none; animation: none; }
</style>
