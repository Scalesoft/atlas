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
//
// `error` is the field's own message under the control, the markup of SettingsRow's (role=alert, red, a warning icon). The
// default slot receives `describedBy`, the error's id, for the control's `aria-describedby`; a control that ignores it still works.
import { useId } from "vue";
import AtlasIcon from "./AtlasIcon.vue";
import { Label } from "./ui/label";

defineProps<{ label?: string; error?: string }>();
const errorId = `${useId()}-error`;
</script>

<template>
  <Label class="mb-3.5 grid gap-2 font-normal">
    <span class="text-sm leading-none font-medium"><slot name="label">{{ label }}</slot></span>
    <slot :described-by="error ? errorId : undefined" :invalid="error ? true : undefined" />
    <p v-if="error" :id="errorId" role="alert" class="flex items-start gap-1.5 text-[12.5px] leading-snug font-semibold text-[var(--error)]"><AtlasIcon name="triangle-alert" :size="14" class="mt-0.5 shrink-0" />{{ error }}</p>
  </Label>
</template>
