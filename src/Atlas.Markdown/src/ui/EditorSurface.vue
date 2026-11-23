<script setup lang="ts" generic="P extends MarkdownProfile">
import { computed, defineAsyncComponent, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import type { MarkdownOutlineMode, MarkdownOutlineOptions } from './outline'
import type { MarkdownEditorController, MarkdownProfile } from '../core'
import { handleMarkdownFileDrop, markdownFileDropErrorEvent, markdownFileDropStateEvent, markdownImportNoticeEvent, markdownInputErrorEvent, markdownRemoteRemovalEvent } from '../core'
import type { MarkdownImportDegradedPart, MarkdownImportNoticeDetail, MarkdownRemoteRemovalDetail } from '../core'
import { createMarkdownOverlayOwnerId } from '../core/overlay'
import { editorForController } from '../core/internal'
import MarkdownEditorRoot from '../vue/MarkdownEditorRoot.vue'
import MarkdownEditorContent from '../vue/MarkdownEditorContent.vue'
import { useControllerState } from '../vue/context'
import EditorControls from './EditorControls.vue'
import type { MarkdownFormattingItem } from './formatting'
import PluginToolbar from './PluginToolbar.vue'
import PresenceStack from './PresenceStack.vue'
import PluginPanels from './PluginPanels.vue'
import MarkdownSource from './MarkdownSource.vue'
import { activeMarkdownPopover, onMarkdownPopoverChange } from '../features/modules/popoverInteraction'
import CollaborationNotice from './CollaborationNotice.vue'
import ConnectionDot from './ConnectionDot.vue'
import { FullscreenIcon, FullscreenExitIcon, CloseIcon } from './icons'
import { markdownStrings } from './strings'
import { surfaceStrings } from './strings/surface'
import { acquireFullscreenLock, hasActiveEditorOverlay, hasActiveModalDialog } from './fullscreen'
import '../styles/editor.css'
import '../styles/motion.css'
import '../styles/layers.css'

defineOptions({ inheritAttrs: false })
const props = withDefaults(defineProps<{
  controller: MarkdownEditorController<P>
  ariaLabel: string
  showBlockAddButton?: boolean
  showBlockInserter?: boolean
  /** The selection bubble on <body>, fixed: for an editor inside a zoomed or clipping host (a board's canvas). */
  bubbleOnBody?: boolean
  /** False when the host draws its own bar over a reader's text selection: the reader's bubble stays off (one bar for readers). */
  readerBubble?: boolean
  readonly?: boolean
  variant?: 'document' | 'compact'
  visibleLines?: 1 | 2 | 3
  formatting?: readonly MarkdownFormattingItem[]
  fullscreenable?: boolean
  /** Shown in the fullscreen header instead of the generic ariaLabel. */
  documentTitle?: string
  showMarkdownSource?: boolean
  outline?: MarkdownOutlineMode | MarkdownOutlineOptions
  /** Collapse the plugin toolbar into a single "Vložit" menu. */
  toolbarPresentation?: 'expanded' | 'insert-menu' | 'hidden'
  /** Show the Markdown fidelity label in the footer. */
  showProfileLabel?: boolean
  /** Show remote collaborators above the content while the session is online. */
  showPresence?: boolean
}>(), { readonly: undefined, variant: 'document', visibleLines: 2, fullscreenable: true, toolbarPresentation: 'expanded', showProfileLabel: true, showPresence: false, showBlockAddButton: true, showBlockInserter: false, readerBubble: true })
const state = useControllerState(props.controller)
const readonlyMode = computed(() => props.readonly ?? !state.value.editable)
// Only an explicit mode collapses the toolbar; anything else keeps the expanded row.
const toolbarPresentation = computed<'expanded' | 'insert-menu' | 'hidden'>(() =>
  props.toolbarPresentation === 'insert-menu' || props.toolbarPresentation === 'hidden' ? props.toolbarPresentation : 'expanded')
const remotePeers = computed(() => props.showPresence && state.value.connection === 'online' ? [...state.value.peers] : [])
const DocumentOutline = defineAsyncComponent(() => import('./DocumentOutline.vue'))
// Shown only after an import kept constructs as text: loaded then, not with every editor.
const ImportNotice = defineAsyncComponent(() => import('./ImportNotice.vue'))
const outlineOptions = computed<MarkdownOutlineOptions>(() => typeof props.outline === 'string' ? { mode: props.outline } : props.outline ?? { mode: 'hidden' })
const outlineEnabled = computed(() => props.variant === 'document' && outlineOptions.value.mode !== 'hidden')
const outlineDocked = ref(false)
watch(outlineEnabled, () => { outlineDocked.value = false })
// Describe the current content's Markdown fidelity, not its storage profile.
const markdownLossy = ref<boolean | null>(null)
const footerVisible = computed(() => props.variant === 'document' && !readonlyMode.value)
// „Celá obrazovka“ is a reading feature too: a read-only document (a locked page) keeps the footer with it, so locking moves nothing.
const footerShown = computed(() => footerVisible.value || (props.variant === 'document' && props.fullscreenable))
// Walks the whole document on every change (~9 ms on a 2 MB page), so only while the label shows.
watch([() => state.value.contentRevision, () => state.value.lifecycle, footerVisible, () => props.showProfileLabel], () => {
  markdownLossy.value = footerVisible.value && props.showProfileLabel && state.value.lifecycle === 'ready'
    ? props.controller.getMarkdownLossy?.() ?? props.controller.getProjection().markdownLossy
    : null
}, { immediate: true })
const engine = computed(() => { void state.value; return editorForController(props.controller) })
/** An editor's floating panel is open (popoverInteraction): the corner card folds into its dot rather than peek out from under it. */
const panelOpen = ref(false)
watch(engine, (editor, _previous, onCleanup) => {
  if (!editor) { panelOpen.value = false; return }
  const sync = () => { panelOpen.value = !!activeMarkdownPopover(editor) }
  sync()
  onCleanup(onMarkdownPopoverChange(editor, sync))
}, { immediate: true })
const shell = ref<HTMLElement | null>(null)
// The page editor shows no frame; focus that arrived by Tab (not mouse, not an Edit button, not typing) gets a quiet bar
// instead (styles/surface.css, WCAG 2.4.7). :focus-visible cannot tell, it always matches a contenteditable.
const keyboardFocus = ref(false)
let tabbed = false
const markTab = (event: KeyboardEvent) => { tabbed = event.key === 'Tab' }
const markPointer = () => { tabbed = false }
onMounted(() => { document.addEventListener('keydown', markTab, true); document.addEventListener('pointerdown', markPointer, true) })
onBeforeUnmount(() => { document.removeEventListener('keydown', markTab, true); document.removeEventListener('pointerdown', markPointer, true) })
const overlayOwnerId = createMarkdownOverlayOwnerId()
const fullscreen = ref(false)
const formattingPopover = ref(false)
const tablePopover = ref(false)
const pluginPopover = ref(false)
const inputError = ref('')
/** Constructs the last paste/drop kept as literal text; cleared when that input is rejected. */
const importNotice = ref<readonly MarkdownImportDegradedPart[] | null>(null)
const dropPending = ref(0)
/** A colleague removed the block being typed in (collab/remoteRemoval.ts): the notice with „Vrátit“ stays until used or closed. */
const removal = ref<MarkdownRemoteRemovalDetail | null>(null)
const onRemoteRemoval = (event: Event) => { removal.value = (event as CustomEvent<MarkdownRemoteRemovalDetail>).detail ?? null }
function restoreRemoved() { removal.value?.restore(); removal.value = null }
function onDragover(event: DragEvent) {
  if (state.value.editable && event.dataTransfer?.types.includes('Files')) {
    event.preventDefault()
    event.dataTransfer.dropEffect = 'copy'
  }
}
function onDrop(event: DragEvent) {
  if (!engine.value || event.defaultPrevented || !event.dataTransfer?.files.length) return
  inputError.value = ''
  const position = engine.value.view.posAtCoords({ left: event.clientX, top: event.clientY })?.pos
  handleMarkdownFileDrop(engine.value, event, position)
}
function onDropError(event: Event) {
  importNotice.value = null
  inputError.value = (event as CustomEvent<{ message?: string }>).detail?.message ?? surfaceStrings.fileDropError
}
function onInputError(event: Event) {
  importNotice.value = null
  inputError.value = (event as CustomEvent<{ message?: string }>).detail?.message ?? surfaceStrings.inputPasteError
}
function onImportNotice(event: Event) {
  const degraded = (event as CustomEvent<MarkdownImportNoticeDetail>).detail?.degraded
  importNotice.value = degraded?.length ? degraded : null
}
function onDropState(event: Event) {
  const pending = (event as CustomEvent<{ pending: number }>).detail.pending
  if (pending > dropPending.value) inputError.value = ''
  dropPending.value = pending
}
watch(shell, (next, previous) => {
  previous?.removeEventListener(markdownFileDropErrorEvent, onDropError)
  previous?.removeEventListener(markdownInputErrorEvent, onInputError)
  previous?.removeEventListener(markdownFileDropStateEvent, onDropState)
  previous?.removeEventListener(markdownImportNoticeEvent, onImportNotice)
  previous?.removeEventListener(markdownRemoteRemovalEvent, onRemoteRemoval)
  next?.addEventListener(markdownFileDropErrorEvent, onDropError)
  next?.addEventListener(markdownInputErrorEvent, onInputError)
  next?.addEventListener(markdownFileDropStateEvent, onDropState)
  next?.addEventListener(markdownImportNoticeEvent, onImportNotice)
  next?.addEventListener(markdownRemoteRemovalEvent, onRemoteRemoval)
})
let releaseLock: ((restoreFocus?: boolean) => void) | undefined
let fullscreenTrigger: HTMLElement | null = null
let eventDocument: Document | undefined
let focusEditorAfterExit = false
function toggleFullscreen(event: MouseEvent) {
  if (!fullscreen.value && (props.variant === 'compact' || !props.fullscreenable)) return
  if (!fullscreen.value) fullscreenTrigger = event.currentTarget as HTMLElement
  fullscreen.value = !fullscreen.value
}
watch(fullscreen, value => {
  if (value && shell.value) {
    releaseLock = acquireFullscreenLock(shell.value, () => engine.value?.view.focus(), fullscreenTrigger)
    eventDocument = shell.value.ownerDocument
    eventDocument.addEventListener('keydown', onKeydown, true)
  }
  else {
    const focusEditor = focusEditorAfterExit
    focusEditorAfterExit = false
    releaseLock?.(!focusEditor)
    releaseLock = undefined
    eventDocument?.removeEventListener('keydown', onKeydown, true)
    eventDocument = undefined
    if (focusEditor && shell.value && !hasActiveModalDialog(shell.value)
      && !shell.value?.closest('[inert]')) engine.value?.view.focus()
  }
}, { flush: 'post' })
watch([() => props.variant, () => props.fullscreenable], ([variant, fullscreenable]) => {
  if (!fullscreen.value || (variant === 'document' && fullscreenable)) return
  focusEditorAfterExit = !readonlyMode.value
  fullscreen.value = false
})
function onKeydown(event: KeyboardEvent) {
  if (event.key !== 'Escape' || event.isComposing || !fullscreen.value || !shell.value || shell.value.inert
    || formattingPopover.value || tablePopover.value || pluginPopover.value || hasActiveModalDialog(shell.value)) return
  if (hasActiveEditorOverlay(shell.value)) return
  if ((event.target as HTMLElement | null)?.closest?.('.md-source, .md-diagram-source-editor, .md-context-toolbar, .dh-wrap')) return
  fullscreen.value = false
  event.stopPropagation()
}
onBeforeUnmount(() => {
  shell.value?.removeEventListener(markdownFileDropErrorEvent, onDropError)
  shell.value?.removeEventListener(markdownInputErrorEvent, onInputError)
  shell.value?.removeEventListener(markdownFileDropStateEvent, onDropState)
  shell.value?.removeEventListener(markdownImportNoticeEvent, onImportNotice)
  shell.value?.removeEventListener(markdownRemoteRemovalEvent, onRemoteRemoval)
  releaseLock?.(false)
  eventDocument?.removeEventListener('keydown', onKeydown, true)
})
const teleportTarget = computed(() => shell.value?.ownerDocument.body)
</script>

<template>
  <Teleport :to="teleportTarget ?? 'body'" :disabled="!fullscreen">
    <MarkdownEditorRoot :controller="controller">
      <div v-bind="$attrs" ref="shell" class="md-editor" :class="{ paper: variant === 'document', compact: variant === 'compact', readonly: readonlyMode, 'is-fullscreen': fullscreen, 'has-outline': outlineEnabled }"
        :data-fullscreen="fullscreen ? 'true' : undefined" :data-variant="variant" :data-md-overlay-owner="overlayOwnerId" data-testid="markdown-editor"
        :role="fullscreen ? 'dialog' : undefined" :aria-modal="fullscreen ? 'true' : undefined" :aria-label="fullscreen ? ariaLabel : undefined"
        :style="{ '--md-visible-lines': visibleLines }" @dragover="onDragover" @drop="onDrop"
        :data-focus-keyboard="keyboardFocus ? 'true' : undefined" @focusin="keyboardFocus = tabbed" @focusout="keyboardFocus = false">
        <!-- Mounted with the view and kept, read-only or not (contract J1.2: editability is a flag, not a mode): the menus
             and the block handle register ProseMirror plugins, and every registration recreates the plugin views, which
             re-renders a collaborative document from Yjs. Registering on the still empty document costs nothing;
             each control hides itself until editor.isEditable. -->
        <EditorControls v-if="engine":editor="engine" :profile="controller.profile" :boundary="shell"
          :show-block-add-button="showBlockAddButton" :show-block-inserter="showBlockInserter" :bubble-on-body="bubbleOnBody"
          :reader-bubble="readerBubble" :formatting="formatting" :controller="controller"
          :compact="variant === 'compact'" :hide-block-handle="false" @formatting-popover="formattingPopover = $event" @table-popover="tablePopover = $event" />
        <header v-if="fullscreen" class="md-fs-header">
          <span class="md-fs-header-label">{{ documentTitle || ariaLabel }}</span>
          <span class="md-fs-header-actions">
            <button type="button" class="md-fs-header-close md-btn" :aria-label="markdownStrings.close" @click="fullscreen = false">
              <CloseIcon /><span>{{ markdownStrings.close }}</span>
            </button>
            <kbd>Esc</kbd>
          </span>
        </header>
        <!--
          Transient messages float over the surface. A projection failure or a file drop can start
          and stop at any moment, and any of these paragraphs in the document flow would push the
          text down and pull it back for as long as the condition lasts.
        -->
        <div class="md-editor-messages">
          <p v-if="state.error && (state.error.kind === 'content' || state.error.kind === 'projection')"
            class="md-drop-error" role="alert" data-testid="editor-content-error">
            {{ state.error.kind === 'content' ? surfaceStrings.contentUnsafeError : surfaceStrings.projectionFailedError }}
            {{ state.error.message }}
          </p>
          <p v-if="inputError" class="md-drop-error" role="alert">{{ inputError }}</p>
          <p v-if="dropPending" class="md-drop-status" role="status">{{ surfaceStrings.droppingFiles(dropPending) }}</p>
          <ImportNotice v-if="importNotice" :parts="importNotice" @dismiss="importNotice = null" />
          <p v-if="removal" class="md-drop-status md-removal-notice" role="status" data-testid="markdown-remote-removal">
            <span>{{ removal.message }}</span>
            <button type="button" class="md-btn" @click="restoreRemoved">{{ surfaceStrings.restoreRemovedBlock }}</button>
            <button type="button" class="md-btn" :aria-label="markdownStrings.close" @click="removal = null"><CloseIcon /></button>
          </p>
        </div>
        <slot name="toolbar" :controller="controller" :state="state"><PluginToolbar :controller="controller" :variant="variant" :presentation="toolbarPresentation" :readonly="readonlyMode" @plugin-popover="pluginPopover = $event" /></slot>
        <MarkdownSource v-if="showMarkdownSource && variant === 'document'" :controller="controller" />
        <div v-if="showPresence && (!readonlyMode || remotePeers.length)" class="md-editor-presence" :aria-hidden="!remotePeers.length ? true : undefined">
          <PresenceStack v-if="remotePeers.length" :peers="remotePeers" />
        </div>
        <slot name="before-content" :controller="controller" :state="state" />
        <div :class="{ 'md-outline-layout': outlineEnabled, 'is-docked': outlineEnabled && outlineDocked }">
          <div :class="{ 'md-outline-document': outlineEnabled }">
            <div class="paper-inner"><MarkdownEditorContent :ariaLabel="ariaLabel" /></div>
          </div>
          <DocumentOutline v-if="engine && outlineEnabled" :editor="engine" :options="outlineOptions" @docked="outlineDocked = $event" />
        </div>
        <!--
          One overlay column: the dot, its detail panel and the card stacked, so they never cover each other. It never takes part in
          the document flow. A compact editor keeps it in its top-right corner; a document keeps it in the bottom-right corner of the
          part of the shell in view (a zero-height sticky anchor after the content), over the document's foot and never over the lines
          being read or the component being edited. While a floating panel of the editor is open the card folds into the dot: the panel
          lives in the body above the page (layers.css) and would otherwise cover it halfway.
        -->
        <div class="md-editor-connection-anchor">
          <div class="md-editor-connection">
            <ConnectionDot :state="state" />
            <CollaborationNotice :state="state" :compact="variant === 'compact'" :folded="panelOpen" />
          </div>
        </div>
        <slot name="inspector" :controller="controller" :state="state"><PluginPanels :controller="controller" :variant="variant" placement="inspector" /></slot>
        <slot name="status" :controller="controller" :state="state"><PluginPanels :controller="controller" :variant="variant" placement="status" /></slot>
        <footer v-if="footerShown" class="paper-footer">
          <span v-if="showProfileLabel && markdownLossy !== null" role="status" aria-live="polite"
            :title="markdownLossy ? surfaceStrings.richDocumentLossyTitle : surfaceStrings.markdownLosslessTitle"
          >{{ markdownLossy ? surfaceStrings.richDocumentLabel : surfaceStrings.markdownLabel }}</span>
          <span v-if="footerVisible" class="md-keyboard-hint">{{ surfaceStrings.blockControlHint }}</span>
          <button v-if="fullscreenable" type="button" class="paper-footer-fs" data-testid="editor-fullscreen-toggle"
            :aria-label="fullscreen ? markdownStrings.exitFullscreen : markdownStrings.fullscreen" @click="toggleFullscreen">
            <FullscreenExitIcon v-if="fullscreen" /><FullscreenIcon v-else />
          </button>
        </footer>
      </div>
    </MarkdownEditorRoot>
  </Teleport>
</template>

<style>
/* As wide as the shell (`left` too): a notice that comes or goes moves no edge of the column, so it is no layout shift (4.0, CLS). */
.md-editor-connection {
  position: absolute; top: 8px; right: 8px; left: 8px; z-index: var(--md-layer-surface, 4);
  display: flex; flex-direction: column; align-items: flex-end; gap: 6px;
  max-width: calc(100% - 16px); pointer-events: none;
}
.md-editor-connection > * { pointer-events: auto; }
.md-editor[data-variant="compact"] .md-editor-connection { top: 4px; right: 4px; gap: 4px; }
/* The outline owns the right edge (44px rail at right: 12px, plus its trigger). Step clear of it. */
.md-editor.has-outline .md-editor-connection { right: 64px; }
/* Fullscreen puts its own header in the top strip; the column clears it. */
.md-editor.is-fullscreen .md-editor-connection { top: 52px; }
/* A compact editor's column is the shell's own (its anchor takes no box); a document's sticks to the bottom of the shell in view. */
.md-editor-connection-anchor { display: contents; }
.md-editor:not([data-variant="compact"]) .md-editor-connection-anchor {
  display: block; position: sticky; bottom: 0; height: 0; z-index: var(--md-layer-surface, 4); pointer-events: none;
}
.md-editor:not([data-variant="compact"]) .md-editor-connection { top: auto; bottom: 12px; z-index: auto; }
/* Clear the 48px document footer wherever one is rendered, as the messages do. */
.md-editor.paper:not(.readonly) .md-editor-connection { bottom: 56px; }
/* Anchored to the shell's bottom edge and sized by its content, so the stack grows upward. */
.md-editor-messages {
  position: absolute; left: 0; right: 0; bottom: 8px; z-index: var(--md-layer-message, 3);
  display: flex; flex-direction: column; align-items: flex-start; gap: 6px;
  padding: 0 14px; pointer-events: none;
}
/* Clear the 48px document footer wherever one is rendered. */
.md-editor.paper:not(.readonly) .md-editor-messages { bottom: 56px; }
.md-editor[data-variant="compact"] .md-editor-messages { bottom: 4px; padding: 0 8px; }
.md-editor-messages > * {
  max-width: min(460px, 100%); margin: 0; padding: 7px 11px;
  border: 1px solid var(--line, #dde4e6); border-radius: 9px; background: var(--paper, #fff);
  box-shadow: 0 8px 24px rgb(14 56 66 / 12%); pointer-events: auto;
}
.md-drop-error { color: var(--danger, #b42318); font-size: 13px; }
.md-drop-status { color: var(--text-dim, #64748b); font-size: 13px; }
.md-removal-notice { display: flex; align-items: center; gap: 8px; color: var(--text, #1f2933); }
.md-removal-notice > span { flex: 1; }
.md-editor-presence { display: flex; align-items: center; min-height: 22px; margin: 8px 16px 0; }
.md-editor[data-variant="compact"] .editor-body {
  font-size: 14px;
  line-height: 1.5;
  padding: 6px 10px;
  height: calc(var(--md-visible-lines, 2) * 21px + 12px);
  min-height: 0;
  overflow-y: auto;
  box-sizing: border-box;
}
.md-editor[data-variant="compact"] .editor-body .ProseMirror { min-height: 0; }
.md-editor[data-variant="compact"] .editor-body p { margin: 0; }
</style>
