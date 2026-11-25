<script setup lang="ts">
import type { PrimitiveProps } from "reka-ui"
import type { HTMLAttributes } from "vue"
import type { ButtonVariants } from "."
import { Primitive } from "reka-ui"
import AtlasIcon from "@/components/AtlasIcon.vue"
import { cn } from "@/lib/utils"
import { buttonVariants } from "."

interface Props extends PrimitiveProps {
  variant?: ButtonVariants["variant"]
  size?: ButtonVariants["size"]
  class?: HTMLAttributes["class"]
  /** Action in progress: the leading icon becomes a spinner, the button is disabled and announces aria-busy. */
  busy?: boolean
}

const props = withDefaults(defineProps<Props>(), {
  as: "button",
})
</script>

<template>
  <Primitive
    data-slot="button"
    :data-variant="variant"
    :data-size="size"
    :as="as"
    :as-child="asChild"
    :aria-busy="busy || undefined"
    :disabled="busy || undefined"
    :class="cn(buttonVariants({ variant, size }), busy && '[&>svg:not(.atlas-spinner)]:hidden', props.class)"
  >
    <AtlasIcon v-if="busy" name="loader-circle" :size="15" class="atlas-spinner" />
    <slot />
  </Primitive>
</template>
