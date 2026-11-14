<script setup lang="ts">
import { computed, nextTick, onMounted, ref, watch } from "vue";
import { shiftDay } from "../activity";
import { formatDay } from "../formatDate";
import t from "../i18n/components-b";
import { locale } from "../i18n";
import { pragueNow } from "@atlas/markdown/core";

/**
 * Kalendář aktivity po dnech (Po–Ne ve sloupcích po týdnech). Jen zobrazuje a hlásí vybraný den;
 * data i reakci na výběr drží rodič: ProfileActivity (uložené změny) a PageReadership (zobrazení stránky).
 * Barvy vychází z `--accent`, takže heatmapa ladí s barevným stylem, který si uživatel zvolil ve Vzhledu.
 * `unit` pojmenuje počet v popisku dne. Den s `marked` (u čtenosti zobrazené heslo) dostane výrazný
 * rámeček v barvě upozornění nezávisle na intenzitě a legenda ho vysvětlí textem `markLegend`.
 */
const props = withDefaults(defineProps<{
  days: { day: string; count: number; marked?: number }[]; from: string; to: string; label: string; selected?: string;
  unit?: (count: number) => string; markLabel?: (count: number) => string; markLegend?: string;
}>(), { selected: "", unit: (count: number) => t.heatmap.changes(count), markLabel: undefined, markLegend: undefined });
const emit = defineEmits<{ select: [day: string] }>();
const today = pragueNow().date;
const cellLabel = (cell: { day: string; count: number; marked: number }) =>
  formatDay(cell.day) + ": " + props.unit(cell.count) + (cell.marked && props.markLabel ? ", " + props.markLabel(cell.marked) : "");
const calendar = computed(() => {
  const counts = new Map(props.days.map(day => [day.day, day]));
  const start = shiftDay(props.from, -((new Date(props.from + "T12:00:00Z").getUTCDay() + 6) % 7));
  const cells: { day: string; count: number; marked: number; outside: boolean; label: string }[] = [];
  for (let day = start; day <= props.to; day = shiftDay(day, 1)) {
    const cell = { day, count: counts.get(day)?.count ?? 0, marked: counts.get(day)?.marked ?? 0 };
    cells.push({ ...cell, outside: day < props.from, label: cellLabel(cell) });
  }
  return cells;
});
const maxCount = computed(() => Math.max(1, ...props.days.map(day => day.count)));
const lastDay = computed(() => props.to < today ? props.to : today);
const focusedDay = ref("");
const calendarElement = ref<HTMLElement>();
const calendarFocus = computed(() => focusedDay.value || (props.from <= today && today <= props.to ? today : props.from));
const monthFormat = new Intl.DateTimeFormat(locale, { month: "short", timeZone: "UTC" });
const monthLabels = computed(() => calendar.value.flatMap((cell, index) => !cell.outside && cell.day.endsWith("-01")
  ? [{ day: cell.day, column: Math.floor(index / 7) + 1, label: monthFormat.format(new Date(cell.day)) }] : []));
const anyMarked = computed(() => props.days.some(day => day.marked));
function intensity(count: number) { return count ? Math.max(1, Math.ceil(count / maxCount.value * 4)) : 0; }
async function calendarKey(event: KeyboardEvent, day: string) {
  const offsets: Record<string, number> = { ArrowUp: -1, ArrowDown: 1, ArrowLeft: -7, ArrowRight: 7 };
  if (!(event.key in offsets) && event.key !== "Home" && event.key !== "End") return;
  event.preventDefault();
  const target = event.key === "Home" ? props.from : event.key === "End" ? lastDay.value : shiftDay(day, offsets[event.key]!);
  focusedDay.value = target < props.from ? props.from : target > lastDay.value ? lastDay.value : target;
  await nextTick();
  calendarElement.value?.querySelector<HTMLButtonElement>(`[data-day="${focusedDay.value}"]`)?.focus();
}
watch(() => [props.from, props.to], () => { focusedDay.value = ""; });
// A calendar wider than its column (a phone) opens at today, the end of the range, not at January: the current weeks are
// what people come for. Only forward, so a past year, or one that fits, stays where it is.
const scroller = ref<HTMLElement>();
function showCurrent() {
  const box = scroller.value?.getBoundingClientRect();
  const cell = calendarElement.value?.querySelector<HTMLElement>(`[data-day="${calendarFocus.value}"]`)?.getBoundingClientRect();
  if (scroller.value && box && cell) scroller.value.scrollLeft += Math.max(0, cell.right - box.right + 3);
}
onMounted(showCurrent);
watch(() => [props.from, props.to], () => nextTick(showCurrent), { flush: "post" });
</script>

<template>
  <div class="activity-heatmap">
    <div ref="scroller" class="calendar-scroll" role="group" :aria-label="label">
      <div class="calendar-layout" :style="{ '--weeks': Math.ceil(calendar.length / 7) }">
        <div class="calendar-months" aria-hidden="true"><span v-for="month in monthLabels" :key="month.day" :style="{ gridColumn: `${month.column} / span 4` }">{{ month.label }}</span></div>
        <div class="calendar-weekdays" aria-hidden="true"><span v-for="day in t.heatmap.weekdays" :key="day">{{ day }}</span></div>
        <div ref="calendarElement" class="activity-calendar">
          <button v-for="cell in calendar" :key="cell.day" type="button" :data-day="cell.day" :data-level="intensity(cell.count)" :data-marked="cell.marked ? '' : undefined"
            :class="{ outside: cell.outside, selected: selected === cell.day }" :disabled="cell.outside || cell.day > today"
            :aria-label="cell.label" :title="cell.label" :aria-pressed="selected === cell.day"
            :tabindex="cell.day === calendarFocus ? 0 : -1" @focus="focusedDay = cell.day" @keydown="calendarKey($event, cell.day)"
            @click="focusedDay = cell.day; emit('select', cell.day)"></button>
        </div>
      </div>
    </div>
    <div class="activity-legend" aria-hidden="true">
      <template v-if="markLegend && anyMarked"><i data-level="0" data-marked></i><span class="activity-legend-mark">{{ markLegend }}</span></template>
      <span>{{ t.heatmap.less }}</span><i v-for="level in [0, 1, 2, 3, 4]" :key="level" :data-level="level"></i><span>{{ t.heatmap.more }}</span>
    </div>
  </div>
</template>

<style scoped>
.activity-heatmap { display: grid; gap: 6px; min-width: 0; color: var(--muted); }
.calendar-scroll { overflow-x: auto; padding: 8px 3px; container: calendar-scroll / inline-size; }
/* The cells stay square at every width: the cell is the column width left after the 24 px weekday column, the 8 px gap and the gaps between weeks. */
.calendar-layout { --gap: 3px; --cell: calc((max(100cqi, 640px) - 32px - var(--gap) * (var(--weeks) - 1)) / var(--weeks)); display: grid; grid-template-columns: 24px minmax(0, 1fr); gap: 8px; width: 100%; min-width: 640px; }
.calendar-months { grid-column: 2; display: grid; grid-template-columns: repeat(var(--weeks), minmax(0, 1fr)); column-gap: var(--gap); font-size: 10px; }
.calendar-weekdays { grid-column: 1; display: grid; grid-template-rows: repeat(7, var(--cell)); row-gap: var(--gap); font-size: 10px; line-height: 10px; }
.calendar-weekdays span:nth-child(2) { grid-row: 3; } .calendar-weekdays span:nth-child(3) { grid-row: 5; }
.activity-calendar { grid-column: 2; display: grid; grid-auto-flow: column; grid-template-rows: repeat(7, var(--cell)); grid-template-columns: repeat(var(--weeks), minmax(0, 1fr)); gap: var(--gap); }
.activity-calendar button, .activity-legend i { display: block; width: 10px; height: 10px; padding: 0; min-width: 0; min-height: 0; border: 1px solid color-mix(in srgb, var(--accent) 16%, transparent); border-radius: 3px; }
.activity-calendar button { width: 100%; height: 100%; cursor: pointer; transition: border-color var(--atlas-motion-fast) var(--atlas-ease-out); }
.activity-calendar button:disabled { cursor: default; }
[data-level="0"] { background: color-mix(in srgb, var(--accent) 7%, var(--surface)); }
[data-level="1"] { background: color-mix(in srgb, var(--accent) 28%, var(--surface)); }
[data-level="2"] { background: color-mix(in srgb, var(--accent) 50%, var(--surface)); }
[data-level="3"] { background: color-mix(in srgb, var(--accent) 74%, var(--surface)); }
[data-level="4"] { background: var(--accent); }
.activity-calendar .outside { visibility: hidden; }
.activity-calendar .selected, .activity-calendar button:focus-visible { outline: 2px solid var(--text); outline-offset: 1px; }
@media (hover: hover) and (pointer: fine) {
  .activity-calendar button:not(:disabled):hover { border-color: var(--accent); }
}
.activity-legend { display: flex; flex-wrap: wrap; align-items: center; justify-content: flex-end; gap: 4px; font-size: 12px; }
.activity-legend-mark { margin-right: 12px; color: var(--warning); font-weight: 600; }
/* A marked day keeps its intensity and gets a warning ring that no accent level can hide. */
.activity-calendar button[data-marked], .activity-legend i[data-marked] { border-color: var(--warning); box-shadow: 0 0 0 1.5px var(--warning); }
</style>
