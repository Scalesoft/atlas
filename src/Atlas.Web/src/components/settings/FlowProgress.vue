<script setup lang="ts">
import t from "../../i18n/settings-flow";
import { nextTick, ref, watch } from "vue";
import { drawIcon } from "../../motion/aiMotion";
import AtlasIcon from "../AtlasIcon.vue";

// `strip` (refresh 2.5, boards 07 and 10): the state of a module's setup as one framed row of numbered steps, the
// next one highlighted; a step with `href` is a link to where it is set, otherwise it emits `select` like a chip.
const props = defineProps<{
  items: { id: string; label: string; detail: string; state: "done" | "current" | "todo"; available: boolean; controls?: string; href?: string }[];
  label?: string;
  busy?: boolean;
  variant?: "chips" | "strip";
}>();
const emit = defineEmits<{ select: [id: string] }>();
// A step that becomes done draws its check (the prototype's result icon, `drawIcon` 240 ms); chips that are
// done when the flow opens simply show it.
const list = ref<HTMLElement>();
watch(() => props.items.map((item) => item.state), async (states, before) => {
  const finished = props.items.filter((item, index) => states[index] === "done" && before[index] !== "done");
  if (!finished.length) return;
  await nextTick();
  for (const item of finished) {
    const check = list.value?.querySelector<SVGElement>(`[data-step-chip="${item.id}"] .flow-chip-mark svg`);
    if (check) drawIcon(check);
  }
});
</script>

<template>
  <ol v-if="variant === 'strip'" ref="list" class="flow-strip" :aria-label="label ?? t.flow.overview" :style="{ '--steps': items.length }">
    <li v-for="(item, index) in items" :key="item.id">
      <component
        :is="item.href ? 'a' : 'button'"
        :type="item.href ? undefined : 'button'"
        :href="item.href && item.available ? item.href : undefined"
        class="flow-strip-step"
        :data-state="item.state"
        :data-step-chip="item.id"
        :disabled="item.href ? undefined : busy || !item.available"
        :aria-disabled="item.href && !item.available ? 'true' : undefined"
        :aria-current="item.state === 'current' ? 'step' : undefined"
        :aria-controls="item.available ? item.controls : undefined"
        @click="!item.href && emit('select', item.id)"
      >
        <span class="flow-chip-mark"><AtlasIcon v-if="item.state === 'done'" name="check" :size="14" /><span v-else>{{ index + 1 }}</span></span>
        <span class="flow-strip-text"><span class="flow-chip-label">{{ item.label }}</span><span class="flow-chip-detail">{{ item.detail }}</span></span>
      </component>
    </li>
  </ol>
  <ol v-else ref="list" class="flow-progress" :aria-label="label ?? t.flow.overview">
    <li v-for="(item, index) in items" :key="item.id">
      <button type="button" class="flow-chip" :data-state="item.state" :data-step-chip="item.id" :data-location-chip="item.id" :disabled="busy || !item.available" :aria-current="item.state === 'current' ? 'step' : undefined" :aria-controls="item.available ? item.controls : undefined" @click="emit('select', item.id)">
        <span class="flow-chip-mark"><AtlasIcon v-if="item.state === 'done'" name="check" :size="14" /><span v-else>{{ index + 1 }}</span></span>
        <span><span class="flow-chip-label">{{ item.label }}</span><span class="flow-chip-detail">{{ item.detail }}</span></span>
      </button>
      <AtlasIcon v-if="index < items.length - 1" name="right" :size="14" class="flow-progress-separator" />
    </li>
  </ol>
</template>

<style scoped>
.flow-progress { display: flex; flex-wrap: wrap; gap: 8px; list-style: none; margin: 0; padding: 0; }
.flow-progress li { display: flex; align-items: center; gap: 8px; min-width: 0; }
.flow-progress-separator { color: var(--muted); flex-shrink: 0; }
.flow-chip { display: flex; align-items: center; gap: 8px; padding: 7px 12px 7px 7px; border: 1px solid var(--line); border-radius: 999px; background: var(--surface); color: var(--text); font: inherit; text-align: left; cursor: pointer; position: relative; isolation: isolate; overflow: hidden; max-width: 100%; transition: border-color var(--atlas-motion-slow), color var(--atlas-motion-slow), box-shadow var(--atlas-motion-slow), scale var(--atlas-motion-fast) var(--atlas-ease-out); }
.flow-chip::before, .flow-chip::after {
  content: ""; position: absolute; inset: 0; z-index: -1; border-radius: inherit; pointer-events: none;
  transform: scaleX(0); transform-origin: left center; opacity: 0;
  transition: transform calc(var(--atlas-motion-slow) + var(--atlas-motion-base)) var(--atlas-ease-out), opacity var(--atlas-motion-base);
}
.flow-chip::before { background: linear-gradient(100deg, var(--accent-soft) 65%, color-mix(in srgb, var(--accent) 22%, var(--surface))); }
.flow-chip::after { background: linear-gradient(100deg, var(--success-bg) 65%, color-mix(in srgb, var(--success) 15%, var(--surface))); }
.flow-chip[data-state="current"]::before, .flow-chip[data-state="done"]::after { transform: scaleX(1); opacity: 1; }
.flow-chip[data-state="done"]::before { transform: scaleX(1); }
.flow-chip:disabled { cursor: default; }
.flow-chip:active:not(:disabled) { scale: var(--atlas-press); }
@media (hover: hover) and (pointer: fine) {
  .flow-chip:not(:disabled):hover { border-color: var(--accent); }
}
.flow-chip:focus-visible { outline: 2px solid var(--accent); outline-offset: 3px; --focus-offset: 3px; }
.flow-chip-mark { display: grid; place-items: center; width: 26px; height: 26px; flex-shrink: 0; border-radius: 50%; background: var(--hover); font-size: 12px; font-weight: 650; transition: background-color var(--atlas-motion-slow), color var(--atlas-motion-slow); }
.flow-chip-label { display: block; font-size: 12.5px; font-weight: 600; overflow-wrap: anywhere; }
.flow-chip-detail { display: block; font-size: 11px; color: var(--muted); }
.flow-chip[data-state="current"] { border-color: var(--accent); box-shadow: 0 0 0 2px color-mix(in srgb, var(--accent) 8%, transparent); }
.flow-chip[data-state="done"] { border-color: color-mix(in srgb, var(--success) 35%, var(--line)); }
.flow-chip[data-state="current"] .flow-chip-mark { background: var(--accent); color: var(--on-accent); }
.flow-chip[data-state="done"] .flow-chip-mark { background: var(--success-bg); color: var(--success); }
.flow-chip[data-state="todo"] { color: var(--muted); }
@media (prefers-reduced-motion: reduce) {
  .flow-chip::before, .flow-chip::after { transform: none !important; transition: opacity var(--atlas-motion-base); }
}

/* Strip (boards 07, 10): one frame, steps side by side with a soft divider; the current step is white with an
   accent number, steps that wait are tinted, done steps carry a check. Wraps to a column on a phone. */
.flow-strip {
  display: grid;
  grid-template-columns: repeat(var(--steps), minmax(0, 1fr));
  margin: 0;
  padding: 0;
  list-style: none;
  overflow: hidden;
  border: 1px solid var(--line);
  border-radius: var(--st-radius, 14px);
  background: var(--surface);
}
.flow-strip > li { min-width: 0; border-right: 1px solid var(--st-line-soft, var(--line)); }
.flow-strip > li:last-child { border-right: 0; }
.flow-strip-step {
  display: flex;
  align-items: center;
  gap: 10px;
  width: 100%;
  height: 100%;
  min-height: 60px;
  padding: 12px 14px;
  border: 0;
  background: transparent;
  color: var(--text);
  font: inherit;
  text-align: left;
  text-decoration: none;
  cursor: pointer;
  transition: background-color var(--atlas-motion-fast) var(--atlas-ease-out);
}
.flow-strip-step[data-state="todo"] { background: var(--st-subtle, var(--hover)); }
.flow-strip-step[data-state="todo"] .flow-chip-detail { color: var(--muted); }
.flow-strip-step[data-state="current"] .flow-chip-mark { background: var(--accent); color: var(--on-accent); }
.flow-strip-step[data-state="current"] .flow-chip-detail { color: var(--accent); }
.flow-strip-step[data-state="done"] .flow-chip-mark { background: var(--success-bg); color: var(--success); }
.flow-strip-step[data-state="todo"] .flow-chip-mark { background: color-mix(in srgb, var(--line) 55%, var(--surface)); color: var(--muted); }
@media (hover: hover) and (pointer: fine) {
  .flow-strip-step:is(a[href], button:not(:disabled)):hover { background: color-mix(in srgb, var(--accent-soft) 45%, var(--surface)); }
}
.flow-strip-step:focus-visible { outline: 2px solid var(--accent); outline-offset: -2px; --focus-offset: -2px; }
.flow-strip-step:is(button:disabled, [aria-disabled="true"]) { cursor: default; }
.flow-strip-text { display: grid; min-width: 0; }
.flow-strip .flow-chip-label { font-size: 14px; }
.flow-strip .flow-chip-detail { font-size: 12px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
@container settings-shell (max-width: 720px) {
  .flow-strip { grid-template-columns: minmax(0, 1fr); }
  .flow-strip > li { border-right: 0; border-bottom: 1px solid var(--st-line-soft, var(--line)); }
  .flow-strip > li:last-child { border-bottom: 0; }
  .flow-strip-step { min-height: 46px; padding-block: 8px; }
}
</style>

