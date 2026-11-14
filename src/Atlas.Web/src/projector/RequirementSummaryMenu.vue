<script setup lang="ts">
import type { Editor } from "@tiptap/core";
import { computed, nextTick, onMounted, ref } from "vue";
import { errorMessage } from "../api";
import AtlasIcon from "../components/AtlasIcon.vue";
import { useToast } from "../composables/useToast";
import t from "../i18n/projector";
import pageText from "../i18n/projector-page";
import { catalogChanged, catalogOf, isCatalogPage } from "./requirementCatalog";
import type { ProjectorHost } from "./requirementsPlugin";

/**
 * The summary's menu in the editor's context menu: a new deployment from this page (its name prefilled, then the server copies the
 * saved page with the blocks' progress reset, spec §1.5), marking the page as a requirement catalog or no longer (spec §1.4) and
 * removing the overview (as Delete on the selected card, with Zpět).
 */
const props = defineProps<{ host: ProjectorHost; editor: Editor; close: () => void; remove?: () => void }>();
const toast = useToast();
const catalog = computed(() => isCatalogPage(props.editor, props.host));
const naming = ref(false);
const title = ref(props.host.pageTitle?.().trim() ? `${props.host.pageTitle().trim()} — ${t.summaryMenu.copySuffix}` : "");
const busy = ref(false);
const error = ref("");
const input = ref<HTMLInputElement>();
const deployment = ref<HTMLButtonElement>();
const created = ref<{ id: string; title: string; warnings: string[] }>();
const openButton = ref<HTMLButtonElement>();
function openCreated() {
  const id = created.value?.id;
  props.close();
  if (id) props.host.open(id);
}
// The server copies what is saved; the page knows what is not yet.
const unsaved = props.host.unsaved?.() ?? false;
onMounted(() => { if (catalog.value === undefined) catalogOf(props.editor, props.host).catch(() => {}); });

async function name() {
  naming.value = true;
  await nextTick();
  input.value?.select();
}
async function back() {
  naming.value = false;
  await nextTick();
  deployment.value?.focus();
}
async function create() {
  const pageId = props.host.pageId();
  if (!pageId || !title.value.trim() || busy.value || !props.editor.isEditable) return;
  busy.value = true;
  error.value = "";
  try {
    // Beside the source page: the same parent.
    const page = await props.host.api.createDeployment(pageId, { spaceId: props.host.spaceId(), parentId: props.host.parentId?.() ?? null, title: title.value.trim() });
    // What of the page's access the copy could not take has to be read, not flash by in a toast: the menu says it.
    if (page.warnings?.length) {
      created.value = { id: page.id, title: page.title, warnings: page.warnings };
      await nextTick();
      openButton.value?.focus();
      return;
    }
    props.close();
    toast.success(pageText.deploymentCreated(page.title), { action: { label: pageText.open, run: () => props.host.open(page.id) } });
  } catch (failure) { error.value = errorMessage(failure); } finally { busy.value = false; }
}
async function toggleCatalog() {
  const pageId = props.host.pageId();
  if (!pageId || !props.editor.isEditable) return;
  try {
    const state = await props.host.api.setCatalogPage(pageId, !catalog.value);
    catalogChanged(props.editor, props.host, state.isCatalog);
    props.close();
    toast.success(state.isCatalog ? pageText.isCatalog : pageText.isNotCatalog);
  } catch (failure) { toast.error(errorMessage(failure)); }
}
</script>

<template>
  <div v-if="created" class="md-context-fields summary-menu-created" role="status" data-summary-created>
    <p class="md-context-field-wide summary-menu-done"><AtlasIcon name="circle-check" :size="16" />{{ pageText.deploymentCreated(created.title) }}</p>
    <div class="md-context-field-wide summary-menu-note">{{ t.summaryMenu.accessPartial }}:
      <ul class="summary-menu-warnings"><li v-for="warning in created.warnings" :key="warning"><AtlasIcon name="info" :size="14" />{{ warning }}</li></ul>
    </div>
    <div class="md-context-field-wide summary-menu-actions">
      <button type="button" class="md-btn" @click="close">{{ t.summaryMenu.close }}</button>
      <button ref="openButton" type="button" class="md-btn-primary" @click="openCreated">{{ pageText.open }}</button>
    </div>
  </div>
  <form v-else-if="naming" class="md-context-fields" @submit.prevent="create">
    <label class="md-context-field md-context-field-wide">{{ t.summaryMenu.newName }}
      <input ref="input" v-model="title" name="title" class="md-input" maxlength="200" required :placeholder="t.summaryMenu.nameExample" />
    </label>
    <p class="md-context-field-wide summary-menu-note">{{ t.summaryMenu.copyNote }}</p>
    <p v-if="unsaved" class="md-context-field-wide summary-menu-note summary-menu-warning" data-summary-unsaved>
      <AtlasIcon name="info" :size="14" />{{ t.summaryMenu.unsaved }}
    </p>
    <p v-if="error" class="md-context-field-wide summary-menu-error" role="alert">{{ error }}</p>
    <div class="md-context-field-wide summary-menu-actions">
      <button type="button" class="md-btn" @click="back">{{ t.stateMenu.back }}</button>
      <button type="submit" class="md-btn-primary" :aria-busy="busy" :disabled="busy">{{ t.summaryMenu.create }}</button>
    </div>
  </form>
  <div v-else class="summary-menu-items">
    <button ref="deployment" type="button" role="menuitem" class="md-context-menu-item" data-summary-action="deployment" @click="name">
      <AtlasIcon name="file-plus" :size="16" />{{ t.summaryMenu.newDeployment }}
    </button>
    <button type="button" role="menuitem" class="md-context-menu-item" data-summary-action="catalog" @click="toggleCatalog">
      <AtlasIcon name="book-open" :size="16" />{{ catalog ? t.summaryMenu.unmarkCatalog : t.summaryMenu.markCatalog }}
    </button>
    <button v-if="remove" type="button" role="menuitem" class="md-context-menu-item" data-summary-action="remove" @click="close(); remove?.()">
      <AtlasIcon name="trash" :size="16" />{{ t.summaryMenu.removeOverview }}
    </button>
  </div>
</template>

<style scoped>
.summary-menu-items { display: grid; gap: 1px; }
.summary-menu-note { margin: 0; color: var(--text-dim, var(--muted)); font-size: 12px; }
.summary-menu-warning { display: flex; align-items: flex-start; gap: 6px; color: var(--text); }
.summary-menu-warning :deep(svg) { flex: none; margin-top: 1px; color: var(--warning, #b7791f); }
.summary-menu-error { margin: 0; color: var(--error); }
.summary-menu-actions { display: flex; justify-content: flex-end; gap: 6px; }
.summary-menu-created { animation: st-fade-in var(--atlas-motion-base) var(--atlas-ease-out); }
.summary-menu-done { display: flex; align-items: center; gap: 6px; margin: 0; font-weight: 600; }
.summary-menu-done :deep(svg) { color: var(--success, #2f855a); }
.summary-menu-warnings { display: grid; gap: 4px; margin: 4px 0 0; padding: 0; list-style: none; color: var(--text); }
.summary-menu-warnings li { display: flex; align-items: flex-start; gap: 6px; }
.summary-menu-warnings :deep(svg) { flex: none; margin-top: 1px; color: var(--warning, #b7791f); }
</style>
