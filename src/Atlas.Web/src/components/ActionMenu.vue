<script setup lang="ts">
import { computed, inject, ref, watch, type ComponentPublicInstance } from "vue";
import { DropdownMenuCheckboxItem, DropdownMenuContent, DropdownMenuGroup, DropdownMenuItem, DropdownMenuPortal, DropdownMenuRadioGroup, DropdownMenuRadioItem, DropdownMenuRoot, DropdownMenuSeparator, DropdownMenuTrigger } from "reka-ui";
import { useEditorTheme } from "../editor/editorSetup";
import { closeMenuWhenLost, focusFirstAction } from "../lib/menuFocus";
import { hostElement } from "../lib/templateRef";
import AtlasIcon from "./AtlasIcon.vue";
import { dialogLayersKey } from "./ui/dialogLayers";
import "./boards/canvas/boardPopover.css";
import "./actionMenu.css";

/**
 * The app's one action menu (board toolbars, the page tree, list rows) in the page editor's context-menu look (`md-context-popover md-context-menu`, the rows of
 * RequirementStateMenu): the board has no editor under it, so `openMarkdownContextPopover` cannot open one. reka-ui gives the
 * editor's keys (arrows, Home, End, Enter, Esc with the focus back on the trigger, a click outside). With `value` the rows are one
 * choice and the current one is checked, visibly as in the editor's menus. The slot is the trigger; `itemAttr` names the data
 * attribute carrying each row's id. `iconSize` draws a row's icon at its own size (text sizes show how big they are).
 * `actions` makes it „Co s tím jde dělat“ of a list row or a tree row (P2, P12): the look of the editor's suggestion list that the page and the
 * board open (icon tiles, titles, mono shortcuts, accent ink on the lit row), without a title (the owner's rule for action and choice menus);
 * a destructive row is red here as in every menu. The first row is lit when the menu opens; like ActionDisclosure it is not modal (a click
 * outside closes it and acts) and closes with the route and when its trigger scrolls away (lib/menuFocus.ts). Inside an AppDialog it stays
 * in the dialog's top layer and Esc closes only the menu (as ui/dropdown-menu does). ActionDisclosure wears the same classes and highlight
 * (actionMenu.css); the look itself loads with the app (@atlas/markdown/styles/menu.css), so a menu shown before any editor is styled.
 */
export interface ActionMenuItem { id: string; label: string; icon?: string; iconSize?: number; shortcut?: string; disabled?: boolean; destructive?: boolean;
  /** A switch among actions („Řadit abecedně“): a menuitemcheckbox with the check while on; selecting it is still `select`. */
  checked?: boolean;
  /** Rows of one group stay together; a thin line parts it from the previous group (the tree's menu: Otevřít, Vytvořit, Upravit, Koš). */
  group?: string }
const props = withDefaults(defineProps<{ items: readonly ActionMenuItem[]; label: string; itemAttr: string; value?: string; side?: "top" | "right" | "bottom" | "left"; align?: "start" | "center" | "end"; actions?: boolean }>(),
  { value: undefined, side: "top", align: "start", actions: false });
const open = defineModel<boolean>("open", { default: false });
const emit = defineEmits<{ select: [id: string]; closeAutoFocus: [event: Event] }>();
// The editor's tokens live on `.md-editor` with its theme; a menu in the body gets them the way the command panel does.
const theme = useEditorTheme();
const radio = computed(() => props.value !== undefined);
const trigger = ref<ComponentPublicInstance>();
closeMenuWhenLost(open, () => hostElement(trigger.value));
// A menu opened inside a native modal dialog is portalled into it (the body under the top layer is inert) and owns Esc there first.
const dialogLayers = inject(dialogLayersKey, undefined);
watch(open, (isOpen, _, onCleanup) => {
  if (isOpen && dialogLayers) onCleanup(dialogLayers.registerEscape(() => { open.value = false; }));
}, { immediate: true, flush: "sync" });
</script>

<template>
  <DropdownMenuRoot v-model:open="open" :modal="false">
    <DropdownMenuTrigger ref="trigger" as-child><slot /></DropdownMenuTrigger>
    <DropdownMenuPortal :to="dialogLayers?.target.value">
      <DropdownMenuContent :side="side" :align="align" :side-offset="6" :collision-padding="8" :aria-label="label" data-positioned
        class="md-editor atlas-document-editor md-context-popover md-context-menu md-context-menu-compact board-menu nodrag nopan nowheel" :class="[theme, { 'board-menu-actions': actions }]"
        @open-auto-focus="focusFirstAction" @close-auto-focus="emit('closeAutoFocus', $event)">
        <component :is="radio ? DropdownMenuRadioGroup : DropdownMenuGroup" :model-value="value" class="md-context-popover-body">
          <!-- The row's id under `itemAttr` (data-board-shape="rect"); a radio row also needs its `value`, a plain item must not get one. -->
          <template v-for="(item, index) in items" :key="item.id">
          <DropdownMenuSeparator v-if="index > 0 && item.group !== items[index - 1]!.group" class="board-menu-separator" />
          <component :is="radio ? DropdownMenuRadioItem : item.checked === undefined ? DropdownMenuItem : DropdownMenuCheckboxItem"
            v-bind="{ [itemAttr]: item.id, ...(radio && { value: item.id }), ...(!radio && item.checked !== undefined && { modelValue: item.checked }) }"
            class="md-context-menu-item" :class="{ 'board-menu-destructive': item.destructive }" :disabled="item.disabled" @select="emit('select', item.id)">
            <span v-if="item.icon" class="board-menu-icon"><AtlasIcon :name="item.icon" :size="item.iconSize ?? 16" /></span>{{ item.label }}<span v-if="item.shortcut" class="board-menu-key">{{ item.shortcut }}</span>
            <AtlasIcon v-if="radio ? item.id === value : item.checked" class="board-menu-check" name="check" :size="14" data-board-menu-check />
          </component>
          </template>
        </component>
      </DropdownMenuContent>
    </DropdownMenuPortal>
  </DropdownMenuRoot>
</template>

<style scoped>
/* The rows, their highlight and the destructive red: actionMenu.css, shared with ActionDisclosure. Icons of different sizes keep the labels on one line. */
.board-menu-icon { display: inline-grid; place-items: center; flex-shrink: 0; width: 18px; }
.board-menu-key { margin-left: auto; padding-left: 16px; color: var(--text-dim, var(--muted)); font-size: 12px; }
.board-menu-check { margin-left: auto; padding-left: 12px; box-sizing: content-box; color: var(--accent); }
.board-menu-key + .board-menu-check { margin-left: 0; }
.board-menu-separator { height: 1px; margin: 4px 6px; background: var(--line, var(--bg-3)); }
/* „Co s tím jde dělat“: the editor's suggestion list (suggestions.css). */
.board-menu-actions { width: 300px; max-width: calc(100vw - 16px); }
.board-menu-actions .md-context-menu-item { font-weight: 500; }
.board-menu-actions .md-context-menu-item[data-highlighted] { background: var(--bg-3, var(--state-hover-bg)); color: var(--chip-accent-fg, var(--accent)); }
.board-menu-actions .md-context-menu-item.board-menu-destructive[data-highlighted] { color: var(--error, var(--destructive)); }
.board-menu-actions .board-menu-icon { width: 28px; height: 28px; border: 1px solid var(--line); border-radius: 7px; color: var(--muted); }
.board-menu-actions [data-highlighted] .board-menu-icon { color: var(--chip-accent-fg, var(--accent)); }
.board-menu-actions .board-menu-key { font: 500 11px var(--font-mono, ui-monospace, monospace); color: var(--muted); }
</style>
