<script setup lang="ts">
import { computed, reactive, ref, useId, watch } from "vue";
import AtlasIcon from "../AtlasIcon.vue";
import InlineReveal from "./InlineReveal.vue";
import StatusBadge from "./StatusBadge.vue";
import EmptyState from "./EmptyState.vue";
import t from "../../i18n/settings-activity";
import { activityTime, foldRepeats, groupActivityByDay, outcomeLabels, type ActivityItem } from "./activity";

/**
 * Who did what when, grouped by day, newest first. Each entry is one sentence with its result;
 * technical details open in place. Entries rise in one after another when the list changes (a filter, a refresh with
 * news); when only older entries are appended ("Starší události") the ones on screen stay put and just the new ones
 * rise in, so the scroll position and what the reader is looking at do not move. Repeats of one entry within minutes fold
 * into it ("3×"); their times open with the details.
 */
const props = withDefaults(defineProps<{ items: ActivityItem[]; now?: Date; compact?: boolean; emptyTitle?: string; emptyText?: string; label?: string }>(), {
  now: () => new Date(),
  compact: false,
  emptyTitle: undefined,
  emptyText: undefined,
  label: undefined,
});
const uid = useId();
const open = reactive(new Set<string>());
const groups = computed(() => groupActivityByDay(props.items, props.now).map((group) => ({ ...group, runs: foldRepeats(group.items) })));
// A new generation (a new key) replays the entrance of the whole list; an append or an unchanged refresh keeps it.
const generation = ref(0);
const batchFrom = ref(0);
let known: string[] = [];
watch(() => props.items.map((item) => item.id), (ids) => {
  if (ids.length === known.length && ids.every((id, index) => id === known[index])) return;
  if (known.length && ids.length > known.length && known.every((id, index) => id === ids[index])) batchFrom.value = known.length;
  else { generation.value++; batchFrom.value = 0; }
  known = ids;
}, { immediate: true });
const positions = computed(() => new Map(props.items.map((item, index) => [item.id, index])));
/** The stagger counts from the first entry of the newest batch and stops at the eighth. */
const stagger = (id: string) => Math.min(Math.max(0, (positions.value.get(id) ?? 0) - batchFrom.value), 8);

function toggle(id: string) {
  if (open.has(id)) open.delete(id);
  else open.add(id);
}
</script>

<template>
  <div class="activity-timeline" :class="{ 'is-compact': compact }">
    <EmptyState v-if="!items.length" :title="emptyTitle ?? t.timeline.emptyTitle" :text="emptyText ?? t.timeline.emptyText" icon="scroll-text" compact />
    <div v-else :key="generation" class="activity-days">
      <section v-for="group in groups" :key="group.key" class="activity-day" :aria-labelledby="`${uid}-${group.key}`">
        <h3 :id="`${uid}-${group.key}`" class="activity-day-label">{{ group.label }}</h3>
        <ol class="activity-list" :aria-label="`${label ?? t.timeline.label}: ${group.label}`">
          <li v-for="{ item, more } in group.runs" :key="item.id" class="activity-item" :data-outcome="item.outcome" :style="{ '--i': stagger(item.id) }">
            <span class="activity-rail" aria-hidden="true"><span class="activity-dot"><AtlasIcon :name="item.icon" :size="14" /></span></span>
            <div class="activity-body">
              <p class="activity-sentence">
                <time class="activity-time" :datetime="item.at">{{ activityTime(item.at) }}</time>
                <span>
                  <strong>{{ item.actor }}</strong>{{ ` ${item.action}${item.target ? " " : ""}` }}<a v-if="item.target && item.href" :href="item.href" class="activity-target" data-activity-target>{{ item.target }}</a><strong v-else-if="item.target" class="activity-target">{{ item.target }}</strong>{{ item.via ? ` ${item.via}` : "" }}
                  <span v-if="more.length" class="activity-repeat" :title="t.timeline.again(more.length, more.map((entry) => activityTime(entry.at)).join(', '))" data-activity-repeat>{{ more.length + 1 }}×</span>
                </span>
              </p>
              <div class="activity-meta">
                <StatusBadge v-if="item.badge" :tone="item.badge.tone" :icon="item.badge.icon" :label="item.badge.label" size="sm" />
                <StatusBadge v-else :tone="outcomeLabels[item.outcome].tone" :icon="outcomeLabels[item.outcome].icon" :label="outcomeLabels[item.outcome].label" size="sm" />
                <span v-if="item.context" class="activity-context">{{ item.context }}</span>
                <!-- One action on the entry itself, e.g. downloading the archive a backup run created. -->
                <slot name="item-actions" :item="item" />
                <button
                  v-if="(item.details?.length || more.length) && !compact"
                  type="button"
                  class="activity-toggle"
                  :aria-expanded="open.has(item.id)"
                  :aria-controls="`${uid}-${item.id}-details`"
                  data-activity-details
                  @click="toggle(item.id)"
                >
                  {{ t.timeline.details }}<AtlasIcon name="down" :size="14" />
                </button>
              </div>
              <InlineReveal v-if="(item.details?.length || more.length) && !compact" :id="`${uid}-${item.id}-details`" :open="open.has(item.id)">
                <dl class="activity-details">
                  <div v-if="more.length">
                    <dt>{{ t.timeline.repeated }}</dt>
                    <dd>{{ more.map((entry) => activityTime(entry.at)).join(", ") }}</dd>
                  </div>
                  <div v-for="detail in item.details" :key="detail.label">
                    <dt>{{ detail.label }}</dt>
                    <dd>{{ detail.value }}<small v-if="detail.hint">{{ detail.hint }}</small></dd>
                  </div>
                </dl>
              </InlineReveal>
            </div>
          </li>
        </ol>
      </section>
    </div>
  </div>
</template>

<style scoped>
.activity-timeline { min-width: 0; container: activity-timeline / inline-size; }
.activity-days { display: grid; gap: 18px; }
.activity-day-label { margin: 0 0 6px; color: var(--muted); font-size: 12px; font-weight: 650; letter-spacing: .04em; text-transform: uppercase; }
.activity-day-label::first-letter { text-transform: uppercase; }
.activity-list { margin: 0; padding: 0; list-style: none; }
.activity-item {
  display: grid;
  grid-template-columns: 30px minmax(0, 1fr);
  gap: 12px;
  animation: st-rise-in var(--atlas-motion-slow) var(--atlas-ease-out) both;
  animation-delay: calc(var(--i) * var(--st-stagger));
}
.activity-rail { position: relative; display: flex; justify-content: center; }
.activity-rail::before { content: ""; position: absolute; top: 30px; bottom: -4px; width: 1px; background: var(--line); }
.activity-item:last-child .activity-rail::before { display: none; }
.activity-dot {
  position: relative;
  display: grid;
  place-items: center;
  width: 28px;
  height: 28px;
  margin-top: 2px;
  border-radius: 50%;
  color: var(--muted);
  background: var(--hover);
}
[data-outcome="allowed"] .activity-dot { color: var(--success); background: var(--success-bg); }
[data-outcome="denied"] .activity-dot { color: var(--error); background: var(--error-bg); }
[data-outcome="changed"] .activity-dot { color: var(--info); background: var(--info-bg); }
[data-outcome="failed"] .activity-dot { color: var(--warning); background: var(--warning-bg); }
.activity-body { min-width: 0; padding: 4px 0 14px; }
.is-compact .activity-body { padding-bottom: 10px; }
.activity-sentence { display: flex; gap: 10px; margin: 0; font-size: 14px; line-height: 1.5; }
.activity-time { flex-shrink: 0; min-width: 38px; color: var(--muted); font-size: 12.5px; font-variant-numeric: tabular-nums; padding-top: 1px; }
.activity-target { overflow-wrap: anywhere; }
a.activity-target { color: var(--accent); font-weight: 650; text-decoration: none; }
a.activity-target:focus-visible { outline: 2px solid var(--accent); outline-offset: 1px; border-radius: 3px; }
@media (hover: hover) and (pointer: fine) {
  a.activity-target:hover { text-decoration: underline; }
}
.activity-repeat { margin-left: 6px; padding: 0 6px; border-radius: 999px; color: var(--muted); background: var(--hover); font-size: 12px; font-weight: 650; font-variant-numeric: tabular-nums; }
.activity-meta { display: flex; flex-wrap: wrap; align-items: center; gap: 6px 10px; margin: 5px 0 0 48px; }
.activity-context { color: var(--muted); font-size: 12.5px; }
.activity-toggle {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  padding: 2px 8px;
  border-radius: 7px;
  color: var(--accent);
  font: inherit;
  font-size: 12.5px;
  font-weight: 600;
  cursor: pointer;
  transition: background-color var(--atlas-motion-fast);
}
@media (hover: hover) and (pointer: fine) {
  .activity-toggle:hover { background: var(--accent-soft); }
}
.activity-toggle:focus-visible { outline: 2px solid var(--accent); outline-offset: 1px; --focus-offset: 1px; }
.activity-toggle > svg { rotate: -90deg; transition: rotate var(--atlas-motion-base) var(--atlas-ease-out); }
/* Turns in step with its panel: InlineReveal opens over the slow token and closes over the base one. */
.activity-toggle[aria-expanded="true"] > svg { rotate: 0deg; transition-duration: var(--atlas-motion-slow); }
.activity-details {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(min(100%, 220px), 1fr));
  gap: 8px 16px;
  margin: 10px 0 0 48px;
  padding: 12px 14px;
  border: 1px solid var(--line);
  border-radius: var(--st-radius-sm);
  background: color-mix(in srgb, var(--canvas) 55%, var(--surface));
}
.activity-details dt { color: var(--muted); font-size: 11.5px; font-weight: 600; }
.activity-details dd { display: grid; gap: 2px; margin: 1px 0 0; font-size: 13px; overflow-wrap: anywhere; }
.activity-details dd small { color: var(--warning); font-size: 11.5px; }
@container activity-timeline (max-width: 560px) {
  .activity-meta, .activity-details { margin-left: 0; }
}
</style>
