<script setup lang="ts">
import t from "../../i18n/settings-flow";
import { onBeforeUnmount, ref, useId } from "vue";
import AtlasIcon from "../AtlasIcon.vue";
import { FEEDBACK_HOLD_MS } from "../../motion/aiMotion";
import { Button } from "../ui/button";

/**
 * A secret shown exactly once (a new vault's master password). It can be revealed and copied;
 * continuing requires confirming it has been stored, and afterwards the value is gone from the
 * page. The parent must drop its own copy on `done`.
 */
const props = withDefaults(defineProps<{ value: string; label?: string; note?: string; confirmLabel?: string; finishedText?: string }>(), {
  label: undefined,
  note: undefined,
  confirmLabel: undefined,
  finishedText: undefined,
});
const emit = defineEmits<{ done: [] }>();
const uid = useId();
const visible = ref(false);
const stored = ref(false);
const copied = ref(false);
const finished = ref(false);
const shown = ref(props.value);
let timer: ReturnType<typeof setTimeout> | undefined;

async function copy() {
  try {
    await navigator.clipboard.writeText(shown.value);
    copied.value = true;
    clearTimeout(timer);
    timer = setTimeout(() => (copied.value = false), FEEDBACK_HOLD_MS);
  } catch {
    copied.value = false;
  }
}
function finish() {
  if (!stored.value) return;
  shown.value = "";
  finished.value = true;
  emit("done");
}
onBeforeUnmount(() => {
  clearTimeout(timer);
  shown.value = "";
});
</script>

<template>
  <div class="one-time-secret" :data-state="finished ? 'finished' : 'shown'">
    <template v-if="!finished">
      <div class="ots-head">
        <span class="st-icon-tile" data-tone="warning"><AtlasIcon name="key-round" :size="20" /></span>
        <div>
          <strong :id="`${uid}-label`">{{ label ?? t.oneTime.label }}</strong>
          <p>{{ t.oneTime.once }} {{ note ?? t.oneTime.note }}</p>
        </div>
      </div>
      <div class="ots-value" :aria-labelledby="`${uid}-label`">
        <code class="st-mono" data-one-time-value>{{ visible ? shown : "•".repeat(Math.min(shown.length, 24)) }}</code>
        <!-- The label of a button reserves the room of the longer one (data-sizer, drawn by CSS, so the text of the button stays the one
             that is shown): the buttons keep their width and the value beside them does not re-wrap when one of them changes. -->
        <Button variant="ghost" size="sm" type="button" :aria-pressed="visible" @click="visible = !visible"><AtlasIcon :name="visible ? 'eye-off' : 'eye'" :size="15" /><span class="ots-label" :data-sizer="t.oneTime.show">{{ visible ? t.oneTime.hide : t.oneTime.show }}</span></Button>
        <Button variant="outline" size="sm" type="button" @click="copy">
          <AtlasIcon :name="copied ? 'check' : 'copy'" :size="15" :class="{ 'ots-copied': copied }" /><span class="ots-label" :data-sizer="t.oneTime.copied">{{ copied ? t.oneTime.copied : t.oneTime.copy }}</span>
        </Button>
      </div>
      <label class="ots-confirm">
        <input v-model="stored" type="checkbox" />
        <span>{{ confirmLabel ?? t.oneTime.confirm }}</span>
      </label>
      <div class="ots-actions">
        <Button type="button" :disabled="!stored" data-one-time-done @click="finish">{{ t.oneTime.next }}</Button>
      </div>
    </template>
    <p v-else class="ots-finished" role="status"><AtlasIcon name="lock" :size="16" />{{ finishedText ?? t.oneTime.finished }}</p>
  </div>
</template>

<style scoped>
.one-time-secret { display: grid; gap: 12px; padding: 16px; border: 1px solid color-mix(in srgb, var(--warning) 40%, var(--line)); border-radius: var(--st-radius); background: color-mix(in srgb, var(--warning-bg) 45%, var(--surface)); }
.one-time-secret[data-state="finished"] { border-color: var(--line); background: var(--surface); }
.ots-head { display: flex; gap: 12px; align-items: flex-start; }
.ots-head strong { font-size: 14.5px; }
.ots-head p { margin: 3px 0 0; color: var(--muted); font-size: 12.5px; line-height: 1.55; }
.ots-value { display: flex; flex-wrap: wrap; align-items: center; gap: 8px; padding: 8px 8px 8px 12px; border: 1px solid var(--line); border-radius: var(--st-radius-sm); background: var(--surface); }
.ots-value code { flex: 1 1 220px; min-width: 0; overflow-wrap: anywhere; font-size: 15px; letter-spacing: .04em; animation: st-fade-in var(--atlas-motion-base) var(--atlas-ease-out); }
.ots-label { display: inline-flex; flex-direction: column; align-items: center; }
.ots-label::after { content: attr(data-sizer); height: 0; overflow: hidden; visibility: hidden; }
.ots-copied { animation: st-pop var(--atlas-motion-pop) var(--atlas-ease-out); color: var(--success); }
.ots-confirm { display: flex; align-items: center; gap: 8px; font-size: 13.5px; cursor: pointer; }
.ots-confirm input { width: 17px; height: 17px; accent-color: var(--accent); }
.ots-actions { display: flex; justify-content: flex-end; }
.ots-finished { display: flex; align-items: center; gap: 8px; margin: 0; color: var(--muted); font-size: 13.5px; animation: st-fade-in var(--atlas-motion-base) var(--atlas-ease-out); }
</style>
