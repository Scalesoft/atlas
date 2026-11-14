<script setup lang="ts">
import { computed, shallowRef, watchEffect } from "vue";
import { ICON_STROKE_WIDTH, ICON_VIEWBOX, MarkdownIcon, iconFragment, iconMarkup, type MarkdownIconName } from "@atlas/markdown/icons";
import { atlasIconNames } from "./atlasIconNames";
import { isIconName } from "../appearance";
import { iconPackMarkup, iconPackPartLoaded, iconPackUnavailable, loadIconPackMarkup } from "../iconPack";
import openAiLogo from "../assets/provider/openai.svg?no-inline";
import claudeLogo from "../assets/provider/claude.svg?no-inline";

const providerMarks: Record<string, string> = {
  "brand-openai": openAiLogo,
  "brand-anthropic": claudeLogo,
};

const props = defineProps<{ name: string; size?: number }>();

const px = computed(() => props.size ?? 18);
const providerMark = computed(() => providerMarks[props.name]);
// Own keys only: a stored name such as "constructor" is well formed and must not resolve through the prototype.
const mapped = computed(() => (Object.hasOwn(atlasIconNames, props.name) ? atlasIconNames[props.name] : undefined) ?? (Object.hasOwn(iconMarkup, props.name) ? (props.name as MarkdownIconName) : undefined));
// A well-formed name the core does not draw may be an icon of the icon pack: an empty box of the same size while its
// part of the pack loads, the drawing after. Parts are requested here only, so a view loads the parts of the icons it shows.
const fromPack = computed(() => !providerMark.value && !mapped.value && isIconName(props.name));
const pending = computed(() => fromPack.value && !iconPackPartLoaded(props.name) && !iconPackUnavailable.value);
const drawing = computed(() => {
  const markup = fromPack.value && iconPackPartLoaded(props.name) ? iconPackMarkup.value : null;
  return markup && Object.hasOwn(markup, props.name) ? markup[props.name] : undefined;
});
// Depends on the name and the arrived parts only: a failed load does not loop; the next part to arrive or the next pack icon
// to mount asks again.
watchEffect(() => {
  if (fromPack.value && !iconPackPartLoaded(props.name)) loadIconPackMarkup(props.name).catch(() => {});
});
// The pack's drawing is cloned from one parsed copy per icon (iconFragment), not parsed for each icon by v-html (3.6 perf).
const packSvg = shallowRef<SVGSVGElement>();
watchEffect(() => {
  const svg = packSvg.value, markup = drawing.value;
  if (svg) svg.replaceChildren(...(markup ? [iconFragment(svg.ownerDocument, markup)] : []));
}, { flush: "post" });
const resolved = computed<MarkdownIconName>(() => {
  if (providerMark.value) return "circle-help";
  if (mapped.value) return mapped.value;
  // A pack icon whose load failed is no unknown name: only the fallback shows.
  if (import.meta.env.DEV && (!fromPack.value || iconPackPartLoaded(props.name))) console.warn(`AtlasIcon: unknown icon "${props.name}"`);
  return "circle-help";
});
</script>
<template>
  <img v-if="providerMark" class="atlas-provider-mark" :src="providerMark" :width="px" :height="px" alt="" aria-hidden="true" draggable="false" />
  <svg
    v-else-if="pending || drawing"
    :viewBox="ICON_VIEWBOX"
    :width="px"
    :height="px"
    :style="{ width: `${px}px`, height: `${px}px` }"
    fill="none"
    stroke="currentColor"
    :stroke-width="ICON_STROKE_WIDTH"
    stroke-linecap="round"
    stroke-linejoin="round"
    aria-hidden="true"
    :data-icon="name"
    :data-icon-state="drawing ? undefined : 'pending'"
    ref="packSvg"
  />
  <MarkdownIcon v-else :name="resolved" :size="px" :style="{ width: `${px}px`, height: `${px}px` }" />
</template>

<style scoped>
.atlas-provider-mark { display: block; flex: none; object-fit: contain; }
</style>
