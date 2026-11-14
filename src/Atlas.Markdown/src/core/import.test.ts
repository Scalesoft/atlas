// @vitest-environment node
import { describe, expect, it, vi } from 'vitest'
import { createMarkdownEditorController } from './index'

// Core must remain usable without a Vue runtime or a browser. vi.mock is hoisted above the static import, so the
// guard fires while the module graph loads. The graph is imported statically on purpose: a dynamic import inside the
// test counted the load of ~250 modules through the Vite server against the 5 s test timeout, which failed on a
// loaded host while the behaviour under test was fine.
vi.mock('vue', () => { throw new Error('Headless core imported Vue') })

describe('framework-neutral entry point', () => {
  it('constructs and destroys a local session without Vue, DOM or network', async () => {
    const controller = createMarkdownEditorController({
      profile: 'markdown-safe', source: { kind: 'local', value: 'Text' },
    })
    expect(controller.getValue()).toBe('Text')
    expect(controller.state.lifecycle).toBe('idle')
    controller.destroy()
    expect(controller.state.lifecycle).toBe('destroyed')
  })
})
