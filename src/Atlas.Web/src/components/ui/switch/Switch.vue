<script setup lang="ts">
import type { SwitchRootEmits, SwitchRootProps } from "reka-ui"
import type { HTMLAttributes } from "vue"
import { reactiveOmit } from "@vueuse/core"
import { SwitchRoot, SwitchThumb, useForwardPropsEmits } from "reka-ui"
import { cn } from "@/lib/utils"

const props = defineProps<SwitchRootProps & { class?: HTMLAttributes["class"] }>()
const emits = defineEmits<SwitchRootEmits>()
const delegatedProps = reactiveOmit(props, "class")
const forwarded = useForwardPropsEmits(delegatedProps, emits)
</script>

<template>
  <SwitchRoot
    data-slot="switch"
    v-bind="forwarded"
    :class="cn(
      'peer data-[state=checked]:bg-primary data-[state=unchecked]:bg-input focus-visible:border-ring focus-visible:ring-ring/50 dark:data-[state=unchecked]:bg-input/80 inline-flex h-4.5 w-8 shrink-0 items-center rounded-full p-0 border border-transparent shadow-xs outline-none focus-visible:ring-[3px] disabled:cursor-not-allowed disabled:opacity-50',
      props.class,
    )"
  >
    <SwitchThumb
      data-slot="switch-thumb"
      :class="cn('bg-background dark:data-[state=unchecked]:bg-foreground dark:data-[state=checked]:bg-primary-foreground pointer-events-none block size-4 rounded-full ring-0 data-[state=checked]:translate-x-[calc(100%-2px)] data-[state=unchecked]:translate-x-0')"
    >
      <slot name="thumb" />
    </SwitchThumb>
  </SwitchRoot>
</template>

<style scoped>
/* The track colours are unlayered, so no layer order of the lazily loaded utilities can leave an off switch transparent (invisible on the card). */
[data-slot="switch"][data-state="unchecked"] { background-color: var(--input-line); }
[data-slot="switch"][data-state="checked"] { background-color: var(--accent); }
/* Motion tokens (motion.css). The thumb slides; with reduced motion it cross-fades in place:
   switching the keyframe name per state restarts the fade on every toggle (the two shared fades are identical). */
[data-slot="switch"] {
  transition: background-color var(--atlas-motion-base) var(--atlas-ease-out), box-shadow var(--atlas-motion-fast) var(--atlas-ease-out);
}
[data-slot="switch"]:active:not(:disabled) [data-slot="switch-thumb"] { scale: var(--atlas-press, .98); }
[data-slot="switch-thumb"] {
  transition: translate var(--atlas-motion-base) var(--atlas-ease-out), scale var(--atlas-motion-fast) var(--atlas-ease-out);
}
@media (prefers-reduced-motion: reduce) {
  [data-slot="switch-thumb"] { transition: none; }
  [data-slot="switch-thumb"][data-state="checked"] { animation: atlas-backdrop-in var(--atlas-motion-base) var(--atlas-ease-out); }
  [data-slot="switch-thumb"][data-state="unchecked"] { animation: st-fade-in var(--atlas-motion-base) var(--atlas-ease-out); }
}
[data-atlas-reduced-motion] [data-slot="switch-thumb"] { transition: none; }
[data-atlas-reduced-motion] [data-slot="switch-thumb"][data-state="checked"] { animation: atlas-backdrop-in var(--atlas-motion-base) var(--atlas-ease-out); }
[data-atlas-reduced-motion] [data-slot="switch-thumb"][data-state="unchecked"] { animation: st-fade-in var(--atlas-motion-base) var(--atlas-ease-out); }
</style>
