/**
 * Shared motion for assist suggestions, links and text changes.
 *
 * Built on the editor's shared motion helper (`markdownMotion`, public core). Durations come from the
 * `--md-assist-*` tokens on the editor element, which default to the core `--md-motion-*` tokens (assist.css), so
 * a host maps the core once and the module follows; the 0.9 assist names still override. Token durations are
 * taken as the host resolved them; `--md-assist-time-scale` (default `--md-motion-time-scale`) multiplies only the
 * fixed prototype literals. Reduced motion follows the prototype's `anim()`: only opacity, background and colour
 * survive, at most 160 ms, linear.
 */
import { animateMarkdownMotion, markdownMotion, markdownMotionFinished, motionMilliseconds } from '../core'
import type { MarkdownMotionDuration } from '../core'

export interface AssistMotion {
  readonly reduced: boolean
  /** A token in milliseconds, as the host resolved it. */
  duration(token: AssistDurationToken): number
  /** A fixed prototype duration (ripple, conceal step, …), multiplied by the time scale. */
  scaled(milliseconds: number): number
  readonly ease: string
  readonly easeInOut: string
  /** A theme colour resolved on the editor (Web Animations keyframes get concrete values, not `var()`). */
  color(token: string, fallback: string): string
  /** `--md-assist-glow-color`, else the accent at 18 %. */
  readonly glowColor: string
}
export type AssistDurationToken = 'fast' | 'base' | 'slow' | 'menu' | 'morph' | 'glow' | 'solidify'

/** The core token each assist token defaults to (assist.css); morph and solidify are the module's own. */
const core: Partial<Record<AssistDurationToken, MarkdownMotionDuration>> = { fast: 'fast', base: 'base', slow: 'slow', menu: 'target', glow: 'glow' }
const own = { morph: 380, solidify: 360 }
/** Every token the module reads and copies onto its body-level overlays. */
export const assistTokens = ['--md-assist-fast', '--md-assist-base', '--md-assist-slow', '--md-assist-menu', '--md-assist-morph', '--md-assist-glow',
  '--md-assist-solidify', '--md-assist-ease', '--md-assist-ease-io', '--md-assist-glow-color', '--md-assist-time-scale'] as const

/** Reads the tokens once per call, so a changed setting or theme applies to the next animation. */
export function assistMotion(source: Element | null | undefined, reducedMotion?: () => boolean): AssistMotion {
  const motion = markdownMotion(source, reducedMotion)
  const token = motion.token
  const scaleValue = Number(token('--md-assist-time-scale', ''))
  const scale = Number.isFinite(scaleValue) && scaleValue > 0 ? scaleValue : motion.scaled(1)
  const accent = token('--accent', '#285ad3')
  return {
    color: token,
    glowColor: token('--md-assist-glow-color', `color-mix(in srgb, ${accent} 18%, transparent)`),
    reduced: motion.reduced,
    duration: name => {
      const coreName = core[name]
      return motionMilliseconds(token(`--md-assist-${name}`, '')) ?? (coreName ? motion.duration(coreName) : own[name as keyof typeof own])
    },
    scaled: value => value * scale,
    ease: token('--md-assist-ease', motion.ease),
    easeInOut: token('--md-assist-ease-io', motion.easeInOut),
  }
}

/** The prototype's `anim()` on the shared helper; without Web Animations (old engines, jsdom) the end state applies. */
export function animate(element: Element | null | undefined, keyframes: Keyframe[], options: KeyframeAnimationOptions, motion: AssistMotion): Pick<Animation, 'finished' | 'cancel'> {
  return animateMarkdownMotion(element, keyframes, options, motion)
}

export const finished = markdownMotionFinished

/* Prototype primitives. Each returns the animation(s) so callers can wait for the end. */

/** `markerIn`: the dashed ring spins in from −140° (280 ms), the icon scales in 60 ms later (240 ms). */
export function markerIn(ring: Element, icon: Element, motion: AssistMotion) {
  animate(ring, [{ transform: 'rotate(-140deg) scale(.5)', opacity: 0 }, { transform: 'none', opacity: 1 }], { duration: motion.duration('slow') }, motion)
  return animate(icon, [{ opacity: 0, transform: 'scale(.4)' }, { opacity: 1, transform: 'none' }], { duration: motion.scaled(240), delay: motion.scaled(60) }, motion)
}
/** One warning ripple: scale 1 → 2.4, opacity .7 → 0, 600 ms. Never repeated. */
export function ripple(element: Element, motion: AssistMotion) {
  return animate(element, [{ opacity: .7, transform: 'scale(1)' }, { opacity: 0, transform: 'scale(2.4)' }], { duration: motion.scaled(600), easing: 'cubic-bezier(.2,.7,.3,1)' }, motion)
}
/** Dismissal of a marker or suggestion: scale .6 + fade, 160 ms. */
export function markerOut(element: Element, motion: AssistMotion) {
  return animate(element, [{ opacity: 1 }, { opacity: 0, transform: 'scale(.6)' }], { duration: motion.scaled(160) }, motion)
}
/** `menuIn`: the list grows from the marker, scale .94 → 1 and 4 px down, 160 ms. */
export function menuIn(element: Element, motion: AssistMotion) {
  return animate(element, [{ opacity: 0, transform: 'scale(.94) translateY(-4px)' }, { opacity: 1, transform: 'none' }], { duration: motion.duration('menu') }, motion)
}
/** `menuOut`: 120 ms, scale .97. */
export function menuOut(element: Element, motion: AssistMotion) {
  return animate(element, [{ opacity: 1 }, { opacity: 0, transform: 'scale(.97)' }], { duration: motion.duration('fast') }, motion)
}
/** The recognised span underlines from the left, 200 ms. */
export function underline(element: Element, motion: AssistMotion) {
  return animate(element, [{ backgroundSize: '0% 1.5px' }, { backgroundSize: '100% 1.5px' }], { duration: motion.duration('base') }, motion)
}
/** A new link: its underline draws from the left and its colour turns to the accent, 280 ms. */
export function linkIn(element: Element, motion: AssistMotion) {
  // The prototype draws this underline as a background line; the link's own decoration waits until it ends.
  const line = { backgroundImage: 'linear-gradient(currentColor, currentColor)', backgroundRepeat: 'no-repeat', backgroundPosition: '0 100%', textDecorationColor: 'transparent' }
  return animate(element, [
    { ...line, backgroundSize: '0% 1px', color: motion.color('--text', 'currentColor') },
    { ...line, backgroundSize: '100% 1px', color: motion.color('--accent', 'currentColor') },
  ], { duration: motion.duration('slow'), fill: 'none' }, motion)
}
/** `morphChip`: from the span's width into the component, opacity .35, scale .96 → 1.03 → 1, 380 ms. */
export function morph(element: Element, fromWidth: number, toWidth: number, motion: AssistMotion) {
  return animate(element, [
    { width: `${fromWidth}px`, opacity: .35, transform: 'scale(.96)' },
    { width: `${toWidth + 3}px`, opacity: 1, transform: 'scale(1.03)', offset: .7 },
    { width: `${toWidth}px`, opacity: 1, transform: 'none' },
  ], { duration: motion.duration('morph'), fill: 'none' }, motion)
}
/** The afterglow of every accepted change, 1.6 s; reduced motion keeps a short background fade (`glowfade`). */
export function glow(element: Element, motion: AssistMotion) {
  const color = motion.glowColor
  return animate(element, [
    { backgroundColor: color, boxShadow: `0 0 0 3px ${color}` },
    { backgroundColor: 'transparent', boxShadow: '0 0 0 3px transparent' },
  ], { duration: motion.duration('glow') }, motion)
}
/** `reveal`: height and opacity from zero (warning bar 280 ms). */
export function reveal(element: HTMLElement, duration: number, motion: AssistMotion) {
  const height = element.offsetHeight
  return animate(element, [{ height: '0px', opacity: 0, overflow: 'hidden' }, { height: `${height}px`, opacity: 1, overflow: 'hidden' }], { duration, fill: 'none' }, motion)
}
/** `collapse`: back to zero height (warning bar 220 ms, proposal panel 260 ms). */
export function collapse(element: HTMLElement, duration: number, motion: AssistMotion) {
  const height = element.offsetHeight
  return animate(element, [
    { height: `${height}px`, opacity: 1, overflow: 'hidden' },
    { height: '0px', opacity: 0, overflow: 'hidden', marginTop: '0px', marginBottom: '0px', paddingTop: '0px', paddingBottom: '0px' },
  ], { duration }, motion)
}
/** `solidify`: the dashed outline joins into a solid line, dashes 6/5 → 11/0, 360 ms ease-in-out. */
export function solidify(rect: Element, motion: AssistMotion) {
  return animate(rect, [{ strokeDasharray: '6 5', stroke: motion.color('--line-2', '#bdd5da') }, { strokeDasharray: '11 0', stroke: motion.color('--line', '#d4e4e7') }],
    { duration: motion.duration('solidify'), easing: motion.easeInOut }, motion)
}
/** Diff: a deletion is struck through from the left (240 ms). */
export function strike(element: Element, delay: number, motion: AssistMotion) {
  return animate(element, [{ backgroundSize: '0% 1.5px' }, { backgroundSize: '100% 1.5px' }], { duration: motion.scaled(240), delay }, motion)
}
/** Diff: an insertion grows out of a blur (300 ms). */
export function grow(element: Element, delay: number, motion: AssistMotion) {
  return animate(element, [{ opacity: 0, transform: 'translateY(4px)', filter: 'blur(3px)' }, { opacity: 1, transform: 'none', filter: 'blur(0)' }], { duration: motion.scaled(300), delay }, motion)
}
/** Stagger between diff parts. */
export const DIFF_STAGGER = 40
