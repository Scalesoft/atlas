<script setup lang="ts">
import { createConcealedContent, type ConcealedContent } from "@atlas/markdown/concealment";
import "@atlas/markdown/concealment/style.css";
import { onBeforeUnmount, onMounted, ref, shallowRef } from "vue";
import { motionEasing, motionMs, prefersReducedMotion } from "../../motion/aiMotion";
import { brandMarks } from "../../whatsNew/brandMarks";
import ReleaseMark from "./ReleaseMark.vue";
import t from "../../i18n/components-release";

/**
 * A release's mark under the editor's invisible ink (`@atlas/markdown/concealment`, the scratch mode of a concealed block), for a
 * release whose change is fundamental (`cover: secret` in its notes, ReleaseSplash): the user scratches it away like a lottery ticket,
 * and once 55 % is open the rest parts on its own and the mark beneath unfolds and gleams. Enter or Space reveals it at once. Only the
 * ink's own look and interaction, nothing new. Only the welcome after the update shows it (the release's notes still unseen).
 * The mark mounts inside the ink's content with the first stroke, so its motion starts as it is uncovered.
 */
const props = defineProps<{ mark: string; label: string }>();

const root = ref<HTMLElement>();
/** The ink's content element while it is released: the mark is teleported there. */
const host = shallowRef<HTMLElement>();
const status = ref("");
/** Wholly uncovered: the ink's frame dissolves and the mark stands free on the splash. */
const open = ref(false);
let surface: ConcealedContent | undefined;
let observer: MutationObserver | undefined;
let done = false;

function uncovered() {
  const content = host.value?.parentElement;
  if (done || !content || content.hasAttribute("aria-hidden")) return;
  done = true;
  observer?.disconnect();
  const el = surface!.element;
  el.removeAttribute("role");
  el.tabIndex = -1;
  el.setAttribute("aria-label", props.label);
  status.value = `Odkryto: ${props.label}`;
  open.value = true;
  // Reduced motion parts no mist: the uncovered rest comes up in a short fade instead.
  if (prefersReducedMotion(root.value)) content.animate([{ opacity: .4 }, { opacity: 1 }], { duration: motionMs(root.value!, "--atlas-motion-base"), easing: motionEasing(root.value!, "--atlas-ease-out") });
}

onMounted(() => {
  void brandMarks[props.mark]?.();
  surface = createConcealedContent({
    ownerDocument: document, kind: "image", layout: "block", access: "available",
    scratchToReveal: true, completeRevealAt: .55, reconcealAfterMs: null, keepRevealed: true,
    reveal: async () => {
      const element = document.createElement("div");
      element.className = "release-cover-mark";
      host.value = element;
      return { element, dispose: () => { host.value = undefined; } };
    },
  });
  const el = surface.element;
  // A button-like control in Czech (the ink's own label names the English keys); Atlas's reduced-motion preview counts too.
  el.setAttribute("role", "button");
  el.setAttribute("aria-label", t.cover.aria(props.label));
  if (prefersReducedMotion(root.value)) el.dataset.concealedReducedMotion = "true";
  root.value!.append(el);
  observer = new MutationObserver(uncovered);
  observer.observe(el, { subtree: true, childList: true, attributes: true, attributeFilter: ["aria-hidden"] });
});
onBeforeUnmount(() => { observer?.disconnect(); done = false; surface?.destroy(); });
</script>

<template>
  <div ref="root" class="release-cover" data-release-cover :data-open="open || undefined" @dragstart.prevent>
    <Teleport v-if="host" :to="host"><ReleaseMark :mark="mark" variant="hero" /></Teleport>
    <span class="sr-only" role="status">{{ status }}</span>
  </div>
</template>

<style scoped>
/* The hero's square from the first frame; the ink fills it and mixes from the paper it lies on, as on a page (palettes.css: the
   editor's --paper is the surface). No selection and no native drag: a drag would cancel the pointer, and the ink resets on that. */
.release-cover { --paper: var(--surface); position: relative; width: min(280px, 100%); aspect-ratio: 1; user-select: none; -webkit-user-select: none; }
/* A lottery ticket: a vertical stroke scratches, it does not scroll. */
.release-cover > :deep(.md-concealed) { position: absolute; inset: 0; min-height: 0; cursor: crosshair; touch-action: none; }
.release-cover :deep(.md-concealed__content) { padding: 0; overflow: hidden; }
.release-cover :deep(.md-concealed__dismiss) { display: none; }
.release-cover :deep(:is(.md-concealed, .md-concealed__content, .md-concealed__particles, .md-concealed__skeleton)) {
  transition: background-color var(--atlas-motion-slow) var(--atlas-ease-out), border-color var(--atlas-motion-slow) var(--atlas-ease-out), opacity var(--atlas-motion-slow) var(--atlas-ease-out);
}
.release-cover[data-open] :deep(.md-concealed) { background: transparent; border-color: transparent; outline: none; cursor: default; }
.release-cover[data-open] :deep(.md-concealed__content) { background-color: transparent; }
.release-cover[data-open] :deep(:is(.md-concealed__particles, .md-concealed__skeleton)) { opacity: 0 !important; }
.release-cover > :deep(.release-mark), .release-cover :deep(.release-cover-mark), .release-cover :deep(.release-cover-mark .release-mark) { width: 100%; height: 100%; }
</style>
