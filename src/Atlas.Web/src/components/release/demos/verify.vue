<script setup lang="ts">
import { computed, onBeforeUnmount, ref } from "vue";
import "../../../collaborationPresence.css";
import { themedUserColor } from "../../../live/collaboratorColors";
import { verificationMark } from "../../../verification/verificationMark";
import { createPointerGlide } from "../../../live/pointerGlide";
import AtlasIcon from "../../AtlasIcon.vue";
import CollaborationPointer from "../../CollaborationPointer.vue";
import StatusBadge from "../../settings/StatusBadge.vue";
import t from "../../../i18n/components-release";
import { useDemoLoop, type At } from "./demoClock";

// Verified content (5.2): the page's chip at its title passes from „Ověřeno“ through „Vyprší za 14 dní“ (a reminder goes to its owner)
// to „Ověření vypršelo“; the owner's pointer opens the chip, presses Ověřit and the page is verified again, valid for the next interval.
const d = t.demos.verify;
const jana = computed(() => themedUserColor("00000004"));
type State = "verified" | "expiring" | "expired";
const root = ref<HTMLElement>();
const chip = ref<HTMLElement>();
const button = ref<HTMLElement>();
const hand = ref<{ $el: HTMLElement }>();
const state = ref<State>("verified"), renewed = ref(false), reminder = ref(false), open = ref(false), pressed = ref(false), fresh = ref(false);
// The chip's icon and tone from the product's one mapping (the page chip, search, link chips).
const badge = computed(() => {
  const mark = verificationMark(state.value)!;
  return { tone: mark.warn ? "warning" : "neutral", icon: mark.icon, label: d[state.value] } as const;
});
const facts = computed(() => (renewed.value ? `${d.today} · ${d.until(d.dates[1]!)}` : d.until(d.dates[0]!)));
const glide = createPointerGlide(() => root.value!, (element, x, y) => { element.style.transform = `translate3d(${x}px, ${y}px, 0)`; });
onBeforeUnmount(() => glide.stop());

/** The middle of an element in the demo's box. */
function centre(el: HTMLElement | undefined): [number, number] {
  const box = root.value!.getBoundingClientRect(), rect = el!.getBoundingClientRect();
  return [rect.left - box.left + rect.width * .55, rect.top - box.top + rect.height * .6];
}
/** A hand moves the pointer along a slight arc, sampled every 50 ms as over the network. */
function move(at: At, from: number, a: [number, number], b: [number, number], ms: number) {
  for (let s = 0; s <= ms; s += 50) {
    const k = s / ms, e = k * k * (3 - 2 * k), bow = Math.sin(Math.PI * k) * 12;
    at(from + s, () => glide.to(hand.value!.$el, a[0] + (b[0] - a[0]) * e, a[1] + (b[1] - a[1]) * e - bow));
  }
}

useDemoLoop(root, (at) => {
  const out: [number, number] = [root.value!.clientWidth + 30, root.value!.clientHeight + 20];
  at(0, () => { state.value = "verified"; renewed.value = false; reminder.value = false; open.value = false; fresh.value = false; glide.to(hand.value!.$el, ...out, true); });
  at(1300, () => { state.value = "expiring"; reminder.value = true; });
  at(3000, () => { reminder.value = false; });
  at(3300, () => { state.value = "expired"; });
  move(at, 3900, out, centre(chip.value), 900);
  at(4900, () => { pressed.value = true; });
  at(5100, () => { pressed.value = false; open.value = true; });
  at(5250, () => move(at, 0, centre(chip.value), centre(button.value), 450));
  at(5900, () => { pressed.value = true; });
  at(6100, () => { pressed.value = false; open.value = false; state.value = "verified"; renewed.value = true; fresh.value = true; });
  at(7400, () => move(at, 0, centre(button.value), out, 700));
  return 9000;
}, () => { state.value = "verified"; renewed.value = true; });
</script>

<template>
  <div ref="root" class="release-demo demo-verify" aria-hidden="true">
    <div class="demo-verify-head">
      <span class="demo-verify-title">{{ d.title }}</span>
      <span ref="chip" class="demo-verify-chip" :class="{ 'is-fresh': fresh }"><StatusBadge :key="state" v-bind="badge" size="sm" /></span>
    </div>
    <p class="demo-verify-facts">{{ d.steward }} <b>{{ d.stewardName }}</b> · {{ facts }}</p>
    <span class="demo-verify-text" /><span class="demo-verify-text is-short" />
    <div class="demo-verify-pop" :class="{ 'is-open': open }">
      <span ref="button" class="demo-verify-button" :class="{ 'is-pressed': pressed && open }"><AtlasIcon name="shield-check" :size="13" />{{ d.verify }}<kbd>V</kbd></span>
    </div>
    <div class="demo-verify-note" :class="{ 'is-shown': reminder }"><AtlasIcon name="clock" :size="13" />{{ d.reminder }}</div>
    <CollaborationPointer ref="hand" name="Jana" :color="jana" :class="{ 'is-pressed': pressed }" />
  </div>
</template>

<style scoped>
.demo-verify { position: relative; display: grid; align-content: start; gap: 6px; height: 100%; padding: 12px 14px; overflow: hidden; font-size: 13px; }
.demo-verify-head { display: flex; align-items: center; gap: 8px; min-width: 0; }
.demo-verify-title { overflow: hidden; font-weight: 700; font-size: 14px; text-overflow: ellipsis; white-space: nowrap; }
.demo-verify-chip { display: inline-flex; flex: none; border-radius: 7px; transition: box-shadow var(--atlas-motion-slow) var(--atlas-ease-out); }
.demo-verify-chip :deep(.status-badge) { animation: st-fade-in var(--atlas-motion-base) var(--atlas-ease-out); }
/* Verified again: a ring of the accent breathes out of the chip once. */
.demo-verify-chip.is-fresh { box-shadow: 0 0 0 4px color-mix(in srgb, var(--accent) 0%, transparent); }
.demo-verify-chip:not(.is-fresh) { box-shadow: 0 0 0 0 color-mix(in srgb, var(--accent) 35%, transparent); }
.demo-verify-facts { margin: 0; overflow: hidden; color: var(--muted); font-size: 12px; text-overflow: ellipsis; white-space: nowrap; }
.demo-verify-facts b { color: var(--text); font-weight: 600; }
.demo-verify-text { display: block; height: 7px; margin-top: 6px; border-radius: 4px; background: color-mix(in srgb, var(--text) 8%, transparent); }
.demo-verify-text.is-short { width: 62%; margin-top: 2px; }
.demo-verify-pop {
  position: absolute; top: 40px; left: 14px; padding: 8px; border: 1px solid var(--line); border-radius: 10px; background: var(--surface);
  box-shadow: var(--refresh-card-shadow-hover, 0 6px 18px rgb(0 0 0 / .08));
  opacity: 0; scale: var(--st-pop-from, .98); transform-origin: top left; pointer-events: none;
  transition: opacity var(--atlas-motion-base) var(--atlas-ease-out), scale var(--atlas-motion-base) var(--atlas-ease-out);
}
.demo-verify-pop.is-open { opacity: 1; scale: 1; }
.demo-verify-button {
  display: inline-flex; align-items: center; gap: 5px; padding: 4px 10px; border-radius: 7px; background: var(--accent); color: var(--on-accent);
  font-size: 12px; font-weight: 600; transition: scale var(--atlas-motion-fast) var(--atlas-ease-out);
}
.demo-verify-button.is-pressed { scale: var(--atlas-press, .97); }
.demo-verify-button kbd { padding: 0 4px; border-radius: 4px; background: color-mix(in srgb, currentColor 18%, transparent); font: 600 10px var(--font-mono, ui-monospace, monospace); }
.demo-verify-note {
  position: absolute; right: 12px; bottom: 10px; display: flex; align-items: center; gap: 6px; max-width: calc(100% - 24px); padding: 5px 10px;
  border: 1px solid var(--line); border-radius: 10px; background: var(--surface); color: var(--muted); font-size: 11.5px; white-space: nowrap;
  box-shadow: var(--refresh-card-shadow-hover, 0 6px 18px rgb(0 0 0 / .08));
  opacity: 0; translate: 0 var(--atlas-rise, 8px); transition: opacity var(--atlas-motion-slow) var(--atlas-ease-out), translate var(--atlas-motion-slow) var(--atlas-ease-out);
}
.demo-verify-note.is-shown { opacity: 1; translate: none; }
</style>
