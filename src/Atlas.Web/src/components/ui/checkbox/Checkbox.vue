<script setup lang="ts">
import type { CheckboxRootEmits, CheckboxRootProps } from "reka-ui"
import type { HTMLAttributes } from "vue"
import { ref } from "vue"
import { reactiveOmit } from "@vueuse/core"
import { CheckboxIndicator, CheckboxRoot, useForwardPropsEmits } from "reka-ui"
import { cn } from "@/lib/utils"

const props = defineProps<CheckboxRootProps & { class?: HTMLAttributes["class"] }>()
const emits = defineEmits<CheckboxRootEmits>()

const delegatedProps = reactiveOmit(props, "class")
const forwarded = useForwardPropsEmits(delegatedProps, emits)
// The tick is drawn only when the reader checks the box: one that arrives checked, or that its owner checks, stands.
const toggled = ref(false)
</script>

<template>
  <CheckboxRoot
    data-slot="checkbox"
    v-bind="forwarded"
    :class="cn(
      'peer border-input dark:bg-input/30 data-[state=checked]:bg-primary data-[state=checked]:text-primary-foreground dark:data-[state=checked]:bg-primary data-[state=checked]:border-primary focus-visible:border-ring focus-visible:ring-ring/50 aria-invalid:ring-destructive/20 dark:aria-invalid:ring-destructive/40 aria-invalid:border-destructive size-4 shrink-0 rounded-[4px] border p-0 shadow-xs transition-shadow outline-none focus-visible:ring-[3px] disabled:cursor-not-allowed disabled:opacity-50',
      props.class,
    )"
    @update:model-value="toggled = true"
  >
    <CheckboxIndicator
      data-slot="checkbox-indicator"
      :data-toggled="toggled ? '' : undefined"
      class="grid place-content-center text-current transition-none"
    >
      <!-- shadcn draws lucide's CheckIcon here; the same path inline keeps lucide out of the bundle. It runs from the
           short stroke to the long one, the way a tick is written, and is 30 units long for the drawing below. -->
      <slot>
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" class="size-3.5" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path pathLength="30" d="M4 12l5 5L20 6" /></svg>
      </slot>
    </CheckboxIndicator>
  </CheckboxRoot>
</template>

<style scoped>
/* Motion tokens (motion.css). Checking the box draws its tick like the confirmation of a StatusBadge (status-check:
   a dash as long as the path runs out), in the base time. Unchecking, the tick fades with the fill instead of
   vanishing a frame before it (Reka keeps the indicator until the animation ends). Reduced motion: both only fade. */
[data-slot="checkbox-indicator"][data-toggled][data-state="checked"] path { stroke-dasharray: 30; animation: status-check var(--atlas-motion-base) var(--atlas-ease-out) both; }
[data-slot="checkbox-indicator"][data-state="unchecked"] { animation: st-fade-out var(--atlas-motion-fast) var(--atlas-ease-out) both; }
@media (prefers-reduced-motion: reduce) {
  [data-slot="checkbox-indicator"][data-toggled][data-state="checked"] path { stroke-dasharray: none; animation-name: st-fade-in; }
}
[data-atlas-reduced-motion] [data-slot="checkbox-indicator"][data-toggled][data-state="checked"] path { stroke-dasharray: none; animation-name: st-fade-in; }
</style>
