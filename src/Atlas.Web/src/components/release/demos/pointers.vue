<script setup lang="ts">
import { onBeforeUnmount, ref } from "vue";
import "../../../collaborationPresence.css";
import { userColor } from "../../../live/objectPresence";
import CollaborationPointer from "../../CollaborationPointer.vue";
import { createPointerGlide } from "../../../live/pointerGlide";
import t from "../../../i18n/components-release";
import { typeText, useDemoLoop, type At } from "./demoClock";

// Co-editors on one page, as in the promo film: Jana's pointer glides in and she writes; Anna selects a word and writes over it. The
// pointers are Atlas's own (CollaborationPointer), carried between samples by the same spring (pointerGlide) as on a live page.
const d = t.demos.pointers;
// Sample people in their colours (userColor, as their carets and avatars have them).
const people = [{ name: "Jana", color: userColor("00000004") }, { name: "Anna", color: userColor("00000007") }] as const;
const root = ref<HTMLElement>();
const janaAt = ref<HTMLElement>();
const annaAt = ref<HTMLElement>();
const pointerViews = ref<{ $el: HTMLElement }[]>([]);
const pointer = (who: number) => pointerViews.value[who]!.$el;
const jana = ref(""), anna = ref(d.before), selected = ref(false), carets = ref([false, false]), pressed = ref([false, false]), fading = ref(false);
const glide = createPointerGlide(() => root.value!, (element, x, y) => { element.style.transform = `translate3d(${x}px, ${y}px, 0)`; });
onBeforeUnmount(() => glide.stop());

/** Where a span starts or ends in the demo's box. */
function point(el: HTMLElement | undefined, end = false): [number, number] {
  const box = root.value!.getBoundingClientRect(), rect = el!.getBoundingClientRect();
  return [(end ? rect.right : rect.left) - box.left, rect.top - box.top + rect.height * .7];
}
/** A hand moves the pointer: samples every 50 ms along a slight arc, as they come over the network. */
function move(at: At, from: number, who: number, a: [number, number], b: [number, number], ms: number) {
  for (let s = 0; s <= ms; s += 50) {
    const k = s / ms, e = k * k * (3 - 2 * k), bow = Math.sin(Math.PI * k) * 14;
    at(from + s, () => glide.to(pointer(who), a[0] + (b[0] - a[0]) * e, a[1] + (b[1] - a[1]) * e - bow));
  }
}
const press = (at: At, ms: number, who: number) => {
  at(ms, () => { pressed.value[who] = true; });
  at(ms + 260, () => { pressed.value[who] = false; });
};

useDemoLoop(root, (at) => {
  const width = root.value!.clientWidth, height = root.value!.clientHeight;
  const budget = point(janaAt.value, true), word = point(annaAt.value), wordEnd = point(annaAt.value, true);
  const janaOut: [number, number] = [-30, 16], annaOut: [number, number] = [width + 30, height - 10];
  const janaRest: [number, number] = [Math.max(budget[0] + 110, width * .62), budget[1] - 4], annaRest: [number, number] = [Math.max(wordEnd[0] + 90, width * .7), wordEnd[1] + 16];
  at(0, () => {
    fading.value = false; jana.value = ""; anna.value = d.before; selected.value = false; carets.value = [false, false];
    glide.to(pointer(0), ...janaOut, true);
    glide.to(pointer(1), ...annaOut, true);
  });
  move(at, 200, 0, janaOut, budget, 900);
  press(at, 1150, 0);
  at(1250, () => { carets.value[0] = true; });
  move(at, 1350, 0, budget, janaRest, 600);
  typeText(at, 1400, d.approved, (text) => { jana.value = text; });
  move(at, 900, 1, annaOut, word, 1000);
  press(at, 1950, 1);
  move(at, 2000, 1, word, wordEnd, 450);
  at(2150, () => { selected.value = true; });
  at(2750, () => { selected.value = false; anna.value = ""; carets.value[1] = true; });
  typeText(at, 2800, d.after, (text) => { anna.value = text; });
  move(at, 3500, 1, wordEnd, annaRest, 600);
  at(6400, () => { fading.value = true; });
  move(at, 6300, 0, janaRest, janaOut, 700);
  move(at, 6300, 1, annaRest, annaOut, 700);
  return 7200;
}, () => {
  jana.value = d.approved; anna.value = d.after; carets.value = [true, true];
  const budget = point(janaAt.value, true), wordEnd = point(annaAt.value, true);
  glide.to(pointer(0), Math.max(budget[0] + 110, root.value!.clientWidth * .62), budget[1] - 4, true);
  glide.to(pointer(1), Math.max(wordEnd[0] + 90, root.value!.clientWidth * .7), wordEnd[1] + 16, true);
});
</script>

<template>
  <div ref="root" class="release-demo demo-pointers" aria-hidden="true">
    <div class="demo-doc" :class="{ 'is-fading': fading }">
      <p class="demo-doc-title">{{ d.title }}</p>
      <p>{{ d.budget }}<span ref="janaAt" />{{ jana }}<span v-if="carets[0]" class="demo-caret" :style="{ '--collaboration-color': people[0].color }"><span class="collaboration-presence__label">Jana</span></span></p>
      <p>{{ d.launch }}<span ref="annaAt" class="demo-word" :class="{ 'is-selected': selected }" :style="{ '--collaboration-color': people[1].color }">{{ anna }}</span><span v-if="carets[1]" class="demo-caret" :style="{ '--collaboration-color': people[1].color }"><span class="collaboration-presence__label">Anna</span></span></p>
      <p class="demo-doc-muted">{{ d.next }}</p>
    </div>
    <CollaborationPointer v-for="(person, index) in people" :key="person.name" ref="pointerViews" :name="person.name" :color="person.color"
      :class="{ 'is-pressed': pressed[index] }" />
  </div>
</template>

<style scoped>
.demo-pointers { position: relative; height: 100%; overflow: hidden; }
.demo-doc { height: 100%; padding: 6px 14px 4px; font-size: 13px; line-height: 2.5; transition: opacity var(--atlas-motion-slow) var(--atlas-ease-out); }
.demo-doc.is-fading { opacity: .35; }
.demo-doc p { margin: 0; white-space: nowrap; }
.demo-doc-title { font-weight: 700; font-size: 14px; }
.demo-doc-muted { color: var(--muted); }
.demo-word { border-radius: 3px; transition: background-color var(--atlas-motion-base) var(--atlas-ease-out); }
.demo-word.is-selected { background: color-mix(in srgb, var(--collaboration-color) 28%, transparent); }
/* The co-editor's caret of a live page (collaborationPresence.css), inline after what they typed. */
.demo-caret { position: relative; display: inline-block; width: 2px; height: 1.25em; margin-left: 1px; vertical-align: -.25em; background: var(--collaboration-color); animation: st-fade-in var(--atlas-motion-fast) var(--atlas-ease-out); }
.demo-caret .collaboration-presence__label { top: -1.2rem; font-size: 10px; }
</style>
