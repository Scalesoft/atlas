import { Extension, Node, type Command, type JSONContent } from '@tiptap/core'
import { PluginKey } from '@tiptap/pm/state'
import Suggestion from '@tiptap/suggestion'
import { createMarkdownChip, iconMarkup, markdownChipSpec, createMarkdownSuggestionRenderer, defineMarkdownAttributeValidator, defineMarkdownCommand, dispatchMarkdownHistoryOperation, defineMarkdownEntityKind, markdownModuleUrl, registerMarkdownChipText, registerMarkdownRecognizer, shouldShowMarkdownSuggestion, type MarkdownChipOptions, type MarkdownIconName, type MarkdownLexiconPage, type MarkdownPlugin, type MarkdownRecognizer } from '../../core'
import { pagesStrings } from './strings'

// „Převést na text“ keeps the title the reference shows; „Převést na běžný odkaz“ keeps the link too.
registerMarkdownChipText('pageReference', node => String(node.attrs.title ?? ''))

export interface MarkdownPageReference {
  id: string; title: string; url: string
  /**
   * Shown beside the title in the suggestion list (for example a space name or "Koncept"). Display only: it is
   * kept by `normalizePageReference` for search results but never written to the inserted node or the document.
   */
  detail?: string
  /** An icon of the editor's set drawn before a resolved link's title (a board link among page links). Display only, as `detail`. */
  icon?: string
  /** A warning (a page whose verification expired): the chip gets `data-warn` for the host to tone it. Display only, as `icon`. */
  warn?: boolean
}
export interface MarkdownPageSource {
  search(query: string, context: { signal: AbortSignal }): Promise<readonly MarkdownPageReference[]>
  /** Resolve current visibility and location; null means unavailable. Never an authorization snapshot. */
  resolve?(id: string, context: { signal: AbortSignal }): Promise<MarkdownPageReference | null>
  /** Pure host URL policy; paired with resolveUrl to enable smart paste. */
  matchUrl?(url: string): boolean
  /** Resolve a host-recognized URL through current permissions, only on explicit request. */
  resolveUrl?(url: string, context: { readonly signal: AbortSignal }): Promise<MarkdownPageReference | null>
  /** The page being edited: its own title in its text is never offered as a link to itself. */
  currentId?: () => string | undefined
}
export interface PagesPluginOptions {
  source?: MarkdownPageSource
  /** Typing [[ is opt-in. Slash selection remains available whenever a source exists. */
  typing?: boolean
  debounceMs?: number
  limit?: number
  /** Host navigation must enforce current permissions. */
  navigate?: (id: string) => void
}
function required(value: unknown): string {
  if (typeof value !== 'string' || !value.trim() || value.length > 4096 || /[\u0000-\u001f\u007f]/u.test(value)) throw new TypeError(pagesStrings.invalidPageReference)
  return value.trim()
}
/** A display label is optional: anything but a short plain string is dropped rather than rejecting the page. */
function optionalDetail(value: unknown): string | undefined {
  return typeof value === 'string' && value.trim() && value.length <= 200 && !/[\u0000-\u001f\u007f]/u.test(value) ? value.trim() : undefined
}
export function normalizePageReference(value: unknown): MarkdownPageReference {
  if (!value || typeof value !== 'object') throw new TypeError(pagesStrings.invalidPageReference)
  const v = value as Record<string, unknown>
  const detail = optionalDetail(v.detail)
  const icon = typeof v.icon === 'string' && Object.hasOwn(iconMarkup, v.icon) ? v.icon : undefined
  return { id: required(v.id), title: required(v.title), url: markdownModuleUrl(required(v.url)), ...(detail ? { detail } : {}), ...(icon ? { icon } : {}), ...(v.warn === true ? { warn: true } : {}) }
}
export function pageReferenceNode(page: MarkdownPageReference): JSONContent {
  const { id, title, url } = normalizePageReference(page)
  return { type: 'pageReference', attrs: { id, title, url } }
}
export const insertPageReference = defineMarkdownCommand<MarkdownPageReference>({
  id: 'pages.insert', apply: page => ({ commands }) => { try { return commands.insertContent(pageReferenceNode(page)) } catch { return false } },
})

/**
 * A link to a part of a page (Atlas: a requirement's section, `?heading=`), as another plugin tells it by a node decoration on the
 * reference, spec `{ [pageSectionSpec]: section }`: it shows the part's live `title` instead of the page's and opens with `open`
 * (none: its own stored url) instead of `navigate`; the resolver then only checks that the page can be opened. Without such a
 * decoration a reference is a plain page link. `page` names the page a part of another page is on, after the title and in what the
 * chip says; `missing` says why the part is gone, and the chip is greyed with it as its tooltip, as a page that cannot be opened.
 */
export interface MarkdownPageSection { readonly title: string; readonly open?: () => void; readonly page?: string; readonly missing?: string }
export const pageSectionSpec = 'pageSection'
const sectionOf = (decorations: readonly { spec: Record<string, unknown> }[]) =>
  decorations.map(decoration => decoration.spec[pageSectionSpec] as MarkdownPageSection | undefined).find(Boolean)
/** What a section adds to its chip (undefined clears it again): its page as the detail, a gone part greyed with why. */
const sectionLook = (part: MarkdownPageSection | undefined, label: string): Partial<MarkdownChipOptions> => ({
  detail: part?.page, state: part?.missing ? 'muted' : undefined, title: part?.missing || (part?.page ? `${label} · ${part.page}` : undefined),
  ariaLabel: part?.page || part?.missing ? [label, part.page, part.missing].filter(Boolean).join(', ') : undefined,
})

/** A page link as the shared inline chip (a tag, as an issue), its title the label and the resolved page's icon before it. */
const pageChip = (page: MarkdownPageReference, editable = false): MarkdownChipOptions => ({ tag: 'a', href: page.url, label: page.title, className: 'md-page-reference', editable })

function decorateLink(link: HTMLAnchorElement, page: MarkdownPageReference, options: PagesPluginOptions, editable = false, section: () => MarkdownPageSection | undefined = () => undefined) {
  const abort = new AbortController()
  const first = section()?.title || page.title
  const chip = createMarkdownChip(link.ownerDocument, { ...pageChip({ ...page, title: first }, editable), ...sectionLook(section(), first) }, link)
  link.dataset.pageReference = page.id
  const click = (event: MouseEvent) => {
    if (link.getAttribute('aria-disabled') === 'true') { event.preventDefault(); return }
    const part = section()
    if (part?.open) { event.preventDefault(); part.open() }
    else if (!part && options.navigate) { event.preventDefault(); options.navigate(page.id) }
  }
  link.addEventListener('click', click)
  const look = (part: MarkdownPageSection | undefined) => `${part?.page ?? ''}\n${part?.missing ?? ''}`
  let resolved: MarkdownPageReference | undefined, available = true, shown = `${first}\n${page.url}\n${look(section())}`
  // A page the reader cannot open shows why instead of its saved title (a private space's title must not leak), greyed, without a link.
  const unavailable = (why: string) => { available = false; chip.update({ label: why, detail: undefined, ariaLabel: undefined, state: 'muted', title: why, editable: false }); link.removeAttribute('href'); link.setAttribute('aria-disabled', 'true') }
  // A section keeps its stored address (the part of the page); a plain link takes the resolved page's.
  const show = () => {
    const part = section(), label = part?.title || resolved?.title || page.title, href = part ? page.url : resolved?.url ?? page.url
    if (!available || shown === `${label}\n${href}\n${look(part)}`) return
    shown = `${label}\n${href}\n${look(part)}`
    chip.update({ label, href, ...(resolved?.icon ? { icon: resolved.icon as MarkdownIconName } : {}), ...sectionLook(part, label) })
  }
  if (options.source?.resolve) {
    void Promise.resolve().then(() => abort.signal.aborted ? null : options.source!.resolve!(page.id, { signal: abort.signal })).then(result => {
      if (abort.signal.aborted) return
      if (!result) { unavailable(pagesStrings.pageUnavailable); return }
      const current = normalizePageReference(result)
      if (current.id !== page.id) throw new TypeError('Resolver změnil identitu stránky.')
      resolved = current; shown = ''
      link.toggleAttribute('data-warn', !!current.warn)
      show()
    }).catch(() => { if (!abort.signal.aborted) unavailable(pagesStrings.verificationFailed) })
  }
  return { show, destroy: () => { abort.abort(); link.removeEventListener('click', click) } }
}
function pageNode(options: PagesPluginOptions) {
  return Node.create({
    name: 'pageReference', group: 'inline', inline: true, atom: true,
    addAttributes() {
      return Object.fromEntries(['id', 'title', 'url'].map(key => [key, { default: null, rendered: false, validate: key === 'url'
        ? defineMarkdownAttributeValidator({ kind: 'url', policy: 'http', maxLength: 4096 }, value => { markdownModuleUrl(required(value)) })
        : defineMarkdownAttributeValidator({ kind: 'text', nonBlank: true, singleLine: true, maxLength: 4096 }, value => { required(value) }) }]))
    },
    // Before the link mark's a[href] (priority 50): a copied page or section link pastes back as the link node, not as a link.
    parseHTML: () => [{ tag: 'a[data-page-reference]', priority: 60, getAttrs: element => {
      try { return normalizePageReference({ id: element.getAttribute('data-page-reference'), title: element.getAttribute('data-page-title') ?? element.textContent, url: element.getAttribute('href') }) } catch { return false }
    } }],
    renderHTML: ({ node }) => { const page = normalizePageReference(node.attrs); return markdownChipSpec(pageChip(page), { 'data-page-reference': page.id, 'data-page-title': page.title }) },
    renderText: ({ node }) => normalizePageReference(node.attrs).title,
    addStorage: () => ({ markdown: { serialize(state: { write(s: string): void; text(s: string): void }, node: { attrs: unknown }) {
      const p = normalizePageReference(node.attrs); state.write('['); state.text(p.title); state.write(`](${p.url.replace(/[\\()]/g, '\\$&')})`)
    } } }),
    addNodeView() { return ({ view, node, decorations }) => {
      const dom = view.dom.ownerDocument.createElement('a')
      let section = sectionOf(decorations)
      const link = decorateLink(dom, normalizePageReference(node.attrs), options, view.editable, () => section)
      // A changed decoration (the section's title now) redraws the label; a changed reference is drawn anew.
      return { dom, destroy: link.destroy, update(next, nextDecorations) {
        if (!next.eq(node)) return false
        section = sectionOf(nextDecorations); link.show()
        return true
      } }
    } },
  })
}

export function createPagesPlugin(options: PagesPluginOptions = {}): MarkdownPlugin {
  return { id: 'pages', apiVersion: 1, entities: [defineMarkdownEntityKind({ kind: 'page', inline: 'pageReference' })], schemaVersion: 1, profiles: ['rich'], create() {
    const resolveUrl = options.source?.resolveUrl?.bind(options.source)
    const key = new PluginKey('markdownPageSuggestions')
    // This per-editor flag enables the slash-triggered session without changing global typing preferences.
    let requested = false
    /** `{ query }` starts the `[[` session with that text already typed (slash parameter `/stránka vpn`). */
    const search = (args: { query?: string } | void): Command => ({ commands, dispatch, state }) => {
      if (!options.source || state.selection.$from.parent.type.spec.code
        || state.selection.$from.marks().some(mark => mark.type.name === 'code' || mark.type.name === 'link')) return false
      if (!dispatch) return true
      requested = true
      const previous = state.selection.$from.parentOffset ? state.doc.textBetween(state.selection.from - 1, state.selection.from) : ''
      const query = args?.query?.replace(/\s+/gu, ' ').trim() ?? ''
      const inserted = commands.insertContent(`${previous && !/[\s(]/u.test(previous) ? ' [[' : '[['}${query}`)
      if (!inserted) requested = false
      return inserted
    }
    const open = defineMarkdownCommand<{ query?: string } | void>({ id: 'pages.search', apply: search })
    const suggestion = Extension.create({ name: 'markdownPageSuggestions', addProseMirrorPlugins() {
      return [Suggestion<MarkdownPageReference>({
        editor: this.editor, pluginKey: key, char: '[[', allowSpaces: true, allowedPrefixes: [' ', '\n', '('], items: () => [], shouldShow: shouldShowMarkdownSuggestion,
        allow: ({ editor, state, range }) => (options.typing === true || requested) && editor.isEditable && !state.selection.$from.parent.type.spec.code
          && (!state.schema.marks.code || !state.doc.rangeHasMark(range.from, range.to, state.schema.marks.code))
          && (!state.schema.marks.link || !state.doc.rangeHasMark(range.from, range.to, state.schema.marks.link)),
        command: ({ editor, range, props }) => {
          if (!editor.isEditable) return
          requested = false
          const node = editor.schema.nodeFromJSON(pageReferenceNode(props))
          dispatchMarkdownHistoryOperation(editor, editor.state.tr.replaceWith(range.from, range.to, node)); editor.view.focus()
        },
        render: () => {
          const renderer = createMarkdownSuggestionRenderer<MarkdownPageReference>({ pluginKey: key, label: pagesStrings.label, testId: 'page-suggestions', hintText: pagesStrings.suggestionHint,
          source: { search: async (query, context) => (await options.source!.search(query, context)).map(normalizePageReference), debounceMs: options.debounceMs ?? 200, limit: options.limit ?? 20, minQueryLength: 0 },
          mapItem: page => ({ id: page.id, title: page.title, description: page.detail ?? page.url }),
          })()
          return { ...renderer, onExit(props) { requested = false; renderer.onExit?.(props) } }
        },
      })]
    } })
    // Titles of existing pages in written text (core/recognition, P15): underlined like a known name, linked only when picked.
    const titles = Extension.create({ name: 'markdownPageTitles', onCreate() {
      const source = options.source!
      const recognizer: MarkdownRecognizer = {
        kind: 'page',
        entries: lexicon => { const self = source.currentId?.()
          return (lexicon?.pages ?? []).flatMap(page => page.id === self ? [] : [{ kind: 'page', id: page.id, name: page.title, form: 'full' as const, auto: false, target: page }]) },
        // shortcut: a title several pages share links only when the source finds just one of them readable; offer each when that shows.
        chip: async (hit, { signal }) => {
          const pages = hit.candidates as readonly MarkdownLexiconPage[]
          const one = pages.length === 1 && source.resolve ? await source.resolve(pages[0]!.id, { signal }) : null
          const found = one ? [one] : (await source.search(hit.text, { signal })).filter(page => pages.some(known => known.id === page.id))
          return found.length === 1 ? pageReferenceNode(found[0]!) : null
        },
        label: hit => pagesStrings.linkSelection(hit.text),
        icon: 'svg:file-text',
      }
      const editor = this.editor, stop = registerMarkdownRecognizer(editor, recognizer)
      editor.on('destroy', stop)
    } })
    // Its own drawing: without one the slash menu showed the default „+“ in the label's place.
    const action = { id: 'pages.search', label: pagesStrings.label, description: pagesStrings.description, icon: 'svg:file-text', keywords: ['page', 'wiki', 'stranka', 'odkaz'], command: open(),
      parameter: { label: pagesStrings.label, command: (text: string) => open({ query: text }) } }
    return {
      extensions: [pageNode(options), ...(options.source ? [suggestion, titles] : [])], commands: [insertPageReference, open],
      references: [{
        id: 'pages.page', label: pagesStrings.label, nodeType: 'pageReference',
        read(node) {
          if (node.type !== 'pageReference') return null
          const page = normalizePageReference(node.attrs)
          return { url: page.url, label: page.title }
        },
        matchUrl: resolveUrl ? options.source?.matchUrl?.bind(options.source) : undefined,
        resolveUrl: resolveUrl ? async (url, context) => {
          const page = await resolveUrl(url, context)
          return page ? pageReferenceNode(page) : null
        } : undefined,
        // The toolbar's link field finds pages as `[[` does: the same source, its order.
        search: options.source ? async (query, context) => (await options.source!.search(query, context)).map(normalizePageReference)
          .map(page => ({ url: page.url, label: page.title, ...(page.detail ? { detail: page.detail } : {}) })) : undefined,
      }],
      actions: { slash: options.source ? [action] : [], toolbar: options.source ? [action] : [] },
      // Ctrl+Space over selected words: the `[[` search with them, as `/stránka words` does.
      intentions: ({ editor, empty, text }) => !options.source || empty || !text.trim() || /[\n￼]/u.test(text) || text.length > 120 ? []
        : [{ id: 'pages.link', label: pagesStrings.linkSelection(text.trim()), icon: 'svg:file-text', keywords: ['odkaz', 'stranka'], run: () => { editor.commands.command(search({ query: text })) } }],
      // The page source carries a reference itself: its link with the same address and title comes back as the reference.
      markdownLossy(doc, lossyOptions) { let found = false; if (!lossyOptions?.ignoreIdentity) doc.descendants(node => { if (node.type.name === 'pageReference') found = true }); return found },
      onRender({ container }) {
        const cleanups = [...container.querySelectorAll<HTMLAnchorElement>('a[data-page-reference]')].map(link => decorateLink(link, normalizePageReference({ id: link.dataset.pageReference, title: link.dataset.pageTitle ?? link.textContent, url: link.getAttribute('href') }), options).destroy)
        return () => cleanups.forEach(cleanup => cleanup())
      },
    }
  } }
}
