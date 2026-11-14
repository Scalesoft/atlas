<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, ref, useId, watch } from 'vue'
import type { Editor } from '@tiptap/core'
import type { MarkdownCommand, MarkdownEditorController } from '../core'
import { editorForController } from '../core/internal'
import { useControllerState } from '../vue/context'
import { useAnchoredPopover } from './useAnchoredPopover'
import PluginActionButton from './PluginActionButton.vue'
import { PlusIcon } from './icons'
import { MarkdownIcon } from '../icons'
import { legacyIconName, type MarkdownIconName } from './iconMarkup'
import { markdownStrings } from './strings'
import { toolbarStrings } from './strings/toolbar'

const props = defineProps<{ controller: MarkdownEditorController; variant: 'document' | 'compact'; presentation?: 'expanded' | 'insert-menu' | 'hidden'; readonly?: boolean }>()
// Reported upward so the surface's fullscreen Escape handling yields while the menu is open,
// the same protocol FormattingToolbar and TableToolbar use.
const emit = defineEmits<{ 'plugin-popover': [open: boolean] }>()
const state = useControllerState(props.controller)
const actions = computed(() => {
  // A hidden row draws nothing: asking every action's state on each published state cost ~10 ms a keystroke (perf review 2026-11-02).
  if (props.presentation === 'hidden') return []
  void state.value
  return props.controller.actions.toolbar.filter(action => !action.variants || action.variants.includes(props.variant))
    .map(action => ({ ...action, state: props.controller.query(action.command) }))
})
// Positive comparisons: an unknown host value keeps the documented expanded row.
const insertMenu = computed(() => props.presentation === 'insert-menu')
// 'hidden': the host renders the plugin actions itself (e.g. in its own page menu).
const hidden = computed(() => props.presentation === 'hidden')
const partitions = computed(() => {
  const groups = new Map<string, typeof actions.value>()
  for (const action of actions.value) {
    const key = action.group?.trim() ? `group:${action.group}` : `action:${action.id}`
    const group = groups.get(key)
    if (group) group.push(action)
    else groups.set(key, [action])
  }
  const primary: typeof actions.value = []
  const overflow: typeof actions.value = []
  let overflowStarted = false
  for (const group of groups.values()) {
    if (!overflowStarted && primary.length + group.length <= 5) primary.push(...group)
    else {
      overflowStarted = true
      overflow.push(...group)
    }
  }
  return { primary, overflow }
})
const primary = computed(() => partitions.value.primary)
const overflow = computed(() => partitions.value.overflow)

const menuOpen = ref(false)
const menuId = useId()
const insertTrigger = ref<HTMLElement | null>(null)
const menuPopup = ref<HTMLElement | null>(null)
function closeMenu(restoreFocus = false) {
  if (!menuOpen.value) return
  menuOpen.value = false
  if (restoreFocus) insertTrigger.value?.focus({ preventScroll: true })
}
function toggleMenu() {
  // The anchored popover registers against the live engine; never open without one.
  if (!menuOpen.value && !editorForController(props.controller)) return
  if (menuOpen.value) closeMenu()
  else menuOpen.value = true
}
// One open popover per editor; the shared owner protocol closes any other menu first.
useAnchoredPopover(() => insertTrigger.value, () => menuPopup.value, undefined, {
  get editor(): Editor { return editorForController(props.controller) as Editor },
  close: () => closeMenu(),
})
function activate(command: MarkdownCommand) {
  closeMenu(true)
  props.controller.dispatch(command)
}
/** `icon: 'svg:paperclip'` names a registry entry; the old component names still resolve. */
function svgIconFor(icon: string | undefined): MarkdownIconName | null {
  return icon?.startsWith('svg:') ? legacyIconName(icon.slice(4)) : null
}
function onMenuKeydown(event: KeyboardEvent) {
  if (event.key !== 'Escape' || event.isComposing) return
  event.preventDefault()
  event.stopPropagation()
  closeMenu(true)
}
function onOutsidePointer(event: Event) {
  const target = event.target as Node | null
  if (!target) return
  if (!menuPopup.value?.contains(target) && !insertTrigger.value?.contains(target)) closeMenu()
}
let listening: Document | undefined
// The outside-click listener exists only while the menu is open.
watch(menuOpen, async open => {
  emit('plugin-popover', open)
  if (open) {
    await nextTick()
    listening = menuPopup.value?.ownerDocument ?? document
    listening.addEventListener('pointerdown', onOutsidePointer)
  }
  else {
    listening?.removeEventListener('pointerdown', onOutsidePointer)
    listening = undefined
  }
})
onBeforeUnmount(() => {
  listening?.removeEventListener('pointerdown', onOutsidePointer)
  listening = undefined
  if (menuOpen.value) emit('plugin-popover', false)
})
watch([insertMenu, () => state.value.editable, () => actions.value.length], ([menu, editable, count]) => {
  if (!menu || !editable || !count) closeMenu()
})
</script>

<template>
  <div v-if="!hidden && !readonly && actions.length" class="md-plugin-toolbar" role="group" :aria-label="toolbarStrings.pluginToolbarAriaLabel">
    <div v-if="insertMenu" class="md-insert-anchor">
      <!-- Shared text-button family (controls.css); `.bb` is only styled inside the bubble/context bars. -->
      <button ref="insertTrigger" type="button" class="md-btn md-insert-trigger" :class="{ on: menuOpen }"
        aria-haspopup="menu" :aria-label="markdownStrings.insert" :disabled="!state.editable" :aria-expanded="menuOpen ? 'true' : 'false'"
        :aria-controls="menuOpen ? menuId : undefined" data-testid="plugin-insert-trigger"
        @mousedown.prevent @click="toggleMenu">
        <PlusIcon /><span class="md-insert-trigger-label">{{ markdownStrings.insert }}</span>
      </button>
      <!-- Shared popover + menu families from styles/contextToolbar.css and controls.css; no local surface style. -->
      <div v-if="menuOpen" :id="menuId" ref="menuPopup" class="md-context-popover md-context-menu md-context-menu-compact"
        role="menu" :aria-label="markdownStrings.insert" @click.stop @keydown="onMenuKeydown">
        <div class="md-context-popover-body">
          <button v-for="action in actions" :key="action.id" type="button" role="menuitem" class="md-context-menu-item"
            :disabled="!action.state.enabled" :title="action.description" :data-command="action.id"
            @mousedown.prevent @click="activate(action.command)">
            <span v-if="action.icon" class="md-menu-item-icon" aria-hidden="true">
              <MarkdownIcon v-if="svgIconFor(action.icon)" :name="svgIconFor(action.icon)!" :size="16" />
              <i v-else-if="action.icon.startsWith('pi ')" :class="action.icon" />
              <span v-else>{{ action.icon }}</span>
            </span>
            <span>{{ action.label }}</span>
          </button>
        </div>
      </div>
    </div>
    <template v-else>
      <PluginActionButton v-for="action in primary" :key="action.id" :action="action"
        @activate="controller.dispatch(action.command)" />
      <details v-if="overflow.length" class="md-plugin-overflow">
        <summary>{{ toolbarStrings.pluginOverflowLabel }} <span class="sr-only">{{ toolbarStrings.pluginOverflowSrLabel }}</span></summary>
        <div class="md-plugin-overflow-menu">
          <PluginActionButton v-for="action in overflow" :key="action.id" :action="action"
            @activate="controller.dispatch(action.command)" />
        </div>
      </details>
    </template>
  </div>
</template>

<style scoped>
.md-plugin-toolbar { display: flex; flex-wrap: wrap; align-items: center; gap: 6px; padding: 8px 12px; border-bottom: 1px solid var(--line); }
/* tokens.css already rings every :focus-visible summary under .md-editor. */
.md-plugin-overflow { position: relative; }
.md-plugin-overflow summary { cursor: pointer; font-size: 12px; color: var(--text-dim); }
.md-plugin-overflow-menu { position: absolute; z-index: var(--z-sticky, 100); right: 0; top: calc(100% + 6px); min-width: 190px; display: grid; gap: 4px; padding: 6px; border: 1px solid var(--line); border-radius: 8px; background: var(--paper); box-shadow: var(--shadow-2); }
.md-plugin-overflow-menu .md-plugin-action { width: 100%; }
.md-insert-anchor { position: relative; display: inline-flex; }
.md-insert-trigger { display: inline-flex; align-items: center; gap: 6px; }
.md-insert-trigger.on { background: var(--accent-soft, #e3f4f7); }
.md-insert-trigger :deep(svg) { width: var(--icon-sm, 16px); height: var(--icon-sm, 16px); }
.md-menu-item-icon, .md-menu-item-icon :deep(svg) { display: inline-grid; place-items: center; width: 16px; height: 16px; }
.sr-only { position: absolute; width: 1px; height: 1px; padding: 0; margin: -1px; overflow: hidden; clip: rect(0, 0, 0, 0); white-space: nowrap; border: 0; }
</style>
