<script setup lang="ts">
import { onMounted, ref } from "vue";
import { prefersReducedMotion } from "../../motion/aiMotion";
import { brandMarks } from "../../whatsNew/brandMarks";

/**
 * The wireframe mark of a release (`mark:` in its notes, `assets/brand/<mark>.svg`, made by tooling/brand/wireframe-mark.mjs). It is
 * decoration: the text beside it names the release. Unlike AtlasBrandLogo (the application's Bloom), it belongs to one release and
 * only appears where that release is presented:
 * - `hero`: large, with the intro, on the splash after the update (ReleaseSplash);
 * - `watermark`: large and faded out by a gradient mask behind a version's heading (ReleaseEntry); the parent positions it;
 * - `icon`: 20 px in the notice about the new release (whatsNew/notice.ts).
 * The SVG carries its own motion (outline, unfolding wireframe, quickening light, then a slow sway). It is drawn inline, so the motion
 * starts when the mark appears (an <img> runs on the page's clock and would show it finished); it stands still at once under reduced
 * motion, the system's or Atlas's own (`data-still`). The space is held while the SVG loads.
 */
const props = withDefaults(defineProps<{ mark: string; variant?: "hero" | "watermark" | "icon" }>(), { variant: "hero" });
const root = ref<HTMLElement>();
const svg = ref("");
const still = ref(false);
const suffix = `m${++instances}`;

onMounted(async () => {
  still.value = prefersReducedMotion(root.value);
  const text = await brandMarks[props.mark]?.().catch(() => "") ?? "";
  // A mark that ends invisible (data-vanishes) plays its intro only as the hero; the changelog watermark and the icon rest as the full mark.
  if (props.variant !== "hero" && text.includes("data-vanishes")) still.value = true;
  // Ids per instance: the same mark may stand on the page twice, one copy in a hidden subtree.
  svg.value = [...text.matchAll(/\bid="([^"]+)"/g)].reduce((out, [, id]) => out.replaceAll(`id="${id}"`, `id="${id}-${suffix}"`).replaceAll(`#${id})`, `#${id}-${suffix})`), text);
});
</script>

<script lang="ts">
/** Ids per instance across every app on the page (the notice renders in the toaster, the gallery mounts several). */
let instances = 0;
</script>

<template>
  <span ref="root" class="release-mark" :data-variant="variant" :data-still="still || undefined" aria-hidden="true" v-html="svg" />
</template>

<style scoped>
.release-mark { display: block; flex: none; pointer-events: none; user-select: none; }
.release-mark :deep(svg) { display: block; width: 100%; height: 100%; }
.release-mark[data-variant="hero"] { width: min(280px, 100%); aspect-ratio: 1; }
/* The body fills about half of the 64-unit square; the icon trims the empty margin so the mark reads at 20 px. */
.release-mark[data-variant="icon"] { width: 20px; height: 20px; }
.release-mark[data-variant="icon"] :deep(svg) { width: 160%; height: 160%; margin: -30%; }
.release-mark[data-variant="watermark"] {
  width: 240px; height: 240px; opacity: .22;
  -webkit-mask-image: linear-gradient(220deg, #000 22%, transparent 75%);
  mask-image: linear-gradient(220deg, #000 22%, transparent 75%);
}
:global(html[data-mode="dark"] .release-mark[data-variant="watermark"]) { opacity: .3; }
</style>
