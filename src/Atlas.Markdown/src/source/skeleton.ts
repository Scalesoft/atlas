// Part of the eager `@atlas/markdown/source` entry: plain DOM, no Monaco.
const monospace = 'var(--font-mono, ui-monospace, SFMono-Regular, Menlo, Consolas, monospace)'
export const SOURCE_EDITOR_LOADING = 'Načítá se editor zdroje…'

/**
 * A static, read-only rendering of the source shown until Monaco is ready: instant content, no layout
 * jump. The loading status appears only when loading takes longer than ~300 ms. Returns the remover.
 * A host that already reports the loading itself (an ancestor with `aria-busy="true"` owning its own
 * `role="status"`, like `MarkdownSourcePanel`) gets the visual skeleton only, so exactly one status speaks.
 */
export function showSourceSkeleton(host: HTMLElement, value: string, loading = SOURCE_EDITOR_LOADING): () => void {
  const doc = host.ownerDocument
  const skeleton = doc.createElement('div')
  skeleton.className = 'md-source-skeleton'
  const announced = !host.closest('[aria-busy="true"]')
  if (announced) {
    skeleton.setAttribute('role', 'status')
    skeleton.setAttribute('aria-busy', 'true')
  }
  skeleton.style.cssText = 'position:absolute;inset:0;margin:0;overflow:hidden;display:flex;flex-direction:column;background:inherit;'
  const label = doc.createElement('span')
  label.style.cssText = 'flex:none;min-height:17px;padding:6px 12px;font-size:12px;line-height:1.4;color:var(--text-dim, #586574);'
  const timer = announced ? setTimeout(() => { label.textContent = loading }, 300) : undefined
  const text = doc.createElement('pre')
  text.setAttribute('aria-hidden', 'true')
  // Only what fits on screen; a 512 KiB source must not cost layout time here.
  text.textContent = value.split('\n', 80).join('\n')
  text.style.cssText = `flex:1;margin:0;padding:0 12px 0 26px;overflow:hidden;white-space:pre-wrap;overflow-wrap:anywhere;font:13px/19px ${monospace};color:var(--text-dim, #586574);opacity:.75;`
  skeleton.append(label, text)
  const position = host.style.position
  if (doc.defaultView?.getComputedStyle(host).position === 'static') host.style.position = 'relative'
  host.append(skeleton)
  return () => {
    clearTimeout(timer)
    if (skeleton.isConnected) { skeleton.remove(); host.style.position = position }
  }
}
