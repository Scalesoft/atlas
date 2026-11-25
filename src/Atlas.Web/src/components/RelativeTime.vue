<script lang="ts">
import { ref } from "vue";
// ONE minute clock for every RelativeTime on the page, so „před 1 minutou“ keeps moving.
const now = ref(Date.now());
setInterval(() => { now.value = Date.now(); }, 60_000);
</script>

<script setup lang="ts">
import { formatAgo, formatDateTime } from "../formatDate";

/** A moment in a list row: „před 5 minutami“ with the exact date and time as the title (a table column uses formatShortDate, a detail formatDateTime). */
defineProps<{ value: string; title?: string }>();
</script>

<template>
  <time :datetime="value" :title="title ?? formatDateTime(value)">{{ formatAgo(value, now) }}</time>
</template>
