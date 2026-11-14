import { computed, markRaw, onBeforeUnmount, onMounted, ref, shallowRef, watch, type Ref } from 'vue'
import { Extension, type AnyExtension, type Editor } from '@tiptap/core'
import type { Node } from '@tiptap/pm/model'
import { Plugin, TextSelection } from '@tiptap/pm/state'
import { createLegacyEditorController } from '../core/controller'
import { configureControllerView, editorForController } from '../core/internal'
import type { MarkdownProfile } from '../core/types'
import { DragPreview } from '../extensions/DragPreview'
import { TableControls } from '../extensions/TableControls'
import { SlashCommands, slashSuggestion, type SlashCommandItem } from '../extensions/SlashCommands'
import { useTiptapVueContext } from '../vue/useTiptapVueContext'

interface LegacyEditorProps {
  readonly modelValue: string
  readonly ariaLabel: string
  readonly readonly: boolean
  readonly compact: boolean
  readonly collaborative: boolean
  readonly placeholder: string
  readonly imageUploadUrl?: string
  readonly resourcePolicy?: import('../features/images/resourcePolicy').MarkdownResourcePolicy
  readonly extensions: AnyExtension[]
  readonly codeBlock?: AnyExtension
  readonly slashSuggestionExtra: SlashCommandItem[]
}
interface LegacyEditorEvents {
  change(markdown: string): void
  created(editor: Editor): void
  replaced(editor: Editor): void
}

/** Adapts the string v-model contract to the shared runtime; owns no editor engine of its own. */
export function useLegacyEditor(
  props: LegacyEditorProps,
  profile: MarkdownProfile,
  host: Ref<HTMLElement | null>,
  events: LegacyEditorEvents,
  imageSlashItem: () => SlashCommandItem | null,
) {
  const editor = shallowRef<Editor | null>(null)
  const revision = ref(0)
  const initialDocument = shallowRef<Node | null>(null)
  let lastEmitted = ''
  let emitTimer: ReturnType<typeof setTimeout> | null = null
  const vueContext = useTiptapVueContext()
  let detachVueContext: (() => void) | undefined
  const { controller, replaceMarkdown } = createLegacyEditorController({
    profile, initialMarkdown: props.modelValue, collaborative: props.collaborative,
    editable: !props.readonly, placeholder: props.placeholder,
    imageUploadUrl: props.imageUploadUrl, resourcePolicy: props.resourcePolicy, extraExtensions: props.extensions,
    codeBlock: props.codeBlock,
  })

  function markdown() { return controller.getProjection().markdown }
  function cancelEmit() {
    if (emitTimer !== null) clearTimeout(emitTimer)
    emitTimer = null
  }
  function flushMarkdownEmit() {
    if (emitTimer === null) return
    cancelEmit()
    if (!editor.value || editor.value.isDestroyed) return
    lastEmitted = markdown()
    events.change(lastEmitted)
  }
  function scheduleEmit() {
    cancelEmit()
    // Compact forms publish on the next timer tick; document editors serialize after typing settles.
    emitTimer = setTimeout(flushMarkdownEmit, props.compact ? 0 : 120)
  }

  const lifecycle = Extension.create({
    name: 'legacyEditorBinding',
    onCreate() {
      initialDocument.value = this.editor.state.doc
      lastEmitted = markdown()
      events.created(this.editor)
    },
    onUpdate: scheduleEmit,
    onBlur: flushMarkdownEmit,
    onTransaction({ transaction }) { if (transaction.docChanged) revision.value++ },
    addProseMirrorPlugins() {
      return [new Plugin({ props: {
        handleDrop(view, _event, _slice, moved) {
          if (!moved) return false
          // Collapse the drop selection after ProseMirror inserts the blocks, so the bubble menu stays closed.
          queueMicrotask(() => {
            if (view.isDestroyed || view.state.selection instanceof TextSelection) return
            const position = Math.min(view.state.selection.to, view.state.doc.content.size)
            view.dispatch(view.state.tr.setSelection(TextSelection.near(view.state.doc.resolve(position), -1)))
          })
          return false
        },
      } })]
    },
  })
  configureControllerView(controller, [
    DragPreview,
    TableControls.configure({ richTableFeatures: profile === 'rich' }),
    SlashCommands.configure({ suggestion: slashSuggestion({
      withImage: imageSlashItem, extra: props.slashSuggestionExtra, compact: props.compact,
    }) }),
    lifecycle,
  ])

  const wordCount = computed(() => {
    void revision.value
    return editor.value?.storage.characterCount?.words?.() ?? 0
  })
  const charCount = computed(() => {
    void revision.value
    return editor.value?.storage.characterCount?.characters?.() ?? 0
  })
  const dirty = computed(() => {
    void revision.value
    return !!editor.value && !!initialDocument.value && !editor.value.state.doc.eq(initialDocument.value)
  })

  watch(() => props.modelValue, value => {
    if (!editor.value || props.collaborative || value === lastEmitted || value === markdown()) return
    cancelEmit()
    replaceMarkdown(value)
    lastEmitted = value
    initialDocument.value = editor.value.state.doc
    events.replaced(editor.value)
  })
  watch(() => props.readonly, readonly => controller.setEditable(!readonly))
  watch(() => props.ariaLabel, ariaLabel => {
    if (!ariaLabel?.trim()) throw new TypeError('A non-empty aria label is required.')
    editor.value?.view.dom.setAttribute('aria-label', ariaLabel)
  })

  onMounted(() => {
    controller.attachView(host.value!, { ariaLabel: props.ariaLabel })
    editor.value = markRaw(editorForController(controller)!)
    detachVueContext = vueContext.attach(editor.value)
    if (import.meta.env.DEV) {
      const registry = editor.value.view.dom.ownerDocument.defaultView as (Window & { __poolEditors?: Set<Editor> }) | null
      if (!registry) return
      registry.__poolEditors ??= new Set()
      registry.__poolEditors.add(editor.value)
    }
  })
  onBeforeUnmount(() => {
    flushMarkdownEmit()
    if (import.meta.env.DEV && editor.value) {
      (editor.value.view.dom.ownerDocument.defaultView as (Window & { __poolEditors?: Set<Editor> }) | null)
        ?.__poolEditors?.delete(editor.value)
    }
    try { controller.destroy() } finally {
      detachVueContext?.()
      editor.value = null
    }
  })

  return { editor, wordCount, charCount, dirty }
}
