// @vitest-environment jsdom
import { describe, expect, it } from 'vitest'
import { createMarkdownEditorController } from '../controller'
import type { MarkdownPlugin } from './types'

describe('reference capability schema boundary', () => {
  it('rejects providers for absent, text or block nodes at construction', () => {
    for (const nodeType of ['notInstalled', 'text', 'paragraph']) {
      const plugin: MarkdownPlugin = { id: 'invalid-reference', apiVersion: 1, create: () => ({
        references: [{ id: 'invalid', label: 'Invalid', nodeType, read: () => null }],
      }) }
      expect(() => createMarkdownEditorController({
        profile: 'rich', plugins: [plugin],
        source: { kind: 'local', value: { format: 'atlas-rich', version: 1, document: { type: 'doc', content: [{ type: 'paragraph' }] } } },
      })).toThrow(/inline atom/)
    }
  })
})
