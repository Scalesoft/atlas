import { mergeAttributes, Node, posToDOMRect, type Editor } from '@tiptap/core'
import { Fragment, Slice, type Node as ProseMirrorNode } from '@tiptap/pm/model'
import { Plugin, PluginKey, Selection, TextSelection, type Transaction } from '@tiptap/pm/state'
import { Decoration, DecorationSet } from '@tiptap/pm/view'
import { openMarkdownModuleForm } from '../modules/forms'
import { dispatchMarkdownHistoryOperation } from '../../engine/historyBoundary'
import { blocksStrings } from '../../ui/strings/blocks'

declare module '@tiptap/core' {
  interface Commands<ReturnType> {
    timedList: {
      /** Toggles a timed list as toggleBulletList does, a bullet, numbered or task list around the selection retyped whole; every item it leaves without a stamp gets today's. */
      toggleTimedList: () => ReturnType
    }
  }
}

// „3. 11. 2026 17:19 — text“ or without the time; lenient: the rest may be empty („3. 11. 2026 17:19 —“, as Markdown trims a fresh stamp).
const STAMP = /^(\d{1,2}\.\s*\d{1,2}\.\s*\d{4})(?:(\s+)(\d{1,2}:\d{2}))?\s+[—–-](?:\s+|$)/

/** Where a timed item's stamp ends: `dateEnd` after the year, `timeEnd` after the time (none without it), `textStart` after the dash. */
export function timedStamp(text: string) {
  const match = STAMP.exec(text)
  if (!match) return null
  const dateEnd = match[1]!.length
  return { dateEnd, timeEnd: match[3] ? dateEnd + match[2]!.length + match[3].length : undefined, textStart: match[0].length }
}

// One formatter: building it loads the time zone data.
let pragueClock: Intl.DateTimeFormat | undefined
/** Now in Europe/Prague (as the server's dates): the day and the time. */
export function pragueNow() {
  pragueClock ??= new Intl.DateTimeFormat('en', { timeZone: 'Europe/Prague', year: 'numeric', month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit', hourCycle: 'h23' })
  // Date.now() explicitly: a faked clock (tests) is not the engine's own.
  const parts = Object.fromEntries(pragueClock.formatToParts(Date.now()).map(part => [part.type, part.value]))
  return { date: `${parts.year}-${parts.month}-${parts.day}`, time: `${parts.hour}:${parts.minute}` }
}

/** A stamp: „1. 11. 2026 23:40“, or the day alone. */
export function historyStampText({ date, time }: { date: string; time?: string } = pragueNow()) {
  const [year, month, day] = date.split('-').map(Number)
  return `${day}. ${month}. ${year}${time ? ` ${time}` : ''}`
}

/** What a new timed item starts with: now and the dash, the caret after it. */
const freshStamp = () => `${historyStampText()} — `

/** The timed item whose first textblock holds the caret at `$pos`, else null. */
function headOf(state: { doc: ProseMirrorNode }, pos: number) {
  const $pos = state.doc.resolve(pos)
  return $pos.depth >= 2 && $pos.node(-1).type.name === 'timedItem' && $pos.index(-1) === 0 ? $pos : null
}

/** Today's stamp at the start of every timed item whose first line the selection touches and that has none. */
function stampItems(tr: Transaction) {
  const { from, to } = tr.selection, found: number[] = []
  tr.doc.nodesBetween(from, to, (node, pos) => {
    const head = node.type.name === 'timedItem' ? node.firstChild : null
    // Not an outer item the caret is nested in: only its first line counts.
    if (head?.isTextblock && pos + 1 <= to && pos + 1 + head.nodeSize >= from && !timedStamp(head.textContent)) found.push(pos + 2)
  })
  const stamp = tr.doc.type.schema.text(freshStamp())
  for (const at of found.reverse()) tr.insert(at, stamp)
  return true
}

// A date the item already starts with, without the dash: „3. 11. 2026 schůzka“ keeps its day and gets the dash.
const DATED = /^\d{1,2}\.\s*\d{1,2}\.\s*\d{4}(?:\s+\d{1,2}:\d{2})?(?=\s|$)/

/** The innermost list around the selection's start and its depth. */
function listAround({ $from }: Selection) {
  for (let depth = $from.depth; depth > 0; depth--) if ($from.node(depth).type.isInGroup('list')) return { list: $from.node(depth), depth }
  return null
}

/** Every item of the timed list around the selection stamped: one that starts with a date keeps it (the dash added), the others get now. */
function stampList(tr: Transaction) {
  const around = listAround(tr.selection)
  if (around?.list.type.name !== 'timedList') return true
  const start = tr.selection.$from.start(around.depth), found: [number, string][] = [], now = freshStamp()
  around.list.forEach((entry, offset) => {
    const head = entry.firstChild
    if (!head?.isTextblock || timedStamp(head.textContent)) return
    const dated = head.firstChild?.isText ? DATED.exec(head.firstChild.text!) : null
    found.push(dated ? [start + offset + 2 + dated[0].length, dated[0].length === head.content.size ? ' — ' : ' —'] : [start + offset + 2, now])
  })
  for (const [at, text] of found.reverse()) tr.insert(at, tr.doc.type.schema.text(text))
  return true
}

const LISTS = ['bulletList', 'orderedList', 'taskList']

/**
 * An item leaving the timed list by Ctrl+Shift+D or Backspace at its start leaves its stamp behind, never as plain text; retyping the
 * list or „Změnit na“ keeps each date as leading text (no data lost). Deletes the stamp of every timed item in `from`–`to`; with `list`, only of that list's own items.
 */
export function dropStamps(tr: Transaction, from: number, to: number, list?: ProseMirrorNode) {
  const found: [number, number][] = []
  tr.doc.nodesBetween(from, to, (node, pos, parent) => {
    const first = node.type.name === 'timedItem' && (!list || parent === list) ? node.firstChild?.firstChild : null
    const stamp = first?.isText ? timedStamp(first.text!) : null
    if (stamp) found.push([pos + 2, pos + 2 + stamp.textStart])
  })
  for (const [start, end] of found.reverse()) tr.delete(start, end)
}

/**
 * The innermost list around the selection retyped in place when it is one of the `from` lists: bullet, numbered or task ↔ timed, whose
 * items hold the same content (tiptap's toggleList cannot change a list's item type). Positions hold, so the selection stays; a timed
 * list's stamps stay as the items' leading text (turning it back recognises them). False when the list is another one.
 */
export function retypeList(tr: Transaction, from: readonly string[], to: string, item: string) {
  const around = listAround(tr.selection)
  if (!around || !from.includes(around.list.type.name)) return false
  const { $from, from: anchor, to: head } = tr.selection, schema = tr.doc.type.schema, text = tr.selection instanceof TextSelection
  const pos = $from.before(around.depth), { list } = around
  tr.replaceWith(pos, pos + list.nodeSize, schema.nodes[to]!.create(list.attrs, list.children.map(entry => schema.nodes[item]!.create(null, entry.content))))
  if (text) tr.setSelection(TextSelection.create(tr.doc, anchor, head))
  return true
}

/**
 * Enter in a timed item: the next item, the rest of the line after its stamp, the caret after the stamp. An item holding only its
 * stamp leaves the list as an empty bullet does; an empty one is the editor's own Enter (it lifts). One transaction, one undo step.
 */
function enter(editor: Editor) {
  const { selection } = editor.state, $head = selection.empty ? headOf(editor.state, selection.from) : null
  if (!$head || !$head.parent.content.size) return false
  const inside = stampAt(editor.state, selection.from)
  if (inside && selection.from > inside.start && selection.from < inside.end) return editStamp(editor, selection.from)
  const text = $head.parent.textContent, stamp = timedStamp(text)
  if (stamp && !text.slice(stamp.textStart).trim() && $head.node(-1).childCount === 1) {
    return editor.chain().command(({ tr }) => { tr.delete($head.start(), $head.end()); return true }).liftListItem('timedItem').run()
  }
  if (!editor.can().splitListItem('timedItem')) return false
  return editor.chain().splitListItem('timedItem').command(({ tr }) => stampItems(tr)).run()
}

/** The stamp of the timed item whose first line holds `pos`: where the line starts and where its stamp text ends (after the dash), else null. */
function stampAt(state: { doc: ProseMirrorNode }, pos: number) {
  const $head = headOf(state, pos), first = $head?.parent.firstChild
  const stamp = first?.isText ? timedStamp(first.text!) : null
  return $head && stamp ? { start: $head.start(), end: $head.start() + stamp.textStart, value: $head.start() + (stamp.timeEnd ?? stamp.dateEnd), lineEnd: $head.end() } : null
}

/** „3. 11. 2026“ or „3. 11. 2026 17:19“ written as a stamp; throws for anything else (the form keeps the old value and shows why). */
function parseStampValue(value: string) {
  const match = /^\s*(\d{1,2})\.\s*(\d{1,2})\.\s*(\d{4})(?:\s+(\d{1,2}):(\d{2}))?\s*$/.exec(value)
  const [day, month, year, hour, minute] = (match?.slice(1) ?? []).map(Number) as number[]
  const date = new Date(year!, month! - 1, day)
  if (!match || date.getMonth() !== month! - 1 || date.getDate() !== day || (match[4] && (hour! > 23 || minute! > 59))) throw new TypeError(blocksStrings.timedStampInvalid)
  return historyStampText({ date: `${year}-${month}-${day}`, time: match[4] ? `${String(hour).padStart(2, '0')}:${match[5]}` : undefined })
}

/**
 * The stamp's own edit (click, F2, Enter inside it): a field at the stamp; an invalid value keeps the old one. False without a stamp.
 * Its anchor is the editor, standing at the stamp's place: the chip is a decoration ProseMirror redraws (the popover's aria attributes
 * on it alone made it redraw), and a popover whose anchor left the page closes.
 */
function editStamp(editor: Editor, pos: number) {
  const found = stampAt(editor.state, pos), { view } = editor
  if (!found || !editor.isEditable) return false
  openMarkdownModuleForm({ editor, anchor: view.dom, positionAnchor: { contextElement: view.dom, getBoundingClientRect: () => posToDOMRect(view, found.start, found.value) },
    title: blocksStrings.timedStampEdit, values: { stamp: editor.state.doc.textBetween(found.start, found.value) },
    fields: [{ key: 'stamp', label: blocksStrings.timedStampLabel }],
    save({ stamp = '' }) {
      const text = parseStampValue(stamp)
      // shortcut: the line is found again by its start; a concurrent edit that moved it makes the save a no-op, track the position if that bites.
      const now = stampAt(editor.state, found.start)
      if (now?.start === found.start) dispatchMarkdownHistoryOperation(editor, editor.state.tr.insertText(text, now.start, now.value))
    } })
  return true
}

/**
 * Backspace right after a stamp with nothing after it removes the stamp; the next Backspace removes the item: an empty item after another
 * one goes, the caret at the end of the item before (lifting it would leave a blank row and split the list); a first one lifts, as a bullet.
 * Backspace inside a stamp or right after it removes the whole stamp, never a character that would leave it plain text.
 */
function backspace(editor: Editor) {
  const { selection } = editor.state, $head = selection.empty ? headOf(editor.state, selection.from) : null
  const found = $head && stampAt(editor.state, selection.from)
  if (found && selection.from > found.start && selection.from <= found.end && found.end < found.lineEnd) return editor.commands.deleteRange({ from: found.start, to: found.end })
  // J4.3: at the start of a top-level item's text it becomes a paragraph, its stamp dropped; it never merges into the list above.
  if ($head && !$head.parentOffset && $head.parent.content.size && $head.node(-3)?.type.name !== 'timedItem') {
    return editor.chain().command(({ tr }) => { if (found) tr.delete(found.start, found.end); return true }).liftListItem('timedItem').run()
  }
  if (!$head || $head.parentOffset !== $head.parent.content.size) return false
  if (!$head.parent.content.size) {
    if ($head.index(-2) === 0 || $head.node(-1).childCount !== 1) return false
    const item = $head.before(-1), size = $head.node(-1).nodeSize
    return editor.chain().command(({ tr }) => {
      tr.delete(item, item + size)
      tr.setSelection(Selection.near(tr.doc.resolve(item), -1))
      return true
    }).run()
  }
  const text = $head.parent.textContent, stamp = timedStamp(text)
  if (!stamp || text.slice(stamp.textStart).trim()) return false
  return editor.commands.deleteRange({ from: $head.start(), to: $head.end() })
}

/** The day and time chips of the timed items `nodesBetween` meets in `from`–`to`, and the ranges of their first lines to redraw. */
function stampDecorations(doc: ProseMirrorNode, from: number, to: number) {
  const found: Decoration[] = [], heads: [number, number][] = []
  doc.nodesBetween(from, to, (node, pos) => {
    if (node.type.name !== 'timedItem') return !node.isTextblock
    const head = node.firstChild, start = pos + 2
    if (!head?.isTextblock) return true
    heads.push([start, start + head.content.size])
    const first = head.firstChild, stamp = first?.isText ? timedStamp(first.text!) : null
    if (stamp) found.push(Decoration.inline(start, start + stamp.dateEnd, { class: 'tt-timed-date' }),
      ...stamp.timeEnd ? [Decoration.inline(start + stamp.dateEnd, start + stamp.timeEnd, { class: 'tt-timed-time' })] : [])
    return true
  })
  return { found, heads }
}

/** A change maps the chips along and draws again only the items it touched, not the whole document at every keystroke. */
function redraw(tr: Transaction, set: DecorationSet) {
  let next = set.map(tr.mapping, tr.doc)
  tr.mapping.maps.forEach((map, index) => map.forEach((_oldFrom, _oldTo, start, end) => {
    const after = tr.mapping.slice(index + 1), size = tr.doc.content.size
    const { found, heads } = stampDecorations(tr.doc, Math.max(0, after.map(start, -1) - 1), Math.min(size, after.map(end, 1) + 1))
    for (const [from, to] of heads) next = next.remove(next.find(from, to))
    next = next.add(tr.doc, found)
  }))
  return next
}

const stampsKey = new PluginKey<DecorationSet>('timedStamps')

/**
 * A static rendering's (staticRenderer.ts, a read page) stamps marked as the editor's decorations mark them: the first text of every
 * timed item. `text` is a Text node starting a line; returns whether it was a stamp.
 */
export function markTimedStamp(text: Text) {
  const stamp = timedStamp(text.data), doc = text.ownerDocument
  if (!stamp) return false
  const wrap = (part: Text, end: number, className: string) => {
    const rest = part.splitText(end), span = doc.createElement('span')
    span.className = className
    part.replaceWith(span)
    span.append(part)
    return rest
  }
  const rest = wrap(text, stamp.dateEnd, 'tt-timed-date')
  if (stamp.timeEnd) wrap(rest, stamp.timeEnd - stamp.dateEnd, 'tt-timed-time')
  return true
}

export function decorateStaticTimedLists(root: ParentNode) {
  root.querySelectorAll('li.tt-timed-item').forEach(item => {
    const text = (item.firstElementChild ?? item).firstChild
    if (text instanceof Text) markTimedStamp(text)
  })
}

/**
 * A timed list („Časová odrážka“): a bullet list whose items start with the day and time they were written, „3. 11. 2026 17:19 — text“.
 * The stamp is the item's own text (its Markdown is a bullet list's, which the requirement history reads); the chip over it is a
 * decoration. A new item is stamped at once (Enter, the command), an empty one on its first character unless that is a digit (typing
 * one's own date). Rich profile only; markdown-safe reads the same Markdown as a bullet list.
 */
export const TimedList = Node.create({
  name: 'timedList',
  group: 'block list',
  content: 'timedItem+',
  addAttributes() {
    // As tiptap-markdown's tight lists: a loose list (a blank line between items) keeps its Markdown.
    return { tight: { default: true, parseHTML: element => element.getAttribute('data-tight') === 'true' || !element.querySelector('p'),
      renderHTML: attributes => ({ 'data-tight': attributes.tight ? 'true' : null }) } }
  },
  parseHTML() {
    return [{ tag: 'ul[data-type="timedList"]', priority: 51 }]
  },
  renderHTML({ HTMLAttributes }) {
    return ['ul', mergeAttributes(HTMLAttributes, { 'data-type': 'timedList', class: 'tt-timed-list' }), 0]
  },
  addStorage() {
    return {
      markdown: {
        serialize(state: { renderList(node: ProseMirrorNode, delim: string, firstDelim: () => string): void }, node: ProseMirrorNode) {
          state.renderList(node, '  ', () => '- ')
        },
        parse: {
          // As tiptap-markdown's task list: a bullet list whose first item starts with a stamp is a timed list.
          updateDOM(element: HTMLElement) {
            element.querySelectorAll('ul:not([data-type]):not(.contains-task-list)').forEach(list => {
              const first = list.querySelector(':scope > li')
              if (first && STAMP.test((first.textContent ?? '').trimStart())) list.setAttribute('data-type', 'timedList')
            })
          },
        },
      },
    }
  },
  addCommands() {
    return {
      toggleTimedList: () => ({ commands, tr }) => {
        // Inside a timed list it is toggled off: nothing to stamp; the items it lifts out of a top-level list (not into an outer timed item) drop theirs.
        const around = listAround(tr.selection), off = around?.list.type.name === this.name
        if (off && tr.selection.$from.node(around.depth - 1).type.name !== 'timedItem') dropStamps(tr, tr.selection.from, tr.selection.to, around.list)
        return (retypeList(tr, LISTS, this.name, 'timedItem') || commands.toggleList(this.name, 'timedItem')) && (off || stampList(tr))
      },
    }
  },
  addKeyboardShortcuts() {
    // Ctrl+Shift+D(atum): the list keys are Ctrl+Shift+7/8/9, the other digits headings (core/keyboardShortcuts.ts).
    return { 'Mod-Shift-d': () => this.editor.commands.toggleTimedList() }
  },
  addProseMirrorPlugins() {
    const { editor } = this
    return [new Plugin<DecorationSet>({
      key: stampsKey,
      state: {
        init: (_, state) => DecorationSet.create(state.doc, stampDecorations(state.doc, 0, state.doc.content.size).found),
        apply: (tr, set) => tr.docChanged ? redraw(tr, set) : set,
      },
      props: {
        decorations: state => stampsKey.getState(state),
        // Plain lines pasted into an item are items, as in a bullet list of notes (the first joins the caret's line); a block or a nested list stays as pasted.
        transformPasted(slice, view) {
          const lines = slice.content
          if (lines.childCount < 2 || slice.openStart > 1 || slice.openEnd > 1 || view.state.selection.$from.node(-1)?.type.name !== 'timedItem') return slice
          let plain = true
          lines.forEach(line => { if (line.type.name !== 'paragraph') plain = false })
          const item = view.state.schema.nodes.timedItem
          return plain && item ? new Slice(Fragment.from(lines.content.map(line => item.create(null, line))), 2, 2) : slice
        },
        // The first character typed in an empty item stamps it, in the same step; a digit starts the user's own date.
        // Text typed with the caret inside a stamp (or before it) goes right after it: typing never turns the stamp into plain text; its
        // edit is the field. A date written over a selected stamp still replaces it.
        handleTextInput(view, from, to, text) {
          const stamp = from === to ? stampAt(view.state, from) : null
          if (stamp && from < stamp.end) {
            view.dispatch(view.state.tr.insertText(text, stamp.end))
            return true
          }
          const $head = from === to ? headOf(view.state, from) : null
          if (!$head || $head.parent.content.size || /^\d/.test(text)) return false
          view.dispatch(view.state.tr.insertText(freshStamp() + text, from, to))
          return true
        },
        handleClick(view, pos, event) {
          const chip = (event.target as Element | null)?.closest?.('.tt-timed-date, .tt-timed-time')
          return !!chip && view.dom.contains(chip) && editStamp(editor, pos)
        },
      },
    })]
  },
})

export const TimedItem = Node.create({
  name: 'timedItem',
  content: 'paragraph block*',
  defining: true,
  parseHTML() {
    return [{ tag: 'li', context: 'timedList/', priority: 51 }]
  },
  renderHTML({ HTMLAttributes }) {
    return ['li', mergeAttributes(HTMLAttributes, { class: 'tt-timed-item' }), 0]
  },
  addStorage() {
    return { markdown: { serialize: (state: { renderContent(node: ProseMirrorNode): void }, node: ProseMirrorNode) => state.renderContent(node), parse: {} } }
  },
  addKeyboardShortcuts() {
    return {
      Enter: ({ editor }) => enter(editor),
      Backspace: ({ editor }) => backspace(editor),
      // Delete inside a stamp removes it whole, as Backspace after it does.
      Delete: ({ editor }) => {
        const { selection } = editor.state, stamp = selection.empty && stampAt(editor.state, selection.from)
        return !!stamp && selection.from < stamp.end && editor.commands.deleteRange({ from: stamp.start, to: stamp.end })
      },
      F2: ({ editor }) => editor.state.selection.empty && editStamp(editor, editor.state.selection.from),
      Tab: ({ editor }) => editor.commands.sinkListItem(this.name),
      'Shift-Tab': ({ editor }) => editor.commands.liftListItem(this.name),
      // The list keys retype a timed list as the toolbar does (core/commands.ts); outside one, false leaves them to tiptap's lists.
      ...Object.fromEntries(([[7, 'orderedList', 'listItem'], [8, 'bulletList', 'listItem'], [9, 'taskList', 'taskItem']] as const)
        .map(([digit, list, item]) => [`Mod-Shift-${digit}`, ({ editor }: { editor: Editor }) => editor.commands.command(({ tr }) => retypeList(tr, ['timedList'], list, item))])),
    }
  },
})
