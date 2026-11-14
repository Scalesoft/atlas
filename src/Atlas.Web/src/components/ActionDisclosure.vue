<script setup lang="ts">
import { nextTick, onMounted, onUnmounted, ref, useId, watch, type ComponentPublicInstance } from 'vue';
import AtlasIcon from './AtlasIcon.vue';
import { DropdownMenu, DropdownMenuContent, DropdownMenuTrigger } from './ui/dropdown-menu';
import { Button, type ButtonVariants } from './ui/button';
import { hostElement } from '../lib/templateRef';
import { focusFirstAction } from '../lib/menuFocus';
// `variant`, `size` and `triggerClass`: a menu that stands where a labelled button stood (Nový → Stránka / Tabule) keeps
// that button's look; the default is the quiet „…“ of every row and bar.
const props = withDefaults(defineProps<{ label: string; disabled?: boolean; triggerTitle?: string; variant?: ButtonVariants["variant"]; size?: ButtonVariants["size"]; triggerClass?: string }>(),
  { variant: "ghost", size: "icon-sm" });
const open = ref(false);
// Two behaviours reka-ui does not provide, both from the September UI audit:
// an overlay must never outlive the route underneath it, and a menu whose
// trigger has just been disabled must not stay open over a control that can no
// longer be reached.
const trigger = ref<ComponentPublicInstance>();
function routeChanged() { open.value = false; }
watch(() => props.disabled, value => { if (value) open.value = false; });
// reka-ui keeps a menu open and simply repositions it when its trigger scrolls
// out of view. The audit settled the opposite: a menu anchored to something the
// reader can no longer see is closed, not relocated. reka-ui returns focus to
// the trigger on close, which is the rest of that behaviour.
function closeIfAnchorLeft() {
  if (!open.value) return;
  const rect = hostElement(trigger.value)?.getBoundingClientRect();
  if (!rect) return;
  if (rect.bottom < 0 || rect.top > window.innerHeight) open.value = false;
}
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
onMounted(() => {
  window.addEventListener('hashchange', routeChanged);
  document.addEventListener('scroll', closeIfAnchorLeft, true);
  window.addEventListener('resize', closeIfAnchorLeft);
});
onUnmounted(() => {
  window.removeEventListener('hashchange', routeChanged);
  document.removeEventListener('scroll', closeIfAnchorLeft, true);
  window.removeEventListener('resize', closeIfAnchorLeft);
});
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
   They were `:deep()` before for the same reason -- slotted buttons were never
   this component's own elements either. */
.action-disclosure-panel { z-index: 50; display: grid; min-width: 232px; max-width: calc(100vw - 24px); max-height: var(--reka-dropdown-menu-content-available-height, calc(100dvh - 16px)); overflow: auto; padding: 6px; border: 1px solid var(--line); border-radius: 8px; color: var(--text); background: var(--surface); box-shadow: var(--shadow-menu); }
.action-disclosure-panel button { display: flex; align-items: center; justify-content: flex-start; gap: 12px; width: 100%; min-height: var(--control-h); margin: 0; padding: 9px 10px; border: 0; border-radius: var(--atlas-radius-sm); color: var(--text); background: transparent; font-family: inherit; font-size: 13px; font-weight: 500; line-height: 1.4; text-align: left; white-space: normal; cursor: pointer; }
.action-disclosure-panel button > svg { width: var(--icon); height: var(--icon); flex: 0 0 var(--icon); color: var(--muted); }
/* A shortcut hint sits at the row's right edge, as in the tree's menu (ActionMenu). */
.action-disclosure-panel button > .action-hint { margin-left: auto; padding-left: 16px; color: var(--text-dim, var(--muted)); font: 500 11px var(--font-mono, ui-monospace, monospace); }
/* An action without an icon (an editor plugin may bring none) keeps the icon's column, so its label lines up with the rest. */
.action-disclosure-panel button > .action-icon-gap { flex: 0 0 var(--icon); }
@media (hover: hover) and (pointer: fine) { .action-disclosure-panel button:hover:not(:disabled) { background: var(--hover); } }
.action-disclosure-panel button:disabled { opacity: .5; cursor: not-allowed; }
/* reka-ui moves focus with `data-highlighted` rather than DOM focus on hover: the pointer gives the row its
   background and colour only, the ring belongs to the keyboard (:focus-visible). */
.action-disclosure-panel button:focus-visible,
.action-disclosure-panel [data-highlighted] { background: var(--accent-soft); color: var(--accent); }
.action-disclosure-panel button:focus-visible { outline: 2px solid var(--accent); outline-offset: -2px; }
/* Groups (ActionDisclosureGroup) stack like the items; a line follows every group but the last. */
.action-disclosure-panel .action-disclosure-group { display: grid; }
.action-disclosure-panel .action-disclosure-group:not(:last-child) { margin-bottom: 6px; padding-bottom: 6px; border-bottom: 1px solid var(--line); }
.action-disclosure-panel button[data-destructive],
.action-disclosure-panel button[data-destructive] > svg { color: var(--error); }
.action-disclosure-panel button[data-destructive]:is(:focus-visible, [data-highlighted]) { background: color-mix(in srgb, var(--error) 10%, transparent); color: var(--error); }
@media (hover: hover) and (pointer: fine) { .action-disclosure-panel button[data-destructive]:hover:not(:disabled) { background: color-mix(in srgb, var(--error) 10%, transparent); } }
.atlas .document-primary-actions, .atlas .document-secondary-actions { display: flex; align-items: center; gap: 7px; }
@media (max-width: 680px) {
  .action-disclosure-panel button { min-height: 44px; }
  .atlas .document-actions { justify-content: space-between; }
  .atlas .document-primary-actions { margin-left: auto; }
}
</style>
