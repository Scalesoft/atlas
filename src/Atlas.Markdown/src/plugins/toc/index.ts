import { defineMarkdownBlockModule, type MarkdownReadonlyJSONContent } from '../../core'
import { tocStrings } from './strings'
import { tocRuntime } from './runtime'

export interface MarkdownTocData { version: 1; title: string; minLevel: number; maxLevel: number }
export interface MarkdownTocHeading { title: string; level: number; index: number; headingId?: string }
export function normalizeToc(value: unknown): MarkdownTocData {
  const data = value as Partial<MarkdownTocData> | null
  if (!data || data.version !== 1 || typeof data.title !== 'string' || data.title.length > 200
    || !Number.isInteger(data.minLevel) || !Number.isInteger(data.maxLevel)
    || data.minLevel! < 1 || data.maxLevel! > 6 || data.minLevel! > data.maxLevel!) {
    throw new TypeError(tocStrings.titleRequired)
  }
  return { version: 1, title: data.title, minLevel: data.minLevel!, maxLevel: data.maxLevel! }
}
/** Headings come from document data, never from labels inside other rendered modules. */
export function collectTocHeadings(document: MarkdownReadonlyJSONContent, options: Pick<MarkdownTocData, 'minLevel' | 'maxLevel'>): MarkdownTocHeading[] {
  const entries: MarkdownTocHeading[] = []
  let index = 0
  const text = (node: MarkdownReadonlyJSONContent): string => node.text ?? (node.type === 'hardBreak' ? ' ' : (node.content ?? []).map(text).join(''))
  const walk = (node: MarkdownReadonlyJSONContent) => {
    if (node.type === 'heading') {
      const level = Number(node.attrs?.level ?? 1)
      const headingIndex = index++
      if (level >= options.minLevel && level <= options.maxLevel) entries.push({ title: text(node).trim() || tocStrings.headingWithoutText, level, index: headingIndex,
        ...(typeof node.attrs?.headingId === 'string' && node.attrs.headingId ? { headingId: node.attrs.headingId } : {}),
      })
    }
    node.content?.forEach(walk)
  }
  walk(document)
  return entries
}
const module = defineMarkdownBlockModule<MarkdownTocData>({
  id: 'toc', name: 'tableOfContents', dataValidator: 'tableOfContents', get label() { return tocStrings.defaultTitle }, get description() { return tocStrings.slashDescription }, icon: 'outline', keywords: ['obsah', 'toc', 'rejstrik', 'osnova'], watchDocument: true,
  defaults: { version: 1, title: 'Obsah dokumentu', minLevel: 1, maxLevel: 3 },
  normalize: normalizeToc,
  text: data => `${data.title || 'Obsah dokumentu'} (automaticky podle nadpisů ${data.minLevel}–${data.maxLevel})`,
  // The list is the page's own headings: no download, no wait; the stub already has the size of the list (J8.1).
  exactStub: true,
  // Measured in the page editor (2026-11-10): the header and the list's margins 53 px, an entry 27.2 px and 2 px between entries.
  stubHeight: (data, document) => Math.round(53 + 29.2 * Math.max(1, collectTocHeadings(document, data).length)),
  load: async () => tocRuntime,
})
export const createTocPlugin = module.createPlugin
export const insertToc = module.insert
export const createTocNode = module.node
