<script setup lang="ts">
import { computed, ref } from "vue";
import { collaboratorChoices, themedCollaboratorColor } from "../../../live/collaboratorColors";
import t from "../../../i18n/components-release";
import { useDemoLoop } from "./demoClock";

// Moje barva (5.2): one palette of twelve colours for carets, avatars and pointers. The choice moves from Automaticky to a colour and
// your caret and avatar take it; a colleague who joins the page keeps a colour of their own, as two people at one page never share one.
const d = t.demos.color;
// The collaborator palette itself, in the variant the surface draws (dark in the dark theme), as Moje barva shows it.
const palette = computed(() => collaboratorChoices.map(choice => themedCollaboratorColor(choice.swatch.light)));
const root = ref<HTMLElement>();
const pick = ref(-1), other = ref(false);
const mine = computed(() => palette.value[pick.value < 0 ? 1 : pick.value]!);
const theirs = computed(() => palette.value[6]!);

useDemoLoop(root, (at) => {
  at(0, () => { pick.value = -1; other.value = false; });
  [0, 3, 5].forEach((index, step) => at(900 + step * 700, () => { pick.value = index; }));
  at(3300, () => { other.value = true; });
  at(6600, () => { other.value = false; });
  return 7200;
}, () => { pick.value = 5; other.value = true; });
</script>

<template>
  <div ref="root" class="release-demo demo-color" aria-hidden="true">
    <p class="demo-color-title">{{ d.title }}</p>
    <div class="demo-color-swatches">
      <span class="demo-color-auto" :class="{ 'is-picked': pick < 0 }">{{ d.auto }}</span>
      <span v-for="(colour, index) in palette" :key="colour" class="demo-color-swatch" :class="{ 'is-picked': pick === index }" :style="{ background: colour }" />
    </div>
    <p class="demo-color-line">
      {{ d.line }}<span class="demo-color-caret" :style="{ '--demo-colour': mine }"><span>{{ d.you }}</span></span>
      <span class="demo-color-caret is-other" :class="{ 'is-shown': other }" :style="{ '--demo-colour': theirs }"><span>{{ d.other }}</span></span>
    </p>
    <span class="demo-color-avatars">
      <span :style="{ background: mine }">{{ d.you.charAt(0) }}</span>
      <span class="is-other" :class="{ 'is-shown': other }" :style="{ background: theirs }">PS</span>
    </span>
  </div>
</template>

<style scoped>
.demo-color { position: relative; display: grid; align-content: start; gap: 10px; height: 100%; padding: 12px 14px; overflow: hidden; font-size: 13px; }
.demo-color-title { margin: 0; font-weight: 700; font-size: 14px; }
.demo-color-swatches { display: flex; flex-wrap: wrap; align-items: center; gap: 5px; }
.demo-color-auto { padding: 2px 8px; border: 1px solid var(--line); border-radius: 999px; color: var(--muted); font-size: 11px; transition: box-shadow var(--atlas-motion-base) var(--atlas-ease-out), color var(--atlas-motion-base) var(--atlas-ease-out); }
.demo-color-swatch { width: 13px; height: 13px; border-radius: 50%; transition: box-shadow var(--atlas-motion-base) var(--atlas-ease-out); }
.demo-color-auto.is-picked { color: var(--text); box-shadow: 0 0 0 2px var(--accent); }
.demo-color-swatch.is-picked { box-shadow: 0 0 0 2px var(--surface), 0 0 0 4px currentColor; color: var(--text); }
.demo-color-line { margin: 6px 0 0; white-space: nowrap; }
.demo-color-caret { position: relative; display: inline-block; width: 2px; height: 1.25em; margin-left: 2px; vertical-align: -.25em; background: var(--demo-colour); transition: background-color var(--atlas-motion-base) var(--atlas-ease-out); }
.demo-color-caret > span {
  position: absolute; top: -1.35em; left: 0; padding: 0 5px; border-radius: 4px 4px 4px 0; background: var(--demo-colour); color: #fff; font-size: 10px; line-height: 1.5; white-space: nowrap;
  transition: background-color var(--atlas-motion-base) var(--atlas-ease-out);
}
.demo-color-caret.is-other { margin-left: 36px; }
.demo-color-caret.is-other, .demo-color-avatars .is-other { opacity: 0; transition: opacity var(--atlas-motion-slow) var(--atlas-ease-out); }
.demo-color-caret.is-other.is-shown, .demo-color-avatars .is-other.is-shown { opacity: 1; }
.demo-color-avatars { position: absolute; top: 10px; right: 14px; display: flex; }
.demo-color-avatars > span {
  display: grid; place-items: center; width: 22px; height: 22px; border: 2px solid var(--surface); border-radius: 50%; color: #fff; font-size: 9px; font-weight: 700;
  transition: background-color var(--atlas-motion-base) var(--atlas-ease-out), opacity var(--atlas-motion-slow) var(--atlas-ease-out);
}
.demo-color-avatars > span + span { margin-left: -6px; }
</style>
