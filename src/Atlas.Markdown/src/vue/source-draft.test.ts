// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest'
import { createApp, effectScope, h, isRef, nextTick, ref, shallowRef, type App, type EffectScope, type Ref } from 'vue'
import type { MarkdownRichValue } from '../core'
import type { SourceEditor, SourceEditorOptions } from '../source'
import type { MarkdownSourceDraftOptions } from './useMarkdownSourceDraft'

// The panel must never pull Monaco in a unit test: stand in for the lazy editor.
const mounts: { host: HTMLElement; options: SourceEditorOptions; editor: SourceEditor & { value: string; readonly: boolean; diagnostics: unknown[] } }[] = []
let failMount = false
/** While set, every mount waits for it (Monaco still downloading). */
let hold: Promise<void> | undefined
vi.mock('../source', async original => ({
  ...(await original<typeof import('../source')>()),
  prefetchSourceEditor: () => false,
  mountSourceEditor: vi.fn(async (host: HTMLElement, options: SourceEditorOptions) => {
    await hold
    if (failMount) throw new Error('offline')
    const editor = {
      value: options.value, readonly: !!options.readonly, diagnostics: [] as unknown[],
      getValue: () => editor.value, setValue(value: string) { editor.value = value }, setLanguage() {},
      setDiagnostics(items: readonly unknown[]) { editor.diagnostics = [...items] }, setReadonly(value: boolean) { editor.readonly = value },
      revealLine: vi.fn(), focus: vi.fn(), layout() {}, destroy: vi.fn(),
    }
    mounts.push({ host, options, editor })
    return editor
  }),
}))

const { useMarkdownSourceDraft, MarkdownSourcePanel } = await import('./index')
const { markdownSourceDraftViewActivity } = await import('./useMarkdownSourceDraft')
// Cold import of the secrets plugin graph, kept out of the test below's timeout on a loaded host.
const { createSecretsPlugin } = await import('../plugins/secrets')
// Cold imports of the panel, the draft and the session, kept out of the tests' waits on a loaded host.
await Promise.all([import('./MarkdownSourcePanel.vue'), import('./sourceDraft'), import('../source/session')])

const text = (value: string) => ({ type: 'text', text: value })
const page = (...paragraphs: string[]): MarkdownRichValue => ({ format: 'atlas-rich', version: 1, document: {
  type: 'doc', content: paragraphs.map(item => ({ type: 'paragraph', content: [text(item)] })),
} as MarkdownRichValue['document'] })
const withProtected = (): MarkdownRichValue => ({ format: 'atlas-rich', version: 1, document: { type: 'doc', content: [
  { type: 'paragraph', content: [text('Úvod')] },
  // Underline has no Markdown syntax: the paragraph becomes a protected block.
  { type: 'paragraph', content: [{ ...text('Podtržený'), marks: [{ type: 'underline' }] }] },
  { type: 'paragraph', content: [text('Závěr')] },
] } as MarkdownRichValue['document'] })

const scopes: EffectScope[] = []
const apps: App[] = []
afterEach(() => {
  scopes.splice(0).forEach(scope => scope.stop()); apps.splice(0).forEach(app => app.unmount())
  mounts.splice(0); failMount = false; hold = undefined; document.body.replaceChildren(); vi.useRealTimers()
})
type Save = MarkdownSourceDraftOptions['save']
function draftFor<S extends Save>(value = ref(page('První', 'Druhý')), save: S = vi.fn(async () => ({ ok: true as const })) as unknown as S) {
  const readonly = ref(false)
  const scope = effectScope(); scopes.push(scope)
  const draft = scope.run(() => useMarkdownSourceDraft({ value, readonly, save }))!
  return { draft, value, readonly, save, scope }
}

describe('useMarkdownSourceDraft', () => {
  it('opens a session on the live value and saves through the host endpoint, never the document', async () => {
    const save = vi.fn(async (_value: MarkdownRichValue, _text: string, _context: { signal: AbortSignal }) => ({ ok: true as const }))
    const { draft, value } = draftFor(undefined, save)
    expect(draft.status.value).toBe('closed')
    expect(await draft.open()).toBe(true)
    expect(draft.text.value).toBe('První\n\nDruhý\n')
    expect(draft.dirty.value).toBe(false)
    expect(draft.canApply.value).toBe(false)
    draft.text.value = 'První upravený\n\nDruhý\n'
    expect(draft.dirty.value).toBe(true)
    expect(await draft.apply()).toBe(true)
    expect(save).toHaveBeenCalledOnce()
    const [saved, submitted, context] = save.mock.calls[0]!
    expect(submitted).toBe('První upravený\n\nDruhý\n')
    expect(JSON.stringify(saved.document)).toContain('První upravený')
    expect(context.signal.aborted).toBe(false)
    // The host value is untouched by the draft; the saved page is the new baseline.
    expect(JSON.stringify(value.value)).not.toContain('upravený')
    expect(draft.dirty.value).toBe(false)
    expect(draft.notice.value?.kind).toBe('saved')
    // The host catching up with the saved page is no change for the draft.
    const revision = draft.revision.value
    value.value = saved
    await nextTick()
    expect(draft.stale.value).toBe(false)
    expect(draft.revision.value).toBe(revision)
  })

  it('keeps the draft on a 409 conflict and blocks Apply until Reload', async () => {
    const save = vi.fn(async () => ({ ok: false as const, reason: 'conflict' as const }))
    const { draft, value } = draftFor(undefined, save)
    await draft.open()
    draft.text.value = 'Můj návrh\n\nDruhý\n'
    expect(await draft.apply()).toBe(false)
    expect(draft.text.value).toBe('Můj návrh\n\nDruhý\n')
    expect(draft.stale.value).toBe(true)
    expect(draft.notice.value?.kind).toBe('conflict')
    expect(draft.canApply.value).toBe(false)
    value.value = page('Cizí změna', 'Druhý')
    await nextTick()
    expect(draft.text.value).toBe('Můj návrh\n\nDruhý\n')
    // Returning to the source can mount a new panel, whose start() calls open() again.
    // An existing dirty session must survive that call even after the live page moved.
    expect(await draft.open()).toBe(true)
    expect(draft.text.value).toBe('Můj návrh\n\nDruhý\n')
    expect(draft.dirty.value).toBe(true)
    expect(draft.notice.value?.kind).toBe('conflict')
    expect(draft.canApply.value).toBe(false)
    expect(await draft.reload()).toBe(true)
    expect(draft.text.value).toBe('Cizí změna\n\nDruhý\n')
    expect(draft.stale.value).toBe(false)
    expect(draft.dirty.value).toBe(false)
  })

  it('treats a thrown 409 as a conflict and lets a draft retry after others finish live edits', async () => {
    const save = vi.fn()
      .mockResolvedValueOnce({ ok: false, reason: 'live-edits' })
      .mockRejectedValueOnce(Object.assign(new Error('Conflict'), { status: 409 }))
    const { draft } = draftFor(undefined, save)
    await draft.open()
    draft.text.value = 'Návrh\n'
    expect(await draft.apply()).toBe(false)
    expect(draft.notice.value?.kind).toBe('live-edits')
    expect(draft.stale.value).toBe(true)
    expect(draft.canApply.value).toBe(true)
    expect(await draft.apply()).toBe(false)
    expect(draft.notice.value?.kind).toBe('conflict')
    expect(draft.canApply.value).toBe(false)
    expect(draft.text.value).toBe('Návrh\n')
  })

  it('follows a changed base while clean and turns stale while dirty', async () => {
    vi.useFakeTimers()
    const { draft, value } = draftFor()
    await draft.open()
    value.value = page('Nový', 'Druhý')
    await nextTick(); await vi.advanceTimersByTimeAsync(300)
    await vi.waitFor(() => expect(draft.text.value).toBe('Nový\n\nDruhý\n'))
    draft.text.value = 'Rozpracováno\n'
    value.value = page('Ještě novější')
    await nextTick()
    expect(draft.stale.value).toBe(true)
    expect(draft.notice.value?.kind).toBe('changed')
    expect(draft.text.value).toBe('Rozpracováno\n')
    expect(draft.canApply.value).toBe(false)
    // Discarding drops the draft and regenerates the current page.
    draft.discard()
    await vi.waitFor(() => expect(draft.text.value).toBe('Ještě novější\n'))
    expect(draft.stale.value).toBe(false)
  })

  it('reports protected block edits as diagnostics without saving, and honours readonly', async () => {
    const { draft, save, readonly } = draftFor(ref(withProtected()))
    await draft.open()
    const block = draft.protectedBlocks.value[0]!
    const lines = draft.text.value.split('\n')
    lines[block.startLine] = 'Chráněný blok: přepsáno'
    draft.text.value = lines.join('\n')
    expect(await draft.apply()).toBe(false)
    expect(save).not.toHaveBeenCalled()
    expect(draft.notice.value?.kind).toBe('invalid')
    expect(draft.diagnostics.value.some(item => item.severity === 'error')).toBe(true)
    draft.text.value = draft.baseText.value.replace('Úvod', 'Úvod upraven')
    readonly.value = true
    expect(draft.canApply.value).toBe(false)
    expect(await draft.apply()).toBe(false)
    expect(save).not.toHaveBeenCalled()
  })

  it('ignores discard while a save is in flight', async () => {
    let finish!: () => void
    const save = vi.fn(() => new Promise<void>(resolve => { finish = resolve }))
    const { draft } = draftFor(undefined, save)
    await draft.open()
    draft.text.value = 'Návrh\n'
    const applying = draft.apply()
    draft.discard()
    expect(draft.text.value).toBe('Návrh\n')
    finish()
    expect(await applying).toBe(true)
  })

  it('does not regenerate a clean draft under the reader, then follows the base when they leave', async () => {
    vi.useFakeTimers()
    const { draft, value } = draftFor()
    await draft.open()
    const active = markdownSourceDraftViewActivity(draft)!
    active.value = true
    const revision = draft.revision.value
    value.value = page('Cizí změna', 'Druhý')
    await nextTick(); await vi.advanceTimersByTimeAsync(300)
    expect(draft.text.value).toBe('První\n\nDruhý\n')
    expect(draft.revision.value).toBe(revision)
    active.value = false
    await nextTick(); await vi.advanceTimersByTimeAsync(300)
    await vi.waitFor(() => expect(draft.text.value).toBe('Cizí změna\n\nDruhý\n'))
  })

  it('marks a draft stale when the reader starts editing on a base that moved under them', async () => {
    const { draft, value } = draftFor()
    await draft.open()
    markdownSourceDraftViewActivity(draft)!.value = true
    value.value = page('Cizí změna', 'Druhý')
    await nextTick()
    expect(draft.stale.value).toBe(false)
    draft.text.value = 'Můj návrh\n\nDruhý\n'
    await nextTick()
    expect(draft.stale.value).toBe(true)
    expect(draft.notice.value?.kind).toBe('changed')
    expect(draft.canApply.value).toBe(false)
  })

  it('aborts an in-flight save when the draft closes', async () => {
    let signal!: AbortSignal
    let finish!: () => void
    const save = vi.fn((_value: MarkdownRichValue, _text: string, context: { signal: AbortSignal }) => { signal = context.signal; return new Promise<void>(resolve => { finish = resolve }) })
    const { draft } = draftFor(undefined, save)
    await draft.open()
    draft.text.value = 'Návrh\n'
    const applying = draft.apply()
    expect(draft.busy.value).toBe(true)
    draft.close()
    expect(signal.aborted).toBe(true)
    finish()
    expect(await applying).toBe(false)
    expect(draft.status.value).toBe('closed')
    expect(draft.notice.value).toBeUndefined()
  })

  it('carries secret references through the page source next to protected blocks', async () => {
    const secretId = 'd847b3af-4a87-4e97-aab8-a74a4cf48c89'
    const plugins = [createSecretsPlugin({ services: {
      discover: async () => 'hidden', requestAccess: async () => {}, reveal: async () => { throw new Error('never') },
      copy: async () => {}, subscribeRevocation: () => () => {},
    } })]
    const card = { type: 'secretReference', attrs: { secretId, field: 'password', fields: ['username', 'password'], showCopy: true, showDetails: true, autoHideSeconds: 60 } }
    const sentence = { type: 'paragraph', content: [text('Heslo '), { type: 'secretInlineReference', attrs: { secretId, field: 'password', fields: null, showCopy: false, showDetails: false, autoHideSeconds: null } }] }
    const original = { format: 'atlas-rich', version: 1, plugins: [{ id: 'secrets', version: 4 }], document: { type: 'doc', content: [
      ...(withProtected().document.content as unknown[]), card, sentence,
    ] } } as MarkdownRichValue
    const save = vi.fn(async (_value: MarkdownRichValue) => ({ ok: true as const }))
    const scope = effectScope(); scopes.push(scope)
    const draft = scope.run(() => useMarkdownSourceDraft({ value: ref(original), readonly: ref(false), plugins, save }))!
    expect(await draft.open()).toBe(true)
    // Only the underlined paragraph is protected; the references are ordinary, editable Markdown.
    expect(draft.protectedBlocks.value).toHaveLength(1)
    expect(draft.text.value).toContain(`::secret{secretId=${secretId} fields="username password" showDetails=true}`)
    expect(draft.text.value).toContain(`Heslo :secret{secretId=${secretId} showCopy=false autoHideSeconds=null}`)
    draft.text.value = draft.text.value.replace('Závěr', 'Konec')
    expect(await draft.apply()).toBe(true)
    const saved = save.mock.calls[0]![0].document.content as unknown[]
    expect(saved.slice(3)).toEqual([card, sentence])
    expect(JSON.stringify(saved)).toContain('Konec')
  })
})

describe('MarkdownSourcePanel', () => {
  type Draft = ReturnType<typeof draftFor>['draft']
  function mountPanel(draft: Draft | Ref<Draft>) {
    const host = document.createElement('div'); document.body.append(host)
    const app = createApp({ render: () => h(MarkdownSourcePanel, { draft: isRef(draft) ? draft.value : draft }) }); apps.push(app); app.mount(host)
    return host
  }
  const alive = () => mounts.filter(item => !(item.editor.destroy as ReturnType<typeof vi.fn>).mock.calls.length)

  it('mounts again after the draft closed and reopened while Monaco was still loading', async () => {
    let release!: () => void
    hold = new Promise(resolve => { release = resolve })
    const { draft } = draftFor()
    mountPanel(draft)
    await vi.waitFor(() => expect(draft.status.value).toBe('ready'))
    await nextTick()
    draft.close()
    await nextTick()
    expect(await draft.open()).toBe(true)
    release(); hold = undefined
    await vi.waitFor(() => expect(alive()).toHaveLength(1))
    await new Promise(resolve => setTimeout(resolve, 20))
    // The mount started before close was destroyed; exactly one editor stays alive.
    expect(alive()).toHaveLength(1)
  })

  it('applies a readonly change that happens while the editor loads', async () => {
    let release!: () => void
    hold = new Promise(resolve => { release = resolve })
    const { draft, readonly } = draftFor()
    mountPanel(draft)
    await vi.waitFor(() => expect(draft.status.value).toBe('ready'))
    await nextTick()
    readonly.value = true
    await nextTick()
    release()
    await vi.waitFor(() => expect(mounts).toHaveLength(1))
    await vi.waitFor(() => expect(mounts[0]!.editor.readonly).toBe(true))
  })

  it('has one loading status, and remounts on a replaced draft', async () => {
    let release!: () => void
    hold = new Promise(resolve => { release = resolve })
    const first = draftFor(ref(page('První stránka'))).draft
    const current = shallowRef(first)
    const host = mountPanel(current)
    await vi.waitFor(() => expect(first.status.value).toBe('ready'))
    await nextTick()
    expect(host.querySelectorAll('[role=status]')).toHaveLength(1)
    release(); hold = undefined
    await vi.waitFor(() => expect(mounts).toHaveLength(1))
    const second = draftFor(ref(page('Druhá stránka'))).draft
    current.value = second
    await vi.waitFor(() => expect(mounts).toHaveLength(2))
    expect(mounts[1]!.options.value).toBe('Druhá stránka\n')
    expect(mounts[0]!.editor.destroy).toHaveBeenCalled()
    mounts[1]!.options.onChange!('Upraveno\n')
    expect(second.text.value).toBe('Upraveno\n')
    expect(first.text.value).toBe('První stránka\n')
  })
  it('mounts the editor after the session, applies through the draft and follows readonly', async () => {
    const { draft, save, readonly } = draftFor()
    const host = mountPanel(draft)
    await vi.waitFor(() => expect(mounts).toHaveLength(1))
    const { options, editor } = mounts[0]!
    expect(options.language).toBe('markdown')
    expect(options.value).toBe('První\n\nDruhý\n')
    const applyButton = () => [...host.querySelectorAll('button')].find(button => button.textContent === 'Použít')
    await vi.waitFor(() => expect(applyButton()).toBeTruthy())
    expect(applyButton()!.disabled).toBe(true)
    options.onChange!('Upraveno\n')
    await nextTick()
    expect(applyButton()!.disabled).toBe(false)
    applyButton()!.click()
    await vi.waitFor(() => expect(save).toHaveBeenCalledOnce())
    await vi.waitFor(() => expect(host.querySelector('[role=status]')?.textContent).toContain('uložen'))
    expect(editor.value).toBe('Upraveno\n')
    readonly.value = true
    await nextTick()
    expect(editor.readonly).toBe(true)
    expect(applyButton()).toBeUndefined()
  })

  it('keeps a readonly text and offers retry when the editor cannot load', async () => {
    failMount = true
    const { draft } = draftFor()
    const host = mountPanel(draft)
    await vi.waitFor(() => expect(host.querySelector('[role=alert]')).not.toBeNull())
    expect(host.querySelector('pre')?.textContent).toBe('První\n\nDruhý\n')
    const retry = [...host.querySelectorAll('button')].find(button => button.textContent === 'Zkusit znovu')!
    failMount = false
    retry.click()
    await vi.waitFor(() => expect(mounts).toHaveLength(1))
    await vi.waitFor(() => expect(host.querySelector('pre')).toBeNull())
  })

  it('puts the caret into the editor from the control that opened it, never away from another field', async () => {
    const focused = async (opener: HTMLElement) => {
      document.body.append(opener); opener.focus()
      mountPanel(draftFor().draft)
      await vi.waitFor(() => expect(mounts).toHaveLength(1))
      await nextTick()
      const calls = (mounts[0]!.editor.focus as ReturnType<typeof vi.fn>).mock.calls.length
      apps.splice(0).forEach(app => app.unmount()); mounts.splice(0); opener.remove()
      return calls
    }
    const button = (attribute: string, value: string) => { const element = document.createElement('button'); element.setAttribute(attribute, value); return element }
    expect(await focused(button('role', 'tab'))).toBe(1)
    // A host's own segmented switch (Dokument | Zdroj): the pressed option opened the source.
    expect(await focused(button('aria-pressed', 'true'))).toBe(1)
    expect(await focused(document.createElement('input'))).toBe(0)
  })

  it('passes on Esc that the editor did not use itself', async () => {
    const escaped = vi.fn()
    const host = document.createElement('div'); document.body.append(host)
    const app = createApp({ render: () => h(MarkdownSourcePanel, { draft: draftFor().draft, onEscape: escaped }) }); apps.push(app); app.mount(host)
    await vi.waitFor(() => expect(mounts).toHaveLength(1))
    mounts[0]!.options.onEscape!()
    expect(escaped).toHaveBeenCalledOnce()
  })
})
