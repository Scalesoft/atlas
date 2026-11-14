<script setup lang="ts">
import { computed, onMounted, ref } from "vue";
import { logApi, type LogApi, type LogFilter, type LogLevel } from "../../../logs/logApi";
import t from "../../../i18n/settings-logs";
import AtlasIcon from "../../AtlasIcon.vue";
import { Switch } from "../../ui/switch";
import FilterMenu from "../FilterMenu.vue";
import SettingsPage from "../SettingsPage.vue";
import type { ChoiceOption } from "../types";
import { focusQuietly } from "../../../keyboard/navigation";
import { useSettingsRoute } from "../useSettingsRoute";
import LogList from "./LogList.vue";
import { timeRanges } from "./logFormat";

/**
 * Správa wiki → Provoz → Systémový log: the server's own log, newest first (LogList). Filters by level and time, in the URL (`uroven`, `cas`,
 * `pozadavek` from a line's request id, `live`), so a view can be sent as a link. `api` is replaced by the gallery.
 */
const props = withDefaults(defineProps<{ api?: LogApi }>(), { api: () => logApi });
const router = useSettingsRoute();
const root = ref<HTMLElement>();

const urlQuery = computed(() => router.route.value.query);
function setParam(key: string, value: string) { router.setQuery({ ...router.route.value.query, [key]: value || undefined }); }
const level = computed({ get: () => urlQuery.value.uroven ?? "", set: (value: string) => setParam("uroven", value) });
const time = computed({ get: () => urlQuery.value.cas ?? "", set: (value: string) => setParam("cas", value) });
const live = computed({ get: () => urlQuery.value.live === "1", set: (value: boolean) => setParam("live", value ? "1" : "") });
const traceId = computed(() => urlQuery.value.pozadavek ?? "");
// The window starts when the filter is chosen (or the page opened with it); live lines keep coming after it.
const filter = computed<LogFilter>(() => ({
  level: (level.value || undefined) as LogLevel | undefined,
  from: timeRanges[time.value] ? new Date(Date.now() - timeRanges[time.value]!).toISOString() : undefined,
  traceId: traceId.value || undefined,
}));

const levelOptions: ChoiceOption[] = [
  { value: "", label: t.levels.all, icon: "filter" }, { value: "information", label: t.levels.information, icon: "info" },
  { value: "warning", label: t.levels.warning, icon: "triangle-alert", tone: "warning" }, { value: "error", label: t.levels.error, icon: "circle-x", tone: "danger" },
];
const timeOptions: ChoiceOption[] = [
  { value: "", label: t.times.any, icon: "history" }, { value: "15m", label: t.times.m15, icon: "clock" }, { value: "1h", label: t.times.h1, icon: "clock" },
  { value: "24h", label: t.times.h24, icon: "clock" }, { value: "7d", label: t.times.d7, icon: "calendar" },
];

onMounted(() => { const level = root.value?.querySelector<HTMLElement>('[data-filter="level"]'); if (level) focusQuietly(level); });
</script>

<template>
  <SettingsPage :title="t.title" icon="file-text">
    <div ref="root" class="logs">
      <div class="log-filters" role="search" :aria-label="t.filterLabel">
        <FilterMenu v-model="level" :options="levelOptions" :label="t.level" icon="filter" data-filter="level" />
        <FilterMenu v-model="time" :options="timeOptions" :label="t.time" icon="history" data-filter="time" />
        <label class="log-switch"><Switch v-model="live" data-log-live />{{ t.live }}</label>
        <button v-if="traceId" type="button" class="st-link log-chip" data-log-trace-chip @click="setParam('pozadavek', '')"><AtlasIcon name="close" :size="13" />{{ t.request(traceId.slice(0, 8)) }}</button>
        <a class="st-link log-chip log-export" :href="props.api.exportUrl(filter)" download data-log-export><AtlasIcon name="download" :size="13" />{{ t.download }}</a>
      </div>
      <LogList :api="props.api" :filter="filter" :live="live" @trace="setParam('pozadavek', $event)" />
    </div>
  </SettingsPage>
</template>

<style scoped>
.logs { display: grid; gap: 12px; min-width: 0; }
.log-filters { display: flex; flex-wrap: wrap; align-items: center; gap: 8px 12px; min-width: 0; }
.log-switch { display: inline-flex; align-items: center; gap: 8px; font-size: 13px; cursor: pointer; }
.log-chip { display: inline-flex; align-items: center; gap: 4px; font-size: 12.5px; background: none; cursor: pointer; }
.log-export { margin-left: auto; }
</style>
