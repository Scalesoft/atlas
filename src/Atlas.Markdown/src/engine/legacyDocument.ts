import type { JSONContent } from '@tiptap/core'
import { timedStamp } from '../features/timed/timedList'
import { LINK_REL } from '../core/documentConstraints'

/** Nodes whose content expression starts with a paragraph ("paragraph block*"). */
const startsWithParagraph = new Set(['listItem', 'taskItem', 'timedItem'])

/**
 * Lossless repair of shapes Atlas 2.x stored and rendered but the schema refuses,
 * applied wherever a stored document is opened; Atlas.Markdown.Sync's LegacyDocumentRepair is the server's copy.
 * - A mark repeated on one text node (a link nested in a link) keeps its last, innermost copy: the one 2.x rendered innermost and a click opened.
 * - A list or task item that is empty or starts with another block gets an empty leading paragraph, as ProseMirror fills a required node.
 * - One contact per block (Atlas 3.2): a contactCard of several contacts inside a content list becomes consecutive single-contact cards.
 * - One overview block (Atlas 3.4): Atlas's deployment summary `requirementSummary` is the page overview `overview` of kind `requirements`,
 *   its id kept (src/Atlas.Web/src/overview; the host keeps the old node in its schema so a live document that holds one still opens).
 * - A history line (Atlas 3.5.1) the MCP help wrote as `- 30. 10. 2026 — text` and the server read as two nested ordered lists
 *   (start 30 › start 10 › "2026 — text") is one paragraph "30. 10. 2026 — text" again, its marks kept; only inside a `requirement`.
 * - A timed list (Atlas 3.6, rich revision 5): a bullet list whose first item starts with a stamp „3. 11. 2026 17:19 — “ is a `timedList`
 *   of `timedItem`s, on any page, its attributes and Markdown unchanged (features/timed/timedList.ts).
 * - No disclosure block (Atlas 3.6, rich revision 7: every heading folds): a `disclosure` is a heading carrying its title, then its body,
 *   in place; nested ones too. Its title is its first non-empty `disclosureTitle` (revision 6), else its `title` attribute (before), else
 *   „Podrobnosti“; the heading is one level below the nearest top-level heading before it (2–6), 3 with none (a heading in a cell, list
 *   item or callout does not count). Its body takes that level, the next block the one before again: consecutive disclosures stay
 *   siblings, a nested one goes below its parent.
 * - Link attributes (Atlas 4.0): a target other than _blank, a rel other than noopener noreferrer and any class are dropped (the
 *   attribute takes its default), as the editor renders none of them.
 * Returns `document` itself when nothing changes and never mutates it; anything malformed is left to validation.
 */
export function repairLegacyDocument(document: JSONContent): JSONContent {
  return repair(document, false, { level: undefined })
}

/** The level of the last top-level heading seen so far, in document order (a disclosure's heading goes one below it). */
interface Walk { level: number | undefined }

function repair(document: JSONContent, requirement: boolean, walk: Walk): JSONContent {
  if (!document || typeof document !== 'object' || Array.isArray(document)) return document
  let copy: JSONContent | undefined
  if (Array.isArray(document.marks)) {
    const marks = document.marks.filter((mark, index, all) => !all.some((other, later) => later > index && other?.type === mark?.type)).map(linkMark)
    if (marks.length !== document.marks.length || marks.some((mark, index) => mark !== document.marks![index])) copy = { ...document, marks }
  }
  if (Array.isArray(document.content)) {
    const inside = requirement || document.type === 'requirement'
    let content = blocks(document.content, inside, walk, document.type === 'doc').flatMap(child => {
      const contacts = child?.type === 'contactCard' ? child.attrs?.data?.contacts : undefined
      return Array.isArray(contacts) && contacts.length > 1
        ? contacts.map(contact => ({ ...child, attrs: { ...child.attrs, data: { ...child.attrs!.data, contacts: [contact] } } }))
        : [child]
    })
    if (inside) {
      // The empty paragraph a list item got because it started with the day's list.
      if (startsWithParagraph.has(document.type ?? '') && emptyParagraph(content[0]) && historyLine(content[1])) content = content.slice(1)
      content = content.flatMap(child => historyLine(child) ?? [child])
    }
    if (content.length !== document.content.length || content.some((child, index) => child !== document.content![index])) copy = { ...(copy ?? document), content }
  }
  if (document.type === 'requirementSummary') copy = { ...(copy ?? document), type: 'overview', attrs: { ...(copy ?? document).attrs, kind: 'requirements' } }
  if (document.type === 'bulletList' && stamped((copy ?? document).content?.[0])) {
    const list = copy ?? document
    copy = { ...list, type: 'timedList', content: list.content!.map(item => item?.type === 'listItem' ? { ...item, type: 'timedItem' } : item) }
  }
  const current = copy ?? document
  if (startsWithParagraph.has(document.type ?? '') && (current.content == null || Array.isArray(current.content)) && current.content?.[0]?.type !== 'paragraph') {
    copy = { ...current, content: [{ type: 'paragraph' }, ...(current.content ?? [])] }
  }
  return copy ?? document
}

/** Repaired children in document order, each disclosure among them a heading and its body (its nested disclosures too). `top`: the
 * children are top-level blocks, whose headings set the level. */
function blocks(children: JSONContent[], requirement: boolean, walk: Walk, top: boolean): JSONContent[] {
  return children.flatMap(child => {
    if (child?.type !== 'disclosure' || !Array.isArray(child.content)) {
      if (top && child?.type === 'heading') walk.level = Number.isInteger(child.attrs?.level) ? child.attrs!.level : 1
      return [repair(child, requirement, walk)]
    }
    const before = walk.level
    const level = Math.min(6, Math.max(2, before === undefined ? 3 : before + 1))
    const title = child.content.find(node => node?.type === 'disclosureTitle' && node.content?.length)?.content
      ?? (typeof child.attrs?.title === 'string' && child.attrs.title ? [{ type: 'text', text: child.attrs.title }] : [{ type: 'text', text: 'Podrobnosti' }])
    walk.level = level
    const body = blocks(child.content.filter(node => node?.type !== 'disclosureTitle'), requirement, walk, top)
    walk.level = before
    return [{ type: 'heading', attrs: { level }, content: title }, ...body]
  })
}

/**
 * The blocks a history line read as nested ordered lists stands for: `list` is an orderedList starting at a day (1–31) with one item that
 * holds, after its empty leading paragraph, an orderedList starting at a month (1–12) with one item whose first paragraph starts with a
 * four-digit year. That paragraph gets "D. M. " in front (a text of its own before a marked first text); the blocks after it in both items follow.
 */
function historyLine(list: JSONContent | undefined): JSONContent[] | undefined {
  const day = dateItem(list, 31)
  const [months, ...after] = day ? itemBlocks(day) : []
  const month = dateItem(months, 12)
  const [first, ...rest] = month ? itemBlocks(month) : []
  const text = first?.type === 'paragraph' ? first.content?.[0] : undefined
  if (text?.type !== 'text' || !/^\d{4}(?!\d)/.test(text.text ?? '')) return undefined
  const date = `${start(list)}. ${start(months)}. `
  const inlines = text.marks?.length ? [{ type: 'text', text: date }, ...first!.content!] : [{ ...text, text: date + text.text }, ...first!.content!.slice(1)]
  return [{ ...first, content: inlines }, ...rest, ...after]
}

const start = (list: JSONContent | undefined): number => Number.isInteger(list?.attrs?.start) ? list!.attrs!.start : 1

/** The single listItem of an orderedList whose start is 1 to `max`. */
function dateItem(list: JSONContent | undefined, max: number): JSONContent | undefined {
  const day = start(list)
  return list?.type === 'orderedList' && day >= 1 && day <= max && list.content?.length === 1 && list.content[0]?.type === 'listItem' ? list.content[0] : undefined
}

/** An item's blocks without its empty leading paragraph. */
const itemBlocks = (item: JSONContent): JSONContent[] => (item.content ?? []).slice(emptyParagraph(item.content?.[0]) ? 1 : 0)

const emptyParagraph = (node: JSONContent | undefined): boolean => node?.type === 'paragraph' && !node.content?.length

/** A list item whose first paragraph's text starts with a stamp (its text nodes, as the paragraph's textContent). */
function stamped(item: JSONContent | undefined): boolean {
  const first = item?.type === 'listItem' && Array.isArray(item.content) ? item.content[0] : undefined
  const text = first?.type === 'paragraph' && Array.isArray(first.content) ? first.content.map(node => node?.type === 'text' && typeof node.text === 'string' ? node.text : '').join('') : ''
  return !!timedStamp(text)
}

/** A link mark without the attributes the editor no longer stores: a target but _blank, a rel but its own, a class; any other mark as it is. */
function linkMark(mark: NonNullable<JSONContent['marks']>[number]): NonNullable<JSONContent['marks']>[number] {
  const attrs = mark?.type === 'link' ? mark.attrs : undefined
  if (!attrs) return mark
  const dropped = [attrs.target != null && attrs.target !== '_blank' && 'target', attrs.rel != null && attrs.rel !== LINK_REL && 'rel', attrs.class != null && 'class']
    .filter((name): name is string => !!name)
  return dropped.length ? { ...mark, attrs: Object.fromEntries(Object.entries(attrs).filter(([name]) => !dropped.includes(name))) } : mark
}

/** {@link repairLegacyDocument} on the document of a rich envelope; any other value is returned as it is. */
export function repairLegacyValue<T>(value: T): T {
  const envelope = value as { document?: JSONContent } | null
  if (!envelope || typeof envelope !== 'object' || !envelope.document) return value
  const document = repairLegacyDocument(envelope.document)
  return document === envelope.document ? value : { ...envelope, document } as T
}
