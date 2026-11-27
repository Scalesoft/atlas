<script setup lang="ts">
import { nextTick, ref, useId, watch, type ComponentPublicInstance } from 'vue';
import AtlasIcon from './AtlasIcon.vue';
import { DropdownMenu, DropdownMenuContent, DropdownMenuTrigger } from './ui/dropdown-menu';
import { Button, type ButtonVariants } from './ui/button';
import { hostElement } from '../lib/templateRef';
import { closeMenuWhenLost, focusFirstAction } from '../lib/menuFocus';
import { useEditorTheme } from '../editor/editorSetup';
import './boards/canvas/boardPopover.css';
import './actionMenu.css';
// `variant`, `size` and `triggerClass`: a menu that stands where a labelled button stood (Nový → Stránka / Tabule) keeps
// that button's look; the default is the quiet „…“ of every row and bar.
const props = withDefaults(defineProps<{ label: string; disabled?: boolean; triggerTitle?: string; variant?: ButtonVariants["variant"]; size?: ButtonVariants["size"]; triggerClass?: string }>(),
  { variant: "ghost", size: "icon-sm" });
const open = ref(false);
// ActionMenu's look: the editor's menu classes in the editor's theme (light, dark, green accent).
const theme = useEditorTheme();
// The close rules every menu shares (route change, trigger scrolled away), and one of its own from the September UI audit:
// a menu whose trigger has just been disabled must not stay open over a control that can no longer be reached.
const trigger = ref<ComponentPublicInstance>();
closeMenuWhenLost(open, () => hostElement(trigger.value));
watch(() => props.disabled, value => { if (value) open.value = false; });
// The APG menu-button pattern opens on ArrowUp with focus on the *last* item,
// which reka-ui's trigger does not implement -- it opens on ArrowDown, Enter and
// Space only. Reaching the bottom of a long action list without walking the
// whole thing is worth the eight lines.
const panelId = `action-disclosure-${useId()}`;
async function openAtLast(event: KeyboardEvent) {
  if (props.disabled || open.value) return;
  event.preventDefault();
  open.value = true;
  await nextTick();
  await new Promise(resolve => requestAnimationFrame(() => resolve(null)));
  if (!open.value || props.disabled) return;
  const actions = document.getElementById(panelId)?.querySelectorAll<HTMLElement>('[role=menuitem]:not([data-disabled])');
  if (!actions) return;
  actions[actions.length - 1]?.focus();
}
// reka-ui's arrow navigation walks every action of the menu, also those a breakpoint hides (the page menu keeps
// its phone-only actions in the DOM). Focusing a hidden one fails silently, so in a menu opened by the pointer
// ArrowDown and Home did nothing and ArrowUp stopped dead at the first shown action. While something is hidden the
// four keys move among the shown actions here, with reka-ui's own rules (no wrap-around); otherwise they stay its.
function moveAmongShown(event: KeyboardEvent) {
  if (!['ArrowDown', 'ArrowUp', 'Home', 'End'].includes(event.key) || event.ctrlKey || event.altKey || event.metaKey) return;
  const panel = event.currentTarget;
  if (!(panel instanceof HTMLElement)) return;
  const actions = [...panel.querySelectorAll<HTMLElement>('[role=menuitem]:not([data-disabled])')];
  const shown = actions.filter(action => getComputedStyle(action).display !== 'none');
  if (!shown.length || shown.length === actions.length) return;
  const last = shown.length - 1;
  const at = shown.indexOf(document.activeElement as HTMLElement);
  const next = event.key === 'Home' ? 0 : event.key === 'End' ? last
    : event.key === 'ArrowDown' ? Math.min(at + 1, last) : at < 0 ? last : Math.max(at - 1, 0);
  event.preventDefault();
  event.stopPropagation();
  shown[next]?.focus();
}
</script>
<template>
  <DropdownMenu v-model:open="open" :modal="false">
    <!-- The trigger keeps the browser's title: Reka's Tooltip and DropdownMenu both register their anchor on the
         nearest Popper root, so a Tooltip between the menu root and its trigger leaves the menu unpositioned. -->
    <DropdownMenuTrigger
      as-child
      :disabled="disabled"
    ><Button
      :variant="variant"
      :size="size"
      ref="trigger"
      type="button"
      :class="[variant === 'ghost' ? 'text-muted-foreground data-[state=open]:bg-accent data-[state=open]:text-accent-foreground' : undefined, triggerClass]"
      :aria-label="label"
      :title="triggerTitle ?? label"
      :disabled="disabled"
      @keydown.up="openAtLast"
    ><slot name="trigger"><AtlasIcon name="more" :size="18" /></slot></Button></DropdownMenuTrigger>
    <DropdownMenuContent :id="panelId" class="md-editor atlas-document-editor md-context-popover md-context-menu md-context-menu-compact board-menu action-disclosure-panel" :class="theme"
      align="end" :side-offset="6" :collision-padding="12" :aria-label="label" data-positioned @open-auto-focus="focusFirstAction" @keydown.capture="moveAmongShown">
      <div class="md-context-popover-body"><slot /></div>
    </DropdownMenuContent>
  </DropdownMenu>
</template>
<style>
/* The panel is portalled out of this component, so its rules cannot be scoped. Its surface, rows and highlight are ActionMenu's: the editor's
   menu look (@atlas/markdown/styles/menu.css, loaded with the app, so Knihovna, settings and the profile show it before any editor) and
   actionMenu.css. Only what the slotted buttons bring stays here. */
.action-disclosure-panel { padding: 0; max-height: var(--reka-dropdown-menu-content-available-height, calc(100dvh - 16px)); }
.action-disclosure-panel [role^="menuitem"] { justify-content: flex-start; margin: 0; line-height: 1.4; white-space: normal; }
/* A shortcut hint sits at the row's right edge, as in ActionMenu (.board-menu-key). */
.action-disclosure-panel [role^="menuitem"] > .action-hint { margin-left: auto; padding-left: 16px; color: var(--text-dim, var(--muted)); font-size: 12px; }
/* An action without an icon (an editor plugin may bring none) keeps the icon's column, so its label lines up with the rest. */
.action-disclosure-panel [role^="menuitem"] > .action-icon-gap { flex: 0 0 var(--icon-sm, 16px); }
.action-disclosure-panel [role^="menuitem"] > .discussion-count { margin-left: auto; }
/* Groups (ActionDisclosureGroup) stack like the items; a line follows every group but the last. */
.action-disclosure-panel .action-disclosure-group { display: grid; gap: 1px; }
.action-disclosure-panel .action-disclosure-group:not(:last-child) { margin-bottom: 4px; padding-bottom: 4px; border-bottom: 1px solid var(--line); }
.atlas .document-primary-actions, .atlas .document-secondary-actions { display: flex; align-items: center; gap: 7px; }
@media (max-width: 680px) {
  .atlas .document-actions { justify-content: space-between; }
  .atlas .document-primary-actions { margin-left: auto; }
}
</style>
