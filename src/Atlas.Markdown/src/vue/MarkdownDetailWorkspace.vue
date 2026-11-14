<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, ref, useId, watch } from 'vue'
import { markdownStrings } from '../ui/strings'
import { CloseIcon } from '../ui/icons'
import { surfaceStrings } from '../ui/strings/surface'
import { clearMarkdownOverlayTheme, copyMarkdownOverlayTheme } from '../core/overlayTheme'
import { markdownMotion } from '../core/motion'
// One detail at a time: a component detail (Dokument/API/YAML) or the page source (Dokument/Zdroj), never
// both. `label` names the region for assistive technology (default `surfaceStrings.detailAriaLabel`,
// „Detail komponenty“).
const props = defineProps<{ open: boolean; active: string; tabs: { id: string; label: string }[]; title: string; label?: string }>()
// `tab-intent` fires on pointer enter or focus of a tab, before activation, so hosts can warm lazy views.
const emit = defineEmits<{ 'update:active': [id: string]; 'request-close': []; closed: []; 'tab-intent': [id: string] }>()
const id = useId()
const anchor = ref<HTMLElement>()
const promoted = ref(false)
const closing = ref(false)
const lastTab = ref(false)
const workspaceHeight = ref(640)
const index = computed(() => Math.max(0, props.tabs.findIndex(tab => tab.id === props.active)))
const visibleTabs = computed(() => lastTab.value ? props.tabs.filter(tab => tab.id === 'document') : props.tabs)
let previous: HTMLElement | null = null
let foldTimer: ReturnType<typeof setTimeout> | undefined
let closeTimer: ReturnType<typeof setTimeout> | undefined
function clearTimers() { clearTimeout(foldTimer); clearTimeout(closeTimer) }
// Scrolled into view (scroll-margin-top), the pages reach down to the bottom of the visible area, so a long view
// (API reference, source) fills the window instead of ending at a fixed height above blank space. A scrolling
// ancestor (a host's content pane) bounds the area instead of the window.
const MIN_HEIGHT = 320, BOTTOM_GAP = 12
function fillHeight() {
  const root = anchor.value, pages = root?.querySelector<HTMLElement>('.md-detail-pages'), view = root?.ownerDocument.defaultView
  if (!root || !pages || !view) return
  let visible = view.innerHeight
  for (let node = root.parentElement; node && node !== root.ownerDocument.body && node !== root.ownerDocument.documentElement; node = node.parentElement) {
    if (/auto|scroll/.test(view.getComputedStyle(node).overflowY) && node.scrollHeight > node.clientHeight) { visible = node.clientHeight; break }
  }
  const above = (parseFloat(view.getComputedStyle(root).scrollMarginTop) || 0) + pages.getBoundingClientRect().top - root.getBoundingClientRect().top
  workspaceHeight.value = Math.max(MIN_HEIGHT, Math.floor(visible - above - BOTTOM_GAP))
}
function followViewport(on: boolean) {
  const view = anchor.value?.ownerDocument.defaultView
  view?.[on ? 'addEventListener' : 'removeEventListener']('resize', fillHeight)
}
// The workspace wraps the document editor, outside `.md-editor` where theme tokens live (e.g.
// `.md-editor.md-theme-midnight`): copy the editor's tokens onto the workspace while it is promoted,
// so tabs, pages and host views follow the document's theme, also when it changes meanwhile.
let themeObserver: MutationObserver | undefined
function followTheme() {
  themeObserver?.disconnect(); themeObserver = undefined
  const root = anchor.value, editor = root?.querySelector<HTMLElement>('.md-detail-page .md-editor')
  if (!root || !editor) return
  const sync = () => copyMarkdownOverlayTheme(editor, root)
  sync()
  const Observer = root.ownerDocument.defaultView?.MutationObserver
  themeObserver = Observer ? new Observer(sync) : undefined
  themeObserver?.observe(editor, { attributes: true, attributeFilter: ['class', 'style'] })
}
// Collapsed, the anchor inherits from the page again: drop the copied tokens.
function stopTheme() {
  themeObserver?.disconnect(); themeObserver = undefined
  if (anchor.value) clearMarkdownOverlayTheme(anchor.value)
}
watch(() => props.active, async () => {
  await nextTick()
  if (promoted.value && !closing.value) anchor.value?.querySelector<HTMLButtonElement>('[aria-selected=true]')?.focus({ preventScroll: true })
})
function navigate(event: KeyboardEvent) {
  if (!['ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(event.key) || closing.value) return
  event.preventDefault()
  const next = event.key === 'Home' ? 0 : event.key === 'End' ? props.tabs.length - 1 : (index.value + (event.key === 'ArrowRight' ? 1 : props.tabs.length - 1)) % props.tabs.length
  emit('update:active', props.tabs[next]!.id)
  nextTick(() => anchor.value?.querySelectorAll<HTMLButtonElement>('[role=tab]')[next]?.focus({ preventScroll: true }))
}
function escape(event: KeyboardEvent) {
  if (!promoted.value || closing.value || event.defaultPrevented || (event.target as Element)?.closest('dialog')) return
  event.preventDefault(); event.stopPropagation(); emit('request-close')
}
watch(() => props.open, async open => {
  clearTimers()
  if (open) {
    previous = anchor.value?.ownerDocument.activeElement as HTMLElement | null
    promoted.value = true; closing.value = false; lastTab.value = false
    followTheme()
    await nextTick()
    if (!props.open) return
    fillHeight(); followViewport(true)
    anchor.value?.querySelector<HTMLButtonElement>('[aria-selected=true]')?.focus({ preventScroll: true })
    anchor.value?.scrollIntoView({ block: 'start', behavior: 'instant' })
  } else if (promoted.value) {
    emit('update:active', 'document')
    closing.value = true
    // The same tokens as the styles below: fold = slow, then the last tab snaps (base delay, 1.5 × slow) or fades (slow).
    const motion = markdownMotion(anchor.value)
    const slow = motion.duration('slow')
    foldTimer = setTimeout(() => { lastTab.value = true }, slow)
    closeTimer = setTimeout(() => {
      promoted.value = false; closing.value = false; lastTab.value = false
      stopTheme(); followViewport(false)
      nextTick(() => {
        // The opener (a block's toolbar, the host's switch) may be gone or hidden by now: the document's text takes focus instead.
        const doc = anchor.value?.ownerDocument, active = doc?.activeElement
        if (!active || active === doc?.body || anchor.value?.contains(active)) {
          if (previous?.isConnected) previous.focus({ preventScroll: true })
          if (doc?.activeElement !== previous) anchor.value?.querySelector<HTMLElement>('.ProseMirror')?.focus({ preventScroll: true })
        }
        emit('closed')
      })
    }, motion.reduced ? slow * 2 : slow + motion.duration('base') + slow * 1.5)
  }
})
onBeforeUnmount(() => { clearTimers(); themeObserver?.disconnect(); followViewport(false) })
</script>

<template>
  <div ref="anchor" class="md-detail-anchor" :class="{ promoted, closing, 'last-tab': lastTab }"
    :role="promoted ? 'region' : undefined" :aria-label="promoted ? (label ?? surfaceStrings.detailAriaLabel) : undefined"
    :style="{ '--workspace-height': workspaceHeight + 'px' }" @keydown.esc="escape">
    <header v-if="promoted" class="md-detail-header">
      <span class="md-detail-title" :title="title">{{ title }}</span>
      <TransitionGroup tag="div" name="detail-tab" class="md-detail-tabs" role="tablist" :aria-label="surfaceStrings.detailTabsAriaLabel" @keydown="navigate">
        <button v-for="tab in visibleTabs" :id="id + '-tab-' + tab.id" :key="tab.id" type="button" role="tab"
          :aria-controls="id + '-panel-' + tab.id" :aria-selected="active === tab.id" :tabindex="active === tab.id ? 0 : -1"
          :disabled="closing" @pointerenter="emit('tab-intent', tab.id)" @focus="emit('tab-intent', tab.id)" @click="emit('update:active', tab.id)">{{ tab.label }}</button>
      </TransitionGroup>
      <button type="button" class="md-btn" :aria-label="surfaceStrings.detailCloseAriaLabel" :title="markdownStrings.close" :disabled="closing" @click="emit('request-close')"><CloseIcon /></button>
    </header>
    <div class="md-detail-pages">
      <section v-for="(tab, position) in tabs" :id="id + '-panel-' + tab.id" :key="tab.id" :role="promoted ? 'tabpanel' : undefined"
        :aria-labelledby="promoted ? id + '-tab-' + tab.id : undefined" :inert="active !== tab.id"
        class="md-detail-page" :class="{ current: active === tab.id }" :style="{ '--offset': position - index }">
        <slot :name="tab.id" />
      </section>
    </div>
    <slot v-if="promoted" name="confirmation" />
  </div>
</template>

<style scoped>
.md-detail-anchor { --review-paper:var(--paper,#fff); --review-mode:var(--accent,#176b77); position:relative; min-width:0; scroll-margin-top:40px; }
.md-detail-anchor.promoted { padding-top:26px; }
.md-detail-header { position:absolute; z-index:5; top:-16px; left:0; right:0; display:grid; grid-template-columns:minmax(0,1fr) auto minmax(0,1fr); align-items:center; gap:8px; }
.md-detail-title { font-size:11px; overflow:hidden; text-overflow:ellipsis; white-space:nowrap; padding-left:8px; color:var(--text-dim,#586574); }
.md-detail-header > .md-btn { justify-self:end; display:inline-flex; align-items:center; justify-content:center; width:32px; padding:0; }
/* Theme tokens only: a soft green → accent → amber wash of the page background (dark in Midnight). */
.md-detail-tabs { position:relative; display:flex; padding:4px; border-radius:24px; border:1px solid var(--line,#d4e4e7);
  background:linear-gradient(105deg, color-mix(in oklab, var(--green,#137b70) 20%, var(--bg-2,#edf5f6)), var(--accent-soft,#e3f4f7) 52%, color-mix(in oklab, var(--amber,#b66b12) 20%, var(--bg-2,#edf5f6)));
  box-shadow:var(--shadow-1,0 1px 2px rgb(21 43 38 / .06)); }
.md-detail-tabs button { border:0; background:transparent; color:var(--text-dim,#49676e); padding:8px 22px; border-radius:20px; font:inherit; font-size:12px; white-space:nowrap; cursor:pointer; }
.md-detail-tabs button:hover:not([aria-selected=true]):not(:disabled) { color:var(--text,#102f36); }
.md-detail-tabs button[aria-selected=true] { background:var(--review-paper); color:var(--review-mode); box-shadow:var(--shadow-1,0 1px 2px rgb(21 43 38 / .06)), 0 0 0 1px var(--line,#d4e4e7); }
.md-detail-tabs button:focus-visible { outline:var(--focus-ring,2px solid var(--review-mode)); outline-offset:1px; }
.promoted .md-detail-pages { position:relative; height:var(--workspace-height); overflow:hidden; border-radius:12px; background:var(--paper,#fff); }
.promoted .md-detail-page { position:absolute; inset:0; overflow:auto; transform:translateX(calc(var(--offset) * 100%)); visibility:hidden; }
.promoted .md-detail-page.current { visibility:visible; }
.md-detail-anchor:not(.promoted) .md-detail-page:not(.current) { display:none; }
/* Timing follows the motion tokens, so a host's slowdown and reduced motion apply (styles/motion.css: reduced motion
   keeps only opacity and colour transitions here, no travel in the keyframes). The close timers in the script derive
   from the same tokens: fold = slow, close = fold + base + 1.5 × slow. */
.promoted .md-detail-page { transition:transform var(--md-motion-slow) var(--md-motion-ease),visibility var(--md-motion-slow); }
.promoted .md-detail-header { animation:detail-open var(--md-motion-slow) var(--md-motion-ease); }
.closing .md-detail-tabs button:not(:first-child) { animation:detail-tab-fold var(--md-motion-slow) var(--md-motion-ease) both; }
.closing .md-detail-title,.closing .md-detail-header > .md-btn { transition:opacity var(--md-motion-base) var(--md-motion-ease); opacity:0; }
.detail-tab-enter-active,.detail-tab-leave-active { transition:opacity var(--md-motion-base) var(--md-motion-ease),transform var(--md-motion-base) var(--md-motion-ease),max-width var(--md-motion-base) var(--md-motion-ease),padding var(--md-motion-base) var(--md-motion-ease); max-width:120px; overflow:hidden; }
.detail-tab-enter-from,.detail-tab-leave-to { opacity:0; transform:translateX(calc(var(--md-motion-rise) * -6)); max-width:0; padding-inline:0; }
.detail-tab-move { transition:transform var(--md-motion-base) var(--md-motion-ease); }
.last-tab .md-detail-tabs { animation:detail-document-snap calc(var(--md-motion-slow) * 1.5) var(--md-motion-ease-io) var(--md-motion-base) both; }
.last-tab .md-detail-tabs::after { content:''; pointer-events:none; position:absolute; inset:-4px; border:2px solid var(--review-mode); border-radius:inherit; animation:detail-snap-ring var(--md-motion-base) var(--md-motion-ease) var(--md-motion-slow) both; }
@keyframes detail-open { from { opacity:0; transform:translateY(calc(var(--md-motion-rise) * 3)) scaleX(calc(1 - (1 - var(--md-motion-pop-from)) * .75)); } }
@keyframes detail-tab-fold { to { opacity:0; transform:translateX(calc(var(--md-motion-rise) * -9)) scaleX(calc(1 - (1 - var(--md-motion-pop-from)) * 3.5)); } }
@keyframes detail-document-snap {
  0% { transform:scale(1); opacity:1; }
  25% { transform:translateY(-4px) scale(1.12,1.06); opacity:1; }
  66% { transform:translateY(5px) scale(.18,.62); opacity:1; }
  84% { transform:translateY(8px) scale(.08,.18); opacity:.8; }
  100% { transform:translateY(10px) scale(0); opacity:0; }
}
@keyframes detail-snap-ring { from { opacity:.5; transform:scale(.35); } to { opacity:0; transform:scale(1.65,1.8); } }
@keyframes detail-snap-fade { to { opacity:0; } }
/* Reduced motion: the last tab fades into the document (no snap, no ring), without delay. */
@media(prefers-reduced-motion:reduce) {
  .last-tab .md-detail-tabs { animation:detail-snap-fade var(--md-motion-slow) var(--md-motion-ease) both; }
  .last-tab .md-detail-tabs::after { display:none; }
}
:global([data-md-motion=reduced] .last-tab .md-detail-tabs) { animation:detail-snap-fade var(--md-motion-slow) var(--md-motion-ease) both; }
:global([data-md-motion=reduced] .last-tab .md-detail-tabs::after) { display:none; }
@media(max-width:600px) {
  .md-detail-header { grid-template-columns:minmax(0,1fr) auto; top:-14px; }
  .md-detail-title { display:none; }
  .md-detail-tabs { justify-self:center; }
  .md-detail-tabs button { padding:8px 12px; }
  .md-detail-header > .md-btn { padding:6px; }
}
</style>
