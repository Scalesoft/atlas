<script setup lang="ts">
import { nextTick, ref, useId, watch, type ComponentPublicInstance } from 'vue';
import AtlasIcon from './AtlasIcon.vue';
import { DropdownMenu, DropdownMenuContent, DropdownMenuTrigger } from './ui/dropdown-menu';
import { Button, type ButtonVariants } from './ui/button';
import { hostElement } from '../lib/templateRef';
import { closeMenuWhenLost, focusFirstAction } from '../lib/menuFocus';
// `variant`, `size` and `triggerClass`: a menu that stands where a labelled button stood (Nový → Stránka / Tabule) keeps
// that button's look; the default is the quiet „…“ of every row and bar.
const props = withDefaults(defineProps<{ label: string; disabled?: boolean; triggerTitle?: string; variant?: ButtonVariants["variant"]; size?: ButtonVariants["size"]; triggerClass?: string }>(),
  { variant: "ghost", size: "icon-sm" });
const open = ref(false);
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
    <DropdownMenuContent :id="panelId" class="action-disclosure-panel" align="end" :side-offset="6" :collision-padding="12" :aria-label="label" @open-auto-focus="focusFirstAction" @keydown.capture="moveAmongShown">
      <slot />
    </DropdownMenuContent>
  </DropdownMenu>
</template>
<style>
/* The panel is portalled out of this component, so its rules cannot be scoped.
   The page editor's menu look (controls.css `.md-context-menu`, as ActionMenu wears it): its surface, 13 px rows with a 16 px icon
   10 px from the label, and ActionMenu's highlight. Written out here rather than borrowed through the editor's classes: the editor's
   stylesheet comes with its lazy chunk, and these menus show before it (Knihovna, settings, the profile). */
.action-disclosure-panel { z-index: 50; display: grid; gap: 1px; min-width: 200px; max-width: calc(100vw - 24px); max-height: var(--reka-dropdown-menu-content-available-height, calc(100dvh - 16px)); overflow: auto; padding: 4px; border: 1px solid var(--line); border-radius: var(--radius-md, 8px); color: var(--text); background: var(--surface); box-shadow: var(--shadow-menu); }
.action-disclosure-panel button { box-sizing: border-box; display: flex; align-items: center; justify-content: flex-start; gap: 10px; width: 100%; min-height: var(--control-h, 36px); margin: 0; padding: 6px 10px; border: 0; border-radius: var(--radius-sm, 6px); outline: none; color: var(--text); background: transparent; font: inherit; font-size: 13px; line-height: 1.4; text-align: left; white-space: normal; cursor: pointer; }
.action-disclosure-panel button > svg { width: var(--icon-sm, 16px); height: var(--icon-sm, 16px); flex: 0 0 auto; }
/* A shortcut hint sits at the row's right edge, as in ActionMenu (.board-menu-key). */
.action-disclosure-panel button > .action-hint { margin-left: auto; padding-left: 16px; color: var(--text-dim, var(--muted)); font-size: 12px; }
/* An action without an icon (an editor plugin may bring none) keeps the icon's column, so its label lines up with the rest. */
.action-disclosure-panel button > .action-icon-gap { flex: 0 0 var(--icon-sm, 16px); }
.action-disclosure-panel button:disabled { opacity: .5; cursor: not-allowed; }
/* ActionMenu's highlight: the pointer and the keyboard light the row alike, the ring belongs to the keyboard. The editor's --bg-3 is
   defined by its lazy theme stylesheet, so outside the editor the shell's hover tint stands in. */
.action-disclosure-panel :is(button:focus-visible, [data-highlighted]) { background: var(--bg-3, var(--hover)); }
.action-disclosure-panel button:focus-visible { outline: var(--focus-ring); outline-offset: -2px; }
/* Groups (ActionDisclosureGroup) stack like the items; a line follows every group but the last. */
.action-disclosure-panel .action-disclosure-group { display: grid; gap: 1px; }
.action-disclosure-panel .action-disclosure-group:not(:last-child) { margin-bottom: 4px; padding-bottom: 4px; border-bottom: 1px solid var(--line); }
/* Destructive rows are red in every menu (ActionMenu's .board-menu-destructive). */
.action-disclosure-panel button[data-destructive],
.action-disclosure-panel button[data-destructive] > svg { color: var(--error, var(--destructive)); }
.atlas .document-primary-actions, .atlas .document-secondary-actions { display: flex; align-items: center; gap: 7px; }
@media (max-width: 680px) {
  .action-disclosure-panel button { min-height: 44px; }
  .atlas .document-actions { justify-content: space-between; }
  .atlas .document-primary-actions { margin-left: auto; }
}
</style>
