<script setup lang="ts">
import componentsTree from "../i18n/components-tree";
import { computed } from "vue";
import StatusBadge from "./settings/StatusBadge.vue";
// The page state is derived here and nowhere else (Domain Model): unpublished changes = published && version > publishedVersion.
const props = withDefaults(
  defineProps<{ isPublished: boolean; version: number; publishedVersion: number | null; hidePublished?: boolean }>(),
  { hidePublished: false },
);
const state = computed(() =>
  !props.isPublished
    ? { status: "draft", text: componentsTree.badge.draft, tone: "neutral" as const, icon: "pencil" }
    : props.version > (props.publishedVersion ?? 0)
      ? { status: "unpublished", text: componentsTree.badge.unpublished, tone: "warning" as const, icon: "triangle-alert" }
      : { status: "published", text: componentsTree.badge.published, tone: "success" as const, icon: "circle-check" },
);
// Lists of pages (space, library, dashboard) badge only what needs attention: `hidePublished`
// keeps the full words but leaves the usual state, Publikováno, unmarked (content first, 2026-09-28).
const shown = computed(() => !props.hidePublished || state.value.status !== "published");
</script>
<template>
  <StatusBadge v-if="shown" :tone="state.tone" :icon="state.icon" :label="state.text" size="sm" :data-status="state.status" />
</template>
