<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref } from 'vue'
const props = defineProps<{ render: (container: HTMLElement) => void | (() => void) }>()
const element = ref<HTMLElement>()
let cleanup: void | (() => void)
onMounted(() => { cleanup = props.render(element.value!) })
onBeforeUnmount(() => { if (typeof cleanup === 'function') cleanup() })
</script>
<template><span ref="element" aria-hidden="true" /></template>
