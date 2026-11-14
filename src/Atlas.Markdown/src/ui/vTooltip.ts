import type { Directive } from 'vue'

/**
 * Local `v-tooltip` fallback so the package never requires a globally registered directive
 * (an unresolved directive warns on every render in a clean host). Hosts with a design-system
 * tooltip can still wrap the components; native `title` text stays available to keyboard and
 * screen-reader users either way.
 */
export const vTooltip: Directive<HTMLElement, string | undefined> = {
  mounted(element, binding) {
    if (binding.value) element.setAttribute('title', binding.value)
  },
  updated(element, binding) {
    if (binding.value) element.setAttribute('title', binding.value)
    else element.removeAttribute('title')
  },
}
