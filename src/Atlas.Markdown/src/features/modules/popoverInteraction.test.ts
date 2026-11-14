// @vitest-environment jsdom
import { describe, expect, it } from 'vitest'
import type { Editor } from '@tiptap/core'
import { activeMarkdownPopover, onMarkdownPopoverChange, registerMarkdownPopover } from './popoverInteraction'

function fakeEditor(): Editor { return {} as Editor }

describe('popover change notifications', () => {
  it('notifies a listener when a popover opens, is replaced, and closes — the primitive the table cell orb uses to hide while a different popover covers the same cell', () => {
    const editor = fakeEditor()
    const anchorA = document.createElement('button')
    const anchorB = document.createElement('button')
    const seen: Array<ReturnType<typeof activeMarkdownPopover>> = []
    const stop = onMarkdownPopoverChange(editor, () => seen.push(activeMarkdownPopover(editor)))

    const releaseA = registerMarkdownPopover(editor, { anchor: anchorA, key: 'a', close: () => {} })
    expect(seen).toHaveLength(1)
    expect(seen[0]?.anchor).toBe(anchorA)

    // A second popover replaces the first (one active popover per editor); the orb belonging to
    // anchorA should now report "some other popover is open" once its owner re-checks.
    registerMarkdownPopover(editor, { anchor: anchorB, key: 'b', close: () => {} })
    expect(seen).toHaveLength(2)
    expect(activeMarkdownPopover(editor)?.anchor).toBe(anchorB)
    expect(activeMarkdownPopover(editor)?.anchor).not.toBe(anchorA)

    releaseA() // stale release token from the already-replaced popover — must not affect state or notify again
    expect(seen).toHaveLength(2)
    expect(activeMarkdownPopover(editor)?.anchor).toBe(anchorB)

    const releaseB = registerMarkdownPopover(editor, { anchor: anchorB, key: 'b', close: () => {} })
    releaseB()
    expect(seen).toHaveLength(4) // replace (b re-registers over itself) + release
    expect(activeMarkdownPopover(editor)).toBeUndefined()
    stop()
  })

  it('stops notifying once unsubscribed', () => {
    const editor = fakeEditor()
    let calls = 0
    const stop = onMarkdownPopoverChange(editor, () => { calls++ })
    stop()
    registerMarkdownPopover(editor, { anchor: document.createElement('button'), key: 'x', close: () => {} })
    expect(calls).toBe(0)
  })
})
