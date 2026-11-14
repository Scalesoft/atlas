<script setup lang="ts">
import type { TooltipProviderProps } from "reka-ui"
import { TooltipProvider } from "reka-ui"

// 400 ms everywhere: the application's provider passes the same, so the gallery and a component mounted on its own
// open a tip after the same delay (a focus opens it at once).
const props = withDefaults(defineProps<TooltipProviderProps>(), {
  delayDuration: 400,
})
</script>

<template>
  <TooltipProvider v-bind="props">
    <slot />
  </TooltipProvider>
</template>
