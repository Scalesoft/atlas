// Monaco's modules import their own `.css` files. In the library build the `lazy-monaco` plugin
// (packages/markdown/vite.config.ts) turns each such import into `registerMonacoStyle(css)`, so the
// stylesheet travels inside the lazy source editor chunk instead of the package's global style.css.
// In the playground dev server Vite injects Monaco's files itself; only our own rules (for example the
// protected-block decorations) are registered here then.
const styles: string[] = []
const installed = new WeakMap<Document | ShadowRoot, { element: HTMLStyleElement; count: number }>()

export function registerMonacoStyle(css: string): void {
  if (css) styles.push(css)
}

/** Adds the registered stylesheet once per document (and shadow root) that hosts an editor. */
export function installMonacoStyles(host: HTMLElement): void {
  if (!styles.length) return
  const doc = host.ownerDocument
  const root = host.getRootNode()
  for (const target of root instanceof doc.defaultView!.ShadowRoot ? [doc, root] : [doc]) {
    const current = installed.get(target)
    // Styles registered after the first mount (a later lazy module) are appended to the same element.
    if (current?.element.isConnected) {
      if (current.count !== styles.length) { current.element.textContent = styles.join('\n'); current.count = styles.length }
      continue
    }
    const style = doc.createElement('style')
    style.dataset.atlasMonaco = ''
    style.textContent = styles.join('\n')
    // First in <head> so host and theme rules still override Monaco's defaults at equal specificity.
    if (target instanceof doc.defaultView!.ShadowRoot) target.prepend(style)
    else doc.head.prepend(style)
    installed.set(target, { element: style, count: styles.length })
  }
}
