import { joinMotion } from './motionScheduler'
import { trackVisibility } from './visibility'

interface Particle {
  u: number; v: number; x: number; y: number; vx: number; vy: number; radius: number; phase: number
  /** Twinkle tempo, rim depth behind a reveal wave, and whether the wave leaves this speck behind. */
  speed: number; rim: number; stray: boolean; bright: boolean
  /** Which themed glint colour this speck flashes in, and whether its glint flares into a star. */
  tint: number; star: boolean
  /** Own drift in px/s, the current it eases into, and a lifetime: specks glide, fade in, fade out and are reborn elsewhere. */
  du: number; dv: number; cu: number; cv: number; age: number; life: number
  /** Momentum a passing pointer gave the speck's resting place (px per 16 ms), and how long it glides on. */
  ku: number; kv: number; glide: number
  /** Retired after fading out because the device draws fewer specks; revived when detail returns. */
  dormant: boolean
}
// Invisible-ink density: a fine, even grain of tiny specks rather than a few large dots.
// Fewer, slightly larger and brighter specks keep the sparkle visible at a lower drawing cost.
// The shared detail level keeps the total affordable; a large surface only draws the part around the viewport.
const MAX_PARTICLES = 6000
const MIN_PARTICLES = 90
/** A tiny capsule on a slow device still twinkles. */
const MIN_DRAWN = 24
const AREA_PER_PARTICLE = 30
/** A surface larger than the viewport draws a window this much of the viewport beyond each of its edges. */
const WINDOW_MARGIN = .25
/** Longest canvas side in device pixels; the window keeps it well inside browser canvas limits. */
const CANVAS_MAX = 2048
/** The synthetic reveal mask has a fixed size, whatever the surface. */
const MASK_WIDTH = 512
const MASK_HEIGHT = 256
/** Calm, coherent currents: small eddies drifting at about 4.5 px/s, the same flow for neighbouring specks. */
const CURRENT_SPEED = 4.5
const CURRENT_SCALE = .03
/** A pointer passing by pushes specks away and hands them momentum; they glide on and settle elsewhere. */
const PUSH_REACH = 92
const PUSH_KICK = .6
const PUSH_CARRY = .05
/** Frame steps are real elapsed time in 16 ms units, capped at one idle frame so a long gap never jumps. */
const MAX_STEP = 8
/** Theme colours the specks are drawn in, with their fallbacks. */
const PALETTE = [
  ['--concealed-speck', '#ffffff'], ['--concealed-speck-shade', '#6f6a62'],
  ['--concealed-glint-1', '#ffe1a1'], ['--concealed-glint-2', '#ffc9e0'], ['--concealed-glint-3', '#e3d2ff'],
] as const
function lifetime() { return 900 + Math.random() * 2600 }
/** Three slowly moving sine waves: a smooth field of small eddies, cheap enough for thousands of specks. */
function current(x: number, y: number, now: number) {
  return Math.sin(x * CURRENT_SCALE + now * .00021) * 1.7 + Math.cos(y * CURRENT_SCALE * 1.4 - now * .00017) * 1.4
    + Math.sin((x - y) * CURRENT_SCALE * .6 + now * .00011) * 1.1
}
function speck(): Particle {
  const life = lifetime()
  const particle: Particle = {
    u: Math.random(), v: Math.random(), x: 0, y: 0, vx: 0, vy: 0,
    radius: .32 + Math.pow(Math.random(), 2.2) * .7, phase: Math.random() * Math.PI * 2,
    speed: .6 + Math.random() * 1.1, rim: Math.pow(Math.random(), 1.8) * 16, stray: Math.random() < .035, bright: Math.random() < .66,
    tint: Math.floor(Math.random() * 3), star: Math.random() < .16,
    du: 0, dv: 0, cu: 0, cv: 0, age: Math.random() * life, life, ku: 0, kv: 0, glide: .97 + Math.random() * .012, dormant: false,
  }
  return particle
}
function contextOf(target: HTMLCanvasElement) {
  try { return target.getContext('2d') } catch { return null }
}

/** Fixed-size synthetic mask and particle geometry; never receives concealed payload data. */
export function createParticleField(surface: HTMLElement, layer: HTMLElement, onMask: (mask: string) => void, onUnavailable: () => void,
  /** While scratching: the uncovered share of the surface (0–1) and where the stroke is. */
  onUncovered?: (share: number, x: number, y: number) => void) {
  const doc = surface.ownerDocument
  const win = doc.defaultView
  const canvas = doc.createElement('canvas')
  canvas.dataset.concealedCanvas = ''
  canvas.setAttribute('aria-hidden', 'true')
  canvas.width = canvas.height = 0
  layer.append(canvas)
  // Canvases cost memory per surface and browsers cap the total (notably iOS): the display canvas holds pixels only
  // while the surface is near the viewport, and the reveal mask exists only once something is uncovered.
  let context: CanvasRenderingContext2D | null | undefined
  let backed = false
  let mask: { accumulated: HTMLCanvasElement; context: CanvasRenderingContext2D; output: HTMLCanvasElement; outputContext: CanvasRenderingContext2D } | null | undefined
  function maskCanvas() {
    if (mask === undefined) {
      const accumulated = doc.createElement('canvas'); const output = doc.createElement('canvas')
      accumulated.width = output.width = MASK_WIDTH; accumulated.height = output.height = MASK_HEIGHT
      const maskContext = contextOf(accumulated); const outputContext = contextOf(output)
      mask = maskContext && outputContext ? { accumulated, context: maskContext, output, outputContext } : null
    }
    return mask
  }
  function releaseMask() {
    if (mask) mask.accumulated.width = mask.output.width = 0
    if (mask !== null) mask = undefined
  }
  // Specks are created as the surface grows; a small inline secret never pays for the largest field.
  const particles: Particle[] = []
  /** Specks the area calls for, how many the device's detail level keeps, and how many are still drawn while thinning out. */
  let areaCount = MIN_PARTICLES
  let targetCount = 0
  let particleCount = 0
  /** The whole surface; mask, brush and pointer positions use its coordinates. */
  let surfaceWidth = 320
  let surfaceHeight = 160
  /** The drawn window: the whole surface, or for a surface larger than the viewport the part around it. */
  let width = 320
  let height = 160
  let windowX = 0
  let windowY = 0
  let windowed = false
  let placing = false
  let frame = 0
  let disposed = false
  /** Near the viewport; without IntersectionObserver always. */
  let visible = !win?.IntersectionObserver
  let paused = false
  let coverage = 1
  let marked = false
  let lastMark: { x: number; y: number } | undefined
  /** The uncovered bitmap at cover start and a smooth random field deciding where the mist returns first. */
  let erosion: { original: ImageData; field: Float32Array; output: ImageData } | undefined
  let publishedAt = 0
  let publishTimer: ReturnType<typeof setTimeout> | undefined
  let pointer: { x: number; y: number; time: number; denied: boolean; scale?: number; vx: number; vy: number } | undefined
  /** One expanding reveal ring; specks inside it are swept onto a bright snow-plough rim. */
  let wave: { x: number; y: number; radius: number } | undefined
  /** 0–1 while a denied user holds for access: the whole field trembles and bubbles harder. */
  let agitation = 0
  /** Speck colours come from CSS custom properties so hosts can theme the ink. */
  let palette = { speck: '#ffffff', shade: '#6f6a62', glints: ['#ffe1a1', '#ffc9e0', '#e3d2ff'] }
  let paletteRead = false
  // Theme tokens may be color-mix() expressions; hidden probes let the browser resolve them. Their styles never change,
  // so reading them costs no style recalculation.
  const probes = PALETTE.map(([name, fallback]) => {
    const probe = doc.createElement('span')
    probe.hidden = true
    probe.style.color = `var(${name}, ${fallback})`
    layer.append(probe)
    return probe
  })
  function resolveColor(probe: HTMLElement, fallback: string) {
    const value = win?.getComputedStyle?.(probe).color?.trim()
    if (!value) return fallback
    const srgb = /^color\(srgb ([\d.e-]+) ([\d.e-]+) ([\d.e-]+)(?: \/ ([\d.e-]+))?\)$/.exec(value)
    if (!srgb) return value
    const channel = (part: string) => Math.round(Math.max(0, Math.min(1, Number(part))) * 255)
    return `rgba(${channel(srgb[1]!)}, ${channel(srgb[2]!)}, ${channel(srgb[3]!)}, ${srgb[4] ?? 1})`
  }
  function readPalette() {
    paletteRead = true
    const [speckColor, shade, ...glints] = probes.map((probe, i) => resolveColor(probe, PALETTE[i]![1]))
    palette = { speck: speckColor!, shade: shade!, glints }
  }
  function reduced() { return surface.dataset.concealedReducedMotion === 'true' }
  function display() {
    if (context === undefined) context = contextOf(canvas)
    return context
  }
  // Reduced motion keeps the glitter alive by twinkling in place; only travelling motion stops.
  function canAnimate() { return !paused && !disposed && visible && surface.isConnected && doc.visibilityState !== 'hidden' && !!display() }
  /** Gives the display canvas pixels for the drawn window, in the document's batches (see visibility.ts). */
  function back() {
    const ratio = Math.min(win?.devicePixelRatio || 1, 2, CANVAS_MAX / width, CANVAS_MAX / height)
    canvas.width = Math.max(1, Math.round(width * ratio)); canvas.height = Math.max(1, Math.round(height * ratio))
    display()?.setTransform(ratio, 0, 0, ratio, 0, 0)
    backed = true
    layer.dataset.concealedBacked = ''
  }
  function unback() {
    if (!backed) return
    canvas.width = canvas.height = 0; backed = false
    delete layer.dataset.concealedBacked
  }
  function publish() {
    clearTimeout(publishTimer); publishTimer = undefined; publishedAt = Date.now()
    // Covering always applies at once and invalidates any mask that is still decoding.
    const target = marked ? maskCanvas() : undefined
    if (!target) { maskGeneration++; onMask('linear-gradient(transparent, transparent)'); return }
    const { outputContext } = target
    outputContext.clearRect(0, 0, MASK_WIDTH, MASK_HEIGHT)
    outputContext.globalAlpha = coverage
    outputContext.drawImage(target.accumulated, 0, 0)
    outputContext.globalAlpha = 1
    // Preserve a quiet six-pixel glitter rim without inspecting the payload. An inline capsule is barely taller
    // than its text, so a rim there would sit on the uncovered value itself.
    const rim = surface.dataset.concealedLayout === 'inline' ? 0 : 6
    const rimX = rim * MASK_WIDTH / surfaceWidth
    const rimY = rim * MASK_HEIGHT / surfaceHeight
    outputContext.clearRect(0, 0, rimX, MASK_HEIGHT); outputContext.clearRect(MASK_WIDTH - rimX, 0, rimX, MASK_HEIGHT)
    outputContext.clearRect(0, 0, MASK_WIDTH, rimY); outputContext.clearRect(0, MASK_HEIGHT - rimY, MASK_WIDTH, rimY)
    emitMask(target.output.toDataURL('image/png'))
    if (measuring) { measuring = false; measure() }
  }
  /** After a scratch: how much of the fixed-size synthetic mask is open, sampled on a sparse grid. */
  let measuring = false
  function measure() {
    const maskContext = mask?.context
    if (!onUncovered || !maskContext || !lastMark || typeof maskContext.getImageData !== 'function') return
    let data: Uint8ClampedArray
    try { data = maskContext.getImageData(0, 0, MASK_WIDTH, MASK_HEIGHT).data } catch { return }
    let open = 0; let samples = 0
    for (let y = 2; y < MASK_HEIGHT; y += 4) for (let x = 2; x < MASK_WIDTH; x += 4) { samples++; if (data[(y * MASK_WIDTH + x) * 4 + 3]! > 96) open++ }
    onUncovered(open / samples, lastMark.x, lastMark.y)
  }
  let maskGeneration = 0
  /** A new mask image is applied only once decoded; swapping in an undecoded image made the reveal flicker. */
  function emitMask(url: string) {
    const generation = ++maskGeneration
    const value = `url("${url}")`
    const Picture = win?.Image
    const picture = Picture ? new Picture() : undefined
    if (!picture || typeof picture.decode !== 'function') { onMask(value); return }
    picture.src = url
    const apply = () => { if (generation === maskGeneration && !disposed) onMask(value) }
    picture.decode().then(apply, apply)
  }
  // Encoding the mask is the costly part of scratching; coalesce bursts of pointer moves.
  function schedulePublish() {
    if (Date.now() - publishedAt >= 40) { publish(); return }
    publishTimer ??= setTimeout(publish, 40)
  }
  function spot(x: number, y: number, scale = 1) {
    const maskContext = maskCanvas()?.context
    if (!maskContext) return
    // A large brush with a wide, feathered edge: the centre clears, the fog fades out gently around it.
    const size = brushRadius() * (.88 + Math.random() * .24) * scale
    maskContext.save()
    maskContext.translate(x * MASK_WIDTH / surfaceWidth, y * MASK_HEIGHT / surfaceHeight)
    maskContext.scale(size * MASK_WIDTH / surfaceWidth, size * .9 * MASK_HEIGHT / surfaceHeight)
    const gradient = maskContext.createRadialGradient(0, 0, 0, 0, 0, 1)
    gradient.addColorStop(0, 'rgba(0, 0, 0, .55)'); gradient.addColorStop(.3, 'rgba(0, 0, 0, .45)')
    gradient.addColorStop(.65, 'rgba(0, 0, 0, .16)'); gradient.addColorStop(1, 'rgba(0, 0, 0, 0)')
    maskContext.fillStyle = gradient
    maskContext.fillRect(-1, -1, 2, 2)
    maskContext.restore()
  }
  /** Brush radius grows only slightly with the surface, within firm limits. */
  function brushRadius() { return Math.min(110, Math.max(44, Math.sqrt(surfaceWidth * surfaceHeight) * .2)) }
  /** Prepare the fog to roll back along the uncovered path. False when pixels cannot be read. */
  function startCover(fillAll: boolean) {
    const maskContext = maskCanvas()?.context
    if (!maskContext || typeof maskContext.getImageData !== 'function' || typeof maskContext.putImageData !== 'function') return false
    if (fillAll) { maskContext.fillStyle = 'black'; maskContext.fillRect(0, 0, MASK_WIDTH, MASK_HEIGHT); marked = true }
    let original: ImageData
    try { original = maskContext.getImageData(0, 0, MASK_WIDTH, MASK_HEIGHT) } catch { return false }
    // A coarse random grid, smoothly interpolated, plus a gentle sweep from a random side: the mist
    // drifts in as one soft, continuous front — no discrete blobs or rings.
    const gridX = 7; const gridY = 4
    const grid = Float32Array.from({ length: (gridX + 1) * (gridY + 1) }, () => Math.random())
    const angle = Math.random() * Math.PI * 2; const dirX = Math.cos(angle); const dirY = Math.sin(angle)
    const smooth = (t: number) => t * t * (3 - 2 * t)
    const field = new Float32Array(MASK_WIDTH * MASK_HEIGHT)
    let min = Infinity; let max = -Infinity
    for (let y = 0; y < MASK_HEIGHT; y++) {
      const gy = y / (MASK_HEIGHT - 1) * gridY; const row = Math.min(gridY - 1, Math.floor(gy)); const ty = smooth(gy - row)
      for (let x = 0; x < MASK_WIDTH; x++) {
        const gx = x / (MASK_WIDTH - 1) * gridX; const column = Math.min(gridX - 1, Math.floor(gx)); const tx = smooth(gx - column)
        const at = (c: number, r: number) => grid[r * (gridX + 1) + c]!
        const top = at(column, row) + (at(column + 1, row) - at(column, row)) * tx
        const bottom = at(column, row + 1) + (at(column + 1, row + 1) - at(column, row + 1)) * tx
        const noise = top + (bottom - top) * ty
        const sweep = (x / MASK_WIDTH - .5) * dirX + (y / MASK_HEIGHT - .5) * dirY
        const value = noise * .55 + sweep * .45
        field[y * MASK_WIDTH + x] = value
        if (value < min) min = value
        if (value > max) max = value
      }
    }
    const span = max - min || 1
    for (let i = 0; i < field.length; i++) field[i] = (field[i]! - min) / span
    erosion = { original, field, output: maskContext.createImageData(MASK_WIDTH, MASK_HEIGHT) }
    return true
  }
  /** Progress 0–1: a wide, soft threshold band glides through the field and slowly thickens the fog. */
  function erode(progress: number) {
    const maskContext = mask?.context
    if (!erosion || !maskContext) return
    const band = .5
    const threshold = Math.min(1, progress) * (1 + band) - band
    const source = erosion.original.data; const target = erosion.output.data; const field = erosion.field
    for (let i = 0, p = 3; i < field.length; i++, p += 4) {
      const alpha = source[p]!
      if (!alpha) { target[p] = 0; continue }
      const k = Math.max(0, Math.min(1, (field[i]! - threshold) / band))
      target[p] = alpha * k * k * (3 - 2 * k)
    }
    maskContext.putImageData(erosion.output, 0, 0)
    publish()
  }
  function circle(x: number, y: number, radius: number) {
    const maskContext = maskCanvas()?.context
    if (!maskContext || radius <= 0) return
    maskContext.save()
    maskContext.translate(x * MASK_WIDTH / surfaceWidth, y * MASK_HEIGHT / surfaceHeight)
    maskContext.scale(radius * MASK_WIDTH / surfaceWidth, radius * MASK_HEIGHT / surfaceHeight)
    const gradient = maskContext.createRadialGradient(0, 0, 0, 0, 0, 1)
    gradient.addColorStop(0, 'black'); gradient.addColorStop(Math.max(0, 1 - 10 / radius), 'black'); gradient.addColorStop(1, 'transparent')
    maskContext.fillStyle = gradient
    maskContext.fillRect(-1, -1, 2, 2)
    maskContext.restore()
  }
  function settle(particle: Particle) { particle.x = particle.u * width; particle.y = particle.v * height; particle.vx = 0; particle.vy = 0 }
  function reborn(particle: Particle) {
    particle.u = Math.random(); particle.v = Math.random(); particle.age = 0; particle.life = lifetime(); particle.dormant = false
    particle.ku = 0; particle.kv = 0
    settle(particle)
  }
  function travel(particle: Particle, index: number, dt: number, now: number) {
    // Like glitter suspended in water: each speck eases into the local current, and neighbours share it. The current
    // changes slowly, so each speck looks it up every fourth frame, in turns.
    const step = dt / 16
    if ((index + frame) % 4 === 0) {
      const angle = current(windowX + particle.u * width, windowY + particle.v * height, now)
      particle.cu = Math.cos(angle) * CURRENT_SPEED; particle.cv = Math.sin(angle) * CURRENT_SPEED
    }
    particle.du += (particle.cu - particle.du) * .03 * step
    particle.dv += (particle.cv - particle.dv) * .03 * step
    // Momentum from a passing pointer carries the resting place on, fading with the speck's own glide.
    if (particle.ku || particle.kv) {
      const glide = Math.pow(particle.glide, step)
      particle.ku *= glide; particle.kv *= glide
      if (Math.abs(particle.ku) + Math.abs(particle.kv) < .002) { particle.ku = 0; particle.kv = 0 }
    }
    particle.u += (particle.du * dt / 1000 + particle.ku * step) / width; particle.v += (particle.dv * dt / 1000 + particle.kv * step) / height
    if (particle.u < 0 || particle.u >= 1 || particle.v < 0 || particle.v >= 1) {
      particle.u = (particle.u + 1) % 1; particle.v = (particle.v + 1) % 1
      settle(particle)
    }
  }
  /** Scrolling moves the window over a large surface: specks keep their place on the page, and those it leaves
   *  behind wrap round into the part it uncovers, which is still off screen in its margin. */
  function shift(dx: number, dy: number) {
    const jump = Math.abs(dx) >= width || Math.abs(dy) >= height
    for (const particle of particles) {
      if (jump) { particle.u = Math.random(); particle.v = Math.random(); settle(particle); continue }
      particle.u -= dx / width; particle.v -= dy / height; particle.x -= dx; particle.y -= dy
      if (particle.u < 0 || particle.u >= 1 || particle.v < 0 || particle.v >= 1) {
        particle.u = (particle.u + 1) % 1; particle.v = (particle.v + 1) % 1
        settle(particle)
      }
    }
  }
  /** Centres the window on the viewport, within the surface. */
  function place() {
    placing = false
    const rect = surface.getBoundingClientRect()
    const clamp = (value: number, max: number) => Math.max(0, Math.min(max, Math.round(value)))
    const x = clamp(-rect.left - (width - (win?.innerWidth || width)) / 2, surfaceWidth - width)
    const y = clamp(-rect.top - (height - (win?.innerHeight || height)) / 2, surfaceHeight - height)
    if (x === windowX && y === windowY) return
    shift(x - windowX, y - windowY)
    windowX = x; windowY = y
    canvas.style.transform = `translate(${x}px, ${y}px)`
    // The colour clouds behind the specks follow the window, so a long surface keeps its soft light everywhere.
    layer.style.setProperty('--concealed-window-x', `${x}px`); layer.style.setProperty('--concealed-window-y', `${y}px`)
  }
  function draw(now: number, step = 0) {
    const ctx = context
    if (!ctx || !backed) return
    frame++
    ctx.clearRect(0, 0, width, height)
    const drifting = step > 0 && !reduced()
    const dt = step * 16
    // Without access the specks never part; the entire field trembles, unpleasantly and a bit faster.
    const tremor = drifting && surface.dataset.concealedDenied === 'true' ? .7 + agitation * 2.4 : 0
    // Positions from the host are in surface coordinates; the specks live in the drawn window.
    const push = pointer && !wave ? pointer : undefined
    const pushX = push ? push.x - windowX : 0
    const pushY = push ? push.y - windowY : 0
    const pushScale = push?.scale ?? 1
    const brush = Math.max(PUSH_REACH, brushRadius() * 1.1)
    const waveX = wave ? wave.x - windowX : 0
    const waveY = wave ? wave.y - windowY : 0
    // Per-frame constants of the springs, and the clocks of sway, shimmer and glint.
    const damp = Math.pow(.73, step); const pull = .04 * step; const ride = Math.min(1, .55 * step)
    const swayX = now * .0011; const swayY = now * .0013; const shimmerAt = now * .0034; const glintAt = now * .0036
    let fill = ''
    for (let i = 0; i < particleCount; i++) {
      const particle = particles[i]!
      if (particle.dormant) { if (i >= targetCount) continue; reborn(particle) }
      if (dt > 0) {
        particle.age += dt
        // A reveal wave keeps its rim population; everywhere else specks are continuously reborn. Specks beyond a
        // lowered count retire once faded out, so the field thins without a single speck vanishing mid-glint.
        if (particle.age >= particle.life && !wave) { if (i >= targetCount) { particle.dormant = true; continue } reborn(particle) }
        else if (drifting && !wave) travel(particle, i, dt, now)
      }
      // A small sway on top of the drift keeps neighbouring specks from moving in lockstep.
      let targetX = particle.u * width + (drifting ? Math.sin(swayX * particle.speed + particle.phase) * 1.2 : 0)
      let targetY = particle.v * height + (drifting ? Math.cos(swayY * particle.speed + particle.phase * 2) : 0)
      let swept = false
      if (wave && !particle.stray) {
        const dx = targetX - waveX; const dy = targetY - waveY
        const distance = Math.hypot(dx, dy)
        if (distance < wave.radius + particle.rim) {
          const angle = distance < .1 ? particle.phase : Math.atan2(dy, dx)
          targetX = waveX + Math.cos(angle) * (wave.radius + particle.rim)
          targetY = waveY + Math.sin(angle) * (wave.radius + particle.rim)
          swept = true
        }
      }
      if (push) {
        if (drifting && !push.denied) {
          // Pushing hands the speck momentum instead of a temporary offset: it glides on and settles elsewhere,
          // each a little differently. A resting pointer keeps pushing; a moving one also drags specks along.
          const dx = particle.x - pushX; const dy = (particle.y - pushY) / .78
          const distance = Math.hypot(dx, dy)
          const reach = brush * pushScale
          if (distance < reach) {
            const fall = (reach - distance) / reach
            const angle = (distance < .1 ? particle.phase : Math.atan2(dy, dx)) + (Math.random() - .5) * .5
            const kick = fall * fall * PUSH_KICK * (.6 + Math.random() * .8) * step
            particle.ku += Math.cos(angle) * kick + push.vx * fall * PUSH_CARRY * step
            particle.kv += Math.sin(angle) * kick * .78 + push.vy * fall * PUSH_CARRY * step
          }
        } else {
          // Wiping and denied hovering share one motion: specks drift apart from the pointer gradually, on the
          // same soft spring as the rest of the glitter. Only a denied pointer adds the unpleasant jitter.
          // The push holds as long as the pointer rests on the surface; specks drift back only after it leaves.
          const dx = targetX - pushX; const dy = (targetY - pushY) / .78
          const distance = Math.hypot(dx, dy)
          const reach = (push.denied ? 92 * (1 + agitation * .7) : Math.max(92, brushRadius() * 1.1)) * pushScale
          if (distance < reach) {
            const angle = distance < .1 ? particle.phase : Math.atan2(dy, dx)
            const force = (reach - distance) * .9
            const jitter = push.denied ? Math.sin(now * .026 + particle.phase) * 7 * (1 + agitation * 1.5) : 0
            targetX += Math.cos(angle) * force - Math.sin(angle) * jitter
            targetY += Math.sin(angle) * force * .78 + Math.cos(angle) * jitter
          }
        }
      }
      if (step > 0) {
        if (swept) {
          // The wave front outruns a spring, so swept specks ride it directly and stay a crisp rim.
          particle.x += (targetX - particle.x) * ride; particle.y += (targetY - particle.y) * ride
          particle.vx = 0; particle.vy = 0
        } else {
          particle.vx = (particle.vx + (targetX - particle.x) * pull) * damp
          particle.vy = (particle.vy + (targetY - particle.y) * pull) * damp
          particle.x += particle.vx * step; particle.y += particle.vy * step
        }
      }
      // Soft shimmer plus rare sharp glints; the rim of a reveal wave catches the most light.
      const shimmer = (Math.sin(shimmerAt * particle.speed + particle.phase) + 1) * .5
      let glint = 0
      if (particle.bright) {
        // sin¹⁴: only the very top of each swing flashes, so the power is skipped below it.
        const swing = Math.sin(glintAt * particle.speed + particle.phase * 3)
        if (swing > .5) { const s2 = swing * swing; const s4 = s2 * s2; glint = s4 * s4 * s4 * s2 }
      }
      const inside = wave && particle.stray && Math.hypot(particle.x - waveX, particle.y - waveY) < wave.radius
      // Each speck fades in and out over its life, so the field keeps sparkling as specks come and go.
      const envelope = swept ? 1 : Math.sin(Math.PI * Math.min(1, particle.age / particle.life))
      const lit = swept
      // A speck gliding after a push catches a little more light, so the motion reads even in dense ink.
      const moving = particle.ku || particle.kv ? Math.min(1, Math.sqrt(particle.ku * particle.ku + particle.kv * particle.kv) * .35) : 0
      ctx.globalAlpha = Math.min(1, (.4 + shimmer * .45 + glint * .6 + (lit ? .35 : 0) + moving * .3 + agitation * .2) * (inside ? .45 : 1) * (lit ? 1 : .18 + envelope * .82))
      const color = particle.bright || lit || moving > .4 ? palette.speck : palette.shade
      if (color !== fill) { ctx.fillStyle = color; fill = color }
      const size = particle.radius * 2 * (1 + glint * .9 + (lit ? .25 : 0) + moving * .25)
      // Tremor is applied at draw time so the spring cannot smooth its high frequency away.
      const shakeX = tremor ? Math.sin(now * .031 * particle.speed + particle.phase * 11) * tremor : 0
      const shakeY = tremor ? Math.cos(now * .027 * particle.speed + particle.phase * 13) * tremor : 0
      ctx.fillRect(particle.x + shakeX - size / 2, particle.y + shakeY - size / 2, size, size)
      if (particle.star && glint > .55) {
        // At its peak a speck flares into a tiny coloured four-point star, like glitter catching light.
        const arm = size * (1.2 + glint * 2.6); const thin = Math.max(.45, size * .22)
        const cx = particle.x + shakeX; const cy = particle.y + shakeY
        ctx.globalAlpha = Math.min(1, glint * (.12 + envelope * .88))
        fill = palette.glints[particle.tint]!
        ctx.fillStyle = fill
        ctx.fillRect(cx - arm, cy - thin / 2, arm * 2, thin)
        ctx.fillRect(cx - thin / 2, cy - arm, thin, arm * 2)
      }
    }
    ctx.globalAlpha = 1
    while (particleCount > targetCount && particles[particleCount - 1]!.dormant) particleCount--
  }
  /** Sizes the field for its area and the detail level; added specks start dark and fade in like reborn ones. */
  function fit(fresh: boolean) {
    targetCount = Math.max(Math.min(MIN_DRAWN, areaCount), Math.min(areaCount, Math.round(areaCount * detail)))
    while (particles.length < targetCount) particles.push(speck())
    for (let i = particleCount; i < targetCount; i++) { if (fresh) reborn(particles[i]!); else particles[i]!.dormant = false }
    particleCount = Math.max(particleCount, targetCount)
  }
  // The shared loop decides when this field draws; theme colours are re-read only at its calm moments.
  const motion = joinMotion(doc, (now, elapsed, restyle, level) => {
    if (!canAnimate()) { motion.stop(); if (!surface.isConnected || !visible) onUnavailable(); return }
    if (!backed) back()
    if (restyle || !paletteRead) readPalette()
    if (placing) place()
    if (level !== detail) { detail = level; fit(true) }
    draw(now, Math.min(MAX_STEP, elapsed / 16))
  }, reduced)
  let detail = motion.detail()
  function wake() { paused = false; if (canAnimate()) motion.wake() }
  /** New surface size, from the resize observer (no forced layout) or the viewport changing around a large surface. */
  function resize(surfaceSize?: { width: number; height: number }) {
    if (disposed) return
    if (surfaceSize) { surfaceWidth = Math.max(1, surfaceSize.width || 320); surfaceHeight = Math.max(1, surfaceSize.height || 160) }
    const viewportWidth = win?.innerWidth || surfaceWidth; const viewportHeight = win?.innerHeight || surfaceHeight
    width = Math.min(surfaceWidth, Math.ceil(viewportWidth * (1 + 2 * WINDOW_MARGIN)))
    height = Math.min(surfaceHeight, Math.ceil(viewportHeight * (1 + 2 * WINDOW_MARGIN)))
    const large = width < surfaceWidth || height < surfaceHeight
    if (large !== windowed) {
      windowed = large
      if (large) doc.addEventListener('scroll', scrolled, { capture: true, passive: true })
      else doc.removeEventListener('scroll', scrolled, { capture: true })
    }
    windowX = windowY = 0
    canvas.style.transform = ''
    canvas.style.width = windowed ? `${width}px` : ''; canvas.style.height = windowed ? `${height}px` : ''
    for (const [name, value] of [['x', '0px'], ['y', '0px'], ['w', `${width}px`], ['h', `${height}px`]]) {
      if (windowed) layer.style.setProperty(`--concealed-window-${name}`, value); else layer.style.removeProperty(`--concealed-window-${name}`)
    }
    placing = windowed
    areaCount = Math.min(MAX_PARTICLES, Math.max(MIN_PARTICLES, Math.round(width * height / AREA_PER_PARTICLE)))
    fit(false)
    for (const particle of particles) settle(particle)
    if (backed) back()
    if (backed && canAnimate()) { if (!paletteRead) readPalette(); if (placing) place(); draw(Date.now()) }
    publish(); wake()
  }
  function scrolled() { placing = true; wake() }
  function viewport() { resize() }
  /** Pointer position plus its velocity in px per 16 ms, which a push partly hands on to the specks. */
  function point(x: number, y: number, denied: boolean) {
    const time = Date.now()
    const previous = pointer
    const elapsed = previous ? Math.max(8, time - previous.time) : 16
    const vx = previous && elapsed < 120 ? (x - previous.x) / elapsed * 16 : 0
    const vy = previous && elapsed < 120 ? (y - previous.y) / elapsed * 16 : 0
    pointer = { x, y, time, denied, vx, vy }
  }
  function move(x: number, y: number, denied: boolean) {
    // Scratching is direct manipulation, not autonomous motion, so it works with reduced motion too.
    if (disposed) return
    point(x, y, denied)
    if (!denied && maskCanvas()) {
      const spacing = brushRadius() / 5
      const steps = lastMark ? Math.min(48, Math.max(1, Math.ceil(Math.hypot(x - lastMark.x, y - lastMark.y) / spacing))) : 1
      for (let i = 1; i <= steps; i++) spot(lastMark ? lastMark.x + (x - lastMark.x) * i / steps : x, lastMark ? lastMark.y + (y - lastMark.y) * i / steps : y)
      lastMark = { x, y }; marked = true; measuring = true; schedulePublish()
    }
    wake()
  }
  function reset() {
    pointer = undefined; wave = undefined; lastMark = undefined; marked = false; coverage = 1; erosion = undefined
    releaseMask()
    publish()
  }
  const Resize = win?.ResizeObserver
  const resizeObserver = Resize ? new Resize(entries => {
    const box = entries.at(-1)?.borderBoxSize?.[0]
    if (box) resize({ width: box.inlineSize, height: box.blockSize })
    else { const rect = surface.getBoundingClientRect(); resize({ width: rect.width, height: rect.height }) }
  }) : undefined
  resizeObserver?.observe(surface)
  // The document shares canvas pixels out in batches; the ink draws from just before it scrolls into view.
  const tracking = trackVisibility(doc, surface, {
    near(near) {
      if (near === visible) return
      visible = near
      if (visible) { placing = windowed; wake() } else { motion.stop(); onUnavailable() }
    },
    backed: () => backed,
    back() { if (!disposed) back() },
    unback() {
      unback()
      // Far off screen the colour clouds may return to rest unseen, and their animation layer goes (see style.css).
      delete surface.dataset.concealedAurora
    },
  })
  function visibility() { if (doc.visibilityState === 'hidden') motion.stop(); else wake() }
  // A system theme switch recolours the ink at once; host theme switches are caught by the periodic re-read.
  const scheme = win?.matchMedia?.('(prefers-color-scheme: dark)')
  win?.addEventListener('resize', viewport)
  doc.addEventListener('visibilitychange', visibility)
  scheme?.addEventListener?.('change', readPalette)
  if (!tracking) back()
  resize({ width: 0, height: 0 })
  /** A pointer passing over the ink stirs the specks without uncovering anything (explicit-release surfaces). */
  function stir(x: number, y: number) {
    if (disposed) return
    point(x, y, false); wake()
  }
  return {
    move, stir, reset, wake,
    /** Grow the reveal ring to `radius` around a synthetic origin. */
    ripple(x: number, y: number, radius: number) {
      if (disposed || reduced()) return
      wave = { x, y, radius }
      circle(x, y, radius)
      marked = true; publish(); wake()
    },
    endStroke() { lastMark = undefined; pointer = undefined; wave = undefined },
    setAgitation(value: number) { agitation = Math.max(0, Math.min(1, value)); if (agitation) wake() },
    startCover,
    setCoverage(value: number) {
      const next = Math.max(0, Math.min(1, value))
      // With a rolling fog the bitmap itself is eroded; otherwise the whole reveal fades.
      if (erosion) { if (next === 1) erosion = undefined; else { erode(1 - next); return } }
      coverage = next; publish()
    },
    pause() { paused = true; motion.stop() },
    destroy() {
      if (disposed) return
      disposed = true; motion.remove(); pointer = undefined
      clearTimeout(publishTimer); publishTimer = undefined
      resizeObserver?.disconnect(); if (tracking) tracking.untrack()
      win?.removeEventListener('resize', viewport); doc.removeEventListener('visibilitychange', visibility)
      doc.removeEventListener('scroll', scrolled, { capture: true })
      scheme?.removeEventListener?.('change', readPalette)
      canvas.remove(); unback(); releaseMask()
    },
  }
}
