<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, reactive, ref, watch } from "vue";
import { animateAtlas, literalMs, motionScale } from "../../motion/aiMotion";
import { orderPublicationChecks, type PublicationCheck } from "../../publishing/publication";
import { CHECK_ROW_GROW_MS, checkRowSchedule, settleCheckRow, unfoldIn } from "../../publishing/findingsMotion";
import AiGlyph from "../ai/AiGlyph.vue";
import AiProvenance from "../ai/AiProvenance.vue";
import AtlasIcon from "../AtlasIcon.vue";
import SettingsListRow from "../settings/SettingsListRow.vue";
import StatusBadge from "../settings/StatusBadge.vue";
import { Button } from "../ui/button";
import t from "../../i18n/components-publishing";

/**
 * Findings of the check before publishing (P5, plan §9), the rows of the "Před publikací" panel of PublishControl.
 * Rows come in a fixed order (passwords, links, empty sections) as SettingsListRow with the type icon and the
 * severity in words (StatusBadge), never the AI glyph: these are rules. `running` adds the row of the server part
 * still being checked ("Kontroluji…") where the links will be.
 *
 * Every row is in the DOM with its text and actions at once. The motion of the prototype (moment 6: spinner, the
 * panel grows by the row, the icon draws itself, the row flashes) only plays over it, within about 600 ms.
 */
const props = withDefaults(defineProps<{
  checks: readonly PublicationCheck[];
  /** The server part is still running. */
  running?: boolean;
}>(), { running: false });
const emit = defineEmits<{ reveal: [check: PublicationCheck] }>();

const RUNNING = "publication-check-running";
const sorted = computed(() => orderPublicationChecks(props.checks));
interface Entry { key: string; check: PublicationCheck | null }
const entries = computed<Entry[]>(() => {
  const list: Entry[] = sorted.value.map(check => ({ key: check.id, check }));
  if (props.running) list.splice(list.filter(entry => entry.check?.kind === "secret").length, 0, { key: RUNNING, check: null });
  return list;
});

const list = ref<HTMLUListElement>();
/** Rows whose result icon shows; the others still show the spinner. */
const settled = reactive(new Set<string>());
const seen = new Set<string>();
const timers = new Set<ReturnType<typeof setTimeout>>();
const done = (entry: Entry) => !!entry.check && settled.has(entry.key);

function rowElement(key: string) {
  return [...(list.value?.children ?? [])].find((row): row is HTMLElement => row instanceof HTMLElement && row.dataset.key === key) ?? null;
}
async function settle(key: string) {
  settled.add(key);
  await nextTick();
  const row = rowElement(key);
  if (row) settleCheckRow(row);
}
/** Plays the rows that just appeared, in order; `unfold` grows the panel by each of them. */
function play(keys: string[], unfold: boolean) {
  const schedule = checkRowSchedule(keys.length);
  // The gallery's "Zpomalit 3×" stretches the pauses as well as the animations.
  const scale = list.value ? motionScale(list.value) : 1;
  keys.forEach((key, index) => {
    const row = rowElement(key);
    if (unfold && row) unfoldIn(row, index * schedule.step, CHECK_ROW_GROW_MS);
    if (key === RUNNING) return;
    const timer = setTimeout(() => { timers.delete(timer); void settle(key); }, schedule.settleAt(index) * scale);
    timers.add(timer);
  });
}
function fresh() {
  const keys = entries.value.map(entry => entry.key).filter(key => !seen.has(key));
  keys.forEach(key => seen.add(key));
  return keys;
}
onMounted(() => play(fresh(), true));
// Late rows (the links) replace the running row: the list eases from its old height to the new one.
watch(entries, async () => {
  const before = list.value?.offsetHeight ?? 0;
  await nextTick();
  const keys = fresh();
  if (!keys.length || !list.value) return;
  const after = list.value.offsetHeight;
  if (after !== before) animateAtlas(list.value, [{ height: `${before}px`, overflow: "hidden" }, { height: `${after}px`, overflow: "hidden" }], { duration: literalMs(list.value, CHECK_ROW_GROW_MS), fill: "none" });
  play(keys, false);
});
onBeforeUnmount(() => { for (const timer of timers) clearTimeout(timer); timers.clear(); });

const severityLabel = (check: PublicationCheck) => (check.severity === "warning" ? t.findings.warning : t.findings.info);
const severityIcon = (check: PublicationCheck) => (check.severity === "warning" ? "triangle-alert" : "info");
</script>

<template>
  <ul ref="list" class="publish-findings" :aria-label="t.findings.label" :aria-busy="running || undefined">
    <SettingsListRow
      v-for="entry in entries"
      :key="entry.key"
      :data-key="entry.key"
      class="publish-finding"
      :data-state="done(entry) ? 'done' : 'checking'"
      :title="entry.check?.text ?? t.findings.checking"
    >
      <template #leading>
        <span class="publish-finding-icon" :data-tone="done(entry) ? entry.check!.severity : undefined" aria-hidden="true">
          <AiGlyph v-if="done(entry) && entry.check?.provenance" :size="16" />
          <AtlasIcon v-else-if="done(entry)" :key="`${entry.key}:icon`" :name="severityIcon(entry.check!)" :size="16" />
          <AtlasIcon v-else name="loader-circle" :size="16" class="publish-finding-spinner" />
        </span>
      </template>
      <template v-if="entry.check" #meta>
        <StatusBadge size="sm" :tone="entry.check.severity" :label="severityLabel(entry.check)" />
      </template>
      <template v-if="entry.check && (entry.check.target || entry.check.openHref)" #actions>
        <Button v-if="entry.check.target" size="sm" variant="outline" type="button" @click="emit('reveal', entry.check)">{{ t.findings.show }}</Button>
        <Button v-if="entry.check.openHref" as="a" size="sm" variant="ghost" :href="entry.check.openHref" target="_blank" rel="noopener">{{ t.findings.open }}</Button>
      </template>
      <AiProvenance v-if="entry.check?.provenance" :provenance="entry.check.provenance" :sources="[]" />
    </SettingsListRow>
  </ul>
</template>

<style scoped>
/* The prototype's .chk rows inside a 420 px panel; refresh 2.5 (board 04) draws each as a quiet card with a round icon. */
.publish-findings { display: grid; gap: 8px; margin: 0; padding: 0; list-style: none; }
.publish-findings :deep(.publish-finding) { border: 1px solid color-mix(in srgb, var(--line) 80%, transparent); border-radius: 10px; background: var(--surface); }
.publish-findings :deep(.settings-list-row-line) { gap: 6px 10px; padding: 9px 10px; }
.publish-findings :deep(.settings-list-row-main) { gap: 10px; }
.publish-findings :deep(.settings-list-row-text strong) { font-size: 13px; }
.publish-findings :deep(.settings-list-row-meta) { padding-left: 32px; }
.publish-finding[data-state="checking"] :deep(.settings-list-row-text strong) { color: var(--muted); font-weight: 500; }
.publish-finding-icon { display: grid; flex-shrink: 0; place-items: center; width: 22px; height: 22px; border-radius: 50%; background: var(--hover); color: var(--muted); }
.publish-finding-icon[data-tone="warning"] { background: var(--warning-bg); color: var(--warning); }
.publish-finding-icon[data-tone="info"] { background: var(--accent-soft); color: var(--accent); }
.publish-finding-icon[data-tone="success"] { background: var(--success-bg); color: var(--success); }
.publish-finding-icon :deep(svg) { width: 13px; height: 13px; }
/* Rules work with a spinner, never with twinkle (plan §0a). The panel is portalled out of .atlas, so it spins here
   with the shared keyframes; reduced motion (or the gallery's preview of it) stops it. */
.publish-finding-spinner { animation: atlas-spin var(--atlas-motion-spin) linear infinite; }
[data-atlas-reduced-motion] .publish-finding-spinner { animation: none; }
@media (prefers-reduced-motion: reduce) {
  .publish-finding-spinner { animation: none; }
}
</style>
