<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, ref } from "vue";
import { api, errorMessage } from "../../../../api";
import type { AiEvaluationReport } from "../../../../ai/reports";
import { revealSentences } from "../../../../motion/aiMotion";
import { Button } from "../../../ui/button";
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "../../../ui/table";
import ErrorState from "../../ErrorState.vue";
import LoadingNote from "../../LoadingNote.vue";
import SettingsSection from "../../SettingsSection.vue";
import t from "../../../../i18n/settings-ai";
import { formatNumber } from "../../../../i18n";
const props = withDefaults(defineProps<{ run?: (signal: AbortSignal) => Promise<AiEvaluationReport> }>(), {
  run: (signal: AbortSignal) => api<AiEvaluationReport>("/admin/ai/evaluate", { method: "POST", signal }),
});
const result = ref<AiEvaluationReport>();
const error = ref("");
const running = ref(false);
const root = ref<HTMLElement>();
const passed = computed(() => result.value?.items.filter(row => row.passed).length ?? 0);
let request: AbortController | undefined;
async function start() {
  if (running.value) return;
  const current = request = new AbortController();
  running.value = true; error.value = ""; result.value = undefined;
  try {
    const value = await props.run(current.signal);
    if (current.signal.aborted) return;
    result.value = value;
    await nextTick();
    if (root.value && !current.signal.aborted) revealSentences(root.value);
  } catch (failure) { if (!current.signal.aborted) error.value = errorMessage(failure); }
  finally { if (request === current) running.value = false; }
}
function cancel() { request?.abort(); request = undefined; running.value = false; }
onBeforeUnmount(cancel);
</script>
<template>
  <SettingsSection :title="t.evaluation.title" icon="circle-check" :description="t.evaluation.description">
    <p class="st-muted">{{ t.evaluation.note }}</p>
    <div class="eval-actions">
      <Button v-if="!running" type="button" variant="outline" data-eval-run @click="start">{{ t.evaluation.run }}</Button>
      <template v-else><LoadingNote :label="t.evaluation.running" /><Button type="button" variant="ghost" data-eval-cancel @click="cancel">{{ t.evaluation.cancel }}</Button></template>
    </div>
    <ErrorState v-if="error" :title="t.evaluation.failed" :text="error" @retry="start" />
    <div v-if="result" ref="root" role="status" data-eval-result>
      <p><strong>{{ t.evaluation.passed(passed, result.items.length) }}</strong> · {{ result.setVersion }} · {{ result.promptVersion }}</p>
      <Table><TableHeader><TableRow><TableHead>{{ t.evaluation.case }}</TableHead><TableHead>{{ t.evaluation.result }}</TableHead><TableHead>{{ t.evaluation.time }}</TableHead></TableRow></TableHeader>
        <TableBody><TableRow v-for="row in result.items" :key="row.id"><TableCell>{{ row.question }}</TableCell><TableCell>{{ row.code ? t.evaluation.modelFailed : row.passed ? t.evaluation.ok : t.evaluation.notOk }}<small v-if="row.invalidCitations">{{ t.evaluation.invalidCitations(row.invalidCitations) }}</small></TableCell><TableCell>{{ formatNumber(row.latencyMs) }} ms</TableCell></TableRow></TableBody>
      </Table>
    </div>
  </SettingsSection>
</template>
<style scoped>
/* The section body spaces its children with a gap; the browser's paragraph margins doubled it. */
p { margin: 0; }
[data-eval-result] { display: grid; gap: 10px; min-width: 0; overflow-wrap: anywhere; }
.eval-actions { display: flex; gap: 12px; flex-wrap: wrap; align-items: center; }
small { display: block; color: var(--muted); }
</style>
