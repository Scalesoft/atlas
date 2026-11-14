<script setup lang="ts">
import t from "../../../i18n/boards";
import { computed, ref } from "vue";
import { PopoverContent, PopoverPortal, PopoverRoot, PopoverTrigger } from "reka-ui";
import { boardColors, type BoardColor, type BoardEdgeState, type BoardPropsPatch, type BoardThickness } from "../../../boards/boardApi";
import { blockLayoutOf, propsOf, type Alignment } from "../../../boards/boardFlow";
import { directionOf, directionPatch, type ConnectorDirection } from "../../../boards/boardConnectorStyle";
import type { BoardViewItem } from "../../../boards/boardSession";
import { useEditorTheme } from "../../../editor/editorSetup";
import AtlasIcon from "../../AtlasIcon.vue";
import AppearanceColorPicker from "../../settings/AppearanceColorPicker.vue";
import { colorAttributes, colorLabel } from "./boardPalette";
import ActionMenu, { type ActionMenuItem } from "../../ActionMenu.vue";
import { isBoardCard } from "../../../boards/objectViews";
import { onlyCards } from "../../../boards/boardCommands";
import "./boardPopover.css";

/**
 * The floating toolbar above the selection (spec §3.2): colour (every kind), shape type, text size and alignment, a connector's route
 * and quick styles (direction, flow, dash, thickness — for every selected connector), state and label, a frame's „Vybrat obsah“, a page card's display and detail, a group's collapse and ungrouping, order, Otevřít and delete; for several
 * items the common ones plus grouping, align and distribute.
 * It is the page editor's context toolbar (`md-context-toolbar`, its `.bb` buttons and keys: arrows, Home and End walk the buttons,
 * Esc goes back to the board) with the editor's context menus (ActionMenu); it travels with the selection. Every action is one
 * event, which the canvas turns into one batch.
 */
/** `arrangeable`: the host lays out cards of the selection („Uspořádat jako architekturu“, a deployment board). */
const props = defineProps<{ items: readonly BoardViewItem[]; canEdit: boolean; arrangeable?: boolean }>();
const emit = defineEmits<{
  /** `run`: the palette's opening; the colours tried in it (arrows walk the swatches and set each) are one undo step. */
  style: [patch: { color?: BoardColor | null; props?: BoardPropsPatch }, run?: string];
  reorder: [where: "front" | "back"]; duplicate: []; remove: []; open: []; editText: [];
  align: [how: Alignment]; distribute: [axis: "horizontal" | "vertical"]; layout: [];
  group: []; ungroup: []; collapse: [collapsed: boolean]; detail: []; selectMembers: [];
}>();
const theme = useEditorTheme();

const single = computed(() => (props.items.length === 1 ? props.items[0] : undefined));
/** Only connectors selected (one or several): their quick styles show, read from the first, applied to all. */
const connectors = computed(() => props.items.length > 0 && props.items.every(item => item.kind === "connector"));
const kind = computed(() => (connectors.value ? "connector" : single.value?.kind));
const removal = computed(() => (onlyCards(props.items) ? t.toolbar.removeFromBoard : t.toolbar.delete));
const first = computed(() => single.value ?? (connectors.value ? props.items[0] : undefined));
const options = computed(() => (first.value ? propsOf(first.value) : {}));
// Every kind takes a colour (owner, 27. 10.): a card shows it as a stripe and tint, a frame as its area and name.
const current = computed(() => props.items.find(item => item.color)?.color);
/** Editor components only (owner, 28. 10.): their colour is a background, from the appearance palette of spaces, tone by tone. */
const backgrounds = computed(() => props.items.length > 0 && props.items.every(item => blockLayoutOf(item)));
const paletteLabel = computed(() => (backgrounds.value ? t.toolbar.background : t.toolbar.color));
const noColour = computed(() => (backgrounds.value ? t.toolbar.noBackground : t.toolbar.noColor));
/** The trigger names what is set now: „Pozadí: Modrá · tmavá“, „Barva: Žlutá“, „Pozadí: Bez pozadí“. */
const colorTitle = computed(() => `${paletteLabel.value}: ${current.value ? colorLabel(current.value) : noColour.value}`);
// The picker of a component's background (the spaces' AppearanceColorPicker): its leading options come first in the quick row; „none“ is „Bez pozadí“.
const backgroundLeading = [{ value: "none", label: t.toolbar.noBackground }, { value: "white", label: colorLabel("white") }, { value: "black", label: colorLabel("black") }];
let paletteRun = 0;
// Beside the toolbar where there is room for it; on a phone below it, where the popover can shift along the screen's width.
const paletteSide = ref<"left" | "bottom">("left");
function paletteOpened(open: boolean) {
  if (!open) return;
  paletteRun++;
  paletteSide.value = window.innerWidth < 768 ? "bottom" : "left";
}
const background = computed({
  get: () => current.value ?? "none",
  set: (value: string) => emit("style", { color: value === "none" ? null : value as BoardColor }, `palette#${paletteRun}`),
});
/** Opening the palette puts the focus on the current swatch (the one in the tab order), as the roving focus of a radio group expects. */
function focusCurrent(event: Event) {
  event.preventDefault();
  // The checked swatch: the board's palette marks it by its tab stop, the shared picker's radio group by data-active (Reka moves
  // into it from the group); none checked there (a mixed selection), the first swatch.
  const body = event.target as HTMLElement | null;
  (body?.querySelector<HTMLElement>("[role=radio][data-active], [role=radio][tabindex='0']") ?? body?.querySelector<HTMLElement>("[role=radio]"))?.focus({ preventScroll: true });
}
const directions: ActionMenuItem[] = [
  { id: "forward", label: t.toolbar.oneWay, icon: "arrow-right" }, { id: "backward", label: t.toolbar.reverse, icon: "arrow-left" },
  { id: "both", label: t.toolbar.twoWay, icon: "arrow-right-left" }, { id: "none", label: t.toolbar.noArrows, icon: "minus" },
];
const direction = computed(() => pick(directions, directionOf(options.value)));
const thicknesses: ActionMenuItem[] = [{ id: "s", label: t.toolbar.thin, icon: "minus", iconSize: 12 }, { id: "m", label: t.toolbar.medium, icon: "minus", iconSize: 16 }, { id: "l", label: t.toolbar.thick, icon: "minus", iconSize: 20 }];
const thickness = computed(() => pick(thicknesses, options.value.thickness ?? "m"));
/** The palette's keys: arrows walk the swatches in their six columns (Home, End to the ends), Enter or Space picks one. */
function walkPalette(event: KeyboardEvent) {
  const swatches = [...(event.currentTarget as HTMLElement).querySelectorAll<HTMLButtonElement>("button")];
  const at = swatches.indexOf(event.target as HTMLButtonElement);
  const moves: Record<string, number> = { ArrowLeft: -1, ArrowRight: 1, ArrowUp: -6, ArrowDown: 6 };
  const next = event.key === "Home" ? 0 : event.key === "End" ? swatches.length - 1 : at < 0 || moves[event.key] === undefined ? -1 : Math.min(swatches.length - 1, Math.max(0, at + moves[event.key]!));
  if (next < 0) return;
  event.preventDefault();
  swatches[next]?.focus();
}
// A size's letter is drawn at its size; a note's automatic size fits its text.
const sizeRows = { auto: { id: "auto", label: t.toolbar.automatic, icon: "maximize-2" }, s: { id: "s", label: t.toolbar.small, icon: "type", iconSize: 12 }, m: { id: "m", label: t.toolbar.medium, icon: "type", iconSize: 14 },
  l: { id: "l", label: t.toolbar.large, icon: "type", iconSize: 16 }, xl: { id: "xl", label: t.toolbar.largest, icon: "type", iconSize: 18 } } satisfies Record<string, ActionMenuItem>;
const sizes = computed<ActionMenuItem[]>(() => (kind.value === "note" ? [sizeRows.auto, sizeRows.s, sizeRows.m, sizeRows.l] : [sizeRows.s, sizeRows.m, sizeRows.l, sizeRows.xl]));
const shapes: ActionMenuItem[] = [{ id: "rect", label: t.toolbar.rect }, { id: "rounded", label: t.toolbar.rounded }, { id: "ellipse", label: t.toolbar.ellipse }, { id: "diamond", label: t.toolbar.diamond }];
// „git-branch“ is the registry's line with a bend; AtlasIcon's „workflow“ is the people icon of a space's team (BD-6).
const routes: ActionMenuItem[] = [{ id: "elbow", label: t.toolbar.elbow, icon: "git-branch" }, { id: "curved", label: t.toolbar.curved, icon: "activity" }, { id: "straight", label: t.toolbar.straight, icon: "minus" }];
const states: ActionMenuItem[] = [{ id: "none", label: t.toolbar.noStatus, icon: "minus" }, { id: "waiting", label: t.toolbar.waiting, icon: "hourglass" }, { id: "done", label: t.toolbar.done, icon: "circle-check" }, { id: "blocked", label: t.toolbar.blocked, icon: "octagon-x" }];
/** The row of `list` for a stored value; the first (the default) for none or an unknown one. */
const pick = (list: readonly ActionMenuItem[], value: unknown) => list.find(item => item.id === value) ?? list[0]!;
const shape = computed(() => pick(shapes, options.value.shape));
const size = computed(() => pick(sizes.value, options.value.fontSize ?? (kind.value === "note" ? "auto" : "m")));
const route = computed(() => pick(routes, options.value.route));
const state = computed(() => pick(states, options.value.state));
const groupable = computed(() => props.items.filter(item => item.kind !== "connector").length > 1);
const alignments = computed<ActionMenuItem[]>(() => [
  { id: "left", label: t.toolbar.alignLeft, icon: "align-left" }, { id: "center", label: t.toolbar.alignCenter, icon: "align-center" }, { id: "right", label: t.toolbar.alignRight, icon: "align-right" },
  { id: "top", label: t.toolbar.alignTop, icon: "arrow-up" }, { id: "middle", label: t.toolbar.alignMiddle, icon: "minus" }, { id: "bottom", label: t.toolbar.alignBottom, icon: "arrow-down" },
  { id: "horizontal", label: t.toolbar.distributeH, icon: "arrow-right-left", disabled: props.items.length < 3 },
  { id: "vertical", label: t.toolbar.distributeV, icon: "menu", disabled: props.items.length < 3 },
  ...(props.arrangeable ? [{ id: "architecture", label: t.toolbar.arrange, icon: "layout-dashboard" }] : []),
]);
const alignLabels = { left: t.toolbar.textLeft, center: t.toolbar.textCenter, right: t.toolbar.textRight } as const;
const setProps = (patch: BoardPropsPatch) => emit("style", { props: patch });
const isAlignment = (how: string): how is Alignment => ["left", "center", "right", "top", "middle", "bottom"].includes(how);
function arrange(how: string) {
  if (how === "architecture") emit("layout");
  else if (how === "horizontal" || how === "vertical") emit("distribute", how);
  else if (isAlignment(how)) emit("align", how);
}

/** The editor's toolbar keys: arrows, Home and End walk the buttons in a ring; they never reach the board, which would move the selection. */
function walk(event: KeyboardEvent) {
  if (!["ArrowLeft", "ArrowRight", "Home", "End"].includes(event.key) || !(event.target as Element).matches("button")) return;
  const buttons = [...(event.currentTarget as HTMLElement).querySelectorAll<HTMLButtonElement>("button:not(:disabled)")];
  const at = buttons.indexOf(event.target as HTMLButtonElement);
  const next = event.key === "Home" ? 0 : event.key === "End" ? buttons.length - 1 : (at + (event.key === "ArrowLeft" ? buttons.length - 1 : 1)) % buttons.length;
  event.preventDefault();
  event.stopPropagation();
  buttons[next]?.focus({ preventScroll: true });
}
</script>

<template>
  <div class="md-editor atlas-document-editor md-context-toolbar board-toolbar" :class="theme" role="toolbar"
    :aria-label="single ? t.toolbar.editSingle : t.toolbar.editMany(items.length)" data-board-context-toolbar @keydown="walk">
    <template v-if="canEdit">
      <PopoverRoot @update:open="paletteOpened">
        <PopoverTrigger as-child>
          <button type="button" class="bb" :aria-label="colorTitle" :title="colorTitle" data-board-color-trigger><span class="board-swatch" :class="{ 'board-swatch-none': !current }" v-bind="colorAttributes(current)" /></button>
        </PopoverTrigger>
        <PopoverPortal>
          <!-- Beside the toolbar (left of its first button), over neither the selection it colours nor the other buttons; on a phone below it. -->
          <PopoverContent class="md-editor atlas-document-editor md-context-popover board-palette nodrag nopan nowheel" :class="theme" :side="paletteSide" align="center" :side-offset="8" :collision-padding="8"
            :aria-label="paletteLabel" data-positioned @open-auto-focus="focusCurrent">
            <!-- Not the editor's popover body: its button reset would take the shared tiles' colours. -->
            <div v-if="backgrounds" class="board-background-body">
              <AppearanceColorPicker v-model="background" :label="t.toolbar.basicColors" :leading="backgroundLeading" compact />
            </div>
            <div v-else role="radiogroup" :aria-label="paletteLabel" class="md-context-popover-body board-palette-grid" @keydown="walkPalette">
              <button type="button" role="radio" class="board-swatch board-swatch-option board-swatch-none" :aria-checked="!current" :aria-label="noColour" :title="noColour"
                :tabindex="current && boardColors.includes(current as never) ? -1 : 0" data-board-color-none @click="emit('style', { color: null })" />
              <button v-for="color in boardColors" :key="color" type="button" role="radio" class="board-swatch board-swatch-option" v-bind="colorAttributes(color)"
                :aria-checked="current === color" :aria-label="colorLabel(color)" :title="colorLabel(color)" :tabindex="current === color ? 0 : -1" :data-board-color="color" @click="emit('style', { color })" />
            </div>
          </PopoverContent>
        </PopoverPortal>
      </PopoverRoot>

      <ActionMenu v-if="kind === 'shape'" :items="shapes" :value="shape.id" :label="t.toolbar.shape" item-attr="data-board-shape" @select="setProps({ shape: $event as never })">
        <button type="button" class="bb" :aria-label="t.toolbar.shapeOf(shape.label)" :title="t.toolbar.shapeOf(shape.label)" data-board-shape-menu><AtlasIcon name="box" :size="16" /><AtlasIcon name="chevron-down" :size="12" /></button>
      </ActionMenu>

      <template v-if="kind === 'note' || kind === 'text' || kind === 'shape'">
        <ActionMenu :items="sizes" :value="size.id" :label="t.toolbar.fontSize" item-attr="data-board-font" @select="setProps({ fontSize: $event as never })">
          <button type="button" class="bb" :aria-label="t.toolbar.fontSizeOf(size.label)" :title="t.toolbar.fontSizeOf(size.label)" data-board-font-menu><AtlasIcon name="type" :size="16" /><AtlasIcon name="chevron-down" :size="12" /></button>
        </ActionMenu>
        <template v-if="kind !== 'note'">
          <button v-for="align in (['left', 'center', 'right'] as const)" :key="align" type="button" class="bb" :aria-pressed="(options.align ?? 'left') === align"
            :aria-label="alignLabels[align]" :title="alignLabels[align]" :data-board-align-text="align" @click="setProps({ align })">
            <AtlasIcon :name="`align-${align}`" :size="16" />
          </button>
        </template>
        <button v-if="kind === 'text'" type="button" class="bb" :aria-pressed="!!options.bold" :aria-label="t.toolbar.bold" :title="t.toolbar.bold" data-board-bold @click="setProps({ bold: options.bold ? null : true })"><AtlasIcon name="bold" :size="16" /></button>
      </template>

      <template v-if="kind === 'connector'">
        <ActionMenu :items="routes" :value="route.id" :label="t.toolbar.route" item-attr="data-board-route" @select="setProps({ route: $event as never })">
          <button type="button" class="bb" :aria-label="t.toolbar.routeOf(route.label)" :title="t.toolbar.routeOf(route.label)" data-board-route-menu><AtlasIcon :name="route.icon!" :size="16" /><AtlasIcon name="chevron-down" :size="12" /></button>
        </ActionMenu>
        <!-- Quick styles (owner, 27. 10.): direction, flow, dash and thickness, for every selected connector; the next new one takes them. -->
        <ActionMenu :items="directions" :value="direction.id" :label="t.toolbar.direction" item-attr="data-board-direction" @select="setProps(directionPatch($event as ConnectorDirection))">
          <button type="button" class="bb" :aria-label="t.toolbar.directionOf(direction.label)" :title="t.toolbar.directionOf(direction.label)" data-board-direction-menu><AtlasIcon :name="direction.icon!" :size="16" /><AtlasIcon name="chevron-down" :size="12" /></button>
        </ActionMenu>
        <button type="button" class="bb" :aria-pressed="!!options.animated" :aria-label="t.toolbar.animated" :title="t.toolbar.animated" data-board-animated
          @click="setProps({ animated: options.animated ? null : true })"><AtlasIcon name="activity" :size="16" /></button>
        <button type="button" class="bb" :aria-pressed="options.dash === 'dashed'" :aria-label="t.toolbar.dashed" :title="t.toolbar.dashed" data-board-dash @click="setProps({ dash: options.dash === 'dashed' ? 'solid' : 'dashed' })"><AtlasIcon name="ellipsis" :size="16" /></button>
        <ActionMenu :items="thicknesses" :value="thickness.id" :label="t.toolbar.thickness" item-attr="data-board-thickness" @select="setProps({ thickness: $event === 'm' ? null : $event as BoardThickness })">
          <button type="button" class="bb" :aria-label="t.toolbar.thicknessOf(thickness.label)" :title="t.toolbar.thicknessOf(thickness.label)" data-board-thickness-menu><AtlasIcon name="minus" :size="thickness.iconSize" /><AtlasIcon name="chevron-down" :size="12" /></button>
        </ActionMenu>
        <template v-if="single">
          <ActionMenu :items="states" :value="state.id" :label="t.toolbar.status" item-attr="data-board-state" @select="setProps({ state: $event === 'none' ? null : $event as BoardEdgeState })">
            <button type="button" class="bb" :aria-label="t.toolbar.statusOf(state.label)" :title="t.toolbar.statusOf(state.label)" data-board-state-menu><AtlasIcon :name="state.icon!" :size="16" /><AtlasIcon name="chevron-down" :size="12" /></button>
          </ActionMenu>
          <button type="button" class="bb" :aria-label="t.toolbar.editLabel" :title="t.toolbar.editLabel" data-board-edit-label @click="emit('editText')"><AtlasIcon name="pencil" :size="16" /></button>
        </template>
      </template>

      <button v-if="kind === 'page' && (options.display === 'card' || options.display === 'preview')" type="button" class="bb" :aria-pressed="options.display === 'preview'" :aria-label="options.display === 'preview' ? t.toolbar.showCard : t.toolbar.showPreview" :title="options.display === 'preview' ? t.toolbar.showCard : t.toolbar.showPreview" data-board-display
        @click="setProps({ display: options.display === 'preview' ? 'card' : 'preview' })"><AtlasIcon :name="options.display === 'preview' ? 'file-text' : 'eye'" :size="16" /></button>

      <template v-if="kind === 'group'">
        <button type="button" class="bb" :aria-pressed="!!options.collapsed" :aria-label="options.collapsed ? t.toolbar.expand : t.toolbar.collapse" :title="options.collapsed ? t.toolbar.expand : t.toolbar.collapse" data-board-collapse @click="emit('collapse', !options.collapsed)">
          <AtlasIcon :name="options.collapsed ? 'chevrons-up-down' : 'chevrons-down-up'" :size="16" /></button>
        <button type="button" class="bb" :aria-label="t.toolbar.ungroup" :title="t.toolbar.ungroupTitle" data-board-ungroup @click="emit('ungroup')"><AtlasIcon name="folder-input" :size="16" /></button>
      </template>
      <button v-if="groupable" type="button" class="bb" :aria-label="t.toolbar.group" :title="t.toolbar.groupTitle" data-board-group @click="emit('group')"><AtlasIcon name="folder-plus" :size="16" /></button>

      <ActionMenu v-if="items.length > 1 && !connectors" :items="alignments" :label="t.toolbar.align" item-attr="data-board-align" @select="arrange">
        <button type="button" class="bb" :aria-label="t.toolbar.alignDistribute" :title="t.toolbar.alignDistribute" data-board-align-menu><AtlasIcon name="align-left" :size="16" /><AtlasIcon name="chevron-down" :size="12" /></button>
      </ActionMenu>
      <span class="sep" role="separator" />
    </template>
    <button v-if="kind === 'frame' || kind === 'group'" type="button" class="bb" :aria-label="t.toolbar.selectContents" :title="t.toolbar.selectContentsTitle" data-board-select-members
      @click="emit('selectMembers')"><AtlasIcon name="box" :size="16" /></button>
    <button v-if="kind === 'page'" type="button" class="bb" :aria-label="t.toolbar.openDetail" :title="t.toolbar.openDetail" data-board-detail @click="emit('detail')"><AtlasIcon name="panel-right-close" :size="16" /></button>
    <button v-if="kind === 'page' || (kind === 'block' && canEdit)" type="button" class="bb" :aria-label="kind === 'page' ? t.toolbar.openPage : t.toolbar.editComponent" :title="kind === 'page' ? t.toolbar.openPageTitle : t.toolbar.editComponentTitle" data-board-open @click="emit('open')"><AtlasIcon :name="kind === 'page' ? 'external' : 'pencil'" :size="16" /></button>
    <button v-else-if="single && isBoardCard(single)" type="button" class="bb" :aria-label="t.toolbar.openBoard" :title="t.toolbar.openBoardTitle" data-board-open @click="emit('open')"><AtlasIcon name="external" :size="16" /></button>
    <template v-if="canEdit">
      <button type="button" class="bb" :aria-label="t.toolbar.front" :title="t.toolbar.front" data-board-front @click="emit('reorder', 'front')"><AtlasIcon name="arrow-up" :size="16" /></button>
      <button type="button" class="bb" :aria-label="t.toolbar.back" :title="t.toolbar.back" data-board-back @click="emit('reorder', 'back')"><AtlasIcon name="arrow-down" :size="16" /></button>
      <button type="button" class="bb" :aria-label="t.toolbar.duplicate" :title="t.toolbar.duplicateTitle" data-board-duplicate @click="emit('duplicate')"><AtlasIcon name="copy" :size="16" /></button>
      <button type="button" class="bb" :aria-label="removal" :title="`${removal} (Delete)`" data-board-delete @click="emit('remove')"><AtlasIcon name="trash" :size="16" /></button>
    </template>
  </div>
</template>

<style scoped>
/* The editor's toolbar is fixed to its block; the board's one sits in the canvas, which places it over the selection. */
.board-toolbar { position: relative; z-index: auto; white-space: nowrap; }
/* A finger's target (P17 D5, contract J2.6): 44 px on a touch screen, the toolbar wraps (BoardCanvas) rather than overflows. */
/* Twice the class: it outranks the editor's own button size (40 px). */
@media (pointer: coarse) { .board-toolbar.board-toolbar :deep(button) { min-width: var(--touch-target, 44px); min-height: var(--touch-target, 44px); width: auto; } }
.board-toolbar button[aria-pressed="true"] { color: var(--chip-accent-fg, var(--accent)); background: var(--accent-soft); }
.board-toolbar button:active:not(:disabled) { scale: var(--atlas-press); }
.board-swatch { display: inline-block; width: 18px; height: 18px; border: 1px solid color-mix(in oklab, var(--appearance-fg, var(--muted)) 35%, transparent); border-radius: 999px; background: color-mix(in oklab, var(--appearance-bg, var(--surface)) 55%, var(--appearance-ink, var(--surface))); }
.board-swatch[data-board-color="black"] { background: var(--text); }
.board-palette-grid { display: grid; grid-template-columns: repeat(6, 24px); gap: 6px; animation: st-fade-in var(--atlas-motion-fast) var(--atlas-ease-out); }
.board-swatch-option { width: 24px; height: 24px; min-height: 0; padding: 0; cursor: pointer; transition: box-shadow var(--atlas-motion-fast) var(--atlas-ease-out); }
.board-background-body { padding: 10px; }
/* The popover body's button reset (.md-context-popover-body button) took the swatches' background: every colour looked white. */
.board-palette-grid .board-swatch-option { background: color-mix(in oklab, var(--appearance-bg, var(--surface)) 55%, var(--appearance-ink, var(--surface))); }
.board-palette-grid .board-swatch-option[data-board-color="black"] { background: var(--text); }
/* No colour, on the trigger as in the palette: a struck tile, not one white like „Bílá“. */
.board-swatch.board-swatch-none { background: linear-gradient(to top right, transparent calc(50% - 1px), var(--muted) calc(50% - 1px), var(--muted) calc(50% + 1px), transparent calc(50% + 1px)), var(--surface); }
.board-swatch-option[aria-checked="true"] { box-shadow: 0 0 0 2px var(--paper, var(--surface)), 0 0 0 4px var(--accent); }
.board-swatch-option:focus-visible { outline: 2px solid var(--accent); outline-offset: 2px; }
.board-swatch-option:active { scale: var(--atlas-press); }
</style>
