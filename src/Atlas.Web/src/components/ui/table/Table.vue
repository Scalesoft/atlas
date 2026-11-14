<script setup lang="ts">
import type { HTMLAttributes } from "vue"
import { cn } from "@/lib/utils"

const props = defineProps<{
  class?: HTMLAttributes["class"]
}>()
</script>

<template>
  <div data-slot="table-container" class="relative w-full overflow-auto">
    <!-- `border-collapse` is what Tailwind's preflight gives every table and this app does not import: without it the
         rows' `border-b` never paints and a hovered row shows the 2 px `border-spacing` between its cells. -->
    <table data-slot="table" :class="cn('w-full caption-bottom border-collapse text-sm', props.class)">
      <slot />
    </table>
  </div>
</template>
