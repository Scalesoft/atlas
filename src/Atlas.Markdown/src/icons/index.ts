/**
 * `@atlas/markdown/icons` — the package's icon surface.
 *
 * One registry (`ui/iconMarkup`), two thin factories: `createIconElement` for framework-neutral
 * DOM controls and `MarkdownIcon` for Vue. Hosts import this entry instead of bundling their own
 * icon set, which is what keeps a concept to a single drawing across the wiki and the editor.
 */
import { defineComponent, h, onMounted, onUpdated, shallowRef, type PropType } from 'vue'
import { ICON_STROKE_WIDTH, ICON_VIEWBOX, iconBody, iconFragment, type MarkdownIconName } from '../ui/iconMarkup'

export { ICON_STROKE_WIDTH, ICON_VIEWBOX, createIconElement, iconFragment, iconMarkup, iconSvgMarkup } from '../ui/iconMarkup'
export type { MarkdownIconName } from '../ui/iconMarkup'

export const MarkdownIcon = defineComponent({
  name: 'MarkdownIcon',
  props: {
    name: { type: String as PropType<MarkdownIconName>, required: true },
    /** 16 inside a control, 18 standing on its own. */
    size: { type: Number, default: 16 },
  },
  setup: props => {
    // The drawing is cloned from one parsed copy per icon (iconFragment), not parsed from innerHTML for each icon.
    const svg = shallowRef<SVGSVGElement>()
    let drawn: string | undefined
    const draw = () => {
      const element = svg.value, body = iconBody(props.name)
      if (!element || drawn === body) return
      drawn = body
      element.replaceChildren(iconFragment(element.ownerDocument, body))
    }
    onMounted(draw)
    onUpdated(draw)
    return () => h('svg', {
      ref: svg,
      xmlns: 'http://www.w3.org/2000/svg',
      viewBox: ICON_VIEWBOX,
      width: props.size,
      height: props.size,
      fill: 'none',
      stroke: 'currentColor',
      'stroke-width': ICON_STROKE_WIDTH,
      'stroke-linecap': 'round',
      'stroke-linejoin': 'round',
      'aria-hidden': 'true',
      'data-icon': props.name,
    })
  },
})
