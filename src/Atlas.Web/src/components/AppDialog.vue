<script lang="ts">
// The menu trigger whose item was activated last (one listener for every dialog): see `openerOf`.
let activated: { trigger: HTMLElement; at: number } | null = null;
let tracking = false;
// A lazy dialog's placeholder (data-loading) hands over to the real dialog of the same title without a second entrance.
let handover: { title: string; at: number } | null = null;
function noteMenuActivation(event: Event) {
  if (event instanceof KeyboardEvent && event.key !== "Enter" && event.key !== " ") return;
  const menu = event.target instanceof Element ? event.target.closest('[role="menu"]') : null;
  const trigger = menu ? document.getElementById(menu.getAttribute("aria-labelledby") ?? "") : null;
  if (trigger?.getAttribute("aria-haspopup") === "menu") activated = { trigger, at: performance.now() };
}
function trackMenuActivation() {
  if (tracking) return;
  tracking = true;
  document.addEventListener("click", noteMenuActivation, true);
  document.addEventListener("keydown", noteMenuActivation, true);
}
</script>

<script setup lang="ts">
import { toastsInside } from "@/lib/toastHost";
import { nextTick, onBeforeUnmount, onMounted, provide, ref, useId, watch } from "vue";
import { animateAtlas, foldOut, motionMs, unfoldedClip, unfoldFrom } from "../motion/aiMotion";
import AtlasIcon from "./AtlasIcon.vue";
import { Button } from "./ui/button";
import { dialogLayersKey } from "./ui/dialogLayers";
import t from "../i18n/components-a";

// One dialog chrome for the whole wiki: title on the left, a single icon close on the
// right, no eyebrow, and the overlay disappears when the SPA route changes underneath it.
// size "side" is the panel that slides in from the right over the page at full height (Historie,
// Čtenost stránky): for a tool that works beside the page rather than a question to answer. Its
// content lays itself out (flex column; scroll its own body) and may set --app-dialog-side-width.
const props = withDefaults(
  defineProps<{
    title: string;
    open: boolean;
    size?: "default" | "wide" | "side";
    closeLabel?: string;
    ariaLabel?: string;
    headerClass?: string;
    /** A palette that grows out of its trigger (the search, prototype scene 1), in the time of every other dialog: selector of the trigger. */
    unfoldTrigger?: string;
  }>(),
  { size: "default", closeLabel: () => t.close, ariaLabel: undefined, headerClass: undefined, unfoldTrigger: undefined },
);
const emit = defineEmits<{ close: []; closed: [] }>();
const dialog = ref<HTMLDialogElement>();
const escapeLayers: Array<(event: KeyboardEvent) => void> = [];
provide(dialogLayersKey, {
  target: dialog,
  registerEscape(layer) {
    escapeLayers.push(layer);
    return () => { const index = escapeLayers.indexOf(layer); if (index >= 0) escapeLayers.splice(index, 1); };
  },
});
function nestedEscape(event: KeyboardEvent) {
  if (event.key !== "Escape" || !(event.target instanceof Element) || event.target.closest("dialog") !== dialog.value) return;
  const layer = escapeLayers.at(-1);
  if (!layer) return;
  if (event.isComposing || event.target instanceof HTMLSelectElement) { event.stopPropagation(); return; }
  layer(event);
  event.preventDefault();
  event.stopPropagation();
}
const titleId = `app-dialog-title-${useId()}`;
const focusables = 'a[href], button:not(:disabled), input:not(:disabled), select:not(:disabled), textarea:not(:disabled), [tabindex]:not([tabindex="-1"])';
let opener: HTMLElement | null = null;
let folding: Animation | null = null;
let entering: Animation | null = null;
let backdrop: Animation | null = null;

function supportsBackdropMotion() {
  return typeof KeyframeEffect !== "undefined" && "pseudoElement" in KeyframeEffect.prototype;
}
function backdropOpacity(root: HTMLDialogElement) {
  return supportsBackdropMotion() ? getComputedStyle(root, "::backdrop").opacity : "1";
}
function animateBackdrop(root: HTMLDialogElement, from: string | number, to: number) {
  if (!supportsBackdropMotion()) return; // The existing CSS entrance remains the fallback.
  root.setAttribute("data-animated-backdrop", "");
  backdrop?.cancel();
  backdrop = animateAtlas(root, [{ opacity: from }, { opacity: to }], {
    pseudoElement: "::backdrop", duration: motionMs(root, "--atlas-motion-surface"), fill: "both",
  });
  if (!backdrop) root.removeAttribute("data-animated-backdrop");
}

function currentFrame(root: HTMLDialogElement): Keyframe {
  const style = getComputedStyle(root);
  return { opacity: style.opacity, transform: style.transform, clipPath: style.clipPath };
}
function cancelMotion() {
  const previous = folding;
  folding = null;
  previous?.cancel();
  entering?.cancel();
  entering = null;
  backdrop?.cancel();
  backdrop = null;
}

function focusInside() {
  const root = dialog.value;
  if (!root?.open || !props.open || folding) return;
  // The logical place (keyboard pass): an autofocus control, else the first field to type in, else the first control of the
  // content; the header's close button only when the content has none.
  const content = contentOf(root);
  const field = content.find(item => item.matches('textarea, select, input:not([type="checkbox"], [type="radio"], [type="button"], [type="submit"])'));
  const target = root.querySelector<HTMLElement>("[autofocus]") ?? field ?? content[0] ?? root.querySelector<HTMLElement>(focusables);
  target?.focus({ preventScroll: true });
  awaitContent?.disconnect();
  if (!target || content.length) return;
  // The content is still loading (Koš, Automatický export PDF): the first control it brings takes over from the close
  // button, as long as the reader has not moved yet (UX cycle 5, AD5-5).
  const observer = new MutationObserver(() => {
    if (!root.open || document.activeElement !== target) observer.disconnect();
    else if (contentOf(root).length) { observer.disconnect(); focusInside(); }
  });
  observer.observe(root, { childList: true, subtree: true, attributes: true, attributeFilter: ["disabled"] });
  awaitContent = observer;
  setTimeout(() => observer.disconnect(), 5000);
}
let awaitContent: MutationObserver | undefined;
function contentOf(root: HTMLElement) {
  return [...root.querySelectorAll<HTMLElement>(focusables)].filter(item => !item.closest(".app-dialog-header") && (item as HTMLInputElement).type !== "hidden");
}
// A dialog opened from a menu item: the item disappears with its closing menu, so focus
// returns to the menu's trigger (the menu content is labelled by it).
function openerOf(active: Element | null) {
  if (!(active instanceof HTMLElement) || active === document.body) {
    // The menu goes inert as it closes, so when the owner opens the dialog a moment later (after an await) focus
    // is already on <body>: the trigger of the menu whose item was just activated is the opener.
    const recent = activated && performance.now() - activated.at < 1500 && activated.trigger.isConnected ? activated.trigger : null;
    return recent ?? (active instanceof HTMLElement ? active : null);
  }
  const trigger = document.getElementById(active.closest('[role="menu"]')?.getAttribute("aria-labelledby") ?? "");
  return trigger?.getAttribute("aria-haspopup") === "menu" ? trigger : active;
}
/** An unfolding palette: grows out of its trigger (opacity alone when there is none visible). */
function enter(root: HTMLDialogElement, from?: Keyframe, backdropFrom: string | number = 0) {
  animateBackdrop(root, backdropFrom, 1);
  if (from) {
    // A fold that interrupted the unfolding kept its clip: the way back ends where the unfolding does, on the clip
    // that already holds the shadow, so nothing appears when the animation is removed.
    const clipPath = from.clipPath && from.clipPath !== "none" ? unfoldedClip(root) : "none";
    entering = animateAtlas(root, [from, { opacity: 1, transform: "none", clipPath }], { duration: motionMs(root, "--atlas-motion-surface") });
  } else if (props.unfoldTrigger !== undefined) {
    const trigger = props.unfoldTrigger ? document.querySelector(props.unfoldTrigger) : null;
    entering = unfoldFrom(root, trigger);
  } else if (props.size === "side") {
    // The side panel slides in 3 rise tokens and fades (slow, ease-out); reduced motion keeps the fade only.
    entering = animateAtlas(root, [{ opacity: 0, transform: "translateX(24px)" }, { opacity: 1, transform: "none" }], { duration: motionMs(root, "--atlas-motion-slow") });
  } else {
    // Board "Pohyb v refreshi": the dialog grows from .98 and 8 px over the base duration, behind the 160 ms backdrop.
    entering = animateAtlas(root, [{ opacity: 0, transform: "translateY(8px) scale(.98)" }, { opacity: 1, transform: "none" }], { duration: motionMs(root, "--atlas-motion-base") });
  }
}
/** A plain or side dialog leaves the way it came, in the shorter surface duration, inert at once (the palette's `foldOut` is the same exit and keeps the clip of an interrupted unfolding). */
function leaveOut(root: HTMLDialogElement, from: Keyframe) {
  root.setAttribute("inert", "");
  root.setAttribute("aria-hidden", "true");
  const to = props.size === "side" ? "translateX(24px)" : "translateY(8px) scale(.98)";
  return animateAtlas(root, [from, { opacity: 0, transform: to }], { duration: motionMs(root, "--atlas-motion-surface"), fill: "forwards" });
}
function finishHide(root: HTMLDialogElement) {
  cancelMotion();
  awaitContent?.disconnect();
  root.removeAttribute("inert");
  root.removeAttribute("aria-hidden");
  if (root.open) root.close();
  const restore = opener;
  opener = null;
  if (restore?.isConnected) restore.focus({ preventScroll: true });
  emit("closed");
}
function show() {
  const root = dialog.value;
  if (!root) return;
  if (folding) {
    // Reverse from the frame on screen, rather than flashing the entrance's first frame.
    const from = currentFrame(root);
    const backdropFrom = backdropOpacity(root);
    cancelMotion();
    root.removeAttribute("inert");
    root.removeAttribute("aria-hidden");
    toastsInside(root, true);
    enter(root, from, backdropFrom);
    void nextTick(focusInside);
    return;
  }
  if (root.open) return;
  opener = openerOf(document.activeElement);
  openedAt = location.hash;
  root.showModal();
  toastsInside(root, true);
  if (handover?.title === props.title && performance.now() - handover.at < 100) root.setAttribute("data-animated-backdrop", "");
  else enter(root);
  void nextTick(focusInside);
}
function hide(animated = true) {
  const root = dialog.value;
  if (!root?.open) return;
  toastsInside(root, false);
  if (folding) { if (!animated) finishHide(root); return; }
  const from = currentFrame(root);
  const backdropFrom = backdropOpacity(root);
  entering?.cancel();
  entering = null;
  if (animated) animateBackdrop(root, backdropFrom, 0);
  const fold = animated ? (props.unfoldTrigger !== undefined ? foldOut(root, from) : leaveOut(root, from)) : null;
  if (!fold) { finishHide(root); return; }
  // Closed for the user at once (inert, hidden), removed when the fold ends.
  folding = fold;
  const done = () => { if (folding === fold) finishHide(root); };
  fold.addEventListener("finish", done);
  fold.addEventListener("cancel", () => { if (folding === fold) finishHide(root); });
  // A page whose animations do not run (a hidden or background tab) still closes and gives the focus back to the opener.
  setTimeout(done, (Number(fold.effect?.getComputedTiming().endTime) || 0) + 100);
}
// Escape and the close button only ask; the owner decides (it may be busy or dirty).
function requestClose() { emit("close"); }
// A click on the backdrop asks too, like any dialog; only a press that starts and ends there, so a text selection dragged
// out of the dialog does not close it. The backdrop is the <dialog> itself, hit outside its box.
let pressedBackdrop = false;
function onBackdrop(event: MouseEvent) {
  const root = dialog.value;
  if (!root || event.target !== root) return false;
  const box = root.getBoundingClientRect();
  return event.clientX < box.left || event.clientX > box.right || event.clientY < box.top || event.clientY > box.bottom;
}
function backdropDown(event: MouseEvent) { pressedBackdrop = onBackdrop(event); }
function backdropClick(event: MouseEvent) {
  if (pressedBackdrop && onBackdrop(event)) requestClose();
  pressedBackdrop = false;
}
// A route change is not a request — the overlay must never survive the page underneath it. A hashchange that leaves the
// address where the dialog opened is none: the browser's Back fires popstate, then hashchange, and a leave question opened on
// popstate has already put the address back (U2-2).
let openedAt = "";
function routeChanged() {
  if (!dialog.value?.open || location.hash === openedAt) return;
  hide(false);
  emit("close");
}
watch(() => props.open, value => (value ? show() : hide()));
onMounted(() => {
  window.addEventListener("hashchange", routeChanged);
  trackMenuActivation();
  if (props.open) show();
});
onBeforeUnmount(() => {
  window.removeEventListener("hashchange", routeChanged);
  if (dialog.value?.hasAttribute("data-loading")) handover = { title: props.title, at: performance.now() };
  hide(false);
});
</script>

<template>
  <dialog
    ref="dialog"
    class="app-dialog"
    :data-size="size"
    :aria-label="ariaLabel"
    :aria-labelledby="ariaLabel ? undefined : titleId"
    @cancel.prevent="requestClose"
    @keydown.capture="nestedEscape"
    @pointerdown="backdropDown"
    @click="backdropClick"
  >
    <header class="app-dialog-header" :class="headerClass">
      <h2 :id="titleId">{{ title }}</h2>
      <Button variant="ghost" size="icon-sm" type="button" class="text-muted-foreground" :aria-label="closeLabel" @click="requestClose">
        <AtlasIcon name="close" :size="18" />
      </Button>
      <slot name="header-extra" />
    </header>
    <slot />
    <footer v-if="$slots.footer" class="app-dialog-footer"><slot name="footer" /></footer>
  </dialog>
</template>
