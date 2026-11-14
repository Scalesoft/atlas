import { defineMarkdownCommand, type MarkdownPlugin } from "@atlas/markdown/core";
import type { Node as ProseMirrorNode } from "@tiptap/pm/model";
import { NodeSelection, TextSelection, type Selection } from "@tiptap/pm/state";
import type { EditorView } from "@tiptap/pm/view";
import { glow } from "../motion/aiMotion";
import type { RevealTarget } from "./revealTarget";

/**
 * Host navigation inside the live editor (plan §9, package E): "Zobrazit" of a publication-check finding selects the
 * place, scrolls it into view and lets it glow once, like a citation in the reading view. It is not an editor
 * capability, so it lives in Atlas as a plugin without schema that only contributes the `atlas.reveal` command; it
 * never changes the document.
 */

interface Located {
  readonly selection: (doc: ProseMirrorNode) => Selection;
  /** The node to glow: the block, or the inline node itself. */
  readonly glowAt: number;
}

/** The node at a child-index path from the document and the position before it. */
function nodeAt(doc: ProseMirrorNode, path: readonly number[]): { node: ProseMirrorNode; pos: number } | null {
  if (!path.length) return null;
  let node = doc;
  let start = 0;
  let pos = -1;
  for (const index of path) {
    if (!Number.isInteger(index) || index < 0 || index >= node.childCount) return null;
    let offset = start;
    for (let i = 0; i < index; i++) offset += node.child(i).nodeSize;
    pos = offset;
    node = node.child(index);
    start = pos + 1;
  }
  return { node, pos };
}

function headingSelection(pos: number, node: ProseMirrorNode): Located {
  return { selection: doc => TextSelection.create(doc, pos + 1, pos + 1 + node.content.size), glowAt: pos };
}

const pageHref = (href: unknown, pageId: string) =>
  typeof href === "string" && new RegExp(`#/pages/${pageId.replace(/[^0-9a-z-]/gi, "")}(?:[?/#]|$)`, "i").test(href);

function locatePage(doc: ProseMirrorNode, pageId: string): Located | null {
  const id = pageId.toLowerCase();
  let found: Located | null = null;
  doc.descendants((node, pos) => {
    if (found) return false;
    if (node.type.name === "pageReference" && String(node.attrs.id ?? "").toLowerCase() === id) {
      found = { selection: d => NodeSelection.create(d, pos), glowAt: pos };
      return false;
    }
    if (node.isText) {
      const link = node.marks.find(mark => pageHref(mark.attrs.href, pageId));
      if (!link) return true;
      // The whole link: the following text nodes that carry the same mark belong to it.
      const $pos = doc.resolve(pos);
      const parent = $pos.parent;
      let end = pos + node.nodeSize;
      for (let index = $pos.index() + 1; index < parent.childCount; index++) {
        const next = parent.child(index);
        if (!next.isText || !link.isInSet(next.marks)) break;
        end += next.nodeSize;
      }
      found = { selection: d => TextSelection.create(d, pos, end), glowAt: $pos.before() };
      return false;
    }
    if (pageHref(node.attrs.href, pageId) && NodeSelection.isSelectable(node)) {
      found = { selection: d => NodeSelection.create(d, pos), glowAt: pos };
      return false;
    }
    return true;
  });
  return found as Located | null;
}

export function locateRevealTarget(doc: ProseMirrorNode, target: RevealTarget): Located | null {
  if (target.kind === "page") return locatePage(doc, target.pageId);
  if (target.kind === "heading") {
    let byId: Located | null = null;
    if (target.id) {
      doc.descendants((node, pos) => {
        if (byId) return false;
        if (node.type.name === "heading" && node.attrs.headingId === target.id) { byId = headingSelection(pos, node); return false; }
        return true;
      });
    }
    if (byId) return byId as Located;
    const at = nodeAt(doc, target.path);
    return at && at.node.type.name === "heading" ? headingSelection(at.pos, at.node) : null;
  }
  const at = nodeAt(doc, target.path);
  if (!at || !at.node.isTextblock) return null;
  const { from, to } = target;
  if (!Number.isInteger(from) || !Number.isInteger(to) || from < 0 || to <= from || to > at.node.content.size) return null;
  const start = at.pos + 1;
  return { selection: doc => TextSelection.create(doc, start + from, start + to), glowAt: at.pos };
}

function show(view: EditorView | undefined, pos: number) {
  const element = view?.nodeDOM(pos);
  if (!(element instanceof HTMLElement)) return;
  // Centred rather than at the edge, so a sticky page bar never covers it; reduced motion keeps the scroll instant
  // (motion.css sets scroll-behavior: auto).
  if (typeof element.scrollIntoView === "function") element.scrollIntoView({ block: "center" });
  glow(element);
}

export const revealCommand = defineMarkdownCommand<RevealTarget>({
  id: "atlas.reveal",
  apply: target => ({ tr, dispatch, view }) => {
    const located = locateRevealTarget(tr.doc, target);
    if (!located) return false;
    if (dispatch) {
      tr.setSelection(located.selection(tr.doc)).scrollIntoView();
      // The document does not change, so the node is already where it will be after this transaction.
      show(view, located.glowAt);
    }
    return true;
  },
});

/** The live editor's reveal command; not part of reading, the page source or any save projection. */
export function createRevealPlugin(): MarkdownPlugin {
  return { id: "atlas-reveal", apiVersion: 1, create: () => ({ commands: [revealCommand] }) };
}
