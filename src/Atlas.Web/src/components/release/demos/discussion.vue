<script setup lang="ts">
import { ref } from "vue";
import { userColor } from "../../../live/objectPresence";
import t from "../../../i18n/components-release";
import { typeText, useDemoLoop } from "./demoClock";

// The page's discussion (5.2): a colleague's new comment drops in under the co-editors' avatars, a quick reply is written under it and
// sent with the paper plane, and the bubble flies off to the right, where the dock's Diskuse holds the whole thread.
const d = t.demos.discussion;
const people = [{ initials: "JN", color: userColor("00000004") }, { initials: "AK", color: userColor("00000007") }, { initials: "PS", color: userColor("00000002") }];
const root = ref<HTMLElement>();
const shown = ref(false), replying = ref(false), answer = ref(""), sending = ref(false), docked = ref(false), dock = ref(false);

useDemoLoop(root, (at) => {
  at(0, () => { shown.value = false; replying.value = false; answer.value = ""; sending.value = false; docked.value = false; dock.value = false; });
  at(400, () => { shown.value = true; });
  at(1700, () => { replying.value = true; });
  const end = typeText(at, 2000, d.answer, (text) => { answer.value = text; }, 60);
  at(end + 300, () => { sending.value = true; });
  at(end + 500, () => { sending.value = false; docked.value = true; dock.value = true; });
  at(end + 2700, () => { dock.value = false; });
  return end + 3400;
}, () => { shown.value = true; replying.value = true; answer.value = d.answer; });
</script>

<template>
  <div ref="root" class="release-demo demo-discussion" aria-hidden="true">
    <div class="demo-discussion-bar">
      <span class="demo-discussion-title">{{ d.title }}</span>
      <span class="demo-discussion-avatars"><span v-for="person in people" :key="person.initials" :style="{ background: person.color }">{{ person.initials }}</span></span>
    </div>
    <div class="demo-discussion-bubble" :class="{ 'is-shown': shown && !docked, 'is-docked': docked }">
      <span class="demo-discussion-avatar" :style="{ background: people[0]!.color }">{{ people[0]!.initials }}</span>
      <span class="demo-discussion-body">
        <b>{{ d.name }}</b>
        <span>{{ d.text }}</span>
        <span v-if="!replying" class="demo-discussion-reply">{{ d.reply }}</span>
        <span v-else class="demo-discussion-field">
          <span class="demo-discussion-answer">{{ answer }}<i /></span>
          <span class="demo-discussion-send" :class="{ 'is-pressed': sending }" :title="d.send">
            <svg viewBox="0 0 24 24" width="13" height="13"><path d="M14.5 21.7a.5.5 0 0 0 .9 0l6.5-19a.5.5 0 0 0-.6-.6l-19 6.5a.5.5 0 0 0 0 .9l7.9 3.2a2 2 0 0 1 1.1 1.1z" /><path d="m21.9 2.1-11 11" /></svg>
          </span>
        </span>
      </span>
    </div>
    <span class="demo-discussion-dock" :class="{ 'is-shown': dock }">{{ d.dock }} · 3</span>
  </div>
</template>

<style scoped>
.demo-discussion { position: relative; height: 100%; padding: 10px 14px; overflow: hidden; font-size: 12.5px; }
.demo-discussion-bar { display: flex; align-items: center; justify-content: space-between; gap: 8px; }
.demo-discussion-title { overflow: hidden; font-weight: 700; font-size: 14px; text-overflow: ellipsis; white-space: nowrap; }
.demo-discussion-avatars { display: flex; flex: none; }
.demo-discussion-avatars > span, .demo-discussion-avatar {
  display: grid; place-items: center; width: 22px; height: 22px; border: 2px solid var(--surface); border-radius: 50%; color: #fff; font-size: 9px; font-weight: 700;
}
.demo-discussion-avatars > span + span { margin-left: -6px; }
/* The bubble drops in from the corner of the avatars, then flies off to the right, toward the dock. */
.demo-discussion-bubble {
  position: absolute; top: 40px; right: 14px; display: flex; gap: 8px; width: min(300px, calc(100% - 28px)); padding: 8px 10px;
  border: 1px solid var(--line); border-radius: 12px 4px 12px 12px; background: var(--surface); box-shadow: var(--refresh-card-shadow-hover, 0 6px 18px rgb(0 0 0 / .08));
  opacity: 0; translate: 0 calc(var(--atlas-rise, 8px) * -1); transform-origin: top right;
  transition: opacity var(--atlas-motion-slow) var(--atlas-ease-out), translate var(--atlas-motion-slow) var(--atlas-ease-out);
}
.demo-discussion-bubble.is-shown { opacity: 1; translate: none; }
.demo-discussion-bubble.is-docked { opacity: 0; translate: calc(var(--atlas-rise, 8px) * 15) 0; }
.demo-discussion-avatar { flex: none; width: 24px; height: 24px; border: 0; }
.demo-discussion-body { display: grid; gap: 2px; min-width: 0; }
.demo-discussion-reply { justify-self: start; margin-top: 2px; color: var(--accent); font-size: 11.5px; font-weight: 600; }
.demo-discussion-field { display: flex; align-items: center; gap: 6px; margin-top: 4px; padding: 3px 4px 3px 8px; border: 1px solid var(--line); border-radius: 8px; animation: st-fade-in var(--atlas-motion-base) var(--atlas-ease-out); }
.demo-discussion-answer { flex: 1; min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.demo-discussion-answer i { display: inline-block; width: 1.5px; height: 1.1em; margin-left: 1px; vertical-align: -.2em; background: var(--accent); }
.demo-discussion-send {
  display: grid; flex: none; place-items: center; width: 22px; height: 22px; border-radius: 6px; background: var(--accent); color: var(--accent-contrast, #fff);
  transition: scale var(--atlas-motion-fast) var(--atlas-ease-out);
}
.demo-discussion-send svg { fill: none; stroke: currentColor; stroke-width: 2; stroke-linecap: round; stroke-linejoin: round; }
.demo-discussion-send.is-pressed { scale: var(--atlas-press, .97); }
/* The dock's edge, where the whole thread lives. */
.demo-discussion-dock {
  position: absolute; top: 40px; right: 0; padding: 5px 10px; border: 1px solid var(--line); border-right: 0; border-radius: 8px 0 0 8px;
  background: var(--surface); color: var(--accent); font-size: 11.5px; font-weight: 600;
  opacity: 0; translate: var(--atlas-rise, 8px) 0; transition: opacity var(--atlas-motion-slow) var(--atlas-ease-out), translate var(--atlas-motion-slow) var(--atlas-ease-out);
}
.demo-discussion-dock.is-shown { opacity: 1; translate: none; }
</style>
