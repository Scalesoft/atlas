<script setup lang="ts">
import { computed } from 'vue';
import { isPageIcon, isPageIconColor, type PageDecoration } from '../pageDecorations';
import AtlasIcon from './AtlasIcon.vue';
import '../page-icons.css';
const props = defineProps<{ decoration?: PageDecoration | null; size?: number }>();
const icon = computed(() => isPageIcon(props.decoration?.icon) ? props.decoration!.icon! : undefined);
const color = computed(() => isPageIconColor(props.decoration?.color) ? props.decoration!.color! : 'slate');
</script>
<template><AtlasIcon v-if="icon" class="page-decoration-icon" :data-color="color" :name="icon" :size="size ?? 18" /></template>
