<script setup lang="ts">
import { computed, defineAsyncComponent, onBeforeUnmount, ref, watch } from 'vue'
import type { MarkdownOutlineMode, MarkdownOutlineOptions } from './ui/outline'
import type { Editor, AnyExtension } from '@tiptap/core'
import type { SlashCommandItem } from './extensions/SlashCommands'
import type { MarkdownProfile } from './core/types'
import { createMarkdownOverlayOwnerId } from './core/overlay'
import { useLegacyEditor } from './legacy/useLegacyEditor'
import { AlertTriangleIcon, CloseIcon, FullscreenIcon, FullscreenExitIcon } from './ui/icons'
import EditorControls from './ui/EditorControls.vue'
import { acquireFullscreenLock, hasActiveEditorOverlay, hasActiveModalDialog } from './ui/fullscreen'
import { defaultImageUploadAccept } from './extensions/ImageUpload'
import { pickAndUploadImage } from './features/images/imagePicker'
import { createHttpImageUpload, imageUploadErrorEvent } from './features/images/uploadService'
import type { MarkdownResourcePolicy } from './features/images/resourcePolicy'
import './styles/editor.css'
// Keep the package self-contained: the shared local directive replaces a host-registered `v-tooltip`.
import { vTooltip } from './ui/vTooltip'
import { markdownStrings } from './ui/strings'
import { surfaceStrings } from './ui/strings/surface'

interface Props {
  modelValue: string
  /** Accessible name for the editable textbox. */
  ariaLabel?: string
  /**
   * Markdown-safe is portable; rich additionally enables explicitly lossy table features.
   * The profile is immutable for this component instance. Remount with a new `key` to switch it.
   */
  profile?: MarkdownProfile
  placeholder?: string
  imageUploadUrl?: string
  resourcePolicy?: MarkdownResourcePolicy
  minHeight?: string
  readonly?: boolean
  paper?: boolean
  /**
   * Tight inline mode for short editors embedded in dialogs / form fields.
   * Drops the block handle, reduces font size and padding.
   */
  compact?: boolean
  /** Optional document navigation. Panel mode collapses to an icon without a wave rail. */
  outline?: MarkdownOutlineMode | MarkdownOutlineOptions
  /** Hide the block handle even outside compact mode (e.g. read-only previews). */
  hideBlockHandle?: boolean
  showBlockAddButton?: boolean
  showBlockInserter?: boolean
  /** Show a fullscreen toggle (default true for non-readonly editors). */
  fullscreenable?: boolean
  /**
   * Optional context label shown in the fullscreen header bar (e.g. the
   * requirement title, blocker headline, project name). Without it the user
   * sees only the editor body and forgets which surface they're on.
   */
  fullscreenLabel?: string
  /**
   * Realtime collaborative mode (ADR-018). When true the injected Collaboration
   * extension (arriving through the `extensions` prop, produced by `useCollab`)
   * owns the document: StarterKit undo/redo is disabled (y-tiptap provides
   * per-user undo via trackedOrigins), the markdown `content` init is skipped,
   * and the inbound v-model watcher is disabled so a parent echo can't clobber
   * synced remote edits. The `update:modelValue` emit still fires as an
   * advisory, lossy markdown export (offline fallback + word count), never as
   * truth. PoolEditor itself never imports yjs/signalr — collab arrives purely
   * through `extensions`, keeping the standalone bundle pure.
   */
  collaborative?: boolean
  /** Project-specific Tiptap extensions / nodes / marks appended after the core kit. */
  extensions?: AnyExtension[]
  /** Optional compatible codeBlock extension; remount to change the schema configuration. */
  codeBlock?: AnyExtension
  /**
   * Extra slash-menu items contributed by the consumer (e.g. a YouTrack chip).
   * `withImage` is auto-supplied by core when `imageUploadUrl` is set; consumers
   * only fill `extra`.
   */
  slashSuggestionExtra?: SlashCommandItem[]
}

const props = withDefaults(defineProps<Props>(), {
  ariaLabel: () => surfaceStrings.editorAriaLabel,
  profile: 'markdown-safe',
  placeholder: () => surfaceStrings.legacyPlaceholder,
  minHeight: '240px',
  readonly: false,
  paper: false,
  compact: false,
  hideBlockHandle: false,
  showBlockAddButton: true,
  showBlockInserter: false,
  fullscreenable: true,
  fullscreenLabel: '',
  collaborative: false,
  extensions: () => [],
  slashSuggestionExtra: () => [],
})
const DocumentOutline = defineAsyncComponent(() => import('./ui/DocumentOutline.vue'))
const outlineOptions = computed<MarkdownOutlineOptions>(() => typeof props.outline === 'string' ? { mode: props.outline } : props.outline ?? { mode: 'hidden' })
const outlineEnabled = computed(() => !props.compact && outlineOptions.value.mode !== 'hidden')
const outlineDocked = ref(false)
watch(outlineEnabled, () => { outlineDocked.value = false })

// Tiptap's schema is immutable after editor construction. Capture the initial
// profile so the toolbar and schema cannot diverge if a parent mutates the prop.
const editorProfile: MarkdownProfile = props.profile

const emit = defineEmits<{
  (e: 'update:modelValue', value: string): void
  /** Fires once after Tiptap finishes its initial setContent. */
  (e: 'created', editor: Editor): void
  /** Fires every time the editor content is replaced via the v-model watcher. */
  (e: 'content-replaced', editor: Editor): void
}>()

const contentHost = ref<HTMLElement | null>(null)
const { editor, wordCount, charCount, dirty } = useLegacyEditor(props, editorProfile, contentHost, {
  change: value => emit('update:modelValue', value),
  created: editor => emit('created', editor),
  replaced: editor => emit('content-replaced', editor),
}, buildImageSlashItem)

defineExpose({ editor, wordCount, charCount, dirty })

function buildImageSlashItem(): SlashCommandItem | null {
  if (!props.imageUploadUrl) return null
  return {
    title: surfaceStrings.imageSlashTitle,
    description: surfaceStrings.imageSlashDescription,
    searchTerms: ['image', 'obrazek', 'picture', 'foto'],
    icon: 'svg:image',
    command: ({ editor, range }) => {
      editor.chain().focus().deleteRange(range).run()
      uploadImage()
    },
  }
}

onBeforeUnmount(() => {
  if (uploadErrorTimer) { clearTimeout(uploadErrorTimer); uploadErrorTimer = null }
  releaseFullscreenBodyLock(false)
  fullscreenDocument?.removeEventListener('keydown', onFullscreenKeydown, true)
  fullscreenDocument = null
})

const mdEditorRef = ref<HTMLElement | null>(null)
const overlayOwnerId = createMarkdownOverlayOwnerId()
const formattingPopoverOpen = ref(false)
const tablePopoverOpen = ref(false)

// V25 Phase 7 — fullscreen toggle.
const fullscreen = ref(false)

let fullscreenTrigger: HTMLElement | null = null
function toggleFullscreen(event: MouseEvent) {
  if (!fullscreen.value && (props.compact || props.readonly || !props.fullscreenable)) return
  if (!fullscreen.value) fullscreenTrigger = event.currentTarget as HTMLElement
  fullscreen.value = !fullscreen.value
}

function onFullscreenKeydown(event: KeyboardEvent) {
  const owner = mdEditorRef.value
  if (!owner || hasActiveModalDialog(owner)) return
  if (event.key !== 'Escape' || event.isComposing || !fullscreen.value || owner.inert) return
  // Don't exit fullscreen if a popover / suggestion menu is open — the user
  // pressed Esc to dismiss the popover. Round-1 audit showed double-action
  // (Esc closed both menu AND fullscreen) confused users.
  if (
    formattingPopoverOpen.value || tablePopoverOpen.value
  ) return
  if (hasActiveEditorOverlay(owner)) return
  fullscreen.value = false
  // When the editor lives inside a PrimeVue Dialog the dialog's own ESC
  // handler is also waiting on this event — without stopping propagation the
  // user presses ESC once, exits fullscreen, AND immediately closes the
  // dialog underneath, losing their place. Consume the event so the dialog
  // only closes on the user's next ESC. We deliberately don't call
  // `preventDefault()` — ESC has no native default in editable contexts,
  // and IME engines on some browsers use a default-preventable ESC to
  // cancel composition mid-flight.
  event.stopPropagation()
}

let fullscreenDocument: Document | null = null
let focusEditorAfterExit = false
watch(fullscreen, (on) => {
  if (on) {
    fullscreenDocument = mdEditorRef.value?.ownerDocument ?? null
    if (!fullscreenDocument) return
    acquireFullscreenBodyLock()
    // Capture phase so we run before PrimeVue Dialog's bubble-phase ESC
    // handler — otherwise pressing ESC in a fullscreen editor inside a
    // dialog exits fullscreen AND closes the dialog in the same keystroke.
    fullscreenDocument.addEventListener('keydown', onFullscreenKeydown, true)
  } else {
    const focusEditor = focusEditorAfterExit
    focusEditorAfterExit = false
    releaseFullscreenBodyLock(!focusEditor)
    fullscreenDocument?.removeEventListener('keydown', onFullscreenKeydown, true)
    fullscreenDocument = null
    const owner = mdEditorRef.value
    if (focusEditor && owner && !props.readonly && !hasActiveModalDialog(owner)
      && !owner.closest('[inert]')) editor.value?.view.focus()
  }
}, { flush: 'post' })
watch([() => props.fullscreenable, () => props.readonly, () => props.compact], ([fullscreenable, readonly, compact]) => {
  if (!fullscreen.value || (fullscreenable && !readonly && !compact)) return
  focusEditorAfterExit = !readonly
  fullscreen.value = false
})

let releaseBodyLock: ((restoreFocus?: boolean) => void) | undefined
function acquireFullscreenBodyLock() {
  if (mdEditorRef.value) releaseBodyLock ??= acquireFullscreenLock(mdEditorRef.value, () => editor.value?.view.focus(), fullscreenTrigger)
}
function releaseFullscreenBodyLock(restoreFocus = true) {
  releaseBodyLock?.(restoreFocus)
  releaseBodyLock = undefined
}
const fullscreenTeleportTarget = computed(() => mdEditorRef.value?.ownerDocument.body)

function wordsLabel(n: number): string {
  return surfaceStrings.wordsLabel(n)
}

const uploadError = ref<string | null>(null)
let uploadErrorTimer: ReturnType<typeof setTimeout> | null = null

function showUploadError(msg: string) {
  uploadError.value = msg
  if (uploadErrorTimer) clearTimeout(uploadErrorTimer)
  uploadErrorTimer = setTimeout(() => { uploadError.value = null }, 6000)
}

function onUploadErrorEvent(e: Event) {
  const detail = (e as CustomEvent).detail as { message?: string } | undefined
  showUploadError(detail?.message ?? surfaceStrings.uploadFailed)
}
watch(editor, (next, prev) => {
  if (prev && !prev.isDestroyed) prev.view.dom.removeEventListener(imageUploadErrorEvent, onUploadErrorEvent)
  if (next) next.view.dom.addEventListener(imageUploadErrorEvent, onUploadErrorEvent)
})

function uploadImage() {
  const e = editor.value
  if (e && props.imageUploadUrl) pickAndUploadImage(e, createHttpImageUpload(props.imageUploadUrl), defaultImageUploadAccept)
}
</script>

<template>
  <!-- Teleport to <body> while fullscreen so the editor escapes any
       transformed ancestor (PrimeVue Dialog applies transforms for its
       enter/leave animations, which traps `position: fixed` inside the
       dialog's box and leaves the "fullscreen" editor sitting in a
       quarter-of-viewport whitespace). With teleport disabled the wrapper
       stays in the parent layout. -->
  <Teleport :to="fullscreenTeleportTarget ?? 'body'" :disabled="!fullscreen">
  <div ref="mdEditorRef"
       :class="['md-editor', { readonly, paper, compact, 'is-fullscreen': fullscreen }]"
       :data-fullscreen="fullscreen ? 'true' : null"
       :data-md-overlay-owner="overlayOwnerId"
       :role="fullscreen ? 'dialog' : undefined" :aria-modal="fullscreen ? 'true' : undefined" :aria-label="fullscreen ? (fullscreenLabel || ariaLabel) : undefined"
       data-testid="markdown-editor">
    <Transition name="md-err">
      <div v-if="uploadError" class="md-upload-err" role="alert"
           data-testid="md-upload-error">
        <AlertTriangleIcon class="md-upload-err-ic"/>
        <span class="md-upload-err-msg">{{ uploadError }}</span>
        <button type="button" class="md-upload-err-x"
                :aria-label="surfaceStrings.closeUploadError"
                @click="uploadError = null">
          <CloseIcon/>
        </button>
      </div>
    </Transition>
    <EditorControls v-if="editor && !readonly" :editor="editor" :profile="editorProfile"
      :show-block-add-button="showBlockAddButton" :show-block-inserter="showBlockInserter"
      :boundary="mdEditorRef" :compact="compact" :hide-block-handle="hideBlockHandle"
      @formatting-popover="formattingPopoverOpen = $event" @table-popover="tablePopoverOpen = $event" />

    <!-- Top-right floating toggle: shown only outside paper mode.
         In paper mode the toggle lives in the footer alongside word count. -->
    <button
      v-if="!readonly && fullscreenable && !compact && !paper"
      type="button"
      class="md-fullscreen-toggle"
      data-testid="editor-fullscreen-toggle"
      v-tooltip="fullscreen ? `${markdownStrings.exitFullscreen} (Esc)` : markdownStrings.fullscreen"
      :aria-label="fullscreen ? markdownStrings.exitFullscreen : markdownStrings.fullscreen"
      @click.stop="toggleFullscreen">
      <FullscreenExitIcon v-if="fullscreen"/>
      <FullscreenIcon v-else/>
    </button>

    <!-- Fullscreen header — anchors the user to the surface they're editing
         (the answer of WHICH requirement, the description of WHICH blocker)
         so the maximised editor isn't a context-free void. Rendered only in
         fullscreen + only when the consumer supplied a label. -->
    <header v-if="fullscreen && fullscreenLabel" class="md-fs-header" data-testid="editor-fullscreen-header">
      <span class="md-fs-header-label">{{ fullscreenLabel }}</span>
      <span class="md-fs-header-hint" aria-hidden="true">{{ surfaceStrings.escReturnHint }}</span>
    </header>

    <div :class="{ 'md-outline-layout': outlineEnabled, 'is-docked': outlineEnabled && outlineDocked }">
      <div :class="{ 'md-outline-document': outlineEnabled }">
        <div class="paper-inner"><div ref="contentHost" class="editor-body" :style="{ '--min-h': minHeight }" /></div>
      </div>
      <DocumentOutline v-if="editor && outlineEnabled" :editor="editor" :options="outlineOptions" @docked="outlineDocked = $event" />
    </div>
    <div v-if="paper && !readonly" class="paper-footer">
      <span>{{ wordCount }} {{ wordsLabel(wordCount) }}</span>
      <span class="dot">·</span>
      <span>{{ charCount }} {{ surfaceStrings.charactersUnit }}</span>
      <button
        v-if="fullscreenable && !compact"
        type="button"
        class="paper-footer-fs"
        data-testid="editor-fullscreen-toggle"
        v-tooltip="fullscreen ? `${markdownStrings.exitFullscreen} (Esc)` : markdownStrings.fullscreen"
        :aria-label="fullscreen ? markdownStrings.exitFullscreen : markdownStrings.fullscreen"
        @click.stop="toggleFullscreen">
        <FullscreenExitIcon v-if="fullscreen"/>
        <FullscreenIcon v-else/>
      </button>
    </div>
  </div>
  </Teleport>
</template>
