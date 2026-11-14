<script setup lang="ts">
import { logApi as defaultLogApi, type LogApi } from "../../../logs/logApi";
import { jobApi, type JobApi } from "../../../jobs/jobApi";
import JobDetailView from "./JobDetailView.vue";
import JobListView from "./JobListView.vue";
import JobRunView from "./JobRunView.vue";

/**
 * Správa wiki → Provoz → Úlohy na pozadí: the list (`general/jobs`), a job (`general/jobs/{id}`) and one of its runs
 * (`general/jobs/{id}/runs/{runId}`), the routes the feature pages (Zálohy, Propojit kontakty) link their last run to. `part` is the path below
 * the list; `base` is where the list lives, which the gallery moves. `api` is replaced by the gallery.
 */
withDefaults(defineProps<{ part?: string[]; base?: string; api?: JobApi; logApi?: LogApi }>(), { part: () => [], base: "general/jobs", api: () => jobApi, logApi: () => defaultLogApi });
</script>

<template>
  <JobRunView v-if="part[0] && part[1] === 'runs' && Number.isInteger(Number(part[2]))" :key="`${part[0]}/${part[2]}`" :api="api" :base="base" :job-id="part[0]" :run-id="Number(part[2])" :log-api="logApi" />
  <JobDetailView v-else-if="part[0]" :key="part[0]" :api="api" :base="base" :job-id="part[0]" />
  <JobListView v-else :api="api" :base="base" />
</template>
