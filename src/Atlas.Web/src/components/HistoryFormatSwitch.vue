<script setup lang="ts">
import { useHistoryFormat } from "../historyFormat";
import OptionPills from "./settings/OptionPills.vue";
import t from "../i18n/components-history";

/** Náhled | Markdown of a history view: the page as it reads, or its stored Markdown. Shared by every history view. */
const { format, choose } = useHistoryFormat();
const options = [{ value: "rendered", label: t.format.rendered }, { value: "source", label: t.format.source }];
</script>

<template>
  <OptionPills segmented class="history-format" :model-value="format" :options="options" :label="t.format.label" hide-label @update:model-value="choose($event as 'rendered' | 'source')" />
</template>

<style scoped>
.history-format :deep(.option-pill) { height: 24px; padding: 0 9px; font-size: 12px; }
</style>
