// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest'
import {
  animateMarkdownMotion, markdownConcealDefaults, markdownConcealMotion, markdownMotion, markdownMotionDefaults, markdownMotionTokens, markdownScrollBehavior,
  MARKDOWN_CONCEAL_EASE, MARKDOWN_MOTION_EASE, MARKDOWN_MOTION_EASE_IN_OUT, REDUCED_MOTION_MAX_MS,
} from './motion'
import { dragMotion } from '../features/drag/motion'
import { copyMarkdownOverlayTheme } from './overlayTheme'

type Call = { keyframes: Keyframe[]; options: KeyframeAnimationOptions }
let calls: Call[] = []
function element(style: Record<string, string> = {}) {
  const node = document.createElement('div')
  for (const [name, value] of Object.entries(style)) node.style.setProperty(name, value)
  node.animate = vi.fn((keyframes: Keyframe[] | PropertyIndexedKeyframes | null, options?: number | KeyframeAnimationOptions) => {
    calls.push({ keyframes: keyframes as Keyframe[], options: options as KeyframeAnimationOptions })
    return { finished: Promise.resolve(), cancel() {} } as unknown as Animation
  }) as typeof node.animate
  document.body.append(node)
  return node
}
function reduceMedia(matches: boolean) {
  vi.stubGlobal('matchMedia', (query: string) => ({ matches: matches && query.includes('reduce'), media: query, addEventListener() {}, removeEventListener() {} }))
}
afterEach(() => {
  calls = []
  document.body.replaceChildren()
  document.documentElement.removeAttribute('data-md-motion')
  vi.unstubAllGlobals()
})

describe('markdownMotion', () => {
  it('falls back to the prototype values where no stylesheet resolves the tokens', () => {
    const motion = markdownMotion(element())
    expect(Object.fromEntries(Object.keys(markdownMotionDefaults).map(name => [name, motion.duration(name as keyof typeof markdownMotionDefaults)]))).toEqual(markdownMotionDefaults)
    expect(markdownMotionDefaults).toEqual({ fast: 120, base: 200, slow: 280, menu: 140, target: 160, pop: 320, glow: 1600, loop: 1600, spin: 800, pulse: 1000 })
    expect(motion).toMatchObject({ reduced: false, ease: MARKDOWN_MOTION_EASE, easeInOut: MARKDOWN_MOTION_EASE_IN_OUT, rise: 4 })
    expect(markdownMotion(null).duration('base')).toBe(200)
  })

  it('reads the tokens a host remapped, in ms or s', () => {
    const motion = markdownMotion(element({ '--md-motion-base': '.6s', '--md-motion-target': '480ms', '--md-motion-ease': 'linear', '--md-motion-rise': '0px' }))
    expect(motion.duration('base')).toBe(600)
    expect(motion.duration('target')).toBe(480)
    expect(motion.ease).toBe('linear')
    expect(motion.rise).toBe(0)
  })

  it('multiplies only fixed prototype literals by the time scale: tokens already carry a host slow-down', () => {
    const motion = markdownMotion(element({ '--md-motion-time-scale': '3', '--md-motion-slow': '840ms' }))
    expect(motion.duration('slow')).toBe(840)
    expect(motion.duration('fast')).toBe(120)
    expect(motion.scaled(600)).toBe(1800)
    expect(markdownMotion(element({ '--md-motion-time-scale': '0' })).scaled(600)).toBe(600)
  })

  it('is reduced by the media query, the data-md-motion attribute or the host', () => {
    expect(markdownMotion(element()).reduced).toBe(false)
    expect(markdownMotion(element(), () => true).reduced).toBe(true)
    reduceMedia(true)
    expect(markdownMotion(element()).reduced).toBe(true)
    reduceMedia(false)
    document.documentElement.dataset.mdMotion = 'reduced'
    expect(markdownMotion(element()).reduced).toBe(true)
  })

  it('lists every token, and overlays outside the editor receive them', () => {
    expect(markdownMotionTokens).toEqual(expect.arrayContaining([...Object.keys(markdownMotionDefaults).map(name => `--md-motion-${name}`),
      '--md-motion-ease', '--md-motion-ease-io', '--md-motion-rise', '--md-motion-pop-from', '--md-motion-time-scale']))
    const source = element({ '--md-motion-base': '600ms', '--md-motion-ease-io': 'linear' }), overlay = document.createElement('div')
    copyMarkdownOverlayTheme(source, overlay)
    expect(overlay.style.getPropertyValue('--md-motion-base')).toBe('600ms')
    expect(overlay.style.getPropertyValue('--md-motion-ease-io')).toBe('linear')
    // A value the editor shares with the document stays inherited, so the overlay follows a later reduced switch.
    document.documentElement.style.setProperty('--md-motion-base', '600ms')
    copyMarkdownOverlayTheme(source, overlay)
    expect(overlay.style.getPropertyValue('--md-motion-base')).toBe('')
    expect(overlay.style.getPropertyValue('--md-motion-ease-io')).toBe('linear')
    document.documentElement.style.removeProperty('--md-motion-base')
  })
})

describe('animateMarkdownMotion', () => {
  const frames: Keyframe[] = [{ opacity: 0, transform: 'translateY(8px) scale(.9)', backgroundColor: 'red' }, { opacity: 1, transform: 'none', backgroundColor: 'transparent' }]

  it('plays the keyframes with fill both and the ease token', () => {
    const target = element()
    animateMarkdownMotion(target, frames, { duration: 280 }, markdownMotion(target))
    expect(calls[0]).toEqual({ keyframes: frames, options: { fill: 'both', easing: MARKDOWN_MOTION_EASE, duration: 280 } })
  })

  it('keeps only opacity and colour under reduced motion, at most 160 ms and linear', () => {
    const target = element()
    animateMarkdownMotion(target, frames, { duration: 420, delay: 60 }, markdownMotion(target, () => true))
    expect(calls[0]!.keyframes).toEqual([{ opacity: 0, backgroundColor: 'red' }, { opacity: 1, backgroundColor: 'transparent' }])
    expect(calls[0]!.options).toMatchObject({ duration: REDUCED_MOTION_MAX_MS, easing: 'linear' })
    // Pure movement becomes a still frame instead of a jump.
    animateMarkdownMotion(target, [{ transform: 'translateY(4px)' }, { transform: 'none' }], { duration: 120 }, markdownMotion(target, () => true))
    expect(calls[1]).toMatchObject({ keyframes: [{ opacity: 1 }, { opacity: 1 }], options: { duration: 120, easing: 'linear' } })
    // The 160 ms ceiling slows down with the time scale, like the prototype's `anim()`.
    const slow = element({ '--md-motion-time-scale': '3' })
    animateMarkdownMotion(slow, frames, { duration: 900 }, markdownMotion(slow, () => true))
    expect(calls[2]!.options.duration).toBe(480)
  })

  it('applies the end state at once without Web Animations', async () => {
    const plain = document.createElement('div')
    const animation = animateMarkdownMotion(plain, frames, { duration: 200 }, markdownMotion(plain))
    await expect(animation.finished).resolves.toBeUndefined()
    expect(animateMarkdownMotion(null, frames, { duration: 200 }, markdownMotion(null))).toBeDefined()
  })
})

describe('shared helpers', () => {
  it('scrolls smoothly only without reduced motion', () => {
    expect(markdownScrollBehavior(element())).toBe('smooth')
    expect(markdownScrollBehavior(element(), () => true)).toBe('auto')
    document.documentElement.dataset.mdMotion = 'reduced'
    expect(markdownScrollBehavior(element())).toBe('auto')
  })

  it('drags with the base, target and ease tokens of the editor', () => {
    expect(dragMotion(element())).toMatchObject({ duration: 200, returnDuration: 160, easing: MARKDOWN_MOTION_EASE, reduced: false })
    expect(dragMotion(element({ '--md-motion-base': '600ms', '--md-motion-target': '480ms', '--md-motion-ease': 'linear' }))).toMatchObject({ duration: 600, returnDuration: 480, easing: 'linear' })
    expect(dragMotion(element(), () => true).reduced).toBe(true)
  })
})

describe('markdownConcealMotion', () => {
  it('reads the --md-conceal-* group, with the defaults of the stylesheet as fallback', () => {
    reduceMedia(false)
    const plain = markdownConcealMotion(element())
    expect(plain.duration('unfold')).toBe(markdownConcealDefaults.unfold)
    expect(plain.ease).toBe(MARKDOWN_CONCEAL_EASE)
    const host = markdownConcealMotion(element({ '--md-conceal-unfold': '2.7s', '--md-conceal-ease': 'ease-in' }))
    expect(host.duration('unfold')).toBe(2700)
    expect(host.ease).toBe('ease-in')
  })

  it('keeps entries within 160 ms and linear under reduced motion, and stands still without a stagger or aurora', () => {
    reduceMedia(true)
    const reduced = markdownConcealMotion(element({ '--md-conceal-unfold': '900ms' }))
    expect(reduced.reduced).toBe(true)
    expect(reduced.duration('unfold')).toBe(REDUCED_MOTION_MAX_MS)
    expect(reduced.duration('fast')).toBe(REDUCED_MOTION_MAX_MS)
    expect(reduced.duration('stagger')).toBe(0)
    expect(reduced.duration('aurora')).toBe(0)
    expect(reduced.ease).toBe('linear')
    expect(reduced.easeRoll).toBe('linear')
  })
})
