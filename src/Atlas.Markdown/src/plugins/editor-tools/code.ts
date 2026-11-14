import type { Editor, AnyExtension } from '@tiptap/core'
import { dispatchMarkdownHistoryOperation, type MarkdownPlugin } from '../../core'
import { editorToolsStrings } from './strings'

export const markdownCodeLanguages = [
  { value: 'plaintext', get label() { return editorToolsStrings.codeLanguagePlaintext } },
  { value: 'csharp', get label() { return editorToolsStrings.codeLanguageCsharp } },
  { value: 'sql', get label() { return editorToolsStrings.codeLanguageSql } },
  { value: 'json', get label() { return editorToolsStrings.codeLanguageJson } },
  { value: 'xml', get label() { return editorToolsStrings.codeLanguageXml } },
  { value: 'javascript', get label() { return editorToolsStrings.codeLanguageJavascript } },
  { value: 'typescript', get label() { return editorToolsStrings.codeLanguageTypescript } },
  { value: 'bash', get label() { return editorToolsStrings.codeLanguageBash } },
] as const
export type MarkdownCodeLanguage = typeof markdownCodeLanguages[number]['value']
export interface CodeBlockHighlightingOptions {
  /** Limit the initially loaded language definitions. Plain text is always available. */
  languages?: readonly MarkdownCodeLanguage[]
  /** Stop syntax highlighting huge blocks while preserving all text. Default: 100,000 characters. */
  maxHighlightLength?: number
  controls?: boolean
}
export function setCodeBlockLanguage(editor: Editor, position: number, language: string): boolean {
  if (!editor.isEditable || editor.isDestroyed || !markdownCodeLanguages.some(item => item.value === language)) return false
  const node = editor.state.doc.nodeAt(position)
  if (node?.type.name !== 'codeBlock') return false
  if (node.attrs.language === language) return true
  dispatchMarkdownHistoryOperation(editor, editor.state.tr.setNodeMarkup(position, undefined, { ...node.attrs, language }))
  return true
}
export type HastNode = { type: string; value?: string; tagName?: string; properties?: { className?: unknown }; children?: readonly HastNode[] }
export interface CodeHighlighter {
  listLanguages(): string[]
  registered(name: string): boolean
  highlight(language: string, text: string): { type: 'root'; children: readonly HastNode[] }
  highlightAuto(text: string): { type: 'root'; children: readonly HastNode[] }
}

/** Lowlight with the chosen languages; unknown languages and huge blocks stay plain text. */
async function loadCodeHighlighter(options: CodeBlockHighlightingOptions): Promise<CodeHighlighter> {
  const loaders = {
    plaintext: () => import('highlight.js/lib/languages/plaintext'), csharp: () => import('highlight.js/lib/languages/csharp'),
    sql: () => import('highlight.js/lib/languages/sql'), json: () => import('highlight.js/lib/languages/json'),
    xml: () => import('highlight.js/lib/languages/xml'), javascript: () => import('highlight.js/lib/languages/javascript'),
    typescript: () => import('highlight.js/lib/languages/typescript'), bash: () => import('highlight.js/lib/languages/bash'),
  }
  // The languages load beside the highlighter, not after it: one wave of chunks before an editor can be built, not two (3.6-G open).
  const languages = [...new Set(['plaintext' as const, ...(options.languages ?? markdownCodeLanguages.map(item => item.value))])]
    .map(language => ({ language, definition: (async () => loaders[language]())() }))
  for (const { definition } of languages) definition.catch(() => { /* a language that fails to load stays plain text */ })
  const { createLowlight } = await import('./lowlight')
  const lowlight = createLowlight()
  await Promise.allSettled(languages.map(async ({ language, definition }) => { lowlight.register(language, (await definition).default) }))
  const plain = (text: string) => ({ type: 'root' as const, children: [{ type: 'text' as const, value: text }] })
  // Upstream guesses unknown languages via highlightAuto. Keep those blocks plain and predictable.
  return {
    listLanguages: () => lowlight.listLanguages(), registered: (name: string) => lowlight.registered(name),
    highlight(language: string, text: string) {
      if (text.length > (options.maxHighlightLength ?? 100_000)) return plain(text)
      try { return lowlight.highlight(language, text) as { type: 'root'; children: readonly HastNode[] } } catch { return plain(text) }
    },
    highlightAuto: (text: string) => plain(text),
  }
}

/** Opt-in async factory; core never imports Lowlight or the complete language catalog. */
export async function createCodeBlockHighlighting(options: CodeBlockHighlightingOptions = {}): Promise<AnyExtension> {
  // The node view loads with the rest, not after it (one wave of chunks, 3.6-G open); its failure is awaited below as before.
  const view = options.controls === false ? undefined : import('./codeView')
  view?.catch(() => {})
  const [{ default: CodeBlockLowlight }, safeLowlight, { incrementalHighlighting }] = await Promise.all([
    import('@tiptap/extension-code-block-lowlight'), loadCodeHighlighter(options), import('./codeHighlighting'),
  ])
  const extension = CodeBlockLowlight.configure({ lowlight: safeLowlight, HTMLAttributes: { class: 'tt-code-block' } }).extend({
    addProseMirrorPlugins() {
      const upstream = this.parent?.() ?? []
      // `parent` runs this again for every `extend` below (the node view, the dialect): one highlighter, not one per layer.
      return [...upstream.filter(plugin => !/^(lowlight|codeHighlighting)\$/.test((plugin as unknown as { key: string }).key)), incrementalHighlighting(this.name, safeLowlight, options.controls !== false)]
    },
  })
  if (!view) return extension
  const { codeBlockNodeView } = await view
  return extension.extend({ addNodeView() { return codeBlockNodeView } })
}

/** Lowlight emits only spans with hljs-* classes and text; nothing else is ever created. */
function appendHighlighted(parent: Node, nodes: readonly HastNode[], doc: Document): void {
  for (const node of nodes) {
    if (node.type === 'text') parent.appendChild(doc.createTextNode(node.value ?? ''))
    else if (node.type === 'element') {
      const span = doc.createElement('span')
      const className = node.properties?.className
      if (Array.isArray(className)) span.className = className.filter(name => typeof name === 'string').join(' ')
      appendHighlighted(span, node.children ?? [], doc)
      parent.appendChild(span)
    }
  }
}

/**
 * The static renderer's counterpart of createCodeBlockHighlighting: the same languages, limits and
 * hljs classes, applied to rendered code blocks. Blocks are highlighted as they approach the viewport,
 * so a long document never tokenizes code nobody scrolls to; the text itself is final from the start.
 */
export function createStaticCodeHighlighting(options: CodeBlockHighlightingOptions = {}): MarkdownPlugin {
  return { id: 'static-code-highlighting', apiVersion: 1, profiles: ['markdown-safe', 'rich'], create: () => ({
    onRender({ container, ownerDocument, signal }) {
      const blocks = [...container.querySelectorAll<HTMLElement>('pre > code[class*="language-"]')].filter(code => !code.closest('[data-md-module]'))
      if (!blocks.length) return
      const highlighter = loadCodeHighlighter(options)
      const highlight = (code: HTMLElement) => void highlighter.then(lowlight => {
        const language = [...code.classList].find(name => name.startsWith('language-'))?.slice('language-'.length)
        if (signal.aborted || !code.isConnected || !language || !lowlight.registered(language)) return
        const fragment = ownerDocument.createDocumentFragment()
        appendHighlighted(fragment, lowlight.highlight(language, code.textContent ?? '').children, ownerDocument)
        code.replaceChildren(fragment)
      }, () => { /* Highlighting only decorates; the plain code stays readable. */ })
      const Observer = ownerDocument.defaultView?.IntersectionObserver
      if (!Observer) { blocks.forEach(highlight); return }
      const observer = new Observer(entries => {
        for (const entry of entries) {
          if (!entry.isIntersecting) continue
          observer.unobserve(entry.target)
          highlight(entry.target as HTMLElement)
        }
      }, { rootMargin: '600px 0px' })
      blocks.forEach(block => observer.observe(block))
      return () => observer.disconnect()
    },
  }) }
}
