<script setup lang="ts" generic="P extends MarkdownProfile = 'markdown-safe'">
import { computed, onScopeDispose, watch } from 'vue'
import { getMarkdownContentCapability, type MarkdownContentChange, type MarkdownResourcePolicy } from '../core'
import type { ImageUploadService, LocalMarkdownEditorController, MarkdownCollaborationAdapter, MarkdownControllerOptions, MarkdownEditorController, MarkdownEditorState, MarkdownEmbedResolver, MarkdownImportHandler, MarkdownLexiconSource, MarkdownPlugin, MarkdownProfile, MarkdownRichInput, MarkdownValue, MarkdownValueInput, MarkdownWikiLinkResolver } from '../core'
import { configureMarkdownEditorView } from './controllerView'
import EditorSurface from '../ui/EditorSurface.vue'
import { toPlainMarkdownValue, useMarkdownEditor } from './useMarkdownEditor'
import type { MarkdownFormattingItem } from '../ui/formatting'
import type { MarkdownOutlineMode, MarkdownOutlineOptions } from '../ui/outline'
import type { AnyExtension } from '@tiptap/core'
import { surfaceStrings } from '../ui/strings/surface'

type ContentBinding<V> = { mode?: 'value'; modelValue: V; initialValue?: never; collaboration?: never }
  | { mode: 'revision'; initialValue: V; modelValue?: never; collaboration?: never }
  | { mode?: 'value' | 'revision'; collaboration: MarkdownCollaborationAdapter; modelValue?: never; initialValue?: never }
type ContentSource<T extends MarkdownProfile> = ({ profile?: T & 'markdown-safe' } & ContentBinding<string>)
  | ({ profile: T & 'rich' } & ContentBinding<MarkdownRichInput>)
type SlotContext = { controller: MarkdownEditorController<P>; state: MarkdownEditorState }
const props = withDefaults(defineProps<ContentSource<P> & {
  ariaLabel: string
  /** Show the add button beside the block drag handle (default true). */
  showBlockAddButton?: boolean
  /** Reveal a subtle insertion control between document blocks (default false). */
  showBlockInserter?: boolean
  /** The selection bubble on <body>, fixed: for an editor inside a zoomed or clipping host (a board's canvas). */
  bubbleOnBody?: boolean
  /** False when the host draws its own bar over a reader's text selection: the reader's bubble stays off (one bar for readers). */
  readerBubble?: boolean
  /** A caret that glides to where it goes and blinks only when idle, drawn instead of the native one (default false). Set once. */
  smoothCaret?: boolean
  variant?: 'document' | 'compact'
  visibleLines?: 1 | 2 | 3
  formatting?: readonly MarkdownFormattingItem[]
  readonly?: boolean
  placeholder?: string
  fullscreenable?: boolean
  /** Shown in the fullscreen header instead of the generic ariaLabel. */
  documentTitle?: string
  /** Offer a read-only Markdown projection in document mode. */
  showMarkdownSource?: boolean
  /** Optional document navigation. Defaults to hidden; panel mode has no wave rail. */
  outline?: MarkdownOutlineMode | MarkdownOutlineOptions
  plugins?: readonly MarkdownPlugin[]
  imageUpload?: ImageUploadService
  resourcePolicy?: MarkdownResourcePolicy
  /** Load the optional codeBlock implementation before mounting; immutable for this editor. */
  codeBlock?: AnyExtension
  /** Collapse plugin toolbar actions into a single "Vložit" menu. Defaults to the expanded row. */
  toolbarPresentation?: 'expanded' | 'insert-menu' | 'hidden'
  /** Show the "Rich dokument"/"Markdown" label in the document footer. */
  showProfileLabel?: boolean
  /** Show remote collaborators above the content while the session is online. */
  showPresence?: boolean
  /** `@markdown-import`: front matter/leading H1 of pasted or dropped Markdown; may return `{ omitLeadingHeading }`. */
  onMarkdownImport?: MarkdownImportHandler
  /** `[[Page]]`, `[[Page|alias]]`, `[[Page#heading]]` in pasted/dropped Markdown → `{ href, label? }`, or null to keep the text. */
  resolveWikiLink?: MarkdownWikiLinkResolver
  /** Obsidian `![[file.png]]` / `![[Page#heading]]` → `{ src, alt? }` image or `{ href, label? }` link, or null to keep the text. */
  resolveEmbed?: MarkdownEmbedResolver
  /** What the place knows by name (people, pages, YouTrack projects): names, keys and titles link while typing (P15). Set once. */
  lexicon?: MarkdownLexiconSource
}>(), { fullscreenable: true, toolbarPresentation: 'expanded', showProfileLabel: true, showPresence: false, showBlockAddButton: true, showBlockInserter: false, readerBubble: true })
defineSlots<{
  toolbar?(context: SlotContext): unknown
  'before-content'?(context: SlotContext): unknown
  inspector?(context: SlotContext): unknown
  status?(context: SlotContext): unknown
}>()
const profile = computed(() => (props.profile ?? 'markdown-safe') as P)
const variant = computed(() => props.variant ?? 'document')
const visibleLines = computed(() => props.visibleLines ?? 2)
const readonlyMode = computed(() => props.readonly ?? false)
const fullscreenable = computed(() => props.fullscreenable)
/** An empty editor otherwise gave no hint that "/" opens the block menu. */
const placeholderText = computed(() => props.placeholder ?? surfaceStrings.placeholder)
const emit = defineEmits<{
  'update:modelValue': [value: MarkdownValue<P>]
  change: [value: MarkdownValue<P>, metadata: { readonly origin: 'local' | 'remote' }]
  created: [controller: MarkdownEditorController<P>]
  failed: [error: NonNullable<MarkdownEditorState['error']>]
  contentChange: [change: MarkdownContentChange]
}>()
const revisionMode = props.mode === 'revision'
if (props.mode !== undefined && props.mode !== 'value' && props.mode !== 'revision') throw new TypeError('Unsupported editor event mode.')
if (props.collaboration && (props.modelValue !== undefined || props.initialValue !== undefined)) throw new TypeError('Local value and collaboration are conflicting content sources.')
if (revisionMode && props.modelValue !== undefined) throw new TypeError('Revision mode uses initialValue and explicit replacement; modelValue is not supported.')
if (!revisionMode && props.initialValue !== undefined) throw new TypeError('initialValue requires revision mode.')
const source = props.collaboration ? { kind: 'collaborative' as const, adapter: props.collaboration }
  : { kind: 'local' as const, value: (revisionMode ? props.initialValue : props.modelValue) as MarkdownValueInput<P> }
const initialPlugins = [...(props.plugins ?? [])]
const { controller, state } = useMarkdownEditor<P>({
  profile: profile.value, source,
  plugins: initialPlugins, imageUpload: props.imageUpload, resourcePolicy: props.resourcePolicy,
  codeBlock: props.codeBlock,
  // Read the prop at call time so a replaced listener takes effect without remounting.
  onMarkdownImport: info => props.onMarkdownImport?.(info),
  resolveWikiLink: request => props.resolveWikiLink?.(request),
  resolveEmbed: request => props.resolveEmbed?.(request),
  lexicon: props.lexicon,
  editable: !readonlyMode.value, placeholder: placeholderText.value, compact: variant.value === 'compact',
} as MarkdownControllerOptions<P>)
const viewConfiguration = configureMarkdownEditorView(controller, {
  compact: variant.value === 'compact', imageUpload: props.imageUpload, placeholder: placeholderText.value, smoothCaret: props.smoothCaret,
})
let replacing = false
let created = false
let failed = false
let revision = controller.state.contentRevision
const unsubscribe = controller.subscribe(state => {
  if (!failed && state.lifecycle === 'failed' && state.error) {
    failed = true
    emit('failed', state.error)
  }
  if (!created && state.lifecycle === 'ready') {
    created = true
    revision = state.contentRevision
    emit('created', controller)
    return
  }
  if (state.contentRevision === revision) return
  revision = state.contentRevision
  if (!created || replacing) return
  if (state.lifecycle !== 'ready') return
  if (revisionMode) {
    const stamp = getMarkdownContentCapability(controller)!.getStamp()
    emit('contentChange', Object.freeze({ ...stamp, revision: state.contentRevision, origin: state.changeOrigin }))
    return
  }
  let value: MarkdownValue<P>
  try { value = controller.getValue() } catch { return /* Controller publishes the failure. */ }
  if (controller.state.binding === 'local') emit('update:modelValue', value)
  emit('change', value, { origin: state.changeOrigin })
})
onScopeDispose(unsubscribe)
if (!revisionMode) watch(() => props.modelValue, value => {
  if (controller.state.binding !== 'local') {
    if (value !== undefined) throw new TypeError('A collaborative editor cannot accept modelValue.')
    return
  }
  replacing = true
  try { (controller as LocalMarkdownEditorController<P>).setValue(toPlainMarkdownValue(value as MarkdownValueInput<P>)) } finally { replacing = false }
}, { deep: true })
watch(() => props.mode, mode => {
  if ((mode === 'revision') !== revisionMode) throw new Error('Remount MarkdownEditor with a new key to switch event mode.')
})
watch(() => props.collaboration, collaboration => {
  if (collaboration !== (source.kind === 'collaborative' ? source.adapter : undefined)) throw new Error('Remount MarkdownEditor with a new key to switch document source.')
})
watch(readonlyMode, readonly => controller.setEditable(!readonly))
watch([variant, () => props.imageUpload, () => props.placeholder], ([nextVariant, imageUpload, placeholder]) => {
  viewConfiguration.update({ compact: nextVariant === 'compact', imageUpload, placeholder })
})
watch(profile, nextProfile => {
  if (nextProfile !== controller.profile) throw new Error('Remount MarkdownEditor with a new key to switch profile.')
})
watch(() => props.plugins, plugins => {
  const next = plugins ?? []
  if (next.length !== initialPlugins.length || next.some((plugin, index) => plugin !== initialPlugins[index])) throw new Error('Remount MarkdownEditor with a new key to change plugins.')
}, { deep: false })
defineExpose({ controller })
</script>

<template>
  <EditorSurface :controller="controller" :ariaLabel="ariaLabel" :variant="variant" :readonly="readonlyMode"
    :show-block-add-button="showBlockAddButton" :show-block-inserter="showBlockInserter" :bubble-on-body="bubbleOnBody" :reader-bubble="readerBubble"
    :visible-lines="visibleLines" :fullscreenable="fullscreenable" :document-title="documentTitle" :formatting="formatting" :show-markdown-source="showMarkdownSource" :outline="outline"
    :toolbar-presentation="toolbarPresentation" :show-profile-label="showProfileLabel" :show-presence="showPresence">
    <template v-if="$slots.toolbar" #toolbar><slot name="toolbar" :controller="controller" :state="state" /></template>
    <template v-if="$slots['before-content']" #before-content><slot name="before-content" :controller="controller" :state="state" /></template>
    <template v-if="$slots.inspector" #inspector><slot name="inspector" :controller="controller" :state="state" /></template>
    <template v-if="$slots.status" #status><slot name="status" :controller="controller" :state="state" /></template>
  </EditorSurface>
</template>
