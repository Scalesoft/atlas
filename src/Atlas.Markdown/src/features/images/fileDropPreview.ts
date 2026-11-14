import { imagesStrings } from '../../ui/strings/images'

export type FileDropPreviewKind = 'image' | 'attachment' | 'text' | 'diagram'

/** Local view state only: previews never become document nodes or upload URLs. */
export function createFileDropPreview(doc: Document, files: { file: File; kind: FileDropPreviewKind }[]) {
  const dom = doc.createElement('span')
  dom.className = 'md-file-drop-preview'
  dom.contentEditable = 'false'
  dom.setAttribute('role', 'status')
  dom.setAttribute('aria-live', 'polite')
  dom.setAttribute('aria-busy', 'true')
  const urls: string[] = []
  const ownerUrl = doc.defaultView?.URL
  for (const { file, kind } of files) {
    const card = doc.createElement('span')
    card.className = 'md-file-drop-card'
    card.dataset.fileKind = kind
    // A file the URL implementation refuses (another realm's Blob, as Node 26 under jsdom) keeps the icon card.
    let url: string | undefined
    if (kind === 'image' && ownerUrl?.createObjectURL) try { url = ownerUrl.createObjectURL(file) } catch { url = undefined }
    if (url) {
      const image = doc.createElement('img')
      urls.push(url)
      image.src = url
      image.alt = ''
      image.draggable = false
      image.addEventListener('error', () => { image.hidden = true }, { once: true })
      card.append(image)
    }
    const icon = createIconElement(doc, kind === 'image' ? 'image' : kind === 'diagram' ? 'workflow' : kind === 'attachment' ? 'paperclip' : 'file-text', 24)
    const details = doc.createElement('span')
    details.className = 'md-file-drop-details'
    const name = doc.createElement('span')
    name.className = 'md-file-drop-name'
    name.textContent = file.name
    const status = doc.createElement('span')
    status.className = 'md-file-drop-label'
    status.textContent = imagesStrings.filePreviewStatus(kind)
    details.append(name, status)
    card.append(icon, details)
    dom.append(card)
  }
  return { dom, destroy() { urls.splice(0).forEach(url => ownerUrl?.revokeObjectURL(url)); dom.remove() } }
}
import { createIconElement } from '../../ui/iconMarkup'
