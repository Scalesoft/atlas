import { collapseHeight, revealHeight } from "../motion/aiMotion";
import { foldAll, isFolded, setFolded } from "./foldState";

// Which headings the reader folded (everything under a heading up to the next one of the same or a higher level hidden): the
// reader's own view (foldState.ts, by page and `headingId`), never written into the document. A heading without an id cannot fold.
// No editor import here: the page outline and the anchor navigation unfold through this module without loading the editor.
export { foldAll, isFolded, onFoldChange, setFolded } from "./foldState";
/** The class of a block a folded heading hides (the reading DOM's own class, the editor's node decoration). */
export const FOLDED = "atlas-heading-folded";
/** Each rendered document (the reading container, the editor's view) whose children are the page's blocks. */
/** …with the page each belongs to: the same `headingId` on two pages (a shared template) folds on each page alone. */
const roots = new WeakMap<Element, (() => string | null | undefined) | undefined>();

/** A heading's fold identity: `page#anchorId`; a heading without an id does not fold (its text would lose the fold on a rename). */
export const headingKey = (headingId: unknown, page?: string | null) => typeof headingId === "string" && headingId ? (page ? `${page}#${headingId}` : headingId) : null;

/** Registers a rendered document's root (whose children are the page's blocks) and the page it shows. */
export const foldRoot = (root: Element, page?: () => string | null | undefined) => roots.set(root, page);

/**
 * For each block of a run of siblings (`levels`: the heading level, 0 for any other block), the index of the folded heading that
 * hides it, else -1. A folded heading inside a hidden section is hidden with it.
 */
export function foldedBy(levels: readonly number[], folds: readonly boolean[]): number[] {
  let by = -1;
  return levels.map((level, index) => {
    if (by >= 0 && level && level <= levels[by]!) by = -1;
    const hiddenBy = by;
    if (by < 0 && level && folds[index]) by = index;
    return hiddenBy;
  });
}

/** The headings whose sections hold the block at `index` (its own heading, that heading's heading…), nearest first. */
export function sectionsAround(levels: readonly number[], index: number): number[] {
  const found: number[] = [];
  let below = levels[index] || 7;
  for (let at = index - 1; at >= 0 && below > 1; at--) {
    const level = levels[at]!;
    if (level && level < below) { found.push(at); below = level; }
  }
  return found;
}

export const levelOf = (element: Element | null | undefined) => element && /^H[1-6]$/.test(element.tagName) ? Number(element.tagName[1]) : 0;
const keyOf = (heading: HTMLElement) => headingKey(heading.dataset.headingId, heading.parentElement && roots.get(heading.parentElement)?.());

/** The blocks under a heading that show now: up to the next heading of the same or a higher level, without nested folded parts. */
function sectionOf(heading: HTMLElement): HTMLElement[] {
  const level = levelOf(heading), blocks: HTMLElement[] = [];
  for (let element = heading.nextElementSibling; element && !(levelOf(element) && levelOf(element) <= level); element = element.nextElementSibling)
    if (element instanceof HTMLElement && !element.classList.contains(FOLDED)) blocks.push(element);
  return blocks;
}

/** Whether a heading has anything to fold: a block follows that is not a heading of the same or a higher level. */
export const hasSection = (levels: readonly number[], index: number) => index + 1 < levels.length && !(levels[index + 1] && levels[index + 1]! <= levels[index]!);

const closing = new WeakMap<HTMLElement, Animation[]>();
const settle = (blocks: HTMLElement[]) => blocks.forEach(block => { block.removeAttribute("inert"); block.removeAttribute("aria-hidden"); });

/**
 * Folds or opens a rendered heading's section: the blocks fold away (collapseHeight; a live editor's blocks, which carry their
 * `pmViewDesc`, without marking them) and then hide, or show and unfold (revealHeight); reduced motion fades. A second toggle
 * while it folds keeps it open. `key` defaults to the heading's own.
 */
export function toggleFold(heading: HTMLElement, collapsed?: boolean, key = keyOf(heading)) {
  const root = heading.parentElement;
  if (!root || !roots.has(root) || !key) return;
  const running = closing.get(heading);
  if (running) {
    closing.delete(heading);
    running.forEach(animation => animation.cancel());
    settle(sectionOf(heading));
    if (collapsed !== true) return;
  }
  const folding = collapsed ?? !isFolded(key);
  if (folding === isFolded(key)) return;
  if (!folding) {
    setFolded(key, false);
    sectionOf(heading).forEach(block => revealHeight(block));
    return;
  }
  const blocks = sectionOf(heading), animations = blocks.map(block => collapseHeight(block, undefined, !("pmViewDesc" in block))).filter((animation): animation is Animation => !!animation);
  const done = () => { settle(blocks); setFolded(key, true); animations.forEach(animation => animation.cancel()); };
  if (!animations.length) return done();
  closing.set(heading, animations);
  Promise.all(animations.map(animation => animation.finished)).then(() => {
    if (closing.get(heading) !== animations) return;
    closing.delete(heading);
    done();
  }, () => { /* cancelled: opened again */ });
}

/** Opens every folded section that hides `target` (an anchor, the outline, a citation leading there), at once. */
export function unfoldTo(target: Element) {
  let block: Element = target, root = block.parentElement;
  while (root && !roots.has(root)) { block = root; root = root.parentElement; }
  if (!root) return;
  const blocks = [...root.children], levels = blocks.map(levelOf);
  foldAll(sectionsAround(levels, blocks.indexOf(block)).map(index => keyOf(blocks[index] as HTMLElement)), false);
}
