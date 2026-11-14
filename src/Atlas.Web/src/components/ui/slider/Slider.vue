<script setup lang="ts">
import type { SliderRootEmits, SliderRootProps } from "reka-ui"
import type { HTMLAttributes } from "vue"
import { reactiveOmit } from "@vueuse/core"
import { SliderRange, SliderRoot, SliderThumb, SliderTrack, useForwardPropsEmits } from "reka-ui"
import { cn } from "@/lib/utils"

/**
 * A value on a scale (reka-ui Slider): drag, click the track, or arrow keys / Home / End on the thumb. Choose it over
 * OptionPills when the choices are ordered steps of one quantity (less → more); unordered alternatives stay pills.
 * `thumbLabel` names the thumb, `valueText` reads a step as words (aria-valuetext) instead of its number. The default slot
 * draws inside the track (clipped to it), for a composed meter such as AiEffortMeter.
 */
const props = defineProps<SliderRootProps & { class?: HTMLAttributes["class"]; thumbLabel?: string; valueText?: (value: number) => string }>()
const emits = defineEmits<SliderRootEmits>()
const delegatedProps = reactiveOmit(props, "class", "thumbLabel", "valueText")
const forwarded = useForwardPropsEmits(delegatedProps, emits)
</script>

<template>
  <SliderRoot
    data-slot="slider"
    v-bind="forwarded"
    :class="cn('relative flex w-full touch-none items-center select-none data-[disabled]:opacity-50', props.class)"
  >
    <SliderTrack data-slot="slider-track" class="bg-muted relative h-1.5 w-full grow overflow-hidden rounded-full">
      <SliderRange data-slot="slider-range" class="bg-primary absolute h-full" />
      <slot />
    </SliderTrack>
    <SliderThumb
      v-for="(value, index) in modelValue ?? defaultValue ?? [min ?? 0]"
      :key="index"
      data-slot="slider-thumb"
      :aria-label="thumbLabel"
      :aria-valuetext="valueText?.(value)"
      class="border-primary bg-background ring-ring/50 block size-4 shrink-0 rounded-full border shadow-sm outline-none hover:ring-4 focus-visible:ring-4 disabled:pointer-events-none"
    />
  </SliderRoot>
</template>

<style scoped>
/* Motion tokens (motion.css): the thumb's ring grows and the thumb presses like the switch's. */
[data-slot="slider-thumb"] { transition: box-shadow var(--atlas-motion-fast) var(--atlas-ease-out), scale var(--atlas-motion-fast) var(--atlas-ease-out); cursor: grab; }
[data-slot="slider-thumb"]:active { scale: var(--atlas-press, .98); cursor: grabbing; }
/* A value set from elsewhere (a click on the track, a key, another view of the same state) travels; a drag follows the pointer at once. */
[data-slot="slider"]:not(:active) [data-slot="slider-thumb"] { transition-property: box-shadow, scale, left, right; transition-duration: var(--atlas-motion-fast), var(--atlas-motion-fast), var(--atlas-motion-base), var(--atlas-motion-base); }
[data-slot="slider"]:not(:active) :deep([data-slot="slider-range"]) { transition: left var(--atlas-motion-base) var(--atlas-ease-out), right var(--atlas-motion-base) var(--atlas-ease-out); }
</style>
