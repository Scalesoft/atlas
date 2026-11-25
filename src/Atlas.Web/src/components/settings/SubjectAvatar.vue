<script setup lang="ts">
import { locale } from "../../i18n";
import { computed, ref } from "vue";
import { markdownInitials } from "@atlas/markdown/core/userColor";
import AtlasIcon from "../AtlasIcon.vue";

// A person (initials in a circle) or a group (icon in a rounded square). Decorative: the name is
// always written next to it. `photo`: a contact's logo or photo over it (package G); the initials stay when it fails.
// `color`: the person's own colour (live/collaboratorColors userColor, the colour of their cursor) where people are told apart (history).
const props = withDefaults(defineProps<{ kind: "user" | "group"; name?: string; size?: "xs" | "sm" | "md"; photo?: string; color?: string }>(), { name: undefined, size: "md", photo: undefined, color: undefined });
const failed = ref("");
const initials = computed(() => markdownInitials(props.name, locale));
</script>

<template>
  <span class="subject-avatar" :data-kind="kind" :data-size="size" :style="color ? { background: color, color: '#fff' } : undefined" aria-hidden="true">
    <AtlasIcon v-if="kind === 'group' || !initials" :name="kind === 'group' ? 'users' : 'account'" :size="size === 'xs' ? 11 : size === 'sm' ? 14 : 17" />
    <template v-else>{{ initials }}</template>
    <img v-if="photo && photo !== failed" :src="photo" alt="" loading="lazy" decoding="async" class="subject-avatar-photo" @error="failed = photo ?? ''">
  </span>
</template>

<style scoped>
.subject-avatar { display: grid; place-items: center; flex-shrink: 0; width: 36px; height: 36px; border-radius: 50%; color: var(--muted); background: var(--hover); font-size: 12px; font-weight: 700; }
.subject-avatar[data-size="sm"] { width: 28px; height: 28px; font-size: 11px; }
.subject-avatar[data-size="xs"] { width: 18px; height: 18px; font-size: 8px; }
.subject-avatar[data-kind="group"] { border-radius: 11px; color: var(--accent); background: var(--accent-soft); }
.subject-avatar { position: relative; overflow: hidden; }
.subject-avatar-photo { position: absolute; inset: 0; width: 100%; height: 100%; object-fit: cover; background: var(--paper, #fff); }
</style>
