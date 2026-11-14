import { beforeAll } from 'vitest'

// jsdom lays nothing out and has no Range rects; ProseMirror's scrollToSelection asks for them when a focused editor dispatches
// (an undo from a toolbar button), which then threw outside the test as an unhandled error. Empty rects, as a browser off screen.
if (typeof Range !== 'undefined') {
  Range.prototype.getClientRects ??= function () { return Object.assign([], { item: () => null }) as unknown as DOMRectList }
  Range.prototype.getBoundingClientRect ??= function () { return new DOMRect(0, 0, 0, 0) }
}

// Tests parse and write Markdown right after creating an editor, before its controller's dialect load resolves: load
// the dialect and the forms of the plugins that have one first. In beforeAll, so a test file's mocks apply to them.
// The invisible ink too: a sealed block or a secret loads it without anyone awaiting it, and a file that ended before
// the import did failed the run with EnvironmentTeardownError (rail.ts loaded after teardown, CI run 154).
beforeAll(async () => {
  await Promise.all([import('./src/plugins/charts'), import('./src/plugins/contacts'), import('./src/concealment')])
  await (await import('./src/engine/markdownDialectLoader')).loadMarkdownDialect()
})
