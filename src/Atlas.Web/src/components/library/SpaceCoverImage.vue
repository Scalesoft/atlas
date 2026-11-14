<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from "vue";
import type { Space } from "../../api";
import { spaceCoverSrc } from "../../spaceCover";
import AtlasIcon from "../AtlasIcon.vue";

/**
 * The image of a space as a layer that fills the box its host gives it (a height or an aspect ratio, set through a
 * class): the uploaded image or the built-in drawing (spaceCover.ts) blended into the space's two colours (duotone),
 * an upload with `tinted` off shown as it is, and a space without an image gets a quiet pattern in its own colours
 * with its icon drawn large. A drawing is always tinted; an image that fails to load falls back to the pattern.
 * Decorative: the name of the space stands beside it.
 *
 * Use it wherever a space shows its image outside its card: the head of the space, a thumbnail of a drawing, a
 * preview. For the card itself choose SpaceCard, which lays this under the card's icon, title and facts.
 */
const props = withDefaults(defineProps<{
  space: Pick<Space, "id" | "icon" | "color"> & Partial<Pick<Space, "cover">>;
  /** Shown instead of the stored image: the file being uploaded, or the URL of a previewed drawing. */
  src?: string;
  /** Ask for the image at once instead of when it comes near the viewport. */
  eager?: boolean;
  /** Ask for it ahead of other requests (what the page shows first). */
  priority?: boolean;
  /** Show the image as soon as it is decoded. The card passes false while it is still rising in. */
  instant?: boolean;
  /** The size of the icon drawn on the pattern. */
  markSize?: number;
}>(), { src: undefined, markSize: 150 });

// An image the server no longer serves (or a server without them) falls back to the pattern.
const failed = ref(false);
const url = computed(() => props.src ?? spaceCoverSrc(props.space.id, props.space.cover));
// The image reserves its box. One that arrives while its host is still entering is simply part of that entrance;
// only one that comes later (a slow connection) fades in, so it never pops. One the browser already has is there at
// once. Faded in every time, the covers were the page's largest paint 280 ms later than they had arrived (LCP of
// Knihovna 260 → 544 ms).
const loaded = ref(false);
const late = ref(false);
let frames = 0;
// An early image is shown two frames on, when the host's entrance is under way. Painted in the entrance's very first
// frame, at opacity 0, it is not counted as painted until the entrance ends: Knihovna's largest paint was then
// reported ~250 ms after the cover could be seen (224 or 490 ms from run to run; 244 ms with the wait).
function show() {
  if (loaded.value || frames) return;
  if (props.instant) loaded.value = true;
  else frames = requestAnimationFrame(() => { frames = requestAnimationFrame(() => { frames = 0; loaded.value = true; }); });
}
function remember(element: unknown) {
  const image = element as HTMLImageElement | null;
  if (image?.complete && image.naturalWidth > 0) show();
}
function arrived() {
  late.value = props.instant;
  show();
}
watch(url, () => { failed.value = false; loaded.value = false; cancelAnimationFrame(frames); frames = 0; });
onBeforeUnmount(() => cancelAnimationFrame(frames));
// Only an uploaded image (or the one handed in) can be shown as it is; a drawing is always in the space's colours.
const kind = computed(() => (!url.value || failed.value ? "pattern" : (props.src || props.space.cover?.source === "Upload") && props.space.cover?.tinted === false ? "photo" : "tinted"));
</script>

<template>
  <div class="space-cover" :data-cover="kind" :data-color="space.color" aria-hidden="true">
    <template v-if="kind !== 'pattern'">
      <img :ref="remember" class="space-cover-image" :class="{ 'is-loaded': loaded, 'is-late': late }" :src="url" alt="" decoding="async" :loading="eager ? 'eager' : 'lazy'" :fetchpriority="priority ? 'high' : undefined" draggable="false" @load="arrived" @error="failed = true" />
      <span v-if="kind === 'tinted'" class="space-cover-tint" />
    </template>
    <template v-else>
      <span class="space-cover-pattern" />
      <AtlasIcon class="space-cover-mark" :name="space.icon" :size="markSize" />
    </template>
  </div>
</template>

<style scoped>
/* Colours come from the space's palette hue (--ah, --ac of styles/appearance.css, set by data-color) and the
   theme tokens, so any accent and dark mode work. Cover light/dark are the duotone's highlight and shadow. */
.space-cover {
  --space-cover-base: oklch(95% calc(var(--ac, .1) * .3) var(--ah, 220));
  --space-cover-light: var(--space-cover-base);
  --space-cover-dark: oklch(62% calc(var(--ac, .1) * .75) var(--ah, 220));
  --space-cover-filter: grayscale(1) contrast(1.1);
  --space-cover-glow: oklch(86% calc(var(--ac, .1) * .55) var(--ah, 220));
  --space-cover-dot: oklch(80% calc(var(--ac, .1) * .4) var(--ah, 220));
  --space-cover-mark: var(--space-cover-dark);
  position: relative;
  overflow: hidden;
  isolation: isolate;
  background: var(--space-cover-base);
}
/* Dark mode: the duotone and the pattern turn deep, the drawn icon light. (A whole :global() selector: a partial
   one would make Vue drop the scoped part.) */
:global(:is(:root, .atlas)[data-mode="dark"] .space-cover) {
  /* The image is inverted first, so its paper turns into the deep colour and its drawing into the light one. */
  --space-cover-base: oklch(27% calc(var(--ac, .1) * .35) var(--ah, 220));
  --space-cover-light: oklch(68% calc(var(--ac, .1) * .6) var(--ah, 220));
  --space-cover-dark: oklch(27% calc(var(--ac, .1) * .4) var(--ah, 220));
  --space-cover-filter: grayscale(1) invert(1) contrast(1.1);
  --space-cover-glow: oklch(36% calc(var(--ac, .1) * .5) var(--ah, 220));
  --space-cover-dot: oklch(40% calc(var(--ac, .1) * .35) var(--ah, 220));
  --space-cover-mark: oklch(72% calc(var(--ac, .1) * .6) var(--ah, 220));
}
.space-cover[data-cover="tinted"] { background: var(--space-cover-light); }
.space-cover-image, .space-cover-tint, .space-cover-pattern { position: absolute; inset: 0; }
.space-cover-image { width: 100%; height: 100%; object-fit: cover; }
/* Duotone: the grey image multiplies the light colour (its highlights stay light), the dark colour lightens
   its shadows, so any image ends up in the space's two colours. */
[data-cover="tinted"] .space-cover-image { filter: var(--space-cover-filter); mix-blend-mode: multiply; }
.space-cover-tint { background: var(--space-cover-dark); mix-blend-mode: lighten; pointer-events: none; }
.space-cover-pattern {
  background:
    radial-gradient(260px 160px at 85% 0%, var(--space-cover-glow), transparent 70%),
    radial-gradient(1px 1px at 1px 1px, var(--space-cover-dot) 1px, transparent 0) 0 0 / 14px 14px;
}
.space-cover-mark { position: absolute; top: -18px; right: -14px; color: var(--space-cover-mark); opacity: .35; stroke-width: 1.2; }
.space-cover-mark :deep(*) { stroke-width: 1.2; }
/* The image is hidden until it has loaded. With its host still entering it is just there; a late one blends into the
   colour under it. */
.space-cover-image { opacity: 0; }
.space-cover-image.is-loaded { opacity: 1; }
.space-cover-image.is-late { transition: opacity var(--atlas-motion-base) var(--atlas-ease-out); }
</style>
