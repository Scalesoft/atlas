import Image from '@tiptap/extension-image'
import { createImageView } from '../features/images/imageView'
import { imagePresentation, imageStyle, imageViewportStyle, imageCropStyle } from '../features/images/presentation'
import { defineMarkdownAttributeValidator } from '../schema/constraints'

export const RichImage = Image.extend({
  addAttributes() { return { ...this.parent?.(), presentation: {
    default: null, rendered: false, validate: defineMarkdownAttributeValidator({ kind: 'structured', validator: 'imagePresentation' }, value => { imagePresentation(value) }),
    parseHTML: element => { try { return imagePresentation(JSON.parse(element.getAttribute('data-presentation') ?? 'null')) } catch { return null } },
  } } },
  renderHTML({ node, HTMLAttributes }) {
    const p = imagePresentation(node.attrs.presentation)
    const attrs = { ...HTMLAttributes, style: imageStyle(p), 'data-presentation': JSON.stringify(p), class: 'tt-image' }
    return ['figure', { class: 'md-image-static' },
      ['span', { class: 'md-image-viewport', style: imageViewportStyle(p) },
        ['span', { class: 'md-image-crop', style: imageCropStyle(p) }, ['img', attrs]]],
      ...(p.caption ? [['figcaption', {}, p.caption] as import('@tiptap/pm/model').DOMOutputSpec] : []),
    ]
  },
  addNodeView() { return createImageView },
})
