import type { MarkdownContentCapability, MarkdownEditorController, MarkdownProfile } from './types'

const capabilities = new WeakMap<MarkdownEditorController, MarkdownContentCapability>()

/** Internal registration; structural controllers never receive fabricated snapshot guarantees. */
export function registerMarkdownContentCapability<P extends MarkdownProfile>(controller: MarkdownEditorController<P>, capability: MarkdownContentCapability<P>): void {
  capabilities.set(controller, capability as MarkdownContentCapability)
}

/** Returns null for external controllers that do not implement this package's snapshot boundary. */
export function getMarkdownContentCapability<P extends MarkdownProfile>(controller: MarkdownEditorController<P>): MarkdownContentCapability<P> | null {
  return (capabilities.get(controller) as MarkdownContentCapability<P> | undefined) ?? null
}
