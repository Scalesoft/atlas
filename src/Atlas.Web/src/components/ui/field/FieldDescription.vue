<script setup lang="ts">
import type { HTMLAttributes } from "vue"
import { cn } from "@/lib/utils"

const props = defineProps<{
  class?: HTMLAttributes["class"]
}>()
</script>

<template>
  <!-- `m-0`: shadcn counts on Tailwind's preflight to clear a paragraph's margins, and this app does not import it.
       The 1em left under the description made every choice card 12.5 px taller below its text than above it. -->
  <p
    data-slot="field-description"
    :class="cn(
      'text-muted-foreground m-0 text-sm leading-normal font-normal in-data-[orientation=horizontal]:text-balance',
      'last:mt-0 nth-last-2:-mt-1 [[data-variant=legend]+&]:-mt-1.5',
      '[&>a:hover]:text-primary [&>a]:underline [&>a]:underline-offset-4',
      props.class,
    )"
  >
    <slot />
  </p>
</template>
