import "./collaborationPointer.css";

/**
 * A co-editor's mouse pointer, the same mark on a board (BoardPresence) and over a page (collaborationPresence): an arrow in
 * the person's colour with their name. Its tip is at (2, 2) of the 22 × 26 box.
 */
export const COLLABORATION_POINTER_PATH = "M2 2L2 21L7.5 16L11 24L14.5 22.5L11 15L19 15Z";

/** The pointer for a DOM-owned overlay (a page's, collaborationPresence); a Vue view renders CollaborationPointer.vue, the same markup. */
export function createPointerElement(doc: Document = document): HTMLElement {
  const element = doc.createElement("div");
  element.className = "collaboration-pointer";
  const svg = doc.createElementNS("http://www.w3.org/2000/svg", "svg");
  svg.setAttribute("viewBox", "0 0 22 26");
  const path = doc.createElementNS("http://www.w3.org/2000/svg", "path");
  path.setAttribute("d", COLLABORATION_POINTER_PATH);
  svg.append(path);
  const name = doc.createElement("span");
  name.className = "collaboration-pointer__name";
  element.append(svg, name);
  return element;
}
