import { afterEach, expect, it, vi } from "vitest";
import { defineComponent, h, nextTick } from "vue";
import { mount } from "@vue/test-utils";
import { applyGalleryMotion, resetGalleryMotion, useGalleryMotion } from "./galleryMotion";
import { motionScale, prefersReducedMotion } from "../../motion/aiMotion";

// The prototype's "Zpomalit 3×" and "Omezený pohyb" apply to the whole gallery (motion unification plan §6.1):
// the root carries them, so portals and body-level overlays follow too, and leaving the gallery clears them.
afterEach(() => {
  resetGalleryMotion();
  vi.mocked(window.matchMedia).mockImplementation((query: string) => ({ matches: false, media: query, addEventListener: vi.fn(), removeEventListener: vi.fn() }) as unknown as MediaQueryList);
  document.documentElement.removeAttribute("data-atlas-reduced-motion");
  document.documentElement.style.removeProperty("--atlas-motion-scale");
});

const Host = defineComponent({ setup() { applyGalleryMotion(); return () => h("p", "galerie"); } });

it("applies the switches to the document root and clears them when the gallery unmounts", async () => {
  const host = mount(Host);
  const root = document.documentElement;
  const portal = document.createElement("div");
  document.body.append(portal);
  expect(root.hasAttribute("data-atlas-reduced-motion")).toBe(false);
  expect(motionScale(portal)).toBe(1);

  const motion = useGalleryMotion();
  motion.slow.value = true;
  motion.reduced.value = true;
  await nextTick();
  expect(root.style.getPropertyValue("--atlas-motion-scale")).toBe("3");
  expect(motionScale(portal)).toBe(3);
  expect(prefersReducedMotion(portal)).toBe(true);

  motion.slow.value = false;
  await nextTick();
  expect(root.style.getPropertyValue("--atlas-motion-scale")).toBe("");

  motion.slow.value = true;
  await nextTick();
  host.unmount();
  expect(root.hasAttribute("data-atlas-reduced-motion")).toBe(false);
  expect(root.style.getPropertyValue("--atlas-motion-scale")).toBe("");
  portal.remove();
});

it("starts with reduced motion on when the system asks for it", () => {
  vi.mocked(window.matchMedia).mockImplementation((query: string) => ({ matches: query.includes("prefers-reduced-motion"), media: query, addEventListener: vi.fn(), removeEventListener: vi.fn() }) as unknown as MediaQueryList);
  const motion = useGalleryMotion();
  expect(motion.systemReduced).toBe(true);
  expect(motion.reduced.value).toBe(true);
});
