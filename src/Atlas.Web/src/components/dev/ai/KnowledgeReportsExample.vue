<script setup lang="ts">
import { ref } from "vue";
import type { AiReportsClient, DuplicateClient, DuplicatePair } from "../../../ai/reports";
import AiEvaluationSection from "../../settings/general/ai/AiEvaluationSection.vue";
import AiReportsSection from "../../settings/general/ai/AiReportsSection.vue";
import DuplicatePagesSection from "../../settings/space/DuplicatePagesSection.vue";
import SettingsPage from "../../settings/SettingsPage.vue";
import OptionPills from "../../settings/OptionPills.vue";
const kind = ref("usage");
const evaluate = async () => ({ setVersion: "answer-diagnostic@1", promptVersion: "answer@1", items: [
  { id: "vpn", question: "Jak se připojím k VPN?", expectedAnswerable: true, answerable: true, passed: true, invalidCitations: 0, code: null, latencyMs: 1280 },
  { id: "holiday", question: "Kolik dní dovolené mám za rok?", expectedAnswerable: false, answerable: false, passed: true, invalidCitations: 0, code: null, latencyMs: 940 },
] });
const client: AiReportsClient = {
  usage: async () => ({ since: "2026-09-29T00:00:00Z", until: "2026-10-05T00:00:00Z", items: [
    { task: "search.answer", kind: "chat", operation: "chat", calls: 128, errors: 2, inputTokens: 42000, outputTokens: 7200, inputKnownCalls: 128, outputKnownCalls: 128, p50Ms: 1350, p95Ms: 4900, estimatedCost: 0.12, costKnownCalls: 128 },
    { task: "editor.transform", kind: "quick", operation: "chat", calls: 36, errors: 0, inputTokens: null, outputTokens: 900, inputKnownCalls: 0, outputKnownCalls: 24, p50Ms: 600, p95Ms: 1400, estimatedCost: null, costKnownCalls: 0 },
    { task: "index.embed", kind: "embeddings", operation: "embedding", calls: 56, errors: 1, inputTokens: 62000, outputTokens: null, inputKnownCalls: 56, outputKnownCalls: 0, p50Ms: 110, p95Ms: 350, estimatedCost: null, costKnownCalls: 0 },
  ], providers: [
    { provider: "anthropic", location: "cloud", model: "claude-sonnet", calls: 128, errors: 2, canceled: 0, inputTokens: 42000, outputTokens: 7200, inputKnownCalls: 128, outputKnownCalls: 128, costKnownCalls: 128, estimatedCost: 0.12, lastCallAt: "2026-10-05T13:42:00Z" },
    { provider: "openai-compatible", location: "company", model: "local-instruct", calls: 36, errors: 0, canceled: 0, inputTokens: null, outputTokens: 900, inputKnownCalls: 0, outputKnownCalls: 24, costKnownCalls: 0, estimatedCost: null, lastCallAt: "2026-10-05T11:10:00Z" },
    { provider: "openai-compatible", location: "company", model: "local-embedding", calls: 56, errors: 1, canceled: 0, inputTokens: 62000, outputTokens: null, inputKnownCalls: 56, outputKnownCalls: 0, costKnownCalls: 0, estimatedCost: null, lastCallAt: "2026-10-05T08:30:00Z" },
  ], days: [30, 28, 32, 35, 31, 34, 30].map((calls, index) => ({
    day: new Date(Date.UTC(2026, 8, 29 + index)).toISOString(), calls, errors: index === 2 || index === 4 || index === 6 ? 1 : 0,
    canceled: 0, inputTokens: [14000, 13000, 16000, 16000, 15000, 16000, 14000][index], outputTokens: [1000, 1100, 1200, 1200, 1100, 1300, 1200][index], inputKnownCalls: [25, 23, 27, 29, 25, 28, 27][index], outputKnownCalls: [20, 20, 22, 23, 22, 24, 21][index],
    costKnownCalls: [18, 18, 18, 19, 18, 19, 18][index], estimatedCost: [0.017, 0.017, 0.017, 0.018, 0.017, 0.018, 0.016][index],
  })) }),
  quality: async () => ({ since: "2026-09-28T00:00:00Z", until: "2026-10-05T00:00:00Z", items: [
    { task: "search.answer", promptVersion: "answer@1", rating: "up", reason: null, count: 23 },
    { task: "search.answer", promptVersion: "answer@1", rating: "down", reason: "wrong_sources", count: 3 },
    { task: "chat.answer", promptVersion: "answer@1", rating: "down", reason: "missing_info", count: 4 },
  ], interactions: [
    { task: "editor.transform", kind: "shorten", outcome: "shown", count: 18 },
    { task: "editor.transform", kind: "shorten", outcome: "applied", count: 12 },
    { task: "page.tag-suggest", kind: "tags", outcome: "dismissed", count: 2 },
  ], noSources: [
    { spaceId: "example-infrastructure", spaceName: "Infrastruktura", count: 7 },
    { spaceId: null, spaceName: "Celá wiki", count: 3 },
  ] }),
};
let pairs: DuplicatePair[] = [
  { pageId: "example-recovery", title: "Obnova databáze po výpadku", otherPageId: "example-backup", otherTitle: "Obnovení databázové zálohy", similarity: 0.987 },
  { pageId: "example-onboarding", title: "První den v týmu", otherPageId: "example-start", otherTitle: "Nástup nového kolegy", similarity: 0.962 },
];
const duplicates: DuplicateClient = { list: async () => ({ items: [...pairs] }), dismiss: async (_space, first, second) => { pairs = pairs.filter(p => p.pageId !== first || p.otherPageId !== second); } };
</script>
<template>
  <SettingsPage title="Správa znalostí a provozu" icon="activity" description="Skutečné reporty a správa duplicit s místními daty. Report ukazuje poskytovatele, denní volání a pokrytí odhadu ceny; kompaktní varianta patří na přehled AI. Dvojice stránek skládá SettingsListRow. Nejde o další obecnou tabulku ani nový seznam.">
    <OptionPills v-model="kind" label="Ukázka reportu" :options="[{ value: 'usage', label: 'Využití' }, { value: 'quality', label: 'Kvalita' }, { value: 'compact', label: 'Kompaktní souhrn' }]" />
    <AiReportsSection :kind="kind === 'quality' ? 'quality' : 'usage'" :client="client" :export-href="null" :compact="kind === 'compact'" />
    <AiEvaluationSection v-if="kind === 'quality'" :run="evaluate" />
    <DuplicatePagesSection space-id="example" :client="duplicates" />
  </SettingsPage>
</template>
