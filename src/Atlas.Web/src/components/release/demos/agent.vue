<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref } from "vue";
import "../../../collaborationPresence.css";
import { mountAiGlyph } from "../../ai/mountAiGlyph";
import t from "../../../i18n/components-release";
import { typeText, useDemoLoop } from "./demoClock";

// An agent writes into a live page (through MCP, the API or the assistant): its caret stands where it wrote, in the AI ink with its
// glyph, as on a live page (collaborationPresence's is-agent caret), then fades out.
const d = t.demos.agent;
const root = ref<HTMLElement>();
const glyph = ref<HTMLElement>();
const asked = ref(false), text = ref(""), caret = ref(false), leaving = ref(false);
let unmountGlyph: (() => void) | undefined;
onMounted(() => { unmountGlyph = mountAiGlyph(glyph.value!, { size: 12, state: "done" }); });
onBeforeUnmount(() => unmountGlyph?.());

useDemoLoop(root, (at) => {
  at(0, () => { asked.value = false; text.value = ""; caret.value = false; leaving.value = false; });
  at(300, () => { asked.value = true; });
  at(1300, () => { caret.value = true; });
  const end = typeText(at, 1500, d.summary, (value) => { text.value = value; }, 45);
  at(end + 1600, () => { leaving.value = true; });
  return end + 3600;
}, () => { asked.value = true; text.value = d.summary; caret.value = true; });
</script>

<template>
  <div ref="root" class="release-demo demo-agent" aria-hidden="true">
    <p class="demo-agent-title">{{ d.title }}</p>
    <p class="demo-agent-ask" :class="{ 'is-shown': asked }">{{ d.asked }}</p>
    <p class="demo-agent-line">{{ text }}<span v-show="caret" class="demo-agent-caret collaboration-presence__cursor is-agent" :class="{ 'is-leaving': leaving }">
      <span class="collaboration-presence__label"><span ref="glyph" class="collaboration-presence__glyph" /><span>{{ d.name }}</span></span>
    </span></p>
  </div>
</template>

<style scoped>
.demo-agent { height: 100%; padding: 10px 14px; font-size: 13px; line-height: 2.2; }
.demo-agent p { margin: 0; }
.demo-agent-title { font-weight: 700; font-size: 14px; }
.demo-agent-ask {
  width: fit-content; max-width: 100%; margin: 2px 0 6px !important; padding: 2px 10px; line-height: 1.6; border-radius: 12px 12px 12px 4px;
  background: color-mix(in srgb, var(--text) 7%, transparent); color: var(--muted); font-size: 12px;
  opacity: 0; translate: 0 var(--atlas-rise, 8px); transition: opacity var(--atlas-motion-slow) var(--atlas-ease-out), translate var(--atlas-motion-slow) var(--atlas-ease-out);
}
.demo-agent-ask.is-shown { opacity: 1; translate: none; }
/* The agent's caret of a live page, inline after what it wrote. */
.demo-agent-caret.collaboration-presence__cursor { position: relative; display: inline-block; height: 1.25em; margin-left: 1px; vertical-align: -.25em; transform: none; }
/* Its label hangs below: the line above is the question. */
.demo-agent-caret .collaboration-presence__label { top: 1.45em; border-radius: 0 4px 4px 4px; font-size: 10px; }
</style>
