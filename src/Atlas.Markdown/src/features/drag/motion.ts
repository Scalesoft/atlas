import { markdownMotion } from '../../core/motion'

/**
 * Shared timing for document blocks, table axes and dropped files, read from the editor's motion tokens on every
 * drag: neighbours move with `base`, a cancelled block returns with `target`. Under reduced motion positions jump
 * (no in-between frame, docs/drag-interactions.md); `reduced` tells the callers.
 */
export function dragMotion(source: Element | null | undefined, reducedMotion?: () => boolean) {
  const motion = markdownMotion(source, reducedMotion)
  return { duration: motion.duration('base'), returnDuration: motion.duration('target'), easing: motion.ease, reduced: motion.reduced }
}
export function dragTargetTolerance(size: number): number { return Math.min(12, size * .2) }
