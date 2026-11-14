<script setup lang="ts">
import type { TooltipContentEmits, TooltipContentProps } from "reka-ui"
import type { HTMLAttributes } from "vue"
import { inject } from "vue"
import { dialogLayersKey } from "../dialogLayers"
import { reactiveOmit } from "@vueuse/core"
import { TooltipArrow, TooltipContent, TooltipPortal, useForwardPropsEmits } from "reka-ui"
import { cn } from "@/lib/utils"

defineOptions({
  inheritAttrs: false,
})

const props = withDefaults(defineProps<TooltipContentProps & { class?: HTMLAttributes["class"]; inline?: boolean }>(), {
  sideOffset: 4,
  inline: false,
})

const emits = defineEmits<TooltipContentEmits>()

const delegatedProps = reactiveOmit(props, "class", "inline")
const forwarded = useForwardPropsEmits(delegatedProps, emits)
// Like the menu and the popover: inside a modal <dialog> the tip joins the dialog's top layer.
const dialogLayers = inject(dialogLayersKey, undefined)
</script>

<template>
  <!-- `inline` keeps the tip in place instead of the body: inside a modal <dialog> (top layer) a body tip is hidden behind it. -->
  <TooltipPortal :to="dialogLayers?.target.value" :disabled="inline">
    <TooltipContent
      data-slot="tooltip-content"
      v-bind="{ ...forwarded, ...$attrs }"
      :class="cn('bg-foreground text-background animate-in fade-in-0 zoom-in-95 data-[state=closed]:animate-out data-[state=closed]:fade-out-0 data-[state=closed]:zoom-out-95 data-[side=bottom]:slide-in-from-top-2 data-[side=left]:slide-in-from-right-2 data-[side=right]:slide-in-from-left-2 data-[side=top]:slide-in-from-bottom-2 z-50 w-fit rounded-md px-3 py-1.5 text-xs text-balance', props.class)"
    >
      <slot />

      <TooltipArrow class="bg-foreground fill-foreground z-50 size-2.5 translate-y-[calc(-50%_-_2px)] rotate-45 rounded-xs" />
    </TooltipContent>
  </TooltipPortal>
</template>
