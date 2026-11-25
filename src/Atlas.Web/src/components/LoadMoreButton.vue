<script setup lang="ts">
import t from "../i18n/load-more";
import { Button } from "./ui/button";

/**
 * The one "load more" of lists paged by cursor or offset: no icon, „Načíst další“; `older` says „Starší…“ (history only).
 * `busy` shows the button's spinner. `failed` (for a list that already shows rows) adds the quiet failure line and
 * turns the button into the retry. Attributes (click handler, data-*, class) go to the button. Loading happens by
 * click only; the exception is the search palette's own scroll sentinel.
 */
defineOptions({ inheritAttrs: false });
defineProps<{ busy?: boolean; disabled?: boolean; failed?: boolean; older?: boolean }>();
</script>

<template>
  <p v-if="failed" class="notice error" role="alert">{{ t.failed }}</p>
  <Button variant="outline" size="sm" type="button" :busy="busy" :disabled="disabled || busy" v-bind="$attrs">{{ failed ? t.retry : older ? t.older : t.more }}</Button>
</template>
