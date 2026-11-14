<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, useId, watch } from 'vue'
import { posToDOMRect, type Editor } from '@tiptap/core'
import { BubbleMenu } from '@tiptap/vue-3/menus'
import type { MiddlewareState } from '@floating-ui/dom'
import type { MarkdownProfile } from '../createMarkdownKit'
import type { MarkdownCommand, MarkdownEditorController } from '../core/types'
import type { MarkdownAction, MarkdownReferenceProvider } from '../core/plugins/types'
import type { LinkSearch } from './linkSearch'
import { MarkdownIcon } from '../icons'
import ActionIcon from './ActionIcon.vue'
import { legacyIconName } from './iconMarkup'
import { useToolbarCommands } from './useToolbarCommands'
import { useAnchoredPopover } from './useAnchoredPopover'
import { isMarkdownComposition, trackMarkdownComposition } from '../features/modules/composition'
import { formattingGroups, formattingItems, type MarkdownFormattingItem } from './formatting'
import { NodeSelection, PluginKey, type EditorState } from '@tiptap/pm/state'
import { CellSelection } from '@tiptap/pm/tables'
import { NodeRangeSelection } from '@tiptap/extension-node-range'
import {
  BoldIcon, ItalicIcon, UnderlineIcon, StrikeIcon, HighlightIcon,
  CodeIcon, CodeBlockIcon, LinkIcon, BulletListIcon, OrderedListIcon,
  TaskListIcon, QuoteIcon, AlignLeftIcon, AlignCenterIcon, AlignRightIcon, ChevronDownIcon,
  PilcrowIcon, Heading1Icon, Heading2Icon, Heading3Icon, Heading4Icon, Heading5Icon, Heading6Icon,
} from './icons'
import { markdownStrings } from './strings'
import { toolbarStrings } from './strings/toolbar'
import { HEADING_LEVELS } from '../core/documentConstraints'
import { MARKDOWN_OPEN_LINK_EVENT, formatMarkdownShortcut, markdownKeyboardShortcuts } from '../core/keyboardShortcuts'
import { turnBlockInto } from '../features/intentions/turnInto'
import { slashStrings } from './strings/slash'
import { copyMarkdownOverlayTheme } from '../core/overlayTheme'
import { flushDomSelection } from '../core/internal'
import { writeMarkdownClipboard } from '../features/modules/readActions'

const props = withDefaults(defineProps<{ editor: Editor; profile: MarkdownProfile; boundary?: HTMLElement | null; compact?: boolean; formatting?: readonly MarkdownFormattingItem[]; controller?: MarkdownEditorController; bodyBubble?: boolean
  /** False when the host draws its own bar over a reader's selection: one bar for readers. */
  readerBubble?: boolean }>(), { readerBubble: true })
const visibleItems = computed(() => formattingItems(!!props.compact, props.profile === 'rich', props.formatting))
const visible = (item: MarkdownFormattingItem) => visibleItems.value.has(item)
const groupVisible = (index: number) => formattingGroups[index].some(visible)
const separatorBefore = (index: number) => groupVisible(index) && formattingGroups.slice(0, index).some(group => group.some(visible))
const ownerDocument = props.editor.view.dom.ownerDocument
const emit = defineEmits<{ (event: 'popover-open', open: boolean): void }>()
const { query, run, isActive, revision } = useToolbarCommands(() => props.editor, () => props.profile)
// Plugins add selection actions (e.g. hiding the selected blocks under invisible ink) after the block tools.
const selectionActions = computed(() => {
  void revision.value
  return props.compact ? [] : props.controller?.actions.selection.filter(action => (!action.variants || action.variants.includes('document')) && (!action.hideWhenDisabled || props.controller!.query(action.command).enabled)) ?? []
})
watch(selectionActions, actions => {
  const focused = ownerDocument.activeElement as HTMLElement | null
  const id = focused?.dataset.selectionAction
  if (id && !actions.some(action => action.id === id)) props.editor.view.focus()
})
const actionIcon = (action: MarkdownAction) => action.icon?.startsWith('svg:') ? legacyIconName(action.icon.slice(4)) : null
function actionState(action: MarkdownAction) { void revision.value; return props.controller?.query(action.command) ?? { enabled: false, active: false } }
function runAction(action: MarkdownAction) {
  if (!props.controller || !actionState(action).enabled) return
  props.editor.commands.focus()
  props.controller.dispatch(action.command)
}

const headingMenuOpen = ref(false)
const linkMenuOpen = ref(false)
const linkValue = ref('')
const linkError = ref('')
const linkPopupId = useId()
const linkErrorId = useId()
const linkInputId = useId()
const headingPopupId = useId()
const linkInput = ref<HTMLInputElement | null>(null)
const linkLabel = ref<HTMLElement | null>(null)
const bubbleKey = new PluginKey('formattingBubble')
const linkPopup = ref<HTMLElement | null>(null)
let linkComposition: ReturnType<typeof trackMarkdownComposition> | undefined
watch(linkPopup, (element, _previous, onCleanup) => {
  linkComposition = element ? trackMarkdownComposition(element) : undefined
  const composition = linkComposition
  onCleanup(() => { composition?.destroy(); if (linkComposition === composition) linkComposition = undefined })
}, { flush: 'post' })
const linkTrigger = ref<HTMLElement | null>(null)
const headingPopup = ref<HTMLElement | null>(null)
const headingTrigger = ref<HTMLElement | null>(null)
useAnchoredPopover(() => headingTrigger.value, () => headingPopup.value, undefined, { editor: props.editor, close: () => { headingMenuOpen.value = false } })
useAnchoredPopover(() => ({
  contextElement: props.editor.view.dom,
  getBoundingClientRect: () => {
    const { from, to } = props.editor.state.selection
    return posToDOMRect(props.editor.view, from, to)
  },
}), () => linkPopup.value, () => {
  linkInput.value?.focus({ preventScroll: true })
  linkInput.value?.select()
}, { editor: props.editor, close: () => { linkMenuOpen.value = false } })
// Captured once: floating-ui measures after a tick, when the editor may already be gone (its view then throws).
const editorDom = props.editor.view.dom
/** The editor column below the page's sticky bar: the room the bubble may stand in, and the reach of a selection that is still seen. */
function bubbleRoom() {
  const box = (props.boundary ?? editorDom).getBoundingClientRect()
  const bar = parseFloat(getComputedStyle(editorDom).getPropertyValue('--atlas-workspace-bar-height')) || 0
  const top = Math.max(box.top, bar)
  return { x: box.left, y: top, width: box.width, height: Math.max(0, box.bottom - top) }
}
const bubbleOptions = computed(() => ({
  placement: 'top' as const,
  ...props.bodyBubble ? { strategy: 'fixed' as const } : {},
  flip: () => ({ boundary: bubbleRoom(), padding: 8 }),
  // A selection scrolled under the sticky bar takes the bubble with it.
  // floating-ui counts a reference that only touches the edge as hidden (0px overflow), which on a page without layout
  // hides every bubble: it goes once the selection is a pixel past the edge.
  hide: () => ({ boundary: bubbleRoom(), padding: -1 }),
  // Over the editor, not over the page's sidebar (UX ED-8): the bubble wraps within the column (never narrower than a
  // small inset field's own width, which it then leaves on the right).
  size: () => ({ apply: ({ elements }: MiddlewareState) => { elements.floating.style.maxWidth = `${Math.max(bubbleRoom().width - 16, 320)}px` } }),
  shift: (state: MiddlewareState) => {
    const box = bubbleRoom()
    return { padding: 8, boundary: { ...box, width: Math.max(box.width, state.rects.floating.width + 16) } }
  },
  onHide: () => { headingMenuOpen.value = false },
}))
// UX BD-10: a host whose editor sits in a zoomed, clipping canvas (a board) puts the bubble on <body>, in an editor-classed
// box without a box of its own (the bubble styles hang on .md-editor) that carries the editor's theme tokens.
let bodyHost: HTMLElement | undefined, unmounted = false
function bubbleHost(): HTMLElement {
  // A late show (the menu's update timer) after the toolbar went: nothing lands on <body>.
  if (unmounted) return ownerDocument.createElement('div')
  if (!bodyHost?.isConnected) {
    bodyHost = ownerDocument.createElement('div'); bodyHost.className = 'md-editor md-bubble-host'; bodyHost.style.display = 'contents'
    ownerDocument.body.append(bodyHost)
  }
  copyMarkdownOverlayTheme(editorDom, bodyHost)
  return bodyHost
}
// The tool buttons are drawn the first time the bubble is wanted, not with every editor that mounts.
const built = ref(false)
// J5.4: a reader's bubble holds only the read actions (Kopírovat, Kopírovat odkaz), never the formatting.
const reader = ref(false)
function shouldShowBubble(args: { editor: Editor; state: EditorState; element?: HTMLElement }): boolean {
  const show = wantsBubble(args)
  if (show) { built.value = true; reader.value = !args.editor.isEditable }
  return show
}
// A read-only view never takes the focus: the reader's text selection lies in it instead.
function readerSelects(editor: Editor): boolean {
  // The menu's delayed update may still run after the editor went.
  const selection = editor.isEditable || editor.isDestroyed ? null : ownerDocument.getSelection()
  return !!selection && !selection.isCollapsed && editor.view.dom.contains(selection.anchorNode)
}
function wantsBubble({ editor, state, element }: { editor: Editor; state: EditorState; element?: HTMLElement }): boolean {
  const selection = state.selection
  // A caret (every transaction of an opening page) shows no bubble: decided before the focus and DOM selection are read.
  if (selection.empty) return false
  const focus = ownerDocument.activeElement
  const ownsFocus = editor.isFocused || !!element?.contains(focus) || !!headingPopup.value?.contains(focus) || focus === headingTrigger.value || readerSelects(editor)
  return visibleItems.value.size > 0 && ownsFocus && !selection.empty && (editor.isEditable || props.readerBubble)
    && !(selection instanceof NodeSelection) && !(selection instanceof CellSelection) && !(selection instanceof NodeRangeSelection)
    && state.doc.resolve(selection.from).parent.type.name !== 'codeBlock'
}
// The heading the reader's selection starts in, linked by heading-anchors' builder (the host's page address), when it has one.
function readerHeadingUrl(): string | undefined {
  void revision.value
  const heading = props.editor.state.selection.$from.parent
  const build = (props.editor.storage as unknown as Record<string, { headingUrl?: (id: unknown) => string | undefined }>).uniqueID?.headingUrl
  return heading.type.name === 'heading' ? build?.(heading.attrs.headingId) : undefined
}
// The copied button says „Zkopírováno“ for a moment, in place (its label keeps the room of the longer text); a status line reads it out.
const copied = ref<'copy' | 'link' | null>(null)
let copiedTimer: ReturnType<typeof setTimeout> | undefined
async function confirmCopy(which: 'copy' | 'link', done: boolean | Promise<boolean>) {
  if (!await done || unmounted) return
  copied.value = which; clearTimeout(copiedTimer)
  copiedTimer = setTimeout(() => { copied.value = null }, 1200)
}
function copySelection() {
  // The browser's own copy keeps the copied formatting; the plain text when it refuses.
  let done: boolean | Promise<boolean> = false
  try { done = ownerDocument.execCommand('copy') } catch { /* refused */ }
  if (!done) { const { from, to } = props.editor.state.selection; done = writeMarkdownClipboard(ownerDocument, props.editor.state.doc.textBetween(from, to, '\n')) }
  void confirmCopy('copy', done)
}
const toggleCommands = {
  bold: { type: 'toggle-bold' }, italic: { type: 'toggle-italic' },
  underline: { type: 'toggle-underline' }, strike: { type: 'toggle-strike' },
  highlight: { type: 'toggle-highlight' }, code: { type: 'toggle-code' },
  bullet: { type: 'toggle-list', list: 'bullet' }, ordered: { type: 'toggle-list', list: 'ordered' },
  task: { type: 'toggle-list', list: 'task' }, timed: { type: 'toggle-list', list: 'timed' }, quote: { type: 'toggle-blockquote' },
  codeblock: { type: 'toggle-code-block' },
} as const satisfies Record<string, MarkdownCommand>
type BlockKind = 'paragraph' | `h${(typeof HEADING_LEVELS)[number]}`
const headingIcons = [Heading1Icon, Heading2Icon, Heading3Icon, Heading4Icon, Heading5Icon, Heading6Icon]

function currentBlockKind(): BlockKind {
  const level = HEADING_LEVELS.find(level => isActive('heading', { level }))
  return level ? `h${level}` : 'paragraph'
}

function setBlockKind(kind: BlockKind) {
  run(kind === 'paragraph' ? { type: 'set-paragraph' } : { type: 'set-heading', level: Number(kind.slice(1)) as (typeof HEADING_LEVELS)[number] })
  headingMenuOpen.value = false
  // At once, before the picked item leaves the page: tiptap's focus waits a frame, and a key meanwhile reached <body>.
  props.editor.view.focus()
}

// „Informační box“ in „Typ bloku“ (contract §5.4): the block menu's own turn-into, its checked state the callout around the caret.
const calloutAvailable = computed(() => props.profile === 'rich' && !!props.editor.schema.nodes.callout)
function setCallout() {
  headingMenuOpen.value = false
  const { $from } = props.editor.state.selection
  if (!isActive('callout')) turnBlockInto(props.editor, $from.depth ? $from.before(1) : $from.pos, { node: 'callout', attrs: { variant: 'note' } })
  props.editor.view.focus()
}
// The menu goes when the keyboard leaves it (to neither an item nor its trigger).
function onKindFocusOut(event: FocusEvent) {
  const next = event.relatedTarget as Node | null
  if (!headingPopup.value?.contains(next) && !headingTrigger.value?.contains(next)) headingMenuOpen.value = false
}
// The tooltip names the key from the one shortcut table („Přeškrtnuté (Ctrl+Shift+S)“); a title that already has one stays.
function keyHint(title: string, keys: string) {
  const shortcut = title.includes('(') ? undefined : markdownKeyboardShortcuts.find(item => item.keys.join(' ') === keys)
  return shortcut ? `${title} (${formatMarkdownShortcut(shortcut.keys)})` : title
}

// Read at render time so a host's setMarkdownStrings (which runs after this module's imports) is still honored.
const blockKindLabel = (kind: BlockKind) => kind === 'paragraph' ? toolbarStrings.textBlockLabel : toolbarStrings[`heading${kind.slice(1) as `${(typeof HEADING_LEVELS)[number]}`}Label`]

function openLink() {
  if (!props.editor.isEditable) return
  if (linkMenuOpen.value) { cancelLink(); return }
  headingMenuOpen.value = false
  linkValue.value = props.editor.getAttributes('link').href ?? ''
  linkError.value = ''
  linkMenuOpen.value = true
  // The bubble steps aside while the field is open: flipped under the text it covered the field. Back with the editor's focus.
  props.editor.view.dispatch(props.editor.state.tr.setMeta(bubbleKey, 'hide'))
}

// UX WR3-3: over reference providers that search (the pages plugin), the link field also finds pages by name (linkSearch.ts, lazy).
let linkSearch: LinkSearch | undefined, linkSearchReady: Promise<void> | undefined
watch(linkMenuOpen, open => {
  linkSearch?.destroy(); linkSearch = linkSearchReady = undefined
  const sources = open ? ((props.editor.storage as unknown as Record<string, { providers?: readonly MarkdownReferenceProvider[] }>)
    .markdownReferences?.providers ?? []).filter(provider => provider.search) : []
  if (!sources.length) return
  const ready: Promise<void> = linkSearchReady = import('./linkSearch').then(({ createLinkSearch }) => {
    if (ready !== linkSearchReady || !linkInput.value || !linkLabel.value) return
    linkSearch = createLinkSearch({ input: linkInput.value, label: linkLabel.value, sources, pick: target => linkTo(target.url, target.label), error: message => { linkError.value = message } })
    if (linkValue.value) linkSearch.input(linkValue.value)
  }).catch(() => {}).finally(() => { if (ready === linkSearchReady) linkSearchReady = undefined })
}, { flush: 'post' })
function onLinkInput() {
  linkError.value = ''
  linkSearch?.input(linkValue.value)
}
/** Links the selection, the link under the caret, or (a caret elsewhere) inserts the label as a new link. */
function linkTo(href: string, label: string): void {
  const editor = props.editor
  const inserts = editor.state.selection.empty && !editor.isActive('link')
  const done = inserts
    ? query({ type: 'set-link', href }).enabled && editor.chain().focus().insertContent({ type: 'text', text: label, marks: [{ type: 'link', attrs: { href } }] }).run()
    : run({ type: 'set-link', href })
  if (done) { linkMenuOpen.value = false; editor.view.focus(); return }
  linkError.value = toolbarStrings.linkSaveError
  linkInput.value?.focus({ preventScroll: true })
}
function confirmLink() {
  if (linkComposition?.active()) return
  const value = linkValue.value.trim()
  if (!value) { removeLink(); return }
  // Enter before the page search loaded: once it has.
  if (linkSearchReady && !linkSearch) { void linkSearchReady.then(confirmLink); return }
  const href = linkSearch ? linkSearch.confirm(value) : value
  if (href !== null) linkTo(href, href)
}
function removeLink() {
  if (linkComposition?.active()) return
  if (run({ type: 'unset-link' })) { linkMenuOpen.value = false; props.editor.view.focus() }
}
function onEscape(event: KeyboardEvent) {
  if (linkComposition?.active() || isMarkdownComposition(event, props.editor)) return
  if (event.key !== 'Escape' || (!headingMenuOpen.value && !linkMenuOpen.value)) return
  event.preventDefault()
  // J6.3: from the open „Typ bloku“ list the focus goes back to its button; otherwise to the text.
  const active = ownerDocument.activeElement, trigger = headingTrigger.value
  const toTrigger = headingMenuOpen.value && !!trigger && (active === trigger || !!headingPopup.value?.contains(active))
  headingMenuOpen.value = false
  linkMenuOpen.value = false
  // Restore the native selection synchronously; Tiptap delays this on Safari.
  if (toTrigger) trigger!.focus({ preventScroll: true })
  else props.editor.view.focus()
  event.stopPropagation()
}
function onKeydown(event: KeyboardEvent) {
  if (linkComposition?.active() || isMarkdownComposition(event, props.editor)) return
  onEscape(event)
  // A reader's keys reach the page, not the read-only view (which takes no focus): Alt+F10 and Esc over its selection from here.
  if (!props.editor.isDestroyed && !props.editor.isEditable) onEditorKeydown(event)
}
function onReaderSelection() { if (!props.editor.isDestroyed && !props.editor.isEditable) onFocusMoved() }
// The host's link button asks for the link field (Ctrl+K is Atlas's search, so the editor binds no key for it).
function onOpenLink(event: Event) {
  if (!props.editor.isEditable) return
  event.preventDefault()
  void openLink()
}
function onLinkKeydown(event: KeyboardEvent) {
  if (linkComposition?.active() || isMarkdownComposition(event, props.editor)) return
  if (linkSearch?.keydown(event)) { event.preventDefault(); return }
  if (event.key === 'Enter') { event.preventDefault(); confirmLink() }
  else onEscape(event)
}
// The keyboard route into the bubble (UX PG4-1): Alt+F10 over a text selection, as into the editor's other context toolbars and
// the board's; without a text selection Alt+F10 stays the block's controls (BlockHandle skips a handled event). Arrows, Home and
// End move between the buttons, Esc returns to the text with its selection.
const bubbleId = useId()
const bubbleElement = () => ownerDocument.querySelector<HTMLElement>(`[data-md-bubble="${bubbleId}"]`)
const bubbleButtons = (bubble: HTMLElement) => [...bubble.querySelectorAll<HTMLButtonElement>('.bb:not(:disabled)')].filter(button => !button.closest('.bb-popover'))
function onEditorKeydown(event: KeyboardEvent) {
  // J5.2 / J6.3: the first Esc over a text selection hides the bubble and keeps the selection (it shows again when the selection
  // changes); the next one goes on as Esc does in the text (the block around it selected).
  if (event.key === 'Escape' && !event.defaultPrevented && !isMarkdownComposition(event, props.editor)) {
    const bubble = bubbleElement()
    if (!bubble || bubble.style.visibility === 'hidden' || props.editor.state.selection.empty) return
    event.preventDefault(); event.stopImmediatePropagation()
    props.editor.view.dispatch(props.editor.state.tr.setMeta(bubbleKey, 'hide'))
    return
  }
  if (event.defaultPrevented || event.key !== 'F10' || !event.altKey || event.ctrlKey || event.metaKey || event.shiftKey) return
  // Right after a selection the state may not have it yet, nor the bubble its delayed show: both happen now.
  flushDomSelection(props.editor)
  if (!shouldShowBubble({ editor: props.editor, state: props.editor.state, element: bubbleElement() ?? undefined })) return
  event.preventDefault()
  props.editor.view.dispatch(props.editor.state.tr.setMeta(bubbleKey, 'show'))
  props.editor.view.dispatch(props.editor.state.tr.setMeta(bubbleKey, 'updatePosition'))
  void nextTick(() => { const bubble = bubbleElement(); if (bubble) bubbleButtons(bubble)[0]?.focus() })
}
function onBubbleKeydown(event: KeyboardEvent) {
  const target = event.target as HTMLElement
  const bubble = target.closest<HTMLElement>(`[data-md-bubble="${bubbleId}"]`)
  if (!bubble || target.closest('.bb-popover')) return
  if (event.key === 'Escape' && headingMenuOpen.value) { onEscape(event); return }
  if (event.key === 'Escape') {
    event.preventDefault(); event.stopPropagation()
    // A read-only view takes no focus: the reader's bubble goes, the focus leaves it and the selection stays.
    if (reader.value) { props.editor.view.dispatch(props.editor.state.tr.setMeta(bubbleKey, 'hide')); target.blur() }
    props.editor.view.focus(); return
  }
  const buttons = bubbleButtons(bubble), index = buttons.indexOf(target as HTMLButtonElement)
  if (index < 0) return
  const next = event.key === 'ArrowRight' ? (index + 1) % buttons.length : event.key === 'ArrowLeft' ? (index - 1 + buttons.length) % buttons.length
    : event.key === 'Home' ? 0 : event.key === 'End' ? buttons.length - 1 : -1
  if (next < 0) return
  event.preventDefault()
  buttons[next]!.focus()
}
function onOutsidePointer(event: PointerEvent) {
  const target = event.target as Node | null
  if (!target) return
  if (!linkPopup.value?.contains(target) && !linkTrigger.value?.contains(target)) linkMenuOpen.value = false
  if (!headingPopup.value?.contains(target) && !headingTrigger.value?.contains(target)) headingMenuOpen.value = false
}
// A block's own field (a node view's input under the bubble's parent, which the menu's blur check skips) or a popover took the
// focus: the bubble belongs to the text selection, so it steps aside rather than draw over them.
function onFocusMoved() {
  if (props.editor.isDestroyed) return
  // Hidden, the bubble is out of the page or invisible: nothing to hide, no transaction.
  const bubble = bubbleElement()
  if (bubble && bubble.style.visibility !== 'hidden' && !shouldShowBubble({ editor: props.editor, state: props.editor.state, element: bubble })) props.editor.view.dispatch(props.editor.state.tr.setMeta(bubbleKey, 'hide'))
}
function cancelLink() {
  linkMenuOpen.value = false
  props.editor.view.focus()
}
watch([headingMenuOpen, linkMenuOpen], ([heading, link]) => emit('popover-open', heading || link))
watch(visibleItems, () => {
  if (!visible('headings')) headingMenuOpen.value = false
  if (!visible('link')) linkMenuOpen.value = false
})
onMounted(() => {
  ownerDocument.addEventListener('keydown', onKeydown)
  ownerDocument.addEventListener('pointerdown', onOutsidePointer)
  ownerDocument.addEventListener('focusin', onFocusMoved)
  ownerDocument.addEventListener('selectionchange', onReaderSelection)
  props.editor.view.dom.addEventListener(MARKDOWN_OPEN_LINK_EVENT, onOpenLink)
  props.editor.view.dom.addEventListener('keydown', onEditorKeydown, true)
})
onBeforeUnmount(() => {
  ownerDocument.removeEventListener('keydown', onKeydown)
  ownerDocument.removeEventListener('pointerdown', onOutsidePointer)
  ownerDocument.removeEventListener('focusin', onFocusMoved)
  ownerDocument.removeEventListener('selectionchange', onReaderSelection)
  if (!props.editor.isDestroyed) {
    props.editor.view.dom.removeEventListener(MARKDOWN_OPEN_LINK_EVENT, onOpenLink)
    props.editor.view.dom.removeEventListener('keydown', onEditorKeydown, true)
  }
  linkSearch?.destroy()
  emit('popover-open', false)
  unmounted = true; bodyHost?.remove(); clearTimeout(copiedTimer)
})
</script>

<template>
  <!-- BubbleMenu portals its root; keep a local anchor when the tool list becomes empty. -->
  <div v-if="visibleItems.size" style="display: contents">
    <BubbleMenu
      :editor="props.editor"
      :update-delay="80"
      :should-show="shouldShowBubble"
      :options="bubbleOptions"
      :append-to="bodyBubble ? bubbleHost : undefined"
      @mousedown.prevent
      @keydown="onBubbleKeydown"
      :data-md-bubble="bubbleId"
      :plugin-key="bubbleKey"
      class="bubble-menu"
      role="group"
      :aria-label="reader ? toolbarStrings.readerGroupAriaLabel : toolbarStrings.formattingGroupAriaLabel">
      <template v-if="built && reader">
        <button type="button" class="bb" data-testid="bubble-copy" :data-feedback="copied === 'copy' ? 'success' : undefined" :title="toolbarStrings.copyTitle" :aria-label="toolbarStrings.copyTitle" @click="copySelection">
          <MarkdownIcon :name="copied === 'copy' ? 'check' : 'copy'" :size="16" /><span class="bb-said" :data-alt="toolbarStrings.copiedTitle">{{ copied === 'copy' ? toolbarStrings.copiedTitle : toolbarStrings.copyTitle }}</span>
        </button>
        <button v-if="readerHeadingUrl()" type="button" class="bb" data-testid="bubble-copy-link" :data-feedback="copied === 'link' ? 'success' : undefined" :title="toolbarStrings.copyLinkTitle" :aria-label="toolbarStrings.copyLinkTitle" @click="confirmCopy('link', writeMarkdownClipboard(ownerDocument, readerHeadingUrl()!))">
          <MarkdownIcon v-if="copied === 'link'" name="check" :size="16" /><LinkIcon v-else /><span class="bb-said" :data-alt="toolbarStrings.copiedTitle">{{ copied === 'link' ? toolbarStrings.copiedTitle : toolbarStrings.copyLinkTitle }}</span>
        </button>
        <span class="bb-status" role="status" aria-live="polite">{{ copied ? toolbarStrings.copiedTitle : '' }}</span>
      </template>
      <template v-else-if="built">
      <div v-if="visible('headings')" class="bb-group">
        <button
          type="button"
          class="bb bb-kind"
          ref="headingTrigger"
          :class="{ on: headingMenuOpen }"
          aria-haspopup="menu"
          :aria-expanded="headingMenuOpen"
          :aria-controls="headingMenuOpen ? headingPopupId : undefined"
          :title="toolbarStrings.blockTypeTitle(blockKindLabel(currentBlockKind()))"
          data-testid="bubble-kind"
          @click.stop="headingMenuOpen = !headingMenuOpen">
          <span class="bb-kind-label">{{ blockKindLabel(currentBlockKind()) }}</span>
          <ChevronDownIcon class="bb-kind-chevron"/>
        </button>
        <div v-if="headingMenuOpen" :id="headingPopupId" ref="headingPopup" class="bb-popover" role="menu" :aria-label="toolbarStrings.blockTypeMenuAriaLabel" @click.stop @keydown.escape="onEscape" @focusout="onKindFocusOut">
          <button type="button" role="menuitemradio" class="bb-item" :class="{ on: currentBlockKind() === 'paragraph' }" :aria-checked="currentBlockKind() === 'paragraph'" @click="setBlockKind('paragraph')">
            <span class="bb-item-mark"><PilcrowIcon/></span>
            <span class="bb-item-label">{{ blockKindLabel('paragraph') }}</span>
          </button>
          <button v-for="level in HEADING_LEVELS" :key="level" type="button" role="menuitemradio" class="bb-item" :class="{ on: currentBlockKind() === `h${level}` }" :aria-checked="currentBlockKind() === `h${level}`" @click="setBlockKind(`h${level}`)">
            <span class="bb-item-mark"><component :is="headingIcons[level - 1]"/></span>
            <span class="bb-item-label">{{ blockKindLabel(`h${level}`) }}</span>
          </button>
          <button v-if="calloutAvailable" type="button" role="menuitemradio" class="bb-item" :class="{ on: isActive('callout') }" :aria-checked="isActive('callout')" data-testid="bubble-kind-callout" @click="setCallout">
            <span class="bb-item-mark"><MarkdownIcon name="info" :size="16"/></span>
            <span class="bb-item-label">{{ slashStrings.calloutTitle }}</span>
          </button>
        </div>
      </div>
      <span v-if="separatorBefore(1)" class="sep" aria-hidden="true"/>
      <button v-if="visible('bold')" type="button" class="bb" :class="{ on: query(toggleCommands.bold).active }" :aria-pressed="query(toggleCommands.bold).active" :title="toolbarStrings.boldTitle" :aria-label="toolbarStrings.boldTitle" data-testid="bubble-bold" :disabled="!query(toggleCommands.bold).enabled" @click="run(toggleCommands.bold)">
        <BoldIcon/>
      </button>
      <button v-if="visible('italic')" type="button" class="bb" :class="{ on: query(toggleCommands.italic).active }" :aria-pressed="query(toggleCommands.italic).active" :title="toolbarStrings.italicTitle" :aria-label="toolbarStrings.italicTitle" data-testid="bubble-italic" :disabled="!query(toggleCommands.italic).enabled" @click="run(toggleCommands.italic)">
        <ItalicIcon/>
      </button>
      <button v-if="visible('underline')" type="button" class="bb" :class="{ on: query(toggleCommands.underline).active }" :aria-pressed="query(toggleCommands.underline).active" :title="toolbarStrings.underlineTitle" :aria-label="toolbarStrings.underlineTitle" :disabled="!query(toggleCommands.underline).enabled" @click="run(toggleCommands.underline)">
        <UnderlineIcon/>
      </button>
      <button v-if="visible('strike')" type="button" class="bb" :class="{ on: query(toggleCommands.strike).active }" :aria-pressed="query(toggleCommands.strike).active" :title="keyHint(toolbarStrings.strikeTitle, 'Mod Shift S')" :aria-label="keyHint(toolbarStrings.strikeTitle, 'Mod Shift S')" :disabled="!query(toggleCommands.strike).enabled" @click="run(toggleCommands.strike)">
        <StrikeIcon/>
      </button>
      <button v-if="visible('highlight')" type="button" class="bb" :class="{ on: query(toggleCommands.highlight).active }" :aria-pressed="query(toggleCommands.highlight).active" :title="keyHint(toolbarStrings.highlightTitle, 'Mod Shift H')" :aria-label="keyHint(toolbarStrings.highlightTitle, 'Mod Shift H')" :disabled="!query(toggleCommands.highlight).enabled" @click="run(toggleCommands.highlight)">
        <HighlightIcon/>
      </button>
      <button v-if="visible('code')" type="button" class="bb" :class="{ on: query(toggleCommands.code).active }" :aria-pressed="query(toggleCommands.code).active" :title="keyHint(toolbarStrings.inlineCodeTitle, 'Mod E')" :aria-label="keyHint(toolbarStrings.inlineCodeTitle, 'Mod E')" :disabled="!query(toggleCommands.code).enabled" @click="run(toggleCommands.code)">
        <CodeIcon/>
      </button>
      <span v-if="separatorBefore(2)" class="sep" aria-hidden="true"/>
      <button v-if="visible('link')" ref="linkTrigger" type="button" class="bb" :class="{ on: isActive('link') }" :title="toolbarStrings.linkTitle" :aria-label="toolbarStrings.linkTitle" :aria-expanded="linkMenuOpen" :aria-controls="linkMenuOpen ? linkPopupId : undefined" @click="openLink">
        <LinkIcon/>
      </button>
      <template v-if="groupVisible(3)">
      <span v-if="separatorBefore(3)" class="sep" aria-hidden="true"/>
      <button v-if="visible('bullet-list')" type="button" class="bb" :class="{ on: query(toggleCommands.bullet).active }" :aria-pressed="query(toggleCommands.bullet).active" :title="keyHint(toolbarStrings.bulletListTitle, 'Mod Shift 8')" :aria-label="toolbarStrings.bulletListTitle" :disabled="!query(toggleCommands.bullet).enabled" @click="run(toggleCommands.bullet)">
        <BulletListIcon/>
      </button>
      <button v-if="visible('ordered-list')" type="button" class="bb" :class="{ on: query(toggleCommands.ordered).active }" :aria-pressed="query(toggleCommands.ordered).active" :title="keyHint(toolbarStrings.orderedListTitle, 'Mod Shift 7')" :aria-label="toolbarStrings.orderedListTitle" :disabled="!query(toggleCommands.ordered).enabled" @click="run(toggleCommands.ordered)">
        <OrderedListIcon/>
      </button>
      <button v-if="visible('task-list')" type="button" class="bb" :class="{ on: query(toggleCommands.task).active }" :aria-pressed="query(toggleCommands.task).active" :title="keyHint(toolbarStrings.taskListTitle, 'Mod Shift 9')" :aria-label="toolbarStrings.taskListTitle" :disabled="!query(toggleCommands.task).enabled" @click="run(toggleCommands.task)">
        <TaskListIcon/>
      </button>
      <button v-if="visible('timed-list')" type="button" class="bb" :class="{ on: query(toggleCommands.timed).active }" :aria-pressed="query(toggleCommands.timed).active" :title="toolbarStrings.timedListTitle" :aria-label="toolbarStrings.timedListTitle" :disabled="!query(toggleCommands.timed).enabled" @click="run(toggleCommands.timed)">
        <MarkdownIcon name="clock" :size="16"/>
      </button>
      <button v-if="visible('blockquote')" type="button" class="bb" :class="{ on: query(toggleCommands.quote).active }" :aria-pressed="query(toggleCommands.quote).active" :title="keyHint(toolbarStrings.blockquoteTitle, 'Mod Shift B')" :aria-label="toolbarStrings.blockquoteTitle" :disabled="!query(toggleCommands.quote).enabled" @click="run(toggleCommands.quote)">
        <QuoteIcon/>
      </button>
      <button v-if="visible('code-block')" type="button" class="bb" :class="{ on: query(toggleCommands.codeblock).active }" :aria-pressed="query(toggleCommands.codeblock).active" :title="keyHint(toolbarStrings.codeBlockTitle, 'Mod Alt C')" :aria-label="toolbarStrings.codeBlockTitle" :disabled="!query(toggleCommands.codeblock).enabled" @click="run(toggleCommands.codeblock)">
        <CodeBlockIcon/>
      </button>
      <button v-for="action in selectionActions" :key="action.id" type="button" class="bb" :data-selection-action="action.id"
        :class="{ on: actionState(action).active }" :aria-pressed="actionState(action).active" :title="action.label" :aria-label="action.label"
        :disabled="!actionState(action).enabled" @click="runAction(action)">
        <ActionIcon v-if="action.renderIcon" :render="action.renderIcon" />
        <MarkdownIcon v-else-if="actionIcon(action)" :name="actionIcon(action)!" :size="18" />
        <span v-else>{{ action.label }}</span>
      </button>
      </template>
      <template v-if="visible('alignment')">
      <span v-if="separatorBefore(4)" class="sep" aria-hidden="true"/>
      <button type="button" class="bb" :class="{ on: query({ type: 'set-alignment', alignment: 'left' }).active }" :aria-pressed="query({ type: 'set-alignment', alignment: 'left' }).active" :disabled="!query({ type: 'set-alignment', alignment: 'left' }).enabled" :title="toolbarStrings.alignLeftTitle" :aria-label="toolbarStrings.alignLeftTitle" @click="run({ type: 'set-alignment', alignment: 'left' })">
        <AlignLeftIcon/>
      </button>
      <button type="button" class="bb" :class="{ on: query({ type: 'set-alignment', alignment: 'center' }).active }" :aria-pressed="query({ type: 'set-alignment', alignment: 'center' }).active" :disabled="!query({ type: 'set-alignment', alignment: 'center' }).enabled" :title="toolbarStrings.alignCenterTitle" :aria-label="toolbarStrings.alignCenterTitle" @click="run({ type: 'set-alignment', alignment: 'center' })">
        <AlignCenterIcon/>
      </button>
      <button type="button" class="bb" :class="{ on: query({ type: 'set-alignment', alignment: 'right' }).active }" :aria-pressed="query({ type: 'set-alignment', alignment: 'right' }).active" :disabled="!query({ type: 'set-alignment', alignment: 'right' }).enabled" :title="toolbarStrings.alignRightTitle" :aria-label="toolbarStrings.alignRightTitle" @click="run({ type: 'set-alignment', alignment: 'right' })">
        <AlignRightIcon/>
      </button>
      </template>
      </template>
    </BubbleMenu>
  </div>
    <div v-if="linkMenuOpen" :id="linkPopupId" ref="linkPopup" class="link-popover" role="group" :aria-label="toolbarStrings.editLinkAriaLabel">
      <label ref="linkLabel" :for="linkInputId" class="link-label">{{ toolbarStrings.linkAddressLabel }}</label>
      <input
        :id="linkInputId"
        ref="linkInput"
        v-model="linkValue"
        :aria-label="toolbarStrings.linkAddressLabel"
        :aria-invalid="!!linkError"
        :aria-describedby="linkError ? linkErrorId : undefined"
        type="url"
        :placeholder="toolbarStrings.linkPlaceholder"
        class="link-input"
        @input="onLinkInput"
        @keydown="onLinkKeydown"/>
      <p v-if="linkError" :id="linkErrorId" class="link-error" role="alert">{{ linkError }}</p>
      <div class="link-actions">
        <button v-if="isActive('link')" type="button" class="md-btn" @click="removeLink">{{ toolbarStrings.removeLinkLabel }}</button>
        <button type="button" class="md-btn" @click="cancelLink">{{ markdownStrings.cancel }}</button>
        <button type="button" class="md-btn-primary" @click="confirmLink">{{ markdownStrings.apply }}</button>
      </div>
    </div>

</template>
