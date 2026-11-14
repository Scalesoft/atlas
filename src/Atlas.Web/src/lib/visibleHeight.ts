/**
 * How much of `height` px, measured down from the top edge of `el`, a reader can see: what lies above the bottom of
 * the window and of every list that scrolls `el` (the sidebar's list of spaces ends above the account row).
 *
 * A list of hundreds of rows reaches far below it. Eased over its whole height, an unfolding crosses the visible
 * part within a frame or two and a fold leaves it empty until its last moments: the motion happens where nobody
 * sees it. Moving only this much, in the same time and on the same curve, is all of the motion the eye can follow;
 * the rest takes its place out of sight when the animation ends. Measured to the window alone, the motion's landing
 * fell behind the edge of the list: the rows left the view at speed and never came to rest in it.
 *
 * For a fold or an unfolding asked for where the element is in view (its own chevron). One that the list scrolls
 * along with (a page opened in a folded branch) has to move whole.
 */
export function visibleHeight(el: Element, height: number): number {
  const top = el.getBoundingClientRect().top;
  let bottom = window.innerHeight;
  for (let parent = el.parentElement; parent; parent = parent.parentElement) {
    const overflow = getComputedStyle(parent).overflowY;
    if (overflow === "auto" || overflow === "scroll") bottom = Math.min(bottom, parent.getBoundingClientRect().bottom);
  }
  return Math.max(0, Math.min(height, bottom - top));
}
