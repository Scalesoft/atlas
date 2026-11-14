// JS reference implementation of the validator "facts" (the ground truth the .NET side must reproduce):
// apply an update to a replica inside a deep observer and collect what it introduced.
import * as Y from 'yjs'

export interface Facts {
  elements: string[]          // node names of inserted XmlElements (including whole inserted subtrees)
  attrs: string[]             // "node.attr" for attributes set on inserted or existing elements
  marks: string[]             // formatting keys on inserted / reformatted text (y-prosemirror marks)
  embeds: number              // non-string inserts inside XmlText
  otherRoots: string[]        // shared roots other than the XmlFragment that changed
  pendingStructs: boolean
  pendingDs: boolean
}

function walkInserted(node: Y.XmlElement | Y.XmlText | Y.AbstractType<any>, f: { elements: Set<string>; attrs: Set<string>; marks: Set<string>; embeds: number }) {
  if (node instanceof Y.XmlElement) {
    f.elements.add(node.nodeName)
    for (const k of Object.keys(node.getAttributes())) f.attrs.add(`${node.nodeName}.${k}`)
    node.toArray().forEach(c => walkInserted(c as any, f))
  } else if (node instanceof Y.XmlText) {
    for (const op of node.toDelta()) {
      if (typeof op.insert !== 'string') f.embeds++
      for (const k of Object.keys(op.attributes ?? {})) f.marks.add(k)
    }
  }
}

export function factsFor(replica: Y.Doc, update: Uint8Array, fragment = 'default'): Facts {
  const f = { elements: new Set<string>(), attrs: new Set<string>(), marks: new Set<string>(), embeds: 0 }
  const frag = replica.getXmlFragment(fragment)
  const otherRoots = new Set<string>()
  const handler = (events: Y.YEvent<any>[]) => {
    for (const ev of events) {
      const target = ev.target
      for (const [key] of ev.changes.keys) {
        if (target instanceof Y.XmlElement) f.attrs.add(`${target.nodeName}.${key}`)
        else f.attrs.add(`#text.${key}`)
      }
      for (const d of ev.changes.delta as any[]) {
        if (d.insert !== undefined) {
          if (target instanceof Y.XmlText) {
            if (typeof d.insert !== 'string') f.embeds++
            for (const k of Object.keys(d.attributes ?? {})) f.marks.add(k)
          } else {
            for (const c of d.insert as any[]) walkInserted(c, f)
          }
        } else if (d.retain !== undefined && d.attributes) {
          for (const k of Object.keys(d.attributes)) f.marks.add(k)
        }
      }
    }
  }
  frag.observeDeep(handler)
  const shareBefore = new Set(replica.share.keys())
  const onAfter = (tr: Y.Transaction) => {
    for (const type of tr.changed.keys()) {
      let t: any = type
      while (t._item) t = t._item.parent
      if (t !== frag) for (const [name, v] of replica.share) if (v === t) otherRoots.add(name)
    }
  }
  replica.on('afterTransaction', onAfter)
  Y.applyUpdate(replica, update)
  replica.off('afterTransaction', onAfter)
  frag.unobserveDeep(handler)
  for (const name of replica.share.keys()) if (!shareBefore.has(name) && name !== fragment) otherRoots.add(name)
  return {
    elements: [...f.elements].sort(), attrs: [...f.attrs].sort(), marks: [...f.marks].sort(), embeds: f.embeds,
    otherRoots: [...otherRoots].sort(),
    pendingStructs: !!replica.store.pendingStructs, pendingDs: !!replica.store.pendingDs,
  }
}
