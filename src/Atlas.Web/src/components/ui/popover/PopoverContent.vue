<script setup lang="ts">
import type { PopoverContentEmits, PopoverContentProps } from "reka-ui"
import type { HTMLAttributes } from "vue"
import { inject, watch } from "vue"
import { dialogLayersKey } from "../dialogLayers"
import { reactiveOmit } from "@vueuse/core"
import {
  PopoverContent,
  PopoverPortal,
  injectPopoverRootContext,
  useForwardPropsEmits,
} from "reka-ui"
import { cn } from "@/lib/utils"

defineOptions({
  inheritAttrs: false,
})

const props = withDefaults(
  defineProps<PopoverContentProps & { class?: HTMLAttributes["class"] }>(),
  {
    align: "center",
    sideOffset: 4,
  },
)
const emits = defineEmits<PopoverContentEmits>()

const delegatedProps = reactiveOmit(props, "class")

const forwarded = useForwardPropsEmits(delegatedProps, emits)
const dialogLayers = inject(dialogLayersKey, undefined)
const root = injectPopoverRootContext()
watch(() => root.open.value, (open, _, onCleanup) => {
  if (!open || !dialogLayers) return
  onCleanup(dialogLayers.registerEscape(event => {
    emits("escapeKeyDown", event)
    if (!event.defaultPrevented) root.onOpenChange(false)
  }))
}, { immediate: true, flush: "sync" })
</script>

<template>
  <PopoverPortal :to="dialogLayers?.target.value">
    <PopoverContent
      data-slot="popover-content"
      v-bind="{ ...$attrs, ...forwarded }"
      :inert="!root.open.value || undefined"
      :aria-hidden="!root.open.value || undefined"
      :class="
        cn(
          'bg-popover text-popover-foreground data-[state=open]:animate-in data-[state=closed]:animate-out data-[state=closed]:fade-out-0 data-[state=open]:fade-in-0 data-[state=closed]:zoom-out-95 data-[state=open]:zoom-in-95 data-[side=bottom]:slide-in-from-top-2 data-[side=left]:slide-in-from-right-2 data-[side=right]:slide-in-from-left-2 data-[side=top]:slide-in-from-bottom-2 z-50 w-72 max-w-(--reka-popover-content-available-width) max-h-(--reka-popover-content-available-height) overflow-y-auto rounded-md border p-4 shadow-md origin-(--reka-popover-content-transform-origin) outline-hidden',
          props.class,
        )
      "
    >
      <slot />
    </PopoverContent>
  </PopoverPortal>
</template>
