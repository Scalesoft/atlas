import type { Editor } from '@tiptap/core'
import { NodeSelection } from '@tiptap/pm/state'
import { createIconElement, dispatchMarkdownHistoryOperation, markdownStrings, trackMarkdownApplyForm, type MarkdownIconName } from '../../core'
import { concealedStrings } from './strings'
import { completeChoices, MODE_ICONS, NAME, reconcealChoices, seconds, settingsOf, type Choice, type ConcealedContentLabels } from './model'

// Loaded on demand when an author opens a block's panel, so readers never download it.
/** Anchored settings panel in the style of the other module panels: tiles, one draft, one undo step on Použít. */
export function settingsRenderer(editor: Editor, getPos: () => number | undefined, labels: ConcealedContentLabels) {
  return (container: HTMLElement, close: () => void) => {
      const doc = container.ownerDocument
      const strings = concealedStrings
      const at = getPos()
      const node = at === undefined ? null : editor.state.doc.nodeAt(at)
      if (!node || node.type.name !== NAME) { close(); return }
      const draft = { ...settingsOf(node.attrs) }
      const form = doc.createElement('form'); form.className = 'md-concealed-settings__form'
      // A changed draft applies on a click outside as on „Použít“ (D3).
      const changed = trackMarkdownApplyForm(form)
      const refreshers: Array<() => void> = []
      const refresh = () => refreshers.forEach(update => update())
      const section = (title: string, columns: number) => {
        const heading = doc.createElement('p'); heading.className = 'md-concealed-settings__heading'; heading.textContent = title
        const tiles = doc.createElement('div'); tiles.className = 'md-concealed-settings__tiles'; tiles.style.setProperty('--md-concealed-columns', String(columns))
        tiles.setAttribute('role', 'radiogroup'); tiles.setAttribute('aria-label', title)
        form.append(heading, tiles)
        return { heading, tiles }
      }
      const tile = (parent: HTMLElement, options: { label: string; hint: string; icon?: MarkdownIconName; data: Record<string, string>; selected: () => boolean; pick: () => void; disabled?: () => boolean }) => {
        const button = doc.createElement('button'); button.type = 'button'; button.className = 'md-concealed-settings__tile'
        button.setAttribute('role', 'radio'); Object.assign(button.dataset, options.data)
        if (options.icon) { const preview = doc.createElement('span'); preview.className = 'md-concealed-settings__icon'; preview.append(createIconElement(doc, options.icon, 18)); button.append(preview) }
        const label = doc.createElement('span'); label.className = 'md-concealed-settings__label'; label.textContent = options.label
        const hint = doc.createElement('span'); hint.className = 'md-concealed-settings__hint'; hint.textContent = options.hint
        button.append(label, hint)
        button.onclick = () => { if (!options.disabled?.()) { options.pick(); changed(); refresh() } }
        refreshers.push(() => { button.setAttribute('aria-checked', String(options.selected())); button.disabled = !!options.disabled?.() })
        parent.append(button)
      }
      const reveal = section(labels.revealMode, 2)
      tile(reveal.tiles, { label: labels.scratch, hint: strings.scratchHint, icon: MODE_ICONS.scratch, data: { reveal: 'scratch' }, selected: () => draft.reveal === 'scratch', pick: () => { draft.reveal = 'scratch' } })
      tile(reveal.tiles, { label: labels.button, hint: strings.buttonHint, icon: MODE_ICONS.button, data: { reveal: 'button' }, selected: () => draft.reveal === 'button', pick: () => { draft.reveal = 'button' } })
      // A stored value outside the presets (set through the API) stays visible and selectable as its own tile.
      const withStored = (choices: readonly Choice[], stored: number | null, custom: (value: number) => string) =>
        stored === null || choices.some(([value]) => value === stored) ? choices : [...choices, [stored, custom(stored), strings.customHint] as const]
      const reconceal = section(strings.reconcealHeading, 3)
      for (const [value, label, hint] of withStored(reconcealChoices(), draft.reconcealAfterMs, ms => strings.reconcealAfter(seconds(ms))))
        tile(reconceal.tiles, { label, hint, data: { reconceal: value === null ? 'never' : String(value) }, selected: () => draft.reconcealAfterMs === value, pick: () => { draft.reconcealAfterMs = value } })
      const complete = section(strings.completeHeading, 2)
      for (const [value, label, hint] of withStored(completeChoices(), draft.completeAtPercent, share => strings.completeAt(share)))
        tile(complete.tiles, { label, hint, data: { complete: value === null ? 'off' : String(value) }, selected: () => draft.completeAtPercent === value, pick: () => { draft.completeAtPercent = value }, disabled: () => draft.reveal !== 'scratch' })
      refreshers.push(() => { complete.heading.dataset.disabled = String(draft.reveal !== 'scratch') })
      const note = doc.createElement('p'); note.className = 'md-concealed-settings__note'; note.textContent = strings.settingsNote
      const footer = doc.createElement('div'); footer.className = 'md-concealed-settings__footer'
      const cancel = doc.createElement('button'); cancel.type = 'button'; cancel.textContent = markdownStrings.cancel; cancel.onclick = close
      const apply = doc.createElement('button'); apply.type = 'submit'; apply.className = 'md-concealed-settings__apply'; apply.textContent = markdownStrings.apply
      footer.append(cancel, apply); form.append(note, footer)
      refresh()
      form.onsubmit = event => {
        event.preventDefault()
        const position = getPos()
        const target = position === undefined ? null : editor.state.doc.nodeAt(position)
        if (position === undefined || !target || target.type.name !== NAME || !editor.isEditable) { close(); return }
        // The draft applies only here, as one undo step; Cancel or Escape discards it.
        const tr = editor.state.tr.setNodeMarkup(position, undefined, { ...target.attrs, ...settingsOf(draft) })
        tr.setSelection(NodeSelection.create(tr.doc, position))
        dispatchMarkdownHistoryOperation(editor, tr)
        close()
      }
      container.append(form)
  }
}
