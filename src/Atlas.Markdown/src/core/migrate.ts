import { MarkdownDocumentCodec } from '../engine/documentCodec'
import type { MarkdownProfile } from './kit'
import { MarkdownPluginRegistry } from './plugins/registry'
import type { MarkdownPlugin } from './plugins/types'
import type { MarkdownValue, MarkdownValueInput } from './types'

/** Run declared plugin schema migrations and validate the resulting local document value. */
export function migrateMarkdownValue<P extends MarkdownProfile>(
  value: MarkdownValueInput<P>,
  profile: P,
  plugins: readonly MarkdownPlugin[],
): MarkdownValue<P> {
  const registry = new MarkdownPluginRegistry(plugins, profile)
  try {
    return new MarkdownDocumentCodec(profile, registry, { profile }).normalize(value)
  } finally {
    registry.destroy()
  }
}
