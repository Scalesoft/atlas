import type { ImagePresentation } from './presentation'
import { trackMarkdownComposition } from '../modules/composition'
import { trackMarkdownApplyForm } from '../modules/forms'
import { markdownStrings } from '../../ui/strings'
import { imagesStrings } from '../../ui/strings/images'

export type ImagePanel = 'layout' | 'effects' | 'crop' | 'text' | 'replace'
export const imagePanelTitles: Record<ImagePanel, string> = {
  get layout() { return imagesStrings.layoutPanelTitle },
  get effects() { return imagesStrings.effectsPanelTitle },
  get crop() { return imagesStrings.cropPanelTitle },
  get text() { return imagesStrings.textPanelTitle },
  get replace() { return imagesStrings.replacePanelTitle },
}

/** Small, focused editing panels. Their draft stays outside the document until Apply. */
export function renderImagePanel(options: {
  container: HTMLElement; panel: ImagePanel; initial: ImagePresentation; src: string; alt: string
  preview: (draft: ImagePresentation) => void; close: () => void
  apply: (draft: ImagePresentation, src: string, alt: string, keepOpen?: boolean) => void
}) {
  const { container, panel, close } = options
  const doc = container.ownerDocument
  let draft = { ...options.initial }; let src = options.src; let alt = options.alt
  const form = doc.createElement('form'); form.className = 'md-image-panel-form'
  const composition = trackMarkdownComposition(form)
  const error = doc.createElement('p'); error.className = 'md-image-panel-error'; error.setAttribute('role', 'alert')
  const refreshers: (() => void)[] = []
  // A „Použít“ panel applies its changed draft on a click outside (D3); the layout's choices apply on the click itself.
  const changed = panel === 'layout' ? () => {} : trackMarkdownApplyForm(form)
  const refresh = () => { changed(); options.preview(draft); refreshers.forEach(refresh => refresh()) }
  const group = (label: string) => {
    const fieldset = doc.createElement('fieldset'); const legend = doc.createElement('legend'); legend.textContent = label
    fieldset.append(legend); form.append(fieldset); return fieldset
  }
  const choice = (parent: HTMLElement, label: string, action: () => void, active: () => boolean) => {
    const button = doc.createElement('button'); button.type = 'button'; button.className = 'md-image-choice'; button.textContent = label
    // Size and alignment apply on the click itself (the panel stays open for the next choice): no Zrušit/Použít for two clicks.
    button.onclick = () => { action(); refresh(); if (panel === 'layout') try { options.apply(draft, src, alt, true) } catch (cause) { error.textContent = cause instanceof Error ? cause.message : imagesStrings.checkImageSettings } }; parent.append(button)
    refreshers.push(() => button.setAttribute('aria-pressed', String(active())))
    return button
  }
  const range = (parent: HTMLElement, labelText: string, key: 'width' | 'brightness' | 'contrast' | 'saturation' | 'cropLeft' | 'cropRight' | 'cropTop' | 'cropBottom', max: number, min = 0) => {
    const label = doc.createElement('label'); label.className = 'md-image-range'
    const text = doc.createElement('span'); text.textContent = labelText
    const output = doc.createElement('output'); const input = doc.createElement('input'); input.type = 'range'; input.name = key; input.min = String(min); input.max = String(max); input.step = '1'
    input.setAttribute('aria-label', labelText)
    input.oninput = () => { draft = { ...draft, [key]: Number(input.value) }; refresh() }
    refreshers.push(() => {
      if (key.startsWith('crop')) {
        const opposite = { cropLeft: 'cropRight', cropRight: 'cropLeft', cropTop: 'cropBottom', cropBottom: 'cropTop' } as const
        input.max = String(Math.min(max, 94 - draft[opposite[key as keyof typeof opposite]]))
      }
      input.value = String(draft[key]); output.value = `${Math.round(draft[key])} %`
    })
    label.append(text, output, input); parent.append(label)
  }
  const textField = (labelText: string, value: string, action: (value: string) => void, multiline = false) => {
    const label = doc.createElement('label'); label.className = 'md-image-text-field'; label.append(labelText)
    const input = multiline ? doc.createElement('textarea') : doc.createElement('input')
    if (multiline) (input as HTMLTextAreaElement).rows = 2
    input.value = value; input.oninput = () => action(input.value); label.append(input); form.append(label)
    return input
  }
  if (panel === 'layout') {
    const align = group(imagesStrings.alignLabel); align.className = 'md-image-choice-group'
    for (const [value, label] of [['left', imagesStrings.alignLeft], ['center', imagesStrings.alignCenter], ['right', imagesStrings.alignRight]] as const) choice(align, label, () => { draft.align = value }, () => draft.align === value)
    const widths = group(imagesStrings.widthLabel); widths.className = 'md-image-choice-group'
    for (const width of [25, 50, 75, 100]) choice(widths, `${width} %`, () => { draft.width = width }, () => draft.width === width)
  } else if (panel === 'effects') {
    const filters = group(imagesStrings.filterLabel); filters.className = 'md-image-filters'
    for (const [filter, title] of [['none', imagesStrings.filterOriginal], ['grayscale', imagesStrings.filterGrayscale], ['sepia', imagesStrings.filterSepia]] as const) {
      const button = choice(filters, title, () => { draft.filter = filter }, () => draft.filter === filter)
      const thumbnail = doc.createElement('img'); thumbnail.src = options.src; thumbnail.alt = ''; thumbnail.style.filter = filter === 'none' ? '' : `${filter}(1)`; button.prepend(thumbnail)
    }
    const adjustments = group(imagesStrings.adjustmentsLabel); range(adjustments, imagesStrings.brightnessLabel, 'brightness', 200); range(adjustments, imagesStrings.contrastLabel, 'contrast', 200); range(adjustments, imagesStrings.saturationLabel, 'saturation', 200)
  } else if (panel === 'crop') {
    const hint = doc.createElement('p'); hint.className = 'md-image-panel-hint'; hint.textContent = imagesStrings.cropHint; form.append(hint)
    const ratios = group(imagesStrings.aspectRatioLabel); ratios.className = 'md-image-choice-group'
    choice(ratios, imagesStrings.aspectRatioFull, () => { draft.cropLeft = draft.cropRight = draft.cropTop = draft.cropBottom = 0 }, () => !draft.cropLeft && !draft.cropRight && !draft.cropTop && !draft.cropBottom)
    for (const [title, ratio] of [['1 : 1', 1], ['4 : 3', 4 / 3], ['16 : 9', 16 / 9]] as const) {
      const button = choice(ratios, title, () => {
        const sourceRatio = draft.naturalWidth / draft.naturalHeight; const target = draft.rotate % 180 ? 1 / ratio : ratio
        draft.cropLeft = draft.cropRight = Math.min(47, Math.max(0, (1 - target / sourceRatio) * 50))
        draft.cropTop = draft.cropBottom = Math.min(47, Math.max(0, (1 - sourceRatio / target) * 50))
      }, () => false)
      button.disabled = !draft.naturalWidth
    }
  } else if (panel === 'text') {
    textField(imagesStrings.captionLabel, draft.caption, value => { draft.caption = value; refresh() })
    textField(imagesStrings.altTextLabel, alt, value => { alt = value }, true)
    const hint = doc.createElement('p'); hint.className = 'md-image-panel-hint'; hint.textContent = imagesStrings.altTextHint; form.append(hint)
  } else {
    const input = textField(imagesStrings.newImageUrlLabel, src, value => { src = value }); input.setAttribute('placeholder', 'https://…')
    const hint = doc.createElement('p'); hint.className = 'md-image-panel-hint'; hint.textContent = imagesStrings.replaceHint; form.append(hint)
  }
  const actions = doc.createElement('div'); actions.className = 'md-image-panel-actions'
  const cancel = doc.createElement('button'); cancel.type = 'button'; cancel.textContent = markdownStrings.cancel; cancel.onclick = close
  const apply = doc.createElement('button'); apply.type = 'submit'; apply.className = 'md-image-panel-apply'; apply.textContent = markdownStrings.apply
  actions.append(cancel, apply); form.append(error); if (panel !== 'layout') form.append(actions); container.append(form)
  form.onsubmit = event => { event.preventDefault(); if (composition.active()) return; try { options.apply(draft, src, alt); close() } catch (cause) { error.textContent = cause instanceof Error ? cause.message : imagesStrings.checkImageSettings } }
  refreshers.forEach(refresh => refresh())
  return { setDraft(value: ImagePresentation) { draft = { ...value }; refresh() }, destroy: () => composition.destroy() }
}
