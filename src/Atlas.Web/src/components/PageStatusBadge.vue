<script setup lang="ts">
import componentsTree from "../i18n/components-tree";
import { computed } from "vue";
import { Badge } from "./ui/badge";
// The page state is derived here and nowhere else (Domain Model): unpublished changes = published && version > publishedVersion.
const props = withDefaults(
  defineProps<{ isPublished: boolean; version: number; publishedVersion: number | null; hidePublished?: boolean }>(),
  { hidePublished: false },
);
const state = computed(() =>
  !props.isPublished
    ? { status: "draft", text: componentsTree.badge.draft, tone: "muted" }
    : props.version > (props.publishedVersion ?? 0)
      ? { status: "unpublished", text: componentsTree.badge.unpublished, tone: "warning" }
      : { status: "published", text: componentsTree.badge.published, tone: "success" },
);
// Lists of pages (space, library, dashboard) badge only what needs attention: `hidePublished`
// keeps the full words but leaves the usual state, Publikováno, unmarked (content first, 2026-09-28).
const shown = computed(() => !props.hidePublished || state.value.status !== "published");
// shadcn's badge variants are neutral; the product's status tones are semantic.
// The theme carries them, so a plain class reaches them without touching Badge.
const tone = computed(() => ({
  success: "bg-success-soft text-success",
  warning: "bg-warning-soft text-warning",
  muted: "bg-accent text-muted-foreground",
}[state.value.tone] ?? ""));
</script>
<template>
  <Badge v-if="shown" as="span" variant="secondary" :class="tone" :data-status="state.status" :data-tone="state.tone"
    >{{ state.text }}</Badge
  >
</template>
