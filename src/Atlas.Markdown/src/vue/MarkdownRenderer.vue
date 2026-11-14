<script setup lang="ts" generic="P extends MarkdownProfile = 'markdown-safe'">
import { computed, defineAsyncComponent, onBeforeUnmount, onMounted, ref, shallowRef, watch } from 'vue'
import type { MarkdownPlugin, MarkdownProfile, MarkdownRichInput, MarkdownValueInput } from '../core'
import { prepareMarkdownValue, type PreparedMarkdownRender } from '../engine/staticRenderer'
import { loadMarkdownDialect, MARKDOWN_DIALECT_SOURCE, markdownDialectReady } from '../engine/markdownDialectLoader'
import type { MarkdownResourcePolicy } from '../features/images/resourcePolicy'
import type { MarkdownOutlineMode, MarkdownOutlineOptions } from '../ui/outline'

const DocumentOutline = defineAsyncComponent(() => import('../ui/DocumentOutline.vue'))

type RendererValue<T extends MarkdownProfile> = { value: string; profile?: T & 'markdown-safe' }
  | { value: MarkdownRichInput; profile: T & 'rich' }
const props = defineProps<RendererValue<P> & {
  ariaLabel: string
  plugins?: readonly MarkdownPlugin[]
  resourcePolicy?: MarkdownResourcePolicy
  /**
   * `document` lays the content out like MarkdownEditor's document variant, with its paper
   * typography, so a reader can switch between reading and editing without a visual jump.
   */
  variant?: 'plain' | 'document'
  /** Document variant only: the editor's outline, over the rendered headings. */
  outline?: MarkdownOutlineMode | MarkdownOutlineOptions
}>()
const content = ref<HTMLElement | null>(null)
const documentLayout = computed(() => props.variant === 'document')
const outlineOptions = computed<MarkdownOutlineOptions>(() => typeof props.outline === 'string' ? { mode: props.outline } : props.outline ?? { mode: 'hidden' })
const outlineEnabled = computed(() => documentLayout.value && outlineOptions.value.mode !== 'hidden')
const outlineDocked = ref(false)
// The outline reads the rendered content; a new content element remounts it, a new render only refreshes it.
const outlineContent = shallowRef<HTMLElement>()
const outlineRevision = ref(0)
const outlineGeneration = ref(0)

/** The editor's table view wraps every table, and the table styles (both variants) target that wrapper. */
function wrapTables(root: HTMLElement): void {
  for (const table of root.querySelectorAll('table')) {
    if (table.closest('[data-md-module]') || table.parentElement?.classList.contains('tableWrapper')) continue
    const wrapper = root.ownerDocument.createElement('div')
    wrapper.className = 'tableWrapper'
    table.replaceWith(wrapper)
    wrapper.append(table)
  }
}
let cleanup: (() => void) | undefined
let pending: PreparedMarkdownRender | undefined
let generation = 0
let unmounted = false

function render(): void {
  if (unmounted) return
  const container = content.value
  if (!container) return
  const currentGeneration = ++generation
  // Markdown with `a-` components renders once its dialect has loaded.
  if (typeof props.value === 'string' && !markdownDialectReady() && MARKDOWN_DIALECT_SOURCE.test(props.value)) {
    loadMarkdownDialect().then(render, () => {})
    return
  }
  const profile = (props.profile ?? 'markdown-safe') as P
  const prepared = prepareMarkdownValue(props.value as MarkdownValueInput<P>, profile, props.plugins ?? [], container.ownerDocument, props.resourcePolicy)
  if (unmounted || currentGeneration !== generation || content.value !== container) {
    prepared.dispose()
    return
  }
  pending = prepared
  const previous = cleanup
  cleanup = undefined
  try {
    previous?.()
    if (unmounted || currentGeneration !== generation || content.value !== container) {
      pending = undefined
      prepared.dispose()
      return
    }
    pending = undefined
    // Publish the disposer before onRender can synchronously re-enter the host.
    cleanup = prepared.dispose
    prepared.mount(container)
    wrapTables(container)
    if (outlineContent.value === container) outlineRevision.value++
    else { outlineContent.value = container; outlineGeneration.value++ }
  } catch (error) {
    if (pending === prepared) pending = undefined
    if (cleanup === prepared.dispose) cleanup = undefined
    try { prepared.dispose() } catch (cleanupError) {
      if (unmounted || currentGeneration !== generation) return
      throw new AggregateError([error, cleanupError], 'Static renderer replacement failed.', { cause: error })
    }
    if (unmounted || currentGeneration !== generation) return
    throw error
  }
}
onMounted(render)
watch([() => props.value, () => props.profile, () => props.plugins, () => props.resourcePolicy, () => props.variant], render, { deep: true, flush: 'post' })
onBeforeUnmount(() => {
  unmounted = true
  generation++
  const cleanups = [pending?.dispose, cleanup].filter((candidate): candidate is () => void => !!candidate)
  pending = undefined
  cleanup = undefined
  const errors: unknown[] = []
  for (const dispose of new Set(cleanups)) {
    try { dispose() } catch (error) { errors.push(error) }
  }
  if (errors.length) throw new AggregateError(errors, 'Static renderer unmount cleanup failed.')
})
</script>

<template>
  <div class="md-editor readonly md-renderer" :class="{ paper: documentLayout, 'has-outline': outlineEnabled }" role="document" :aria-label="ariaLabel">
    <!-- The document variant mirrors EditorSurface's layout so the same paper and outline styles apply. -->
    <div v-if="documentLayout" :class="{ 'md-outline-layout': outlineEnabled, 'is-docked': outlineEnabled && outlineDocked }">
      <div :class="{ 'md-outline-document': outlineEnabled }">
        <div class="paper-inner"><div class="editor-body"><div ref="content" class="ProseMirror tiptap" contenteditable="false" /></div></div>
      </div>
      <DocumentOutline v-if="outlineEnabled && outlineContent" :key="outlineGeneration" :content="outlineContent" :revision="outlineRevision" :options="outlineOptions" @docked="outlineDocked = $event" />
    </div>
    <!-- The plain variant keeps the editor's body, so every content rule (`.md-editor .editor-body …`: task lists,
         callouts, tables, code, quotes, images) applies; only the paper and outline chrome is left out. -->
    <div v-else class="editor-body"><div ref="content" class="ProseMirror" /></div>
  </div>
</template>

<style scoped>
.md-renderer:not(.paper) { border: 0; background: transparent; box-shadow: none; }
/* A rendering has no editing area to reserve: no minimum height, no inset. */
.md-renderer:not(.paper) > .editor-body { padding: 0; min-height: 0; }
.md-renderer .ProseMirror { cursor: default; }
</style>
