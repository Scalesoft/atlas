import { outOfReach } from "../../lib/outOfReach";

/**
 * `@before-leave` of a `<Transition>` whose leaving child must not hold its place while it fades out (a list that gives
 * way to its empty state, an empty state that gives way to the list): it is pinned where it stands, so what replaces it
 * rises into the same spot instead of waiting below it. Read before the leave starts, while the child is still in the
 * flow; the host must be positioned (the child is placed relative to it) and the leave class sets `position: absolute`.
 * While it fades its replacement is already in the document, so it is taken out of reach (outOfReach.ts).
 */
export function leaveInPlace(element: Element) {
  const node = element as HTMLElement;
  node.style.cssText += `;left:${node.offsetLeft}px;top:${node.offsetTop}px;width:${node.offsetWidth}px;height:${node.offsetHeight}px`;
  outOfReach(node);
}
