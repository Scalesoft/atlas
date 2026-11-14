// @vitest-environment jsdom
import { afterEach, describe, expect, it } from 'vitest'
import { createApp, h, nextTick, ref, type App } from 'vue'
import type { MarkdownPlugin, MarkdownRichValue } from '../core'
import MarkdownRenderer from './MarkdownRenderer.vue'

const apps: App[] = []
afterEach(() => {
  apps.splice(0).forEach(app => app.unmount())
  document.body.replaceChildren()
})

describe('MarkdownRenderer lifecycle', () => {
  it('cleans and aborts each render on replacement and unmount', async () => {
    const value = ref('First')
    const events: string[] = []
    const signals: AbortSignal[] = []
    let generation = 0
    const plugin: MarkdownPlugin = {
      id: 'renderer-lifecycle', apiVersion: 1,
      create: ({ signal }) => {
        const current = ++generation
        signals.push(signal)
        return { onRender: ({ container }) => {
          events.push(`render:${current}:${container.textContent}`)
          return () => events.push(`cleanup:${current}:${signal.aborted}`)
        } }
      },
    }
    const host = document.createElement('div')
    document.body.append(host)
    const app = createApp({ render: () => h(MarkdownRenderer, {
      value: value.value, ariaLabel: 'Static preview', plugins: [plugin],
    }) })
    apps.push(app)
    app.mount(host)
    await nextTick()
    expect(events).toEqual(['render:1:First'])

    value.value = 'Second'
    await nextTick()
    expect(events).toEqual(['render:1:First', 'cleanup:1:true', 'render:2:Second'])
    expect(signals.map(signal => signal.aborted)).toEqual([true, false])

    app.unmount()
    apps.splice(apps.indexOf(app), 1)
    expect(events).toEqual(['render:1:First', 'cleanup:1:true', 'render:2:Second', 'cleanup:2:true'])
    expect(signals.every(signal => signal.aborted)).toBe(true)
  })

  it('keeps the last valid DOM and lifecycle when preparing a replacement fails', async () => {
    const valid: MarkdownRichValue = { format: 'atlas-rich', version: 1, document: {
      type: 'doc', content: [{ type: 'paragraph', content: [{ type: 'text', text: 'Last valid preview' }] }],
    } }
    const value = ref<unknown>(valid)
    const signals: AbortSignal[] = []
    let cleanups = 0
    const plugin: MarkdownPlugin = {
      id: 'atomic-static-render', apiVersion: 1,
      create: ({ signal }) => {
        signals.push(signal)
        return { onRender: () => () => { cleanups++ } }
      },
    }
    const host = document.createElement('div')
    document.body.append(host)
    const errors: unknown[] = []
    const app = createApp({ render: () => h(MarkdownRenderer, {
      value: value.value as MarkdownRichValue, profile: 'rich', ariaLabel: 'Atomic preview', plugins: [plugin],
    }) })
    app.config.errorHandler = error => errors.push(error)
    apps.push(app)
    app.mount(host)
    await nextTick()
    const originalContent = host.querySelector<HTMLElement>('.ProseMirror')
    expect(originalContent?.textContent).toBe('Last valid preview')
    expect(signals).toHaveLength(1)

    value.value = { format: 'atlas-rich', version: 1, document: {
      type: 'doc', content: [{ type: 'unknown-plugin-node' }],
    } }
    await nextTick()

    expect(errors).toHaveLength(1)
    expect(host.querySelector('.ProseMirror')).toBe(originalContent)
    expect(originalContent?.textContent).toBe('Last valid preview')
    expect(signals.map(signal => signal.aborted)).toEqual([false, true])
    expect(cleanups).toBe(0)

    app.unmount()
    apps.splice(apps.indexOf(app), 1)
    expect(signals.every(signal => signal.aborted)).toBe(true)
    expect(cleanups).toBe(1)
  })

  it('does not orphan a render that synchronously unmounts its host from onRender', async () => {
    const value = ref('First')
    const signals: AbortSignal[] = []
    let cleanups = 0
    let generation = 0
    let app!: App
    const plugin: MarkdownPlugin = {
      id: 'reentrant-render-unmount', apiVersion: 1,
      create: ({ signal }) => {
        signals.push(signal)
        const current = ++generation
        return { onRender: () => {
          if (current === 2) app.unmount()
          return () => { cleanups++ }
        } }
      },
    }
    const host = document.createElement('div')
    document.body.append(host)
    const errors: unknown[] = []
    app = createApp({ render: () => h(MarkdownRenderer, {
      value: value.value, ariaLabel: 'Reentrant preview', plugins: [plugin],
    }) })
    app.config.errorHandler = error => errors.push(error)
    apps.push(app)
    app.mount(host)
    await nextTick()

    value.value = 'Second'
    await nextTick()
    apps.splice(apps.indexOf(app), 1)

    expect(errors).toEqual([])
    expect(signals.map(signal => signal.aborted)).toEqual([true, true])
    expect(cleanups).toBe(2)
    expect(host.innerHTML).toBe('')
  })
})
