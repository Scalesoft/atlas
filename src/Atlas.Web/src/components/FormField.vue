<script setup lang="ts">
// One form field: its name over its control.
//
// shadcn's own form examples associate a `Label` with a control by id and put
// the pair in a `grid gap-2` wrapper. This app labels implicitly instead, by
// wrapping the control, which is equally valid HTML and means no field has to
// invent a unique id -- so here the wrapper and the label are the same element.
//
// Two classes make the generated Label behave as a wrapper: `grid` replaces its
// `flex items-center`, which would otherwise set the name beside the control
// rather than above it, and `font-normal` stops the label's own weight from
// reaching the control, which `font: inherit` in shell.css carries through.
// Neither touches Label itself, so `shadcn-vue add label` stays a clean
// overwrite. `mb-3.5` is the vertical rhythm forms had from shell.css before
// the field rules there gave way to this component; a form that lays its fields
// out itself cancels it with `label { margin: 0 }` in its own scoped block.
import { Label } from "./ui/label";

defineProps<{ label?: string }>();
</script>

<template>
  <Label class="mb-3.5 grid gap-2 font-normal">
    <span class="text-sm leading-none font-medium"><slot name="label">{{ label }}</slot></span>
    <slot />
  </Label>
</template>
