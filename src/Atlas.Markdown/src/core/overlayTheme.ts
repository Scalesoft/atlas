import { markdownMotionTokens } from './motion'

const themeTokens = [
  '--bg-1', '--bg-2', '--bg-3', '--paper', '--paper-line', '--paper-shadow',
  '--text', '--text-dim', '--text-dimmer', '--line', '--line-2',
  '--accent', '--accent-ink', '--accent-line', '--accent-soft', '--chip-accent-fg',
  '--green', '--amber', '--red', '--chip-green-fg', '--chip-amber-fg', '--chip-red-fg',
  '--font-sans', '--font-mono', '--shadow-1', '--shadow-2',
  ...markdownMotionTokens,
  '--radius-sm', '--radius-md', '--radius-lg', '--control-h', '--control-h-sm',
  '--icon', '--icon-sm', '--focus-ring', '--focus-offset',
  '--shadow-menu', '--shadow-modal', '--backdrop',
] as const

const motionTokens = new Set<string>(markdownMotionTokens)

/** Copy only editor design tokens to a body-level overlay, outside CSS inheritance. */
export function copyMarkdownOverlayTheme(source: HTMLElement, overlay: HTMLElement): void {
  const view = source.ownerDocument.defaultView
  const style = view?.getComputedStyle(source)
  if (!style) return
  const root = view!.getComputedStyle(source.ownerDocument.documentElement)
  // Every value is read before the first write: a write between two reads made each read recalculate the overlay's style
  // (~50 recalculations, ~20 ms when the slash menu opens, J12.3).
  const values = themeTokens.map(token => {
    const value = style.getPropertyValue(token).trim()
    // A motion token the editor shares with the document stays inherited, so the overlay keeps following reduced
    // motion and previews (styles/motion.css); only a value a host remapped on the editor is copied.
    return value && motionTokens.has(token) && value === root.getPropertyValue(token).trim() ? '' : value
  })
  const colorScheme = style.colorScheme
  themeTokens.forEach((token, index) => { if (values[index]) overlay.style.setProperty(token, values[index]); else overlay.style.removeProperty(token) })
  overlay.style.colorScheme = colorScheme
}

/** Removes what `copyMarkdownOverlayTheme` wrote, for an element that stays in the page. */
export function clearMarkdownOverlayTheme(overlay: HTMLElement): void {
  for (const token of themeTokens) overlay.style.removeProperty(token)
  overlay.style.removeProperty('color-scheme')
}
