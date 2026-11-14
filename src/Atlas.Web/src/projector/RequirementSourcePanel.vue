<script setup lang="ts">
import type { Editor } from "@tiptap/core";
import { computed, nextTick, onBeforeUnmount, onMounted, ref, shallowRef } from "vue";
import { errorMessage } from "../api";
import ErrorState from "../components/settings/ErrorState.vue";
import SearchableChoiceList from "../components/settings/SearchableChoiceList.vue";
import type { ChoiceOption } from "../components/settings/types";
import LoadingSkeleton from "../components/LoadingSkeleton.vue";
import t from "../i18n/projector";
import { foldText } from "../lib/foldText";
import type { TypeAhead } from "../lib/typeAhead";
import RequirementCatalogPanel from "./RequirementCatalogPanel.vue";
import "../components/settings/settings.css";
import type { ProjectorHost } from "./requirementsPlugin";

/**
 * „Požadavek z nasazení“ and „Požadavky ze sady“ (M4a): first the page to take from — a set (a space template with requirement
 * blocks) or another deployment of the space — in a searchable list whose first match Enter picks, then that page's blocks and
 * modules in the catalog's own picker (RequirementCatalogPanel with a `source`), inserted with what they need and reset for this
 * page as a catalog pick is. „Jiné nasazení“ goes back to the list. Why not a new picker: the list is SearchableChoiceList, the
 * blocks CatalogPicker; only the step between them is new.
 */
type Insert = InstanceType<typeof RequirementCatalogPanel>["$props"]["insert"];
const props = defineProps<{ host: ProjectorHost; editor: Editor; insert: Insert; close: () => void; typeAhead?: TypeAhead; presetsOnly?: boolean }>();
type Source = { pageId: string; title: string; preset: boolean; requirements?: number };
const sources = shallowRef<Source[]>([]);
const status = ref<"loading" | "ready" | "error">("loading");
const loadError = ref("");
const query = ref("");
const chosen = shallowRef<Source>();
const root = ref<HTMLElement>();
let closed = false;

const options = computed<ChoiceOption[]>(() => sources.value.map(source => ({
  value: source.pageId, label: source.title, icon: source.preset ? "layout-template" : "list-checks",
  description: source.preset ? `${t.source.preset}${source.requirements ? ` · ${t.counts.requirements(source.requirements)}` : ""}` : t.source.deployment,
})));
const sourceProp = computed(() => chosen.value && {
  label: chosen.value.title, load: () => props.host.api.pageDefinitions!(chosen.value!.pageId),
});

function pick(pageId: string) {
  chosen.value = sources.value.find(source => source.pageId === pageId);
}
async function back() {
  chosen.value = undefined;
  await nextTick();
  root.value?.querySelector<HTMLInputElement>("input[type=search]")?.focus();
}

async function load() {
  status.value = "loading";
  const spaceId = props.host.spaceId(), own = props.host.pageId();
  try {
    const [presets, pages] = await Promise.all([props.host.api.presets?.(spaceId) ?? [], props.presetsOnly ? [] : props.host.api.deployments?.(spaceId) ?? []]);
    if (closed) return;
    sources.value = [...presets.map(preset => ({ ...preset, preset: true })), ...pages.filter(page => page.pageId !== own).map(page => ({ ...page, preset: false }))];
    status.value = "ready";
    await nextTick();
    // What was typed after the slash entry is the search; Enter after it takes the first match (typeAhead.ts).
    const ahead = props.typeAhead?.take();
    if (ahead?.text) query.value = ahead.text;
    const words = foldText(query.value).split(/\s+/).filter(Boolean);
    const first = sources.value.find(source => words.every(word => foldText(source.title).includes(word)));
    if (ahead?.enter && words.length && first) pick(first.pageId);
    else root.value?.querySelector<HTMLInputElement>("input[type=search]")?.focus();
  } catch (failure) {
    if (!closed) { loadError.value = errorMessage(failure); status.value = "error"; }
  }
}
onMounted(load);
onBeforeUnmount(() => { closed = true; });
</script>

<template>
  <div ref="root" class="requirement-source settings-ui" data-requirement-source>
    <template v-if="chosen && sourceProp">
      <button type="button" class="md-btn requirement-source-back" data-requirement-source-back @click="back">← {{ t.source.other }}</button>
      <RequirementCatalogPanel :key="chosen.pageId" :host="host" :editor="editor" :insert="insert" :close="close" :source="sourceProp" />
    </template>
    <LoadingSkeleton v-else-if="status === 'loading'" :rows="4" :label="t.source.loading" />
    <ErrorState v-else-if="status === 'error'" :title="t.source.loadFailed" :text="loadError" @retry="load" />
    <p v-else-if="!sources.length" class="requirement-source-empty" data-requirement-source-empty>
      {{ presetsOnly ? t.source.noPreset : t.source.noSource }}
    </p>
    <SearchableChoiceList v-else v-model:query="query" :model-value="null" :options="options" :label="presetsOnly ? t.source.presetLabel : t.source.sourceLabel"
      :placeholder="presetsOnly ? t.source.searchPreset : t.source.searchSource" :max-height="280" @pick="pick" />
  </div>
</template>

<style scoped>
.requirement-source { display: grid; gap: 8px; min-width: 0; }
.requirement-source-back { justify-self: start; }
.requirement-source-empty { margin: 0; color: var(--muted, var(--text-dim)); }
</style>
