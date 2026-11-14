// The package's own plugins for its authority tests; a host passes its own factories (Atlas: src/Atlas.Web/src/server/liveWrite.ts, the parity fixtures' reference).
import type { MarkdownPlugin } from '../core/plugins/types'
import { loadMarkdownDialect } from '../engine/markdownDialectLoader'
import { createYouTrackPlugin } from '../plugins/youtrack'
import { createContactsPlugin } from '../plugins/contacts'
import { createAttachmentsPlugin } from '../plugins/attachments'
import { createChartsPlugin } from '../plugins/charts'
import { createDiagramsPlugin } from '../plugins/diagrams'
import { createMathPlugin } from '../plugins/math'
import { createTocPlugin } from '../plugins/toc'
import { createEmojiPlugin } from '../plugins/emoji'
import { createPagesPlugin } from '../plugins/pages'
import { createHeadingAnchorsPlugin } from '../plugins/heading-anchors'

export const builtinAuthorityPlugins: Record<string, () => MarkdownPlugin> = {
  youtrack: () => createYouTrackPlugin({ client: { search: async () => { throw new Error('Authority processes cannot query YouTrack.') } } }), contacts: createContactsPlugin, attachments: createAttachmentsPlugin,
  charts: createChartsPlugin, diagrams: createDiagramsPlugin, math: createMathPlugin, toc: createTocPlugin,
  emoji: createEmojiPlugin, pages: createPagesPlugin, 'heading-anchors': createHeadingAnchorsPlugin,
}

// As a host does once before its first request (authority.ts).
await loadMarkdownDialect()
