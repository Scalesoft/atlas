<script setup lang="ts">
import { ref, watch } from "vue";
import AtlasIcon from "../AtlasIcon.vue";
import { Button } from "../ui/button";

/**
 * The send button of a quick reply (the field of the page's discussion and of its peek, one implementation): a light paper plane
 * beside the input, for whoever does not know that Enter sends. Dim while the input is empty (`ready` false; it stays reachable by
 * Tab and says so with aria-disabled, and does nothing), accent when there is text. When the send starts (`busy`) the plane nudges
 * forward once (motion.css, the distance is a token: under reduced motion only a short fade). A submit button of its form: the form's
 * own submit decides what is sent.
 */
const props = defineProps<{ ready: boolean; busy?: boolean; label: string }>();
const nudging = ref(false);
watch(() => props.busy, busy => { if (busy) nudging.value = true; });
function press(event: MouseEvent) {
  // aria-disabled is not disabled: an empty field must not submit.
  if (!props.ready || props.busy) event.preventDefault();
}
</script>

<template>
  <Button type="submit" variant="ghost" size="icon-sm" class="comment-send" :aria-label="label" :title="label" :aria-disabled="!ready || undefined" :aria-busy="busy || undefined"
    :data-ready="ready || undefined" :data-nudge="nudging || undefined" @click="press">
    <AtlasIcon name="send" :size="16" class="comment-send-plane" @animationend="nudging = false" />
  </Button>
</template>

<style scoped>
.comment-send { color: var(--muted); opacity: .55; transition: color var(--atlas-motion-fast) var(--atlas-ease-out), background-color var(--atlas-motion-fast) var(--atlas-ease-out), opacity var(--atlas-motion-fast) var(--atlas-ease-out), scale var(--atlas-motion-fast) var(--atlas-ease-out); }
.comment-send[data-ready] { color: var(--accent); background: var(--accent-soft); opacity: 1; }
.comment-send:active:not([aria-disabled="true"]) { scale: var(--atlas-press); }
.comment-send[aria-disabled="true"] { cursor: default; }
.comment-send:focus-visible { outline: 2px solid var(--accent); outline-offset: 2px; }
@media (hover: hover) and (pointer: fine) { .comment-send[data-ready]:hover { background: color-mix(in srgb, var(--accent) 22%, transparent); } }
.comment-send[data-nudge] .comment-send-plane { animation: atlas-send-nudge var(--atlas-motion-pop) var(--atlas-ease-out); }
</style>
