import { Node } from '@tiptap/core'
import type { Node as ProseMirrorNode } from '@tiptap/pm/model'
import { youtrackStrings } from './strings'
import { createMarkdownChip, defineMarkdownAttributeValidator, markdownChipSpec, registerMarkdownChipText, type MarkdownChipOptions, type MarkdownValueConstraint } from '../../core'
import { normalizeYouTrackIssue, validateIssueText, validateIssueUrl, type YouTrackIssue } from './types'

// Back to text, an issue is its key („PX2-1235“), as typed in a requirement's title.
registerMarkdownChipText('youtrackIssue', node => String(node.attrs.key ?? ''))

const issueText: MarkdownValueConstraint = { kind: 'text', nonBlank: true, singleLine: true, maxLength: 2000 }
const text = defineMarkdownAttributeValidator(issueText, validateIssueText)

/** The issue as the shared inline chip (a tag): the key in monospace, the title, the state muted after it. */
export const youTrackIssueChip = (issue: YouTrackIssue, editable: boolean): MarkdownChipOptions => ({
  tag: 'a', href: issue.url, className: 'md-youtrack-issue', editable,
  label: [{ text: `${issue.key}: `, className: 'md-youtrack-issue-key' }, { text: issue.title, className: 'md-youtrack-issue-title' }],
  ...(issue.state ? { detail: [{ text: ` · ${issue.state}`, className: 'md-youtrack-issue-state' }] } : {}),
  title: issue.state ? youtrackStrings.savedStateTitle(issue.title, issue.state) : issue.title,
})

interface MarkdownSerializerState {
  write(value: string): void
  text(value: string): void
}

export const YouTrackIssueNode = Node.create({
  name: 'youtrackIssue',
  group: 'inline',
  inline: true,
  atom: true,
  selectable: true,
  addAttributes() {
    return {
      id: { default: null, validate: text, rendered: false, parseHTML: () => null },
      key: { default: null, validate: text, rendered: false, parseHTML: () => null },
      title: { default: null, validate: text, rendered: false, parseHTML: () => null },
      url: { default: null, validate: defineMarkdownAttributeValidator({ kind: 'url', policy: 'absolute-http', maxLength: 2000 }, validateIssueUrl), rendered: false, parseHTML: () => null },
      state: { default: null, validate: defineMarkdownAttributeValidator({ ...issueText, nullable: true }, value => { if (value != null) validateIssueText(value) }), rendered: false, parseHTML: () => null },
    }
  },
  parseHTML() {
    return [{
      // Before the link mark's a[href] (priority 50): a copied issue chip pastes back as the issue, not as a link.
      tag: 'a[data-youtrack-issue]', priority: 60,
      getAttrs(element) {
        try {
          return normalizeYouTrackIssue({
            id: element.getAttribute('data-youtrack-issue'),
            key: element.getAttribute('data-youtrack-key'),
            title: element.getAttribute('data-youtrack-title'),
            url: element.getAttribute('href'),
            state: element.getAttribute('data-youtrack-state'),
          })
        } catch { return false }
      },
    }]
  },
  renderHTML({ node }) {
    const issue = normalizeYouTrackIssue(node.attrs)
    return markdownChipSpec(youTrackIssueChip(issue, false), {
      'data-youtrack-issue': issue.id,
      'data-youtrack-key': issue.key,
      'data-youtrack-title': issue.title,
      ...(issue.state ? { 'data-youtrack-state': issue.state } : {}),
      target: '_blank',
      rel: 'noopener noreferrer',
    })
  },
  // In an editor the same chip, whose tooltip names the chip keys while it can be edited.
  addNodeView() {
    return ({ node, view }) => {
      const issue = normalizeYouTrackIssue(node.attrs), { element } = createMarkdownChip(view.dom.ownerDocument, youTrackIssueChip(issue, view.editable))
      Object.assign(element.dataset, { youtrackIssue: issue.id, youtrackKey: issue.key, youtrackTitle: issue.title, ...(issue.state ? { youtrackState: issue.state } : {}) })
      element.setAttribute('target', '_blank'); element.setAttribute('rel', 'noopener noreferrer')
      return { dom: element }
    }
  },
  renderText({ node }) {
    const issue = normalizeYouTrackIssue(node.attrs)
    return `${issue.key}: ${issue.title}${issue.state ? ` · ${issue.state}` : ''} (${issue.url})`
  },
  addStorage() {
    return {
      markdown: {
        serialize(state: MarkdownSerializerState, node: ProseMirrorNode) {
          const issue = normalizeYouTrackIssue(node.attrs)
          state.write('[')
          state.text(`${issue.key}: ${issue.title}${issue.state ? ` · ${issue.state}` : ''}`)
          state.write(`](${issue.url.replace(/[\\()]/g, '\\$&')})`)
        },
      },
    }
  },
})
