import { Extension, posToDOMRect, type Command, type Editor, type JSONContent } from '@tiptap/core'
import { Plugin, PluginKey } from '@tiptap/pm/state'
import { defineMarkdownCommand, defineMarkdownEntityKind, ISSUE_KEY_PATTERN, markdownRecognitionAt, registerMarkdownOverlay, registerMarkdownRecognizer, type MarkdownPlugin, type MarkdownRecognitionEntry, type MarkdownRecognizer } from '../../core'
import { youtrackStrings } from './strings'
import { YouTrackIssueNode } from './issueNode'
import { pickYouTrackIssue, prefetchYouTrackPicker, type YouTrackPickerContext } from './picker'
import { normalizeYouTrackIssue, type YouTrackClient, type YouTrackIssue } from './types'

export interface YouTrackPluginOptions {
  readonly client: YouTrackClient
  /** Pure host URL policy. Smart paste requires this predicate and client.resolve. */
  readonly matchUrl?: (url: string) => boolean
  /** Optional host-specific explanation, rendered as plain text below the search field. */
  readonly pickerHelpText?: string
  /** Hosts may replace the default panel with their own UI. Resolve null to cancel. */
  readonly picker?: (context: YouTrackPickerContext) => Promise<YouTrackIssue | null>
}

function issueContent(issue: unknown): JSONContent {
  return { type: 'youtrackIssue', attrs: normalizeYouTrackIssue(issue) }
}

export const insertYouTrackIssue = defineMarkdownCommand<YouTrackIssue>({
  id: 'youtrack.insert-issue',
  apply: issue => ({ commands }) => {
    try { return commands.insertContent(issueContent(issue)) } catch { return false }
  },
  active: (editor, issue) => editor.isActive('youtrackIssue', { id: issue.id }),
})

interface Target { readonly id: symbol; readonly position: number }
type TargetChange = { readonly reserve: Target } | { readonly clear: symbol }
/** `replaces`: the text right after the reserved position that the picked issue takes the place of (Ctrl+Space over a key). */
interface Pending { readonly id: symbol; readonly editor: Editor; readonly abort: AbortController; readonly query?: string; readonly replaces?: string }
const issueKind = defineMarkdownEntityKind({ kind: 'issue', inline: 'youtrackIssue', recognizers: [{ id: 'issue.key', pattern: 'issue-key' }],
  patterns: { 'issue-key': ISSUE_KEY_PATTERN } })
/** A YouTrack issue key as written: „PX2-1235“ or „#PX2-1235“, a trailing punctuation mark aside. */
const issueKey = new RegExp(`^#?(${issueKind.patterns!['issue-key']})[.,;:!?)]?$`, 'u')

/** Reference plugin using the public core contract and injected host services only. */
export function createYouTrackPlugin(options: YouTrackPluginOptions): MarkdownPlugin {
  if (!options.client || typeof options.client.search !== 'function') throw new TypeError('YouTrack requires a search client.')
  const { client, picker = pickYouTrackIssue } = options
  return {
    id: 'youtrack',
    apiVersion: 1,
    entities: [issueKind],
    schemaVersion: 1,
    profiles: ['rich'],
    create({ signal }) {
      if (picker === pickYouTrackIssue) prefetchYouTrackPicker()
      const resolveUrl = client.resolve?.bind(client)
      const targetKey = new PluginKey<Target | null>('youtrackPendingTarget')
      let pending: Pending | undefined

      const clear = (operation: Pending, updateDocument = true) => {
        if (pending === operation) pending = undefined
        const { editor } = operation
        if (updateDocument && !editor.isDestroyed && targetKey.getState(editor.state)?.id === operation.id) {
          editor.view.dispatch(editor.state.tr.setMeta(targetKey, { clear: operation.id } satisfies TargetChange).setMeta('addToHistory', false))
        }
        operation.abort.abort()
      }
      const cancel = (updateDocument = true) => { if (pending) clear(pending, updateDocument) }
      const onAbort = () => cancel()
      signal.addEventListener('abort', onAbort, { once: true })

      const openPicker = async (operation: Pending) => {
        const { editor, abort, id } = operation
        if (signal.aborted || abort.signal.aborted || editor.isDestroyed || targetKey.getState(editor.state)?.id !== id) {
          clear(operation)
          return
        }
        // Lifecycle anchor outside content: clicking the document must dismiss the panel.
        // Geometry comes from the mapped caret below, never from this hidden marker.
        const anchor = editor.view.dom.ownerDocument.createElement('span')
        anchor.hidden = true
        editor.view.dom.parentElement!.append(anchor)
        const removeAnchor = () => anchor.remove()
        abort.signal.addEventListener('abort', removeAnchor, { once: true })
        try {
          const issue = await picker({
            client, signal: abort.signal, ownerDocument: editor.view.dom.ownerDocument,
            themeElement: editor.view.dom, initialQuery: operation.query,
            editor, anchor, helpText: options.pickerHelpText,
            positionAnchor: {
              contextElement: editor.view.dom,
              getBoundingClientRect: () => {
                const position = targetKey.getState(editor.state)?.position ?? editor.state.selection.from
                return posToDOMRect(editor.view, position, position)
              },
            },
            registerOverlay: overlay => registerMarkdownOverlay(editor, overlay, { modal: true }),
          })
          if (!issue || signal.aborted || abort.signal.aborted || editor.isDestroyed || !editor.isEditable) return
          const target = targetKey.getState(editor.state)
          if (target?.id !== id) return
          const content = issueContent(issue)
          clear(operation)
          const { replaces } = operation
          const end = replaces && editor.state.doc.textBetween(target.position, Math.min(target.position + replaces.length, editor.state.doc.content.size)) === replaces ? target.position + replaces.length : target.position
          editor.commands.insertContentAt({ from: target.position, to: end }, content, { updateSelection: false })
        } catch (error) {
          if (!abort.signal.aborted && !editor.isDestroyed) {
            editor.view.dom.dispatchEvent(new CustomEvent('md-youtrack-error', {
              bubbles: true,
              detail: { message: youtrackStrings.insertFailed, error },
            }))
          }
        } finally {
          abort.signal.removeEventListener('abort', removeAnchor)
          removeAnchor()
          clear(operation)
        }
      }

      /** `{ query }` opens the picker already searching for that text (slash parameter `/úkol 123`). */
      const searchAt = (args: { query?: string; at?: number; replaces?: string } | void): Command => ({ editor, tr, dispatch }) => {
        if (pending || signal.aborted || !tr.selection.$from.parent.isTextblock) return false
        if (dispatch) {
          const operation = { id: Symbol('youtrackPicker'), editor, abort: new AbortController(), query: args?.query, replaces: args?.replaces }
          pending = operation
          tr.setMeta(targetKey, { reserve: { id: operation.id, position: args?.at ?? tr.selection.from } } satisfies TargetChange)
          // A slash command may already have deleted its query in this transaction.
          queueMicrotask(() => { void openPicker(operation) })
        }
        return true
      }
      const search = defineMarkdownCommand<{ query?: string } | void>({ id: 'youtrack.search', apply: args => searchAt(args) })

      // Keys typed in text (core/recognition: a key of a project of the place's lexicon): asked of the host once, then known or unknown.
      const known = new Map<string, JSONContent | null>()
      const verify = async (key: string, signal: AbortSignal) => {
        if (known.has(key)) return known.get(key)!
        const found = client.resolve ? await client.resolve(key, { signal }) : (await client.search(key, { signal })).find(issue => issue.key === key) ?? null
        let content: JSONContent | null = null
        try { content = found ? issueContent(found) : null } catch { /* an invalid answer is no issue */ }
        if (!signal.aborted) known.set(key, content)
        return content
      }
      const recognizer: MarkdownRecognizer = {
        kind: 'issue',
        // The keys confirmed in this editor: typed again they are chips at once, written they are underlined.
        entries: () => [...known].flatMap(([key, content]): MarkdownRecognitionEntry[] => content ? [{ kind: 'issue', id: key, name: key, form: 'full', auto: true, cased: true, target: content }] : []),
        chip: (hit, { signal }) => hit.decision === 'pending' ? verify(hit.text.replace(/^#/u, ''), signal) : (hit.candidates[0] as JSONContent | undefined) ?? null,
        label: hit => youtrackStrings.issueFromText(hit.text.replace(/^#/u, '')),
        icon: 'svg:ticket',
      }
      const anchors = Extension.create({
        name: 'youtrackPendingTarget',
        onCreate() {
          const editor = this.editor, stop = registerMarkdownRecognizer(editor, recognizer)
          editor.on('destroy', stop)
        },
        addProseMirrorPlugins() {
          return [new Plugin<Target | null>({
            key: targetKey,
            state: {
              init: () => null,
              apply(transaction, previous) {
                const change = transaction.getMeta(targetKey) as TargetChange | undefined
                if (change && 'reserve' in change) return change.reserve
                if (change && 'clear' in change && previous?.id === change.clear) return null
                if (!previous) return null
                const mapped = transaction.mapping.mapResult(previous.position, 1)
                return mapped.deleted ? null : { id: previous.id, position: mapped.pos }
              },
            },
            view: () => ({
              update(view) {
                if (pending && (targetKey.getState(view.state)?.id !== pending.id || !pending.editor.isEditable)) cancel()
              },
              destroy: () => cancel(false),
            }),
          })]
        },
      })
      const action = {
        id: 'youtrack.search',
        label: youtrackStrings.pickerHeading,
        description: youtrackStrings.actionDescription,
        icon: 'svg:ticket',
        group: 'integrations',
        priority: 100,
        keywords: ['youtrack', 'issue', 'ukol', 'ticket', 'bug'],
        command: search(),
        parameter: { label: youtrackStrings.pickerHeading, command: (text: string) => search({ query: text }) },
      }
      return {
        extensions: [YouTrackIssueNode, anchors],
        references: [{
          id: 'youtrack.issue', label: youtrackStrings.pickerHeading, nodeType: 'youtrackIssue',
          read(node) {
            if (node.type !== 'youtrackIssue') return null
            const issue = normalizeYouTrackIssue(node.attrs)
            return { url: issue.url, label: `${issue.key}: ${issue.title}${issue.state ? ` · ${issue.state}` : ''}` }
          },
          matchUrl: resolveUrl ? options.matchUrl : undefined,
          resolveUrl: resolveUrl ? async (url, context) => {
            const issue = await resolveUrl(url, context)
            return issue ? issueContent(issue) : null
          } : undefined,
        }],
        commands: [insertYouTrackIssue, search],
        actions: { slash: [action], toolbar: [action] },
        // Ctrl+Space on a written key („PX2-1235“) not confirmed yet: the picker searches it, and the issue picked takes the key's place.
        intentions: ({ editor, text, from }) => {
          const key = issueKey.exec(text.trim())?.[1]
          // A key the engine knows is offered by its own row (core recognition): one row per entity.
          if (!key || pending || markdownRecognitionAt(editor.state, editor.state.selection.from, 'issue')) return []
          const at = from + text.indexOf(text.trim()), written = text.trim().replace(/[.,;:!?)]$/u, '')
          return [{ id: 'youtrack.from-text', label: youtrackStrings.issueFromText(key), icon: 'svg:ticket', keywords: ['youtrack', 'ukol'],
            run: () => { editor.commands.command(searchAt({ query: key, at, replaces: written })) } }]
        },
        markdownLossy(document) {
          let lossy = false
          document.descendants(node => { if (node.type.name === 'youtrackIssue') lossy = true })
          return lossy
        },
        onCreate: () => () => { signal.removeEventListener('abort', onAbort); cancel() },
        onAttach: () => () => cancel(),
      }
    },
  }
}
