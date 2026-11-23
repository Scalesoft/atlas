import { motionMs, prefersReducedMotion } from "../motion/aiMotion";

/**
 * Carries co-editors' pointers between their samples (≤ 20 a second, arriving unevenly over the network) with a critically damped
 * spring, one write per frame: the pointer keeps its speed from one sample to the next instead of easing out and stopping at each
 * (what a CSS transition per sample does), so it never jumps or stutters. It trails its person by about --atlas-motion-fast. Under
 * reduced motion, or when the caller says the ground moved under it (a page's layout), it is placed at once.
 */
export interface PointerGlide {
  /** Moves `element` towards (x, y), in the caller's coordinates; a new element starts there. */
  to(element: HTMLElement, x: number, y: number, snap?: boolean): void;
  /** Draws every pointer again where it is (the caller's coordinates map to the screen differently now, e.g. a board's viewport). */
  redraw(): void;
  forget(element: HTMLElement): void;
  stop(): void;
}

interface Glide { x: number; y: number; vx: number; vy: number; tx: number; ty: number }

export function createPointerGlide(motionRoot: () => Element, draw: (element: HTMLElement, x: number, y: number) => void): PointerGlide {
  const glides = new Map<HTMLElement, Glide>();
  let frame = 0;
  let last = 0;
  let smooth = 0;
  const tick = (now: number) => {
    frame = 0;
    // A frame the tab skipped (hidden, busy) does not fling the pointer past its target.
    const dt = Math.min(Math.max(now - last, 0), 50) / 1000;
    last = now;
    for (const [element, glide] of glides) {
      if (glide.x === glide.tx && glide.y === glide.ty) continue;
      [glide.x, glide.vx] = damp(glide.x, glide.tx, glide.vx, smooth, dt);
      [glide.y, glide.vy] = damp(glide.y, glide.ty, glide.vy, smooth, dt);
      if (Math.abs(glide.tx - glide.x) < .25 && Math.abs(glide.ty - glide.y) < .25 && Math.hypot(glide.vx, glide.vy) < 10) {
        Object.assign(glide, { x: glide.tx, y: glide.ty, vx: 0, vy: 0 });
      }
      draw(element, glide.x, glide.y);
    }
    if ([...glides.values()].some(glide => glide.x !== glide.tx || glide.y !== glide.ty)) frame = requestAnimationFrame(tick);
  };
  return {
    to(element, x, y, snap = false) {
      const glide = glides.get(element);
      if (!glide || snap || prefersReducedMotion(element)) {
        glides.set(element, { x, y, vx: 0, vy: 0, tx: x, ty: y });
        draw(element, x, y);
        return;
      }
      glide.tx = x;
      glide.ty = y;
      if (frame) return;
      smooth = Math.max(motionMs(motionRoot(), "--atlas-motion-fast"), 1) / 1000;
      last = performance.now();
      frame = requestAnimationFrame(tick);
    },
    redraw: () => glides.forEach((glide, element) => draw(element, glide.x, glide.y)),
    forget: element => { glides.delete(element); },
    stop() {
      cancelAnimationFrame(frame);
      frame = 0;
      glides.clear();
    },
  };
}

/** One step of a critically damped spring (Game Programming Gems 4, „Critically damped ease-in/ease-out smoothing“); velocity per second. */
export function damp(current: number, target: number, velocity: number, smoothTime: number, dt: number): [number, number] {
  const omega = 2 / smoothTime;
  const x = omega * dt;
  const decay = 1 / (1 + x + .48 * x * x + .235 * x * x * x);
  const change = current - target;
  const temp = (velocity + omega * change) * dt;
  return [target + (change + temp) * decay, (velocity - omega * temp) * decay];
}
