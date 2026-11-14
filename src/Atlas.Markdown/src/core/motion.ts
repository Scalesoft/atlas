/**
 * The editor's motion in JavaScript, one helper for the core, the plugins and the assist module.
 *
 * Durations and curves come from the `--md-motion-*` tokens of `styles/motion.css` (the owner's motion prototype),
 * read from an element on every call, so a host that remaps them drives Web Animations and CSS alike. Reduced
 * motion follows the prototype's `anim()`: only opacity and colour survive, at most 160 ms, linear.
 * `--md-motion-time-scale` multiplies only fixed prototype literals (`scaled()`), never tokens: a host that slows
 * its motion down passes slowed tokens.
 */

export type MarkdownMotionDuration = 'fast' | 'base' | 'slow' | 'menu' | 'target' | 'pop' | 'glow' | 'loop' | 'spin' | 'pulse'

/** The defaults of `styles/motion.css` (a test keeps both equal), used where no stylesheet resolves the tokens. */
export const markdownMotionDefaults: Readonly<Record<MarkdownMotionDuration, number>> = Object.freeze({
  fast: 120, base: 200, slow: 280, menu: 140, target: 160, pop: 320, glow: 1600, loop: 1600, spin: 800, pulse: 1000,
})
export const MARKDOWN_MOTION_EASE = 'cubic-bezier(.22,1,.36,1)'
export const MARKDOWN_MOTION_EASE_IN_OUT = 'cubic-bezier(.65,0,.35,1)'
/** The longest animation reduced motion keeps (before the time scale). */
export const REDUCED_MOTION_MAX_MS = 160

/** Every motion token: body-level overlays receive them with the editor's theme (`copyMarkdownOverlayTheme`). */
export const markdownMotionTokens = [
  ...(Object.keys(markdownMotionDefaults) as MarkdownMotionDuration[]).map(name => `--md-motion-${name}` as const),
  '--md-motion-surface', '--md-motion-ease', '--md-motion-ease-io', '--md-motion-rise', '--md-motion-pop-from', '--md-motion-time-scale',
] as const

export interface MarkdownMotion {
  /** `prefers-reduced-motion`, `data-md-motion="reduced"` on an ancestor or the host's own setting. */
  readonly reduced: boolean
  /** A duration token in milliseconds, as the host resolved it. */
  duration(token: MarkdownMotionDuration): number
  /** A fixed prototype duration (ripple, stagger, …) multiplied by `--md-motion-time-scale`. */
  scaled(milliseconds: number): number
  readonly ease: string
  readonly easeInOut: string
  /** `--md-motion-rise` in pixels: the small travel of entrances, zero under reduced motion. */
  readonly rise: number
  /** A custom property resolved on the element, or the fallback (Web Animations need concrete values, not `var()`). */
  token(name: string, fallback: string): string
}

/** Milliseconds of a CSS time (`120ms`, `.6s`, a bare number), else undefined. */
export function motionMilliseconds(value: string): number | undefined {
  const match = /^\s*(-?\d*\.?\d+)\s*(ms|s)?\s*$/.exec(value)
  if (!match) return undefined
  const number = Number(match[1])
  return match[2] === 's' ? number * 1000 : number
}

export function isMarkdownReducedMotion(source: Element | null | undefined, reducedMotion?: () => boolean): boolean {
  if (reducedMotion?.()) return true
  if (source?.closest?.('[data-md-motion="reduced"]')) return true
  try { return !!source?.ownerDocument.defaultView?.matchMedia?.('(prefers-reduced-motion: reduce)').matches } catch { return false }
}

/** Reads the tokens once per call, so a changed setting or theme applies to the next animation. */
export function markdownMotion(source: Element | null | undefined, reducedMotion?: () => boolean): MarkdownMotion {
  let style: CSSStyleDeclaration | undefined
  try { style = source?.ownerDocument.defaultView?.getComputedStyle(source) } catch { style = undefined }
  // Browsers resolve `var()` in computed custom properties; an unresolved value (no engine support) falls back.
  const token = (name: string, fallback: string) => { const value = style?.getPropertyValue(name).trim() ?? ''; return value && !value.includes('var(') ? value : fallback }
  const scaleValue = Number(token('--md-motion-time-scale', '1'))
  const scale = Number.isFinite(scaleValue) && scaleValue > 0 ? scaleValue : 1
  const rise = Number.parseFloat(token('--md-motion-rise', '4'))
  return {
    reduced: isMarkdownReducedMotion(source, reducedMotion),
    duration: name => motionMilliseconds(token(`--md-motion-${name}`, '')) ?? markdownMotionDefaults[name],
    scaled: milliseconds => milliseconds * scale,
    ease: token('--md-motion-ease', MARKDOWN_MOTION_EASE),
    easeInOut: token('--md-motion-ease-io', MARKDOWN_MOTION_EASE_IN_OUT),
    rise: Number.isFinite(rise) ? rise : 4,
    token,
  }
}

/** `smooth` unless motion is reduced: an explicit `behavior` overrides the CSS `scroll-behavior` of the page. */
export function markdownScrollBehavior(source: Element | null | undefined, reducedMotion?: () => boolean): ScrollBehavior {
  return isMarkdownReducedMotion(source, reducedMotion) ? 'auto' : 'smooth'
}

type MotionAnimation = Pick<Animation, 'finished' | 'cancel'>
const reducedProperties = ['opacity', 'backgroundColor', 'color', 'offset']
const settled: MotionAnimation = { finished: Promise.resolve() as unknown as Promise<Animation>, cancel() {} }

/**
 * The prototype's `anim()`: `fill: 'both'` and the ease token by default; under reduced motion the keyframes keep
 * only opacity, background and colour (pure movement becomes a still frame), at most 160 ms × the time scale,
 * linear. Without Web Animations (old engines, jsdom) the end state simply applies.
 */
export function animateMarkdownMotion(element: Element | null | undefined, keyframes: Keyframe[], options: KeyframeAnimationOptions, motion: Pick<MarkdownMotion, 'reduced' | 'ease' | 'scaled'>): MotionAnimation {
  if (!element || typeof (element as Element & { animate?: unknown }).animate !== 'function') return settled
  let frames = keyframes
  const timing: KeyframeAnimationOptions = { fill: 'both', easing: motion.ease, ...options }
  if (motion.reduced) {
    frames = keyframes.map(frame => Object.fromEntries(Object.entries(frame).filter(([property]) => reducedProperties.includes(property))) as Keyframe)
    if (!frames.some(frame => 'opacity' in frame || 'backgroundColor' in frame)) frames = [{ opacity: 1 }, { opacity: 1 }]
    timing.duration = Math.min(Number(timing.duration ?? 200), motion.scaled(REDUCED_MOTION_MAX_MS))
    timing.easing = 'linear'
  }
  try {
    const animation = element.animate(frames, timing)
    animation.finished?.catch(() => undefined)
    return animation
  } catch { return settled }
}

export type MarkdownConcealDuration = 'fast' | 'enter' | 'unfold' | 'roll' | 'aurora' | 'stagger'

/**
 * The invisible ink and the secrets vault keep their own long timing (a signature, not the editor's quick one): the
 * `--md-conceal-*` group of `styles/motion.css`. A test keeps these defaults equal to the stylesheet.
 */
export const markdownConcealDefaults: Readonly<Record<MarkdownConcealDuration, number>> = Object.freeze({
  fast: 200, enter: 520, unfold: 900, roll: 2800, aurora: 42000, stagger: 70,
})
export const MARKDOWN_CONCEAL_EASE = 'cubic-bezier(.22,1,.36,1)'
export const MARKDOWN_CONCEAL_EASE_ROLL = 'cubic-bezier(.45,.05,.3,1)'

export interface MarkdownConcealMotion {
  readonly reduced: boolean
  /** A `--md-conceal-*` duration in milliseconds, as the host resolved it (under reduced motion at most 160 ms). */
  duration(token: MarkdownConcealDuration): number
  readonly ease: string
  readonly easeRoll: string
}

export function markdownConcealMotion(source: Element | null | undefined, reducedMotion?: () => boolean): MarkdownConcealMotion {
  const motion = markdownMotion(source, reducedMotion)
  return {
    reduced: motion.reduced,
    duration: name => {
      const resolved = motionMilliseconds(motion.token(`--md-conceal-${name}`, '')) ?? markdownConcealDefaults[name]
      // Without a stylesheet (forced reduced motion, jsdom) the reduced path applies the same ceiling.
      return motion.reduced ? (name === 'aurora' || name === 'stagger' ? 0 : Math.min(resolved, motion.scaled(REDUCED_MOTION_MAX_MS))) : resolved
    },
    ease: motion.reduced ? 'linear' : motion.token('--md-conceal-ease', MARKDOWN_CONCEAL_EASE),
    easeRoll: motion.reduced ? 'linear' : motion.token('--md-conceal-ease-roll', MARKDOWN_CONCEAL_EASE_ROLL),
  }
}

/** Resolves when the animation ends or is cancelled. */
export const markdownMotionFinished = (animation: Pick<Animation, 'finished'>): Promise<void> => Promise.resolve(animation.finished).then(() => undefined, () => undefined)
