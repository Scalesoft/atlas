import { getCurrentInstance, onBeforeUnmount, ref, watch, type Ref } from "vue";
import { forceReducedMotion, MOTION_SCALE_PROPERTY, prefersReducedMotion } from "../../motion/aiMotion";

/**
 * The gallery's two motion switches, the prototype's "Zpomalit 3×" and "Omezený pohyb" (motion unification
 * plan §1.1 rule 5, §6.1): one state for every gallery page. ComponentGallery shows them in its header and
 * applies them to the document root, so they reach the whole UI, including Reka portals and body-level
 * overlays: `--atlas-motion-scale: 3` slows every motion token (motion.css) and the prototype literals of
 * aiMotion.ts, `forceReducedMotion` (as the profile's Omezit animace) forces the reduced path of Atlas and the editor.
 * Pages read them to replay their scenes. Gallery-only; the application never slows motion down.
 */
export interface GalleryMotion {
  slow: Ref<boolean>;
  reduced: Ref<boolean>;
  /** The system already asks for reduced motion: the switch stays on and cannot be turned off. */
  systemReduced: boolean;
}

let state: GalleryMotion | undefined;

export function useGalleryMotion(): GalleryMotion {
  if (!state) {
    const systemReduced = prefersReducedMotion();
    state = { slow: ref(false), reduced: ref(systemReduced), systemReduced };
  }
  return state;
}

/** Applies the switches to `target` (the document root) while the calling component is mounted. */
export function applyGalleryMotion(target: HTMLElement = document.documentElement): GalleryMotion {
  const motion = useGalleryMotion();
  const clear = () => {
    target.style.removeProperty(MOTION_SCALE_PROPERTY);
    forceReducedMotion(target, false);
  };
  const stop = watch([motion.slow, motion.reduced], ([slow, reduced]) => {
    if (slow) target.style.setProperty(MOTION_SCALE_PROPERTY, "3");
    else target.style.removeProperty(MOTION_SCALE_PROPERTY);
    forceReducedMotion(target, reduced);
  }, { immediate: true });
  if (getCurrentInstance()) onBeforeUnmount(() => { stop(); clear(); });
  return motion;
}

/** Tests only: forget the shared state. */
export function resetGalleryMotion() {
  state = undefined;
}
